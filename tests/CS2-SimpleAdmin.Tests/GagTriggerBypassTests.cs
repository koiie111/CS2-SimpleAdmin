using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

/// <summary>
/// The decision of the global command listener for chat lines of a gagged / silenced player (<see cref="ChatGate"/>),
/// not just the string conversion: what is dropped, what is dropped with a notice, which single command may run.
/// "Published" below means: the only ways text reaches other players are Pass (the engine handles the line) and the
/// commands we run ourselves; a gagged player must never get either with free text.
/// </summary>
public class GagTriggerBypassTests
{
    private static readonly string[] Triggers = ["!", "/"];

    // The allow-list a typical server has: this plugin's information commands plus one configured plugin command
    private static bool Allowed(string name) => name is "admin" or "penalties" or "history" or "rank";

    private static ChatDecision Gagged(string command, string text, string[]? triggers = null) =>
        ChatGate.Decide(command, text, true, triggers ?? Triggers, Allowed);

    private static ChatDecision Free(string command, string text) =>
        ChatGate.Decide(command, text, false, Triggers, Allowed);

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
    public void TriggerLinesThatAreNotAnAllowedCommandAreDroppedWithoutRunningAnything(string text)
    {
        var d = Gagged("say", text);
        Assert.Equal(ChatVerdict.Block, d.Verdict);
        Assert.Null(d.Command);
    }

    [Fact]
    public void AnAllowedCommandIsRunOnceAsConsoleCommandAndTheLineIsNotPassedOn()
    {
        var plain = Gagged("say", "!admin");
        Assert.Equal(ChatVerdict.RunCommand, plain.Verdict);
        Assert.Equal("css_admin", plain.Command);

        Assert.Equal("css_penalties", Gagged("say", "/penalties").Command);
        Assert.Equal("css_history 76561198000000001 2", Gagged("say_team", "!history 76561198000000001 2").Command);
        Assert.Equal("css_admin", Gagged("say", "!css_admin").Command);   // prefix is accepted, never doubled
        Assert.Equal("css_rank", Gagged("say", "!RANK").Command);          // case is folded
        Assert.Equal("css_admin", Gagged("say", "!  admin  ").Command);    // surrounding spaces are not an argument
    }

    [Theory]
    [InlineData("css_say", "hello")]
    [InlineData("css_psay", "target hello")]
    [InlineData("css_csay", "hello")]
    [InlineData("css_hsay", "hello")]
    [InlineData("css_cssay", "hello")]
    [InlineData("css_asay", "hello")]
    [InlineData("css_say", "!admin")]
    [InlineData("sm_say", "hello")]
    public void BroadcastCommandsTypedInTheConsoleByAGaggedPlayerAreDropped(string command, string text)
    {
        var d = Gagged(command, text);
        Assert.Equal(ChatVerdict.BlockAndNotify, d.Verdict);
        Assert.Null(d.Command);
    }

    [Fact]
    public void ConfiguredAndRepeatedTriggersFollowTheSameRules()
    {
        string[] custom = [".", "!!"];
        Assert.Equal(ChatVerdict.Block, Gagged("say", ".hello", custom).Verdict);
        Assert.Equal("css_admin", Gagged("say", ".admin", custom).Command);
        Assert.Equal("css_admin", Gagged("say", "!!admin", custom).Command);
        Assert.Equal(ChatVerdict.Block, Gagged("say", "!!!admin", custom).Verdict); // a third prefix char makes the name invalid
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged("say", "!admin", custom).Verdict); // "!" is no trigger here: plain text

        // No triggers configured at all: every line is plain text
        Assert.Equal(ChatVerdict.BlockAndNotify, Gagged("say", "!admin", []).Verdict);
    }

    [Fact]
    public void WithoutAChatPenaltyEverythingKeepsItsNormalPath()
    {
        Assert.Equal(ChatVerdict.Pass, Free("say", "hello").Verdict);       // also a player who only has a MUTE (voice)
        Assert.Equal(ChatVerdict.Pass, Free("say_team", "hello").Verdict);
        Assert.Equal(ChatVerdict.Pass, Free("say", "!hello").Verdict);      // normal chat/commands untouched
        Assert.Equal(ChatVerdict.Pass, Free("say", "@admin text").Verdict); // admin chat handled by the caller
        Assert.Equal(ChatVerdict.Pass, Free("css_say", "hello").Verdict);   // permission is the command's own business
        Assert.Equal(ChatVerdict.Pass, Free("jointeam", "2").Verdict);      // not a chat command at all
        Assert.Equal(ChatVerdict.Block, Free("say", "").Verdict);           // empty lines are dropped for everybody
    }

    [Fact]
    public void ToSilentCommandRequiresAnAllowedName()
    {
        Assert.Null(ChatTriggers.ToSilentCommand("!vip", Triggers, _ => false));
        Assert.Equal("css_vip", ChatTriggers.ToSilentCommand("!vip", Triggers, n => n == "vip"));
        Assert.Null(ChatTriggers.ToSilentCommand("!", Triggers, _ => true));
        Assert.Null(ChatTriggers.ToSilentCommand("plain", Triggers, _ => true));
    }

    [Fact]
    public void TheAllowListIsOurNonBroadcastCommandsPlusTheConfiguredNames()
    {
        GagChatCommands.ResetOwn();
        GagChatCommands.NoteRegistered("css_admin", "css_admin");
        GagChatCommands.NoteRegistered("css_penalties", "css_mypenalties"); // an alias from Commands.json
        GagChatCommands.NoteRegistered("css_say", "css_say");
        GagChatCommands.NoteRegistered("css_psay", "css_tell");             // a renamed broadcast command
        GagChatCommands.NoteRegistered("css_cssay", "css_cssay");

        string[] configured = ["rank", "css_top", "essay", "css_say"];
        Assert.True(GagChatCommands.IsAllowed("admin", configured));
        Assert.True(GagChatCommands.IsAllowed("mypenalties", null));
        Assert.True(GagChatCommands.IsAllowed("ADMIN", null));
        Assert.True(GagChatCommands.IsAllowed("rank", configured));
        Assert.True(GagChatCommands.IsAllowed("top", configured));
        Assert.False(GagChatCommands.IsAllowed("rank", null));
        Assert.False(GagChatCommands.IsAllowed("say", configured));     // contains "say": a broadcast, never allowed
        Assert.False(GagChatCommands.IsAllowed("essay", configured));
        Assert.False(GagChatCommands.IsAllowed("tell", configured));    // renamed psay is not in the list
        Assert.False(GagChatCommands.IsAllowed("cssay", configured));
        Assert.False(GagChatCommands.IsAllowed("", configured));
        GagChatCommands.ResetOwn();
    }
}
