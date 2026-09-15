using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for pairing the current save with the save of the other player as a two-player save, which can only be
/// played while both players are online, or for making the current save a normal save again with "off".
/// </summary>
internal class CoopSaveCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The two-player saves that the command pairs and unpairs.
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
        "Pair your current save with your teammate's as a two-player save, or use 'off' to make it a normal save again.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _coopSave.OnCommand(arguments);
    }
}
