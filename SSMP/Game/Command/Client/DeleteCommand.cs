using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for deleting one of the player's saves by the number of its slot, after it tells what the save is and the
/// same command is typed again, keeping a copy of it first.
/// </summary>
internal class DeleteCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The saves, which delete the save.
    /// </summary>
    private readonly CoopSave _coopSave;

    public DeleteCommand(CoopSave coopSave) {
        _coopSave = coopSave;
    }

    /// <inheritdoc />
    public string Trigger => "/delete";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        "Delete one of your saves: /delete and its number, 1 to 4 from the top of the save menu, then again to confirm.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _coopSave.DeleteSave(arguments);
    }
}
