using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for a player who waits in a boss room for their teammate and doesn't want to wait anymore. It opens the
/// doors of the room for them, while the boss keeps waiting for everyone.
/// </summary>
internal class GiveUpCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The boss room rules that know which room the local player waits in.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    public GiveUpCommand(BossRoomCoop bossRoomCoop) {
        _bossRoomCoop = bossRoomCoop;
    }

    /// <inheritdoc />
    public string Trigger => "/giveup";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Open the doors of the boss room you wait in for your teammate.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _bossRoomCoop.GiveUpWaiting();
    }
}
