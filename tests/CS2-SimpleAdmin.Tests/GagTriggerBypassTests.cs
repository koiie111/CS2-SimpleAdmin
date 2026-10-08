using System.Reflection;
using System.Reflection.Emit;
using CounterStrikeSharp.API.Core;
using CS2_SimpleAdmin.Infrastructure;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdminApi;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// The decision of the global command listener for chat lines of a gagged / silenced / unverified player
/// (<see cref="ChatGate"/>) against the <b>real command registry</b>: the keys, callbacks and default aliases of
/// <see cref="CommandCatalog"/> registered through the same code as production, plus renamed aliases and configured names.
/// "Published" means: the only ways text reaches other players are Pass (the engine / the callback handles the line) and the
/// commands we run ourselves; a restricted player must never get either with free text.
/// </summary>
[Collection("OwnCommands")]
public class GagTriggerBypassTests : IDisposable
{
    private static readonly string[] Triggers = ["!", "/"];
    private static readonly string[] Configured = ["rank", "css_top"]; // commands of other plugins the operator allowed

    public GagTriggerBypassTests() => Registry.Use();

    public void Dispose() => OwnCommands.Reset();

    internal static class Registry
    {
        /// <summary>Registers like production: the default aliases plus optional replacements per key.</summary>
        public static void Use(IReadOnlyDictionary<string, string[]>? overrides = null)
        {
            var configured = CommandCatalog.DefaultAliases.ToDictionary(kv => kv.Key, kv => (string[]?)kv.Value);
            if (overrides != null)
                foreach (var (k, v) in overrides) configured[k] = v;
            var registered = new List<(string, string)>();
            foreach (var (key, aliases) in CommandCatalog.ResolveAliases(configured))
                foreach (var alias in aliases) registered.Add((key, alias));
            OwnCommands.Replace(registered);
        }
    }

    private static ChatDecision Decide(string command, string text, ChatRestriction r, string[]? triggers = null) =>
        ChatGate.Decide(command, text, r, triggers ?? Triggers, Configured);

    private static ChatDecision Gagged(string command, string text, string[]? triggers = null) =>
        Decide(command, text, ChatRestriction.Gagged, triggers);

    private static ChatDecision Free(string command, string text) => Decide(command, text, ChatRestriction.None);

    // ---------------------------------------------------------------- plain text

    [Theory]
    [InlineData("say", "hello")]
    [InlineData("say_team", "hello team")]
    [InlineData("SAY", "shouting")]
    [InlineData("say", "@admin text")]
    [InlineData("say_team", "@admin text")]
    [InlineData("say", " !leading space")]
    [InlineData("say", "\"!quoted")]
    public void PlainTextOfAGaggedPlayerIsDroppedWithANotice(string command, string text) =>
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged(command, text).Verdict);

    [Theory]
    [InlineData("say", "hello")]
    [InlineData("say_team", "hello")]
    [InlineData("css_say", "hello")]
    [InlineData("css_vote", "question yes no")]
    public void AnUnverifiedConnectionPublishesNothingEither(string command, string text) =>
        Assert.Equal(ChatVerdict.BlockUnverified, Decide(command, text, ChatRestriction.Unverified).Verdict);

    [Theory]
    [InlineData("!привет")]
    [InlineData("!потому что у меня мут")]
    [InlineData("!vip привет всем")]
    [InlineData("!hello")]
    [InlineData("!hello world")]
    [InlineData("/текст")]
    [InlineData("/hello")]
    [InlineData("!!x")]
    [InlineData("//x")]
    [InlineData("! x")]
    [InlineData("!")]
    [InlineData("/ ")]
    [InlineData("!ez; quit")]
    [InlineData("!admin; say hi")]
    [InlineData("!admin\nsay hi")]
    [InlineData("!admin\tsay hi")]
    [InlineData("!a\"b")]
    [InlineData("!admin \"quoted\"")]
    [InlineData("!admin back\\slash")]
    [InlineData("!this_name_is_far_too_long_to_be_a_command")]
    [InlineData("!say hello")]
    [InlineData("!css_say hello")]
    [InlineData("!psay all hello")]
    [InlineData("!csay hello")]
    [InlineData("!hsay hello")]
    [InlineData("!cssay hello")]
    [InlineData("!asay hello")]
    [InlineData("!unknowncommand")]
    // The reported hole: other own commands that publish text or change things were allowed just for not containing "say"
    [InlineData("!vote hello yes no")]
    [InlineData("!css_vote hello yes no")]
    [InlineData("!ban 12 0 text")]
    [InlineData("!kick 12 some text")]
    [InlineData("!rename 12 SomeName")]
    [InlineData("!rcon quit")]
    [InlineData("!cvar sv_cheats 1")]
    [InlineData("!map de_dust2")]
    public void TriggerLinesThatAreNotAVerifiedSafeCommandAreDroppedWithoutRunningAnything(string text)
    {
        var d = Gagged("say", text);
        Assert.Equal(ChatVerdict.Block, d.Verdict);
        Assert.Null(d.Command);
    }

    // ---------------------------------------------------------------- the positive list, with the real keys

    [Fact]
    public void EveryDefaultAliasOfAVerifiedSafeKeyRunsOnceAndNothingElseDoes()
    {
        var safeAliases = CommandCatalog.DefaultAliases.Where(kv => OwnCommands.SafeKeys.Contains(kv.Key))
            .SelectMany(kv => kv.Value).ToList();
        Assert.Contains("css_penalties", safeAliases);
        Assert.Contains("css_mypenalties", safeAliases);  // aliases of a safe key are safe
        Assert.Contains("css_comms", safeAliases);

        foreach (var alias in safeAliases)
        {
            var d = Gagged("say", "!" + alias[4..]);
            Assert.Equal(ChatVerdict.RunCommand, d.Verdict);
            Assert.Equal(alias, d.Command);
        }

        // and every other own alias is dropped: nothing runs
        foreach (var (key, aliases) in CommandCatalog.DefaultAliases)
        {
            if (OwnCommands.SafeKeys.Contains(key) || !CommandCatalog.KeysAndMethods.Any(m => m.Key == key)) continue;
            foreach (var alias in aliases)
            {
                var d = Gagged("say", "!" + alias[4..] + " 1");
                Assert.True(d.Verdict == ChatVerdict.Block && d.Command == null, $"{key}/{alias}: {d.Verdict} {d.Command}");
            }
        }
    }

    [Fact]
    public void ArgumentsAreKeptAndPrefixesNeverDoubled()
    {
        Assert.Equal("css_admin", Gagged("say", "!admin").Command);
        Assert.Equal("css_penalties", Gagged("say", "/penalties").Command);
        Assert.Equal("css_history 76561198000000001 2", Gagged("say_team", "!history 76561198000000001 2").Command);
        Assert.Equal("css_admin", Gagged("say", "!css_admin").Command);
        Assert.Equal("css_admin", Gagged("say", "!  ADMIN  ").Command);
        Assert.Equal(ChatVerdict.RunCommand, Decide("say", "!admin", ChatRestriction.Unverified).Verdict);
    }

    [Fact]
    public void EveryKeyIsClassifiedAndTheSafeListContainsOnlyRealNonPublishingKeys()
    {
        var keys = CommandCatalog.KeysAndMethods.Select(k => k.Key).ToHashSet();
        Assert.All(OwnCommands.SafeKeys, k => Assert.Contains(k, keys));
        Assert.All(OwnCommands.PublishingKeys, k => Assert.Contains(k, keys));
        Assert.Empty(OwnCommands.SafeKeys.Intersect(OwnCommands.PublishingKeys));
        foreach (var key in keys)
            Assert.Equal(
                OwnCommands.PublishingKeys.Contains(key) ? OwnCommandClass.PublishesText
                : OwnCommands.SafeKeys.Contains(key) ? OwnCommandClass.Safe : OwnCommandClass.Other,
                OwnCommands.ClassOfKey(key));
        // each default alias of the registry is classified by its key
        foreach (var (key, aliases) in CommandCatalog.DefaultAliases.Where(kv => keys.Contains(kv.Key)))
            foreach (var alias in aliases)
                Assert.Equal(OwnCommands.ClassOfKey(key), OwnCommands.Classify(alias));
    }

    // ---------------------------------------------------------------- console typed broadcast commands, any name

    [Theory]
    [InlineData("css_say", "hello")]
    [InlineData("css_psay", "target hello")]
    [InlineData("css_csay", "hello")]
    [InlineData("css_hsay", "hello")]
    [InlineData("css_cssay", "hello")]
    [InlineData("css_asay", "hello")]
    [InlineData("css_vote", "hello")]
    [InlineData("css_say", "!admin")]
    [InlineData("sm_say", "hello")]
    public void BroadcastCommandsTypedInTheConsoleByAGaggedPlayerAreDropped(string command, string text)
    {
        var d = Gagged(command, text);
        Assert.Equal(ChatVerdict.BlockAndNotify, d.Verdict);
        Assert.Null(d.Command);
    }

    [Theory]
    [InlineData("css_psay", "css_tell", "#12 hello")]       // the reported rename
    [InlineData("css_psay", "tell", "#12 hello")]
    [InlineData("css_vote", "css_poll", "question yes no")]
    [InlineData("css_say", "css_announce", "hello")]
    [InlineData("css_csay", "css_banner", "hello")]
    [InlineData("css_hsay", "css_alert", "hello")]
    [InlineData("css_cssay", "css_color", "hello")]
    [InlineData("css_asay", "css_staff", "hello")]
    public void ARenamedBroadcastAliasKeepsTheClassOfItsKey(string key, string alias, string text)
    {
        Registry.Use(new Dictionary<string, string[]> { [key] = [alias] });
        Assert.Equal(OwnCommandClass.PublishesText, OwnCommands.Classify(alias));
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged(alias, text).Verdict);
        Assert.Equal(ChatVerdict.BlockUnverified, Decide(alias, text, ChatRestriction.Unverified).Verdict);
        Assert.Equal(ChatVerdict.Pass, Free(alias, text).Verdict);       // not restricted: the command's own permission applies
        var viaTrigger = Gagged("say", "!" + alias.Replace("css_", "") + " " + text);
        Assert.Equal(ChatVerdict.Block, viaTrigger.Verdict);             // and the trigger path does not run it either
    }

    [Fact]
    public void AConfigurationCannotAllowADangerousOwnCommandAndTheOldNameHeuristicIsNotTheDefence()
    {
        Registry.Use(new Dictionary<string, string[]> { ["css_psay"] = ["css_tell"], ["css_vote"] = ["css_poll"] });
        // the operator lists the renamed aliases (or the originals) as allowed: still dropped
        string[] hostile = ["tell", "css_tell", "poll", "css_poll", "vote", "css_vote", "psay", "ban", "css_ban"];
        foreach (var line in new[] { "!tell 12 hello", "!poll hello yes no", "!ban 12 0 x" })
            Assert.Equal(ChatVerdict.Block, ChatGate.Decide("say", line, ChatRestriction.Gagged, Triggers, hostile).Verdict);

        // under their default names too (the reported "!vote hello yes no" with @css/generic)
        Registry.Use();
        foreach (var line in new[] { "!vote hello yes no", "!psay 12 hello", "!ban 12 0 x" })
            Assert.Equal(ChatVerdict.Block, ChatGate.Decide("say", line, ChatRestriction.Gagged, Triggers, hostile).Verdict);
        Registry.Use(new Dictionary<string, string[]> { ["css_psay"] = ["css_tell"], ["css_vote"] = ["css_poll"] });

        // a safe own command renamed to something with "say" in it: still safe (decided by its key, not by its spelling)
        Registry.Use(new Dictionary<string, string[]> { ["css_penalties"] = ["css_saymypenalties"] });
        Assert.Equal("css_saymypenalties", Gagged("say", "!saymypenalties").Command);
        Assert.Equal(ChatVerdict.Pass, Gagged("css_saymypenalties", "").Verdict);
    }

    [Fact]
    public void CommandsOfOtherPluginsFollowTheConfiguredListAndTheNameHeuristic()
    {
        Assert.Equal("css_rank", Gagged("say", "!RANK").Command);
        Assert.Equal("css_top", Gagged("say", "!top").Command);               // "css_top" in the list: prefix is ignored
        Assert.Equal(ChatVerdict.Block, Gagged("say", "!stats").Verdict);     // not listed
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged("sm_psay", "x y").Verdict);
        Assert.Equal(ChatVerdict.Block, ChatGate.Decide("say", "!essay", ChatRestriction.Gagged, Triggers, ["essay"]).Verdict);
    }

    [Fact]
    public void ConfiguredAndRepeatedTriggersFollowTheSameRules()
    {
        string[] custom = [".", "!!"];
        Assert.Equal(ChatVerdict.Block, Gagged("say", ".hello", custom).Verdict);
        Assert.Equal("css_admin", Gagged("say", ".admin", custom).Command);
        Assert.Equal("css_admin", Gagged("say", "!!admin", custom).Command);
        Assert.Equal(ChatVerdict.Block, Gagged("say", "!!!admin", custom).Verdict);
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged("say", "!admin", custom).Verdict);
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged("say", "!admin", []).Verdict);
    }

    [Fact]
    public void WithoutAChatRestrictionEverythingKeepsItsNormalPathAndAMuteAloneIsNotOne()
    {
        Assert.Equal(ChatVerdict.Pass, Free("say", "hello").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("say_team", "hello").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("say", "!hello").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("say", "@admin text").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("css_say", "hello").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("css_vote", "q yes no").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("jointeam", "2").Verdict);
        Assert.Equal(ChatVerdict.Block, Free("say", "").Verdict);
    }

    // ---------------------------------------------------------------- the restriction itself (MUTE alone never blocks text)

    [Fact]
    public void OnlyGagSilenceOrAnUnverifiedConnectionRestrictText()
    {
        using var world = new TestWorld();
        TestConfig.Use(_ => { });
        const int slot = 4;
        const ulong steam = 76561198000000004;
        var session = Runtime.Sessions.BeginOrGet(slot, steam, 44, "p", null, out _);

        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(slot, steam, 44, out _));   // not loaded yet
        Assert.True(session.TryBeginLoad(1) > 0);
        Assert.True(session.CompleteLoad(1));
        Assert.Equal(ChatRestriction.None, ChatGuard.Evaluate(slot, steam, 44, out _));

        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Mute, Time.ActualDateTime().AddMinutes(5), 5, 0);
        Assert.Equal(ChatRestriction.None, ChatGuard.Evaluate(slot, steam, 44, out _));         // a MUTE is voice only
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Gag, Time.ActualDateTime().AddMinutes(5), 5, 0);
        Assert.Equal(ChatRestriction.Gagged, ChatGuard.Evaluate(slot, steam, 44, out var ends));
        Assert.NotNull(ends);
        PlayerPenaltyManager.RemovePenaltiesByType(slot, PenaltyType.Gag);
        PlayerPenaltyManager.AddPenalty(slot, PenaltyType.Silence, Time.ActualDateTime().AddMinutes(5), 5, 0);
        Assert.Equal(ChatRestriction.Gagged, ChatGuard.Evaluate(slot, steam, 44, out _));

        // a different connection in the same slot (slot reuse) is not vouched for by the old session, and an old gag of the
        // slot still applies to the slot's occupant until it is replaced by the new connection's own load
        PlayerPenaltyManager.RemoveAllPenalties(slot);
        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(slot, steam + 1, 45, out _));
        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(slot, steam, 99, out _));
        Runtime.Sessions.End(slot);
        Assert.Equal(ChatRestriction.Unverified, ChatGuard.Evaluate(slot, steam, 44, out _));    // no session at all
    }

    // ---------------------------------------------------------------- callbacks: the guard does not depend on the say path

    private static bool CallsGuard(MethodInfo method)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var guard = typeof(ChatGuard).GetMethod(nameof(ChatGuard.MayPublish), BindingFlags.Static | BindingFlags.NonPublic)!.MetadataToken;
        for (var i = 0; i < il.Length - 4; i++)
            if ((il[i] == OpCodes.Call.Value || il[i] == OpCodes.Callvirt.Value) && BitConverter.ToInt32(il, i + 1) == guard)
                return true;
        return false;
    }

    [Fact]
    public void EveryCallbackThatPublishesCallerTextRunsTheGuardAndNoOtherCallbackIsMissedByTheList()
    {
        var methods = CommandCatalog.KeysAndMethods.ToDictionary(k => k.Key, k =>
            typeof(CS2_SimpleAdmin).GetMethod(k.MethodName, BindingFlags.Public | BindingFlags.Instance)!);
        foreach (var key in OwnCommands.PublishingKeys)
            Assert.True(CallsGuard(methods[key]), $"{key} ({methods[key].Name}) must call ChatGuard.MayPublish");

        // The guard is meant for these keys only: the safe ones never need it
        foreach (var key in OwnCommands.SafeKeys)
            Assert.False(CallsGuard(methods[key]), key);
    }

    [Fact]
    public void TheGuardStopsGaggedUnverifiedAndLetsTheConsoleAndFreePlayersThrough()
    {
        var before = (ChatGuard.CallerRestriction, ChatGuard.NotifyCaller);
        try
        {
            var told = new List<ChatRestriction>();
            ChatGuard.NotifyCaller = (_, r) => told.Add(r);
            var player = (CCSPlayerController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CCSPlayerController));

            ChatGuard.CallerRestriction = _ => ChatRestriction.None;
            Assert.True(ChatGuard.MayPublish(player));
            ChatGuard.CallerRestriction = _ => ChatRestriction.Gagged;
            Assert.False(ChatGuard.MayPublish(player));
            ChatGuard.CallerRestriction = _ => ChatRestriction.Unverified;
            Assert.False(ChatGuard.MayPublish(player));
            Assert.True(ChatGuard.MayPublish(null));                      // the server console: the old rules
            Assert.Equal([ChatRestriction.Gagged, ChatRestriction.Unverified], told);
        }
        finally
        {
            (ChatGuard.CallerRestriction, ChatGuard.NotifyCaller) = before;
        }
    }

    [Fact]
    public void ToSilentCommandRequiresAnAllowedName()
    {
        Assert.Null(ChatTriggers.ToSilentCommand("!vip", Triggers, _ => false));
        Assert.Equal("css_vip", ChatTriggers.ToSilentCommand("!vip", Triggers, n => n == "vip"));
        Assert.Null(ChatTriggers.ToSilentCommand("!", Triggers, _ => true));
        Assert.Null(ChatTriggers.ToSilentCommand("plain", Triggers, _ => true));
    }
}

[CollectionDefinition("OwnCommands", DisableParallelization = true)]
public class OwnCommandsCollection;
