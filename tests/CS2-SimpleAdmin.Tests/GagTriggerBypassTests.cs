using CS2_SimpleAdmin.Infrastructure;

namespace CS2_SimpleAdmin.Tests;

public class GagTriggerBypassTests
{
    private static readonly string[] Triggers = { "!", "/" };

    [Theory]
    [InlineData("!привет")]
    [InlineData("!потому что у меня мут")]
    [InlineData("!vip привет всем")]
    [InlineData("!ez; quit")]
    [InlineData("!a\"b")]
    [InlineData("!")]
    [InlineData("/ ")]
    [InlineData("!this_name_is_far_too_long_to_be_a_command")]
    [InlineData("обычный текст")]
    public void FreeTextWithTriggerIsNotExecutedAsCommand(string message) =>
        Assert.Null(ChatTriggers.ToSilentCommand(message, Triggers));

    [Theory]
    [InlineData("!vip", "css_vip")]
    [InlineData("/rank", "css_rank")]
    [InlineData("!ban 12 5 test", "css_ban 12 5 test")]
    public void CommandShapedMessageBecomesConsoleCommand(string message, string expected) =>
        Assert.Equal(expected, ChatTriggers.ToSilentCommand(message, Triggers));
}
