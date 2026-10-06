using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for a player who is shut into an arena whose battle stopped going anywhere: it opens the doors for them
/// alone. A boss room or a story scene that waits for the teammate can't be given up on: it waits until every player is
/// there.
/// </summary>
internal class GiveUpCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The arena rules that know which battle the local player is shut into.
    /// </summary>
    private readonly ArenaCoop _arenaCoop;

    /// <summary>
    /// The message for a player who isn't shut into an arena.
    /// </summary>
    private static string NothingToGiveUpMessage => Lang.Pick(
        "You aren't shut into an arena.",
        "你现在没有被关在遭遇战里。"
    );

    public GiveUpCommand(ArenaCoop arenaCoop) {
        _arenaCoop = arenaCoop;
    }

    /// <inheritdoc />
    public string Trigger => "/giveup";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Open the doors of the arena you are shut into.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (!_arenaCoop.GiveUpBattle()) {
            UiManager.InternalChatBox.AddMessage(NothingToGiveUpMessage);
        }
    }
}
