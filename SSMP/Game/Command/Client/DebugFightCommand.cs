using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;

namespace SSMP.Game.Command.Client;

/// <summary>
/// TEMPORARY debug commands /0, /1 and /2, for trying two fights again in a copy of a save (see
/// BossRoomCoop.DebugCommands). To be taken out again once they are tried.
/// </summary>
internal class DebugFightCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The boss rooms, which carry out the command in every player's game.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    /// <summary>
    /// The number of the command, 0 to 2.
    /// </summary>
    private readonly int _command;

    public DebugFightCommand(BossRoomCoop bossRoomCoop, int command) {
        _bossRoomCoop = bossRoomCoop;
        _command = command;
    }

    /// <inheritdoc />
    public string Trigger => $"/{_command}";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => _command switch {
        0 => "Temporary debug: set the fight at the top of the lift back to before the first ride up, in every " +
             "player's save.",
        1 => "Temporary debug: take every player to the bottom of the lift.",
        _ => "Temporary debug: take every player to the challenge of the other fight, whose win then leads back there."
    };

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _bossRoomCoop.RunDebugCommand(_command);
    }
}
