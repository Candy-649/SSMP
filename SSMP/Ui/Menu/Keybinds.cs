using InControl;

namespace SSMP.Ui.Menu;

/// <summary>
/// Class that stores keybinds for HKMP, to allow them to be (de)serialized to the settings file.
/// </summary>
internal class Keybinds : PlayerActionSet {
    /// <summary>
    /// Keybind to open the chat.
    /// </summary>
    public PlayerAction OpenChat { get; }

    /// <summary>
    /// Keybind to agree to a two-player save with the other player, which both of them press.
    /// </summary>
    public PlayerAction CoopPair { get; }

    public Keybinds() {
        OpenChat = CreatePlayerAction("OpenChat");
        OpenChat.AddDefaultBinding(Key.Y);

        CoopPair = CreatePlayerAction("CoopPair");
        CoopPair.AddDefaultBinding(Key.J);
    }
}
