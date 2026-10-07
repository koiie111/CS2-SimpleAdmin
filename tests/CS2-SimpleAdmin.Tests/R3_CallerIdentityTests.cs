using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// R3: a failed write is reported using a caller identity captured on the game thread; the worker touches no
/// controller / native state, and the message is delivered by a game-thread callback only to the same connection.
/// </summary>
public class R3_CallerIdentityTests : IDisposable
{
    private readonly ConcurrentBag<(string What, int Thread)> _nativeCalls = [];
    private readonly ConcurrentBag<(int Slot, string Message)> _chat = [];
    private readonly ConcurrentBag<string> _console = [];
    private readonly (Func<int, (ulong, int)?> Lookup, Action<int, string> Chat, Action<string> Console) _saved;

    private (ulong SteamId, int UserId)? _slotOccupant;

    public R3_CallerIdentityTests()
    {
        _saved = (CallerRef.Lookup, CallerRef.PrintToChat, CallerRef.PrintToConsole);
        CallerRef.Lookup = _ =>
        {
            _nativeCalls.Add(("lookup", Environment.CurrentManagedThreadId));
            return _slotOccupant;
        };
        CallerRef.PrintToChat = (slot, message) =>
        {
            _nativeCalls.Add(("chat", Environment.CurrentManagedThreadId));
            _chat.Add((slot, message));
        };
        CallerRef.PrintToConsole = message =>
        {
            _nativeCalls.Add(("console", Environment.CurrentManagedThreadId));
            _console.Add(message);
        };
    }

    public void Dispose()
    {
        CallerRef.Lookup = _saved.Lookup;
        CallerRef.PrintToChat = _saved.Chat;
        CallerRef.PrintToConsole = _saved.Console;
    }

    private static readonly CallerRef Admin = new(false, 3, 76561198000000010, 42);

    /// <summary>Queues a write that fails after a delay, then runs the game thread until the report was delivered.</summary>
    private async Task<int> RunDelayedFailure(CallerRef caller, Action beforeFailureIsReported)
    {
        using var world = new TestWorld();
        CS2_SimpleAdmin.DatabaseProvider = new OutageProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerThread = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(CS2_SimpleAdmin.TryQueuePenaltyWork(caller, null, "ban-write", async _ =>
        {
            await gate.Task;
            workerThread = Environment.CurrentManagedThreadId;
            await CS2_SimpleAdmin.ReportWriteFailureAsync("Ban of Target");
            finished.SetResult();
        }));

        beforeFailureIsReported(); // the caller disconnects / the slot is reused while the SQL is "running"
        gate.SetResult();
        await world.PumpUntil(() => finished.Task.IsCompleted);
        return workerThread;
    }

    [Fact]
    public async Task FailureIsDeliveredToTheSameConnectionOnTheGameThreadOnly()
    {
        _slotOccupant = (Admin.SteamId, Admin.UserId); // still connected
        var worker = await RunDelayedFailure(Admin, () => { });

        var message = Assert.Single(_chat);
        Assert.Equal(Admin.Slot, message.Slot);
        Assert.Contains("Ban of Target", message.Message);
        Assert.Empty(_console);
        Assert.All(_nativeCalls, c => Assert.NotEqual(worker, c.Thread)); // nothing native was touched on the worker
        Assert.Contains(_nativeCalls, c => c.What == "lookup");
    }

    [Fact]
    public async Task FailureAfterDisconnectGoesToTheConsoleAndNotToAPlayer()
    {
        _slotOccupant = (Admin.SteamId, Admin.UserId);
        var worker = await RunDelayedFailure(Admin, () => _slotOccupant = null); // caller left

        Assert.Empty(_chat);
        Assert.Contains(_console, m => m.Contains("could NOT be saved"));
        Assert.All(_nativeCalls, c => Assert.NotEqual(worker, c.Thread));
    }

    [Fact]
    public async Task FailureAfterSlotReuseByAnotherAccountNeverReachesThatPlayer()
    {
        _slotOccupant = (Admin.SteamId, Admin.UserId);
        var worker = await RunDelayedFailure(Admin, () => _slotOccupant = (76561198000000099, 77));

        Assert.Empty(_chat);
        Assert.Single(_console);
        Assert.All(_nativeCalls, c => Assert.NotEqual(worker, c.Thread));
    }

    [Fact]
    public async Task SameAccountReconnectingWithANewUserIdIsAlsoNotTheCaller()
    {
        _slotOccupant = (Admin.SteamId, Admin.UserId);
        await RunDelayedFailure(Admin, () => _slotOccupant = (Admin.SteamId, Admin.UserId + 1));

        Assert.Empty(_chat);
        Assert.Single(_console);
    }

    [Fact]
    public async Task ConsoleCallerReportsToTheConsole()
    {
        _slotOccupant = (Admin.SteamId, Admin.UserId);
        await RunDelayedFailure(CallerRef.Console, () => { });
        Assert.Empty(_chat);
        Assert.Single(_console);
    }

    [Fact]
    public void ReportWriteFailureTakesNoControllerAndNoCallerArgument()
    {
        var method = typeof(CS2_SimpleAdmin).GetMethod("ReportWriteFailureAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(string), parameter.ParameterType);
    }

    // ---- static contract: no background lambda may read a controller ----
    // Identifiers that name a CCSPlayerController in this code base. Inside a lambda handed to the DB queue they may only appear
    // inside a game-thread callback (Runtime.OnGameThread / PostAsync / TryPost).
    private static readonly Regex ControllerNames = new(@"\b(caller|player|namePlayer|controller|target|targetPlayer)\b", RegexOptions.Compiled);

    [Fact]
    public void NoDatabaseJobReadsAControllerOutsideAGameThreadCallback()
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles.Production())
        {
            var text = SourceFiles.StripComments(File.ReadAllText(file));
            foreach (Match call in Regex.Matches(text, @"\b(TryQueuePenaltyWork|TryQueueDb|TryEnqueue)\s*\("))
            {
                var open = call.Index + call.Length - 1;
                var args = SourceFiles.Balanced(text, open);
                var body = SourceFiles.StripStringText(SourceFiles.RemoveGameThreadCallbacks(args));
                // the first arguments of TryQueuePenaltyWork are (caller, command, operation, ...): the caller is passed to
                // the game-thread method that captures it, not used by the job
                if (call.Groups[1].Value == "TryQueuePenaltyWork")
                {
                    var lambda = body.IndexOf("=>", StringComparison.Ordinal);
                    if (lambda < 0) continue;
                    body = body[lambda..];
                }

                foreach (Match name in ControllerNames.Matches(body))
                {
                    var line = text.Take(call.Index).Count(ch => ch == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{line} uses '{name.Value}' inside a background job: ...{body.Substring(Math.Max(0, name.Index - 40), Math.Min(80, body.Length - Math.Max(0, name.Index - 40)))}...");
                }
            }
        }

        // Initialization.cs: the CCSPlayerController overload of TryQueuePenaltyWork forwards its own parameter (game thread)
        violations.RemoveAll(v => v.StartsWith("Initialization.cs") && v.Contains("'caller'"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}

/// <summary>Helpers to inspect the plugin's own source for structural rules (no compiler needed).</summary>
internal static class SourceFiles
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CS2-SimpleAdmin.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    public static IEnumerable<string> Production() =>
        new[] { "CS2-SimpleAdmin", "Modules" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    public static string StripComments(string text) =>
        Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", m => new string(' ', m.Length), RegexOptions.Singleline);

    /// <summary>The text between the parenthesis at <paramref name="open"/> and its matching close.</summary>
    public static string Balanced(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return text[(open + 1)..i];
        }

        return text[(open + 1)..];
    }

    /// <summary>Blanks the literal text of strings but keeps interpolation holes ({expr}), which are real code.</summary>
    public static string StripStringText(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        var inString = false;
        var holeDepth = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (!inString)
            {
                if (c == '"') { inString = true; sb.Append('"'); }
                else sb.Append(c);
                continue;
            }

            if (holeDepth == 0)
            {
                if (c == '\\') { i++; sb.Append("  "); }
                else if (c == '"') { inString = false; sb.Append('"'); }
                else if (c == '{' && i + 1 < code.Length && code[i + 1] == '{') { i++; sb.Append("  "); }
                else if (c == '{') { holeDepth = 1; sb.Append(' '); }
                else sb.Append(c == '\n' ? '\n' : ' ');
            }
            else
            {
                if (c == '{') holeDepth++;
                else if (c == '}') holeDepth--;
                sb.Append(holeDepth == 0 && c == '}' ? ' ' : c);
            }
        }

        return sb.ToString();
    }

    public static string RemoveGameThreadCallbacks(string body)
    {
        var result = new System.Text.StringBuilder();
        var rest = 0;
        foreach (Match m in Regex.Matches(body, @"\b(OnGameThread|PostAsync|TryPost)\s*\("))
        {
            if (m.Index < rest) continue;
            result.Append(body, rest, m.Index - rest);
            var open = m.Index + m.Length - 1;
            var inner = Balanced(body, open);
            rest = open + 1 + inner.Length + 1;
        }

        result.Append(body, Math.Min(rest, body.Length), Math.Max(0, body.Length - rest));
        return result.ToString();
    }
}
