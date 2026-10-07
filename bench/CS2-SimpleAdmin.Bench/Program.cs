using System.Diagnostics;
using System.Runtime;
using System.Text;
using CS2_SimpleAdmin;
using CS2_SimpleAdmin.Bench;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;

// Synthetic micro/meso benchmarks: same machine, same data, original algorithm (copied from f544af7) vs new.
// They measure CPU/allocation of the plugin's own code paths, NOT CS2 frame time (see report).

var quick = args.Contains("--quick");
var sb = new StringBuilder();
void Line(string s = "") { Console.WriteLine(s); sb.AppendLine(s); }

Line($"# CS2-SimpleAdmin synthetic benchmarks");
Line();
Line($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}, .NET {Environment.Version}, {RuntimeInformationText()}");
Line($"- CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, logical cores: {Environment.ProcessorCount}");
Line($"- GC: {(GCSettings.IsServerGC ? "server" : "workstation")}, concurrent; Release build; tiered JIT; results after warm-up unless stated");
Line();

var now = new DateTime(2026, 10, 7, 12, 0, 0);
CS2_SimpleAdmin.CS2_SimpleAdmin.UseConfigWithoutPlugin(new CS2_SimpleAdminConfig { OtherSettings = { TimeMode = 1 } });

// ---------------------------------------------------------------- A/B/I: multi-account cache
Line("## A. Ban check with CheckMultiAccountsByIp (connect / 61 s pass)");
Line();
Line("Per check of a non-banned player (worst case for the old full scans). µs, allocations per check.");
Line();
Line("| IP rows | accounts | old p50 | old p99 | old max | old B/op | new p50 | new p99 | new max | new B/op |");
Line("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
var buildRows = new List<string>();
var periodicRows = new List<string>();
foreach (var rows in quick ? new[] { 10_000, 100_000 } : new[] { 10_000, 100_000, 1_000_000 })
{
    var (bans, history, accounts) = Data.Generate(rows, now);
    var sorted = history.OrderBy(h => h.Steamid).ThenBy(h => h.Address).ThenByDescending(h => h.Used_at).ToList();
    var oldBans = bans.Select(b => new OldBanRecord { Id = b.Id, PlayerSteamId = b.PlayerSteamId, PlayerIp = b.PlayerIp, Created = b.Created, Status = b.Status }).ToList();

    // B: build cost
    var (oldCache, oldBuild) = Measure.Build(() => { var c = new OldCache(); c.Initialize(oldBans, sorted); return c; });
    var (snapshot, newBuild) = Measure.Build(() =>
    {
        var builder = new IpIndexBuilder("Unknown");
        builder.Add(history);
        return BanCacheSnapshot.Create(bans, builder.Build(), []);
    });
    buildRows.Add($"| {rows:N0} | {oldBuild.Ms:F0} | {oldBuild.AllocMb:F0} | {oldBuild.RetainedMb:F0} | {newBuild.Ms:F0} | {newBuild.AllocMb:F0} | {newBuild.RetainedMb:F0} |");

    var rnd = new Random(7);
    var players = Enumerable.Range(0, 2000).Select(_ => Data.CleanPlayer(rnd, accounts)).ToArray();
    var iterations = rows >= 1_000_000 ? 30 : rows >= 100_000 ? 200 : 2000;
    var i = 0;
    var oldStats = Measure.Op(() => { var p = players[i++ % players.Length]; oldCache.IsPlayerOrAnyIpBanned("n", p.SteamId, p.Ip, now); }, iterations);
    i = 0;
    var newStats = Measure.Op(() => { var p = players[i++ % players.Length]; snapshot.CheckPlayerOrAnyIp(p.SteamId, p.Ip, 1, 0, true, now); }, 20_000);
    Line($"| {rows:N0} | {accounts:N0} | {oldStats.P50:F1} | {oldStats.P99:F1} | {oldStats.Max:F1} | {oldStats.BytesPerOp:N0} | {newStats.P50:F2} | {newStats.P99:F2} | {newStats.Max:F1} | {newStats.BytesPerOp:N0} |");

    // I: one periodic pass = 64 online players checked
    var online = players.Take(64).ToArray();
    var oldPass = Measure.Op(() => { foreach (var p in online) oldCache.IsPlayerOrAnyIpBanned("n", p.SteamId, p.Ip, now); }, rows >= 1_000_000 ? 3 : 20);
    var newPass = Measure.Op(() => { foreach (var p in online) snapshot.CheckPlayerOrAnyIp(p.SteamId, p.Ip, 1, 0, true, now); }, 500);
    var accountsOld = Measure.Op(() => oldCache.GetAccountsByIp(online[0].Ip), rows >= 1_000_000 ? 5 : 50);
    var accountsNew = Measure.Op(() => snapshot.GetAccountsByIp(IpHelper.IpToUint(online[0].Ip)), 20_000);
    periodicRows.Add($"| {rows:N0} | {oldPass.P50 / 1000:F1} | {newPass.P50 / 1000:F3} | {accountsOld.P50:F0} | {accountsNew.P50:F2} |");

    // C: incremental refresh on the large snapshot
    if (rows == (quick ? 100_000 : 1_000_000))
    {
        var delta = Enumerable.Range(0, 2000).Select(k => new IpHistoryRow
            { Steamid = 76561198000000000L + rnd.Next(0, accounts * 2), Address = (uint)rnd.Next(), Used_at = now, Name = "x" }).ToList();
        var d300 = Measure.Op(() => snapshot.WithIpHistory(delta.Take(300), "Unknown"), 30);
        var d2000 = Measure.Op(() => snapshot.WithIpHistory(delta, "Unknown"), 10);
        var b10 = Measure.Op(() => snapshot.WithBans(bans.Take(10).Select(b => b with { PlayerIp = "1.2.3.4" })), 30);
        Line();
        Line($"Incremental updates on the {rows:N0}-row snapshot (new code only; the old code mutated shared sets in place):");
        Line($"WithIpHistory 300 rows p50 {d300.P50 / 1000:F2} ms ({d300.BytesPerOp / 1024:N0} KB), 2000 rows p50 {d2000.P50 / 1000:F2} ms ({d2000.BytesPerOp / 1024:N0} KB); WithBans 10 changes p50 {b10.P50 / 1000:F2} ms ({b10.BytesPerOp / 1024:N0} KB, active bans {snapshot.ActiveCount:N0}).");
        Line();
    }

    GC.KeepAlive(oldCache);
    GC.KeepAlive(snapshot);
}

Line();
Line("## B. Full cache build (startup / css_reloadbans), background thread");
Line();
Line("| IP rows | old ms | old alloc MB | old retained MB | new ms | new alloc MB | new retained MB |");
Line("|---:|---:|---:|---:|---:|---:|---:|");
foreach (var r in buildRows) Line(r);
Line();
Line("New retained memory includes the IP→accounts reverse index the old code did not have.");
Line();
Line("## I. 61 s pass: ban check of 64 online players, and GetAccountsByIp");
Line();
Line("| IP rows | old pass ms | new pass ms | old GetAccountsByIp µs | new GetAccountsByIp µs |");
Line("|---:|---:|---:|---:|---:|");
foreach (var r in periodicRows) Line(r);
Line();

// ---------------------------------------------------------------- D: chat gag check
Line("## D. Chat gag/silence check (every chat message, game thread)");
Line();
OldPenalties.Penalties[1] = new() { [PenaltyType.Gag] = [(now.AddHours(1), 60, false)] };
PlayerPenaltyManager.RemoveAllPenalties();
PlayerPenaltyManager.AddPenalty(1, PenaltyType.Gag, DateTime.UtcNow.AddHours(1), 60);
var oldGag = Measure.Batch(() => OldPenalties.IsPenalized(1, PenaltyType.Gag, now, out _), 1000, 2000);
var newGag = Measure.Batch(() => PlayerPenaltyManager.IsPenalized(1, PenaltyType.Gag, out _), 1000, 2000);
var oldNone = Measure.Batch(() => OldPenalties.IsPenalized(2, PenaltyType.Gag, now, out _), 1000, 2000);
var newNone = Measure.Batch(() => PlayerPenaltyManager.HasAnyPenalty(2, PenaltyType.Gag, PenaltyType.Silence), 1000, 2000);
Line("| case | old p50 ns | old B/op | new p50 ns | new B/op |");
Line("|---|---:|---:|---:|---:|");
Line($"| gagged player | {oldGag.P50 * 1000:F0} | {oldGag.BytesPerOp:N0} | {newGag.P50 * 1000:F0} | {newGag.BytesPerOp:N0} |");
Line($"| no penalty (fast path) | {oldNone.P50 * 1000:F0} | {oldNone.BytesPerOp:N0} | {newNone.P50 * 1000:F0} | {newNone.BytesPerOp:N0} |");
Line();
Line("Measured in batches of 1000 calls. The new 'gagged' path includes the timezone conversion of Time.ActualDateTime; the old one used the same clock but also copied the list (ToList) on every message.");
Line();

// ---------------------------------------------------------------- F: history output per game-thread callback
Line("## F. css_history: work done in ONE game-thread callback");
Line();
Line("| player history rows | old: format+print all rows in one callback µs | new: largest callback (20 lines) µs | new rows per page |");
Line("|---:|---:|---:|---:|");
foreach (var n in new[] { 10, 1000, 10_000 })
{
    var historyRows = Enumerable.Range(0, n).Select(k => new PenaltyHistoryRow
    {
        Id = k, Type = "BAN", Status = "EXPIRED", Duration = 60, Admin_Name = "Console", Reason = "reason text", Created = now.AddMinutes(-k)
    }).ToList();
    var sink = 0;
    var oldF = Measure.Op(() => { foreach (var r in historyRows) sink += CS2_SimpleAdmin.CS2_SimpleAdmin.FormatHistoryRow(r).Length; }, n >= 10_000 ? 20 : 200);
    var page = historyRows.Take(20).ToList();
    var newF = Measure.Op(() => { foreach (var r in page) sink += r.Reason!.Length + 40; }, 2000);
    Line($"| {n:N0} | {oldF.P50:F0} | {newF.P50:F1} (formatting moved to the DB worker) | {Math.Min(n, CS2_SimpleAdmin.CS2_SimpleAdmin.HistoryConsolePageSize)} |");
    GC.KeepAlive(sink);
}

Line();
Line("Old: the whole history (and its Dapper dynamic rows) was formatted and printed inside one NextWorldUpdate. New: SQL returns one page (50), formatting happens on the worker, the game thread prints ≤20 lines per dispatcher item.");
Line();

// ---------------------------------------------------------------- E: dispatcher
Line("## E. Game-thread dispatcher: cost per world update");
Line();
var updates = new Queue<Action>();
var dispatcher = new GameDispatcher(a => updates.Enqueue(a), capacity: 100_000, maxItemsPerUpdate: 64, budgetMilliseconds: 0.5);
var emptyPump = Measure.Batch(() => dispatcher.Pump(), 1000, 200);
Line($"- Idle (no queued work): no pump is scheduled at all; a pump call on an empty queue costs p50 {emptyPump.P50 * 1000:F0} ns, {emptyPump.BytesPerOp} B.");
var itemCost = Measure.Op(() => Thread.SpinWait(200), 2000);
Line($"- Synthetic item: SpinWait(200) ≈ {itemCost.P50:F1} µs.");
for (var k = 0; k < 10_000; k++) dispatcher.TryPost(() => Thread.SpinWait(200));
var perUpdate = new List<double>();
while (updates.Count > 0)
{
    var start = Stopwatch.GetTimestamp();
    updates.Dequeue()();
    perUpdate.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
}

perUpdate.Sort();
Line($"- 10,000 queued items: drained over {perUpdate.Count} world updates, per-update p50 {perUpdate[perUpdate.Count / 2]:F0} µs, max {perUpdate[^1]:F0} µs (budget 500 µs / 64 items). Old code: 10,000 NextWorldUpdate callbacks, CSS runs up to MaximumFrameTasksExecutedPerTick (core.json) of them per tick.");
Line();

// ---------------------------------------------------------------- G: renames
Line("## G. Rename enforcement (every 5 s and on round start)");
Line();
Line("| renames | online players | old µs | old B | new µs | new B |");
Line("|---:|---:|---:|---:|---:|---:|");
foreach (var p in new[] { 0, 16, 32, 64 })
{
    var renames = Enumerable.Range(0, 1000).ToDictionary(k => 76561198000000000UL + (ulong)k * 13, k => "forced" + k);
    var online = Enumerable.Range(0, p).Select(k => (Steam: 76561198000000000UL + (ulong)k * 7, Name: "p" + k, Valid: true)).ToList();
    var oldR = Measure.Op(() =>
    {
        foreach (var (steamid, name) in renames)
        {
            // Helper.GetPlayerFromSteamid64 → GetValidPlayers(): a new filtered list per rename, then FirstOrDefault
            var list = online.Where(x => x.Valid).ToList();
            var player = list.FirstOrDefault(x => x.Steam == steamid);
            if (player.Valid && player.Name != name) { }
        }
    }, 2000);
    var newR = Measure.Op(() =>
    {
        foreach (var player in online)
            if (player.Valid && renames.TryGetValue(player.Steam, out var name) && player.Name != name) { }
    }, 20_000);
    Line($"| 1,000 | {p} | {oldR.P50:F1} | {oldR.BytesPerOp:N0} | {newR.P50:F2} | {newR.BytesPerOp:N0} |");
}

Line();

// ---------------------------------------------------------------- H: stealth transmit (managed part only)
Line("## H. Stealth CheckTransmit, managed part (one silent admin)");
Line();
Line("| tracked players = recipients | old µs/call | old B/call | new µs/call | new B/call |");
Line("|---:|---:|---:|---:|---:|");
foreach (var p in new[] { 16, 32, 64 })
{
    var players = Enumerable.Range(0, p).Select(k => new FakePlayer(k, 100 + k)).ToList();
    // One transmit bit vector per recipient, as CheckTransmit gets one CCheckTransmitInfo per client; all observer
    // pawns start as "transmitted" (worst case: everything has to be cleared)
    var vectors = Enumerable.Range(0, p).Select(_ => new uint[512]).ToArray();
    void Fill() { foreach (var v in vectors) Array.Fill(v, uint.MaxValue); }
    var oldH = Measure.Op(() =>
    {
        Fill();
        var valid = players.Select(x => new { x.Slot, ObserverPawn = x.Observer }).Where(x => x.ObserverPawn >= 0).ToArray();
        foreach (var recipient in players)
        foreach (var target in valid)
        {
            if (target.Slot == recipient.Slot) continue;
            var bits = vectors[recipient.Slot];
            if (Bits.Contains(bits, target.ObserverPawn)) Bits.Remove(bits, target.ObserverPawn);
        }
    }, 20_000);
    var indices = players.Select(x => x.Observer).ToArray();
    var owners = players.Select(x => x.Slot).ToArray();
    var newH = Measure.Op(() =>
    {
        Fill();
        for (var r = 0; r < players.Count; r++)
        {
            var bits = vectors[r];
            for (var j = 0; j < indices.Length; j++)
                if (owners[j] != r) Bits.Remove(bits, indices[j]);
        }
    }, 20_000);
    var fillOnly = Measure.Op(Fill, 20_000);
    Line($"| {p} | {oldH.P50 - fillOnly.P50:F2} | {oldH.BytesPerOp:N0} | {newH.P50 - fillOnly.P50:F2} | {newH.BytesPerOp:N0} |");
}

Line();
Line("Not included: the old code also resolved every player's ObserverPawn handle (native) on every call and created a controller wrapper per recipient; the new code resolves pawns only after connect/team/spawn/round events (or once per second) and reads the recipient slot directly.");

var outPath = args.FirstOrDefault(a => a.EndsWith(".md"));
if (outPath != null) File.WriteAllText(outPath, sb.ToString());
return;

static string RuntimeInformationText() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

internal sealed record FakePlayer(int Slot, int Observer);

internal static class Bits
{
    public static bool Contains(uint[] v, int bit) => (v[bit >> 5] & (1u << (bit & 31))) != 0;
    public static void Remove(uint[] v, int bit) => v[bit >> 5] &= ~(1u << (bit & 31));
}

internal static class Data
{
    public static (List<BanRecord> Bans, List<IpHistoryRow> History, int Accounts) Generate(int rows, DateTime now)
    {
        var rnd = new Random(42);
        var accounts = Math.Max(10, (int)(rows / 2.5));
        var ipPool = Math.Max(10, (int)(rows * 0.7));
        var history = new List<IpHistoryRow>(rows);
        for (var k = 0; k < rows; k++)
            history.Add(new IpHistoryRow
            {
                Steamid = 76561198000000000L + rnd.Next(0, accounts), Address = (uint)(167772160 + rnd.Next(0, ipPool)),
                Used_at = now.AddMinutes(-rnd.Next(0, 500_000)), Name = "p"
            });
        var bans = new List<BanRecord>();
        for (var k = 1; k <= accounts / 50; k++)
            bans.Add(new BanRecord
            {
                Id = k, PlayerSteamId = 76561198000000000UL + (ulong)rnd.Next(0, accounts), Status = "ACTIVE", Duration = 0,
                Created = now.AddDays(-1), Ends = now.AddDays(-1)
            });
        return (bans, history, accounts);
    }

    /// <summary>A player id outside the generated range (never banned, no shared IP): the full-scan worst case.</summary>
    public static (ulong SteamId, string Ip) CleanPlayer(Random rnd, int accounts) =>
        (76561198000000000UL + (ulong)(accounts + rnd.Next(1, 1_000_000)), $"192.168.{rnd.Next(0, 255)}.{rnd.Next(1, 255)}");
}

internal static class Measure
{
    public sealed record OpStats(double P50, double P99, double Max, long BytesPerOp);
    public sealed record BuildStats(double Ms, double AllocMb, double RetainedMb);

    /// <summary>µs per call (p50/p99/max) and allocated bytes per call, after warm-up.</summary>
    public static OpStats Op(Action op, int iterations)
    {
        for (var w = 0; w < Math.Min(50, iterations); w++) op();
        var samples = new double[iterations];
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < iterations; k++)
        {
            var start = Stopwatch.GetTimestamp();
            op();
            samples[k] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        var alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Array.Sort(samples);
        return new OpStats(samples[iterations / 2], samples[Math.Min(iterations - 1, (int)(iterations * 0.99))], samples[^1], alloc / iterations);
    }

    /// <summary>Per-call µs measured over batches of <paramref name="batch"/> calls (below timer resolution).</summary>
    public static OpStats Batch(Action op, int batch, int samples)
    {
        var stats = Op(() => { for (var k = 0; k < batch; k++) op(); }, samples);
        return new OpStats(stats.P50 / batch, stats.P99 / batch, stats.Max / batch, stats.BytesPerOp / batch);
    }

    public static (T Result, BuildStats Stats) Build<T>(Func<T> build)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var before = GC.GetTotalMemory(true);
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var result = build();
        sw.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        var after = GC.GetTotalMemory(true);
        GC.KeepAlive(result);
        return (result, new BuildStats(sw.Elapsed.TotalMilliseconds, alloc / 1048576.0, (after - before) / 1048576.0));
    }
}
