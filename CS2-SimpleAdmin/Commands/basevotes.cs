using CounterStrikeSharp.API.Core;
using CS2_SimpleAdmin.Infrastructure;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;

namespace CS2_SimpleAdmin;

public partial class CS2_SimpleAdmin
{
    /// <summary>
    /// Handles the vote command, creates voting menu for players, and collects answers.
    /// Displays results after timeout and resets voting state.
    /// </summary>
    /// <param name="caller">The player/admin who initiated the vote, or null for console.</param>
    /// <param name="command">Command object containing question and options.</param>
    [RequiresPermissions("@css/generic")]
    [CommandHelper(minArgs: 2, usage: "<question> [... options ...]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnVoteCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount < 2 || _localizer == null)
            return;

        // The question is text of the caller delivered to everybody: not for gagged / silenced / unverified players
        if (!ChatGuard.MayPublish(caller))
            return;

        Helper.LogCommand(caller, command);

        VoteAnswers.Clear();

        var question = command.GetArg(1);
        var answersCount = command.ArgCount;

        if (caller == null || !SilentPlayers.Contains(caller.Slot))
        {
            for (var i = 2; i <= answersCount - 1; i++)
            {
                VoteAnswers.Add(command.GetArg(i), 0);
            }

            var callerName = caller == null ? _localizer["sa_console"] : caller.PlayerName;
            // One pass over the players, one center message per recipient in the recipient's own language
            VoteDelivery.Deliver(Helper.GetValidPlayers(), new VoteDelivery.Sink<CCSPlayerController>
            {
                Localize = static (player, localizer, name, text) =>
                {
                    using (new WithTemporaryCulture(player.GetLanguage()))
                        return localizer["sa_admin_vote_message", name, text];
                },
                Center = static (player, text) => player.PrintToCenter(text),
                Chat = (player) => player.SendLocalizedMessage(_localizer, "sa_admin_vote_message", callerName, question),
                OpenMenu = player =>
                {
                    using (new WithTemporaryCulture(player.GetLanguage()))
                    {
                        IMenu? voteMenu = Helper.CreateMenu(_localizer["sa_admin_vote_menu_title", question]);
                        if (voteMenu == null) return;
                        for (var i = 2; i <= answersCount - 1; i++)
                            voteMenu.AddMenuOption(command.GetArg(i), Helper.HandleVotes);
                        voteMenu.PostSelectAction = PostSelectAction.Close;
                        voteMenu.Open(player);
                    }
                }
            }, _localizer, callerName, question);

            VoteInProgress = true;
        }

        if (VoteInProgress)
        {
            AddTimer(30, () =>
            {
                foreach (var player in Helper.GetValidPlayers())
                {
                    if (_localizer != null)
                        player.SendLocalizedMessage(_localizer,
                            "sa_admin_vote_message_results",
                            question);
                }

                foreach (var (key, value) in VoteAnswers)
                {
                    foreach (var player in Helper.GetValidPlayers())
                    {
                        if (_localizer != null)
                            player.SendLocalizedMessage(_localizer,
                                "sa_admin_vote_message_results_answer",
                                key,
                                value);
                    }
                }
                VoteAnswers.Clear();
                VoteInProgress = false;
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }
    }
}
/// <summary>
/// Delivery of a vote to the players: exactly one center message per recipient (the previous code printed to <i>all</i>
/// players once per recipient: N² native calls), each localized for its recipient, plus chat line and menu.
/// </summary>
internal static class VoteDelivery
{
    internal sealed class Sink<T>
    {
        public required Func<T, Microsoft.Extensions.Localization.IStringLocalizer, string, string, string> Localize { get; init; }
        public required Action<T, string> Center { get; init; }
        public required Action<T> Chat { get; init; }
        public required Action<T> OpenMenu { get; init; }
    }

    internal static void Deliver<T>(IReadOnlyList<T> players, Sink<T> sink, Microsoft.Extensions.Localization.IStringLocalizer localizer,
        string callerName, string question)
    {
        foreach (var player in players)
        {
            sink.Center(player, sink.Localize(player, localizer, callerName, question));
            sink.Chat(player);
            sink.OpenMenu(player);
        }
    }
}
