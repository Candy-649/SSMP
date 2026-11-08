using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for copying a save to the next empty save slot, like to keep a save to go back to: the save that the player
/// is in, or the save of a slot by its number.
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
    public string Description =>
        "Copy a save to the next empty save slot: /copy in a save saves it and copies it; /copy and a number, 1 to 4 " +
        "from the top of the save menu, copies that save, in the title menu too.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _coopSave.CopySave(arguments);
    }
}
