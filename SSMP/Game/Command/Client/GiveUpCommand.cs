using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for a player who is shut in and doesn't want to be anymore: a boss room whose boss waits for their
/// teammate, or an arena whose battle stopped going anywhere. It opens the doors for them alone.
/// </summary>
internal class GiveUpCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The boss room rules that know which room the local player waits in.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    /// <summary>
    /// The arena rules that know which battle the local player is shut into.
    /// </summary>
    private readonly ArenaCoop _arenaCoop;

    public GiveUpCommand(BossRoomCoop bossRoomCoop, ArenaCoop arenaCoop) {
        _bossRoomCoop = bossRoomCoop;
        _arenaCoop = arenaCoop;
    }

    /// <inheritdoc />
    public string Trigger => "/giveup";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Open the doors of the boss room or arena you are shut into.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        // The arena first, and only one message: a player shut into a battle is not also waiting on a boss
        if (_arenaCoop.GiveUpBattle()) {
            return;
        }

        _bossRoomCoop.GiveUpWaiting();
    }
}
