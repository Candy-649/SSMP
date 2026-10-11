using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command that says whether the current save is a co-op save and who plays it this time, or makes it a normal save
/// again with "off".
/// </summary>
internal class CoopSaveCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The co-op saves that the command tells about.
    /// </summary>
    private readonly CoopSave _coopSave;

    public CoopSaveCommand(CoopSave coopSave) {
        _coopSave = coopSave;
    }

    /// <inheritdoc />
    public string Trigger => "/coopsave";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        "Say whether your current save is a co-op save and who plays it this time, or use 'off' to make it a normal " +
        "save again.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _coopSave.OnCommand(arguments);
    }
}
