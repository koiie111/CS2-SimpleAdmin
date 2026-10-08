using System.Diagnostics;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using Dapper;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R8 and the residual game-thread work: the warns menu reads a fixed-size page in SQL (so the game thread builds a
/// constant number of options however long the history is), admin help is a managed copy read in the background, and a
/// dispatcher item that alone exceeds the budget is visible in the metrics (the budget is checked between items only).
/// </summary>
public class R8_BoundedGameThreadWorkTests
{
    public static IEnumerable<object[]> Engines() => TestDatabases.All();

    private const ulong Steam = 76561198000000001;

    private static async Task SeedWarns(TestDatabase db, int count, ulong steam = Steam)
    {
        await using var c = await db.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        var longReason = new string('r', 250); // sa_warns.reason is VARCHAR(255); longer than the 80-char menu cut
        var sql = "INSERT INTO sa_warns (player_steamid, player_name, admin_steamid, admin_name, reason, duration, ends, created, status, server_id) " +
                  "VALUES (@steam, 'p', 0, 'Console', @reason, 0, @ends, @created, @status, 1)";
        var rows = Enumerable.Range(0, count).Select(i => new
        {
            steam, reason = i % 7 == 0 ? longReason : $"warn {i}", ends = DateTime.Now.AddDays(1), created = DateTime.Now,
            status = i % 5 == 0 ? "ACTIVE" : "EXPIRED"
        });
        await c.ExecuteAsync(sql, rows, tx);
        await tx.CommitAsync();
    }

    [Theory]
    [InlineData("SQLite", 10)]
    [InlineData("SQLite", 10_000)]
    [InlineData("SQLite", 100_000)]
    public async Task WarnsMenuPageSizeIsConstantRegardlessOfHistoryLength(string engine, int history)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        await SeedWarns(db, history);
        var manager = new WarnManager(db.Provider);

        var (total, rows) = await manager.GetPlayerWarnsPageAsync(Steam, 1, WarnManager.MenuPageSize, default);

        Assert.Equal(history, total);
        Assert.Equal(Math.Min(history, WarnManager.MenuPageSize), rows.Count); // O(page), not O(history)
        Assert.All(rows, r => Assert.True((r.Reason?.Length ?? 0) <= Database.SharedQueries.WarnMenuReasonChars)); // cut in SQL
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task PagesAreStableCompleteAndOrderedActiveFirst(string engine)
    {
        await using var db = await TestDatabases.CreateAsync(engine);
        const int history = 1_000;
        await SeedWarns(db, history);
        var manager = new WarnManager(db.Provider);

        var seen = new List<int>();
        var pages = (history + WarnManager.MenuPageSize - 1) / WarnManager.MenuPageSize;
        var phase = "ACTIVE";
        var previousId = int.MaxValue;
        for (var page = 1; page <= pages; page++)
        {
            var (total, rows) = await manager.GetPlayerWarnsPageAsync(Steam, page, WarnManager.MenuPageSize, default);
            Assert.Equal(history, total);
            foreach (var r in rows)
            {
                if (r.Status != phase)
                {
                    Assert.Equal("ACTIVE", phase); // only one switch: active ones first, then the rest
                    phase = r.Status;
                    previousId = int.MaxValue;
                }

                Assert.True(r.Id < previousId, "newest first inside each group");
                previousId = r.Id;
                seen.Add(r.Id);
            }
        }

        Assert.Equal(history, seen.Count);
        Assert.Equal(history, seen.Distinct().Count()); // no repeated and no skipped row across pages
        var (_, beyond) = await manager.GetPlayerWarnsPageAsync(Steam, pages + 1, WarnManager.MenuPageSize, default);
        Assert.Empty(beyond);
    }

    [Fact]
    public async Task WarnsOfEveryServerAreCounted()
    {
        await using var db = await TestDatabases.CreateAsync("SQLite");
        await SeedWarns(db, 20);
        var manager = new WarnManager(db.Provider);
        var (total, rows) = await manager.GetPlayerWarnsPageAsync(Steam, 1, 8, default);
        Assert.Equal(20, total); // whichever server issued them
        Assert.Equal(8, rows.Count);
    }

    [Fact]
    public async Task ADatabaseErrorPropagatesInsteadOfShowingAnEmptyMenu()
    {
        var provider = new OutageProvider();
        await Assert.ThrowsAsync<IOException>(() =>
            new WarnManager(provider).GetPlayerWarnsPageAsync(Steam, 1, 8, default));
    }

    // ---- admin help ----
    [Fact]
    public void AdminHelpIsReadOnceBoundedAndNotAgainUntilTheFileChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"admin_help_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(0, 200).Select(i => i == 3 ? "" : new string('x', 500) + i));
            var cache = new AdminHelpCache();
            Assert.False(cache.IsLoaded);

            var decorated = 0;
            Assert.True(cache.Refresh(path, line => { decorated++; return line; }));
            Assert.True(cache.IsLoaded);
            Assert.Equal(AdminHelpCache.MaxLines, cache.Lines.Length);
            Assert.Equal(200 - AdminHelpCache.MaxLines, cache.OmittedLines);
            Assert.All(cache.Lines, l => Assert.True(l.Length <= AdminHelpCache.MaxLineChars));
            Assert.Equal(" ", cache.Lines[3]); // blank lines stay printable
            Assert.Equal(AdminHelpCache.MaxLines - 1, decorated); // only displayed non-blank lines are processed

            var before = decorated;
            Assert.True(cache.Refresh(path, line => { decorated++; return line; }));
            Assert.Equal(before, decorated); // unchanged file: no re-read, no re-processing

            File.WriteAllText(path, "only line");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.True(cache.Refresh(path, l => l));
            Assert.Equal(["only line"], cache.Lines);
            Assert.Equal(0, cache.OmittedLines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnreadableAdminHelpKeepsTheCopyThatWasLoaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"admin_help_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello");
        var cache = new AdminHelpCache();
        Assert.True(cache.Refresh(path, l => l));
        File.Delete(path);
        File.WriteAllText(path + "x", "");
        Assert.False(cache.Refresh(Path.Combine(Path.GetTempPath(), "no_such_dir_" + Guid.NewGuid(), "x.txt"), l => l));
        Assert.Equal(["hello"], cache.Lines);
        File.Delete(path + "x");
    }

    [Fact]
    public void AdminHelpCommandNoLongerReadsTheFileOnTheGameThread()
    {
        var source = File.ReadAllText(Path.Combine(SourceFiles.Root, "CS2-SimpleAdmin", "Commands", "basecommands.cs"));
        var start = source.IndexOf("public void OnAdminHelpCommand", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("internal static readonly AdminHelpCache", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("File.", body);
        Assert.DoesNotContain("ReadAllLines", body);
        Assert.DoesNotContain("ReplaceColorTags", body); // processed once in the background, not per command
    }

    // ---- dispatcher ----
    [Fact]
    public void AnItemLongerThanTheBudgetIsCountedAndTheBudgetOnlyStopsBetweenItems()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule, budgetMilliseconds: 0.5);
        var ran = 0;
        var overBefore = Interlocked.Read(ref PluginMetrics.DispatcherOverBudgetItems);
        dispatcher.TryPost(() => { Thread.Sleep(5); ran++; });  // one item far above the budget
        dispatcher.TryPost(() => ran++);
        dispatcher.TryPost(() => ran++);

        world.RunOneUpdate();
        Assert.Equal(1, ran); // the long item ran to completion, then the pump stopped
        Assert.True(Interlocked.Read(ref PluginMetrics.DispatcherOverBudgetItems) > overBefore);
        world.RunOneUpdate();
        Assert.Equal(3, ran); // the rest continues on the next update
    }

    [Fact]
    public async Task CancelledPostIsReportedAsCancelledNotAsAnError()
    {
        var world = new FakeWorldUpdates();
        var dispatcher = new GameDispatcher(world.Schedule);
        var task = dispatcher.PostAsync(() => throw new OperationCanceledException());
        world.RunOneUpdate();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }
}
