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

    /// <summary>
    /// Keybind to leave a two-player save that is waiting for a partner who is not coming.
    ///
    /// A key rather than only the chat command, because a held player has been unable to open the chat at all - the
    /// one way out was shut at the one moment it was needed.
    /// </summary>
    public PlayerAction CoopLeave { get; }

    public Keybinds() {
        OpenChat = CreatePlayerAction("OpenChat");
        OpenChat.AddDefaultBinding(Key.Y);

        CoopPair = CreatePlayerAction("CoopPair");
        // Not J, which the game itself binds by default to opening a page of the inventory: both action sets are
        // listening, so one press did both. L is bound to nothing by the game, and sits next to the key that leaves
        // a two-player save, which is the other half of the same choice.
        CoopPair.AddDefaultBinding(Key.L);

        CoopLeave = CreatePlayerAction("CoopLeave");
        CoopLeave.AddDefaultBinding(Key.K);
    }
}
