using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;

namespace SSMP.Game.Command.Client;

/// <summary>
/// TEMPORARY debug command (/1, /2 and /3) that takes every player to the place where one of the late dreams starts,
/// for trying them without playing up to them. To be taken out again once the dreams are tried.
/// </summary>
internal class DebugDreamCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The boss rooms, which send the players.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    /// <summary>
    /// The place that the command sends the players to, 1 to 3.
    /// </summary>
    private readonly int _place;

    public DebugDreamCommand(BossRoomCoop bossRoomCoop, int place) {
        _bossRoomCoop = bossRoomCoop;
        _place = place;
    }

    /// <inheritdoc />
    public string Trigger => $"/{_place}";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        $"Temporary debug: take every player to dream entrance {_place}, in a save in the third act (see /0).";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _bossRoomCoop.StartDebugTravel(_place);
    }
}
