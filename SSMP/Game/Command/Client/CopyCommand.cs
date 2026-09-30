using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for copying the save that the player is in to the next empty save slot, like to keep a save to go back to.
/// </summary>
internal class CopyCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The saves, which copy the save.
    /// </summary>
    private readonly CoopSave _coopSave;

    public CopyCommand(CoopSave coopSave) {
        _coopSave = coopSave;
    }

    /// <inheritdoc />
    public string Trigger => "/copy";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Save the game and copy your save to the next empty save slot.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _coopSave.CopySave();
    }
}
