using InControl;

namespace SSMP.Ui.Menu;

/// <summary>
/// Class that stores keybinds for SSMP, to allow them to be (de)serialized to the settings file.
/// </summary>
internal class Keybinds : PlayerActionSet {
    /// <summary>
    /// The gamepad button that gives up on waiting to be pulled back up and goes to the bench, so that no keyboard is
    /// needed once the game is running.
    ///
    /// Pressing down the left stick is the only button the game itself leaves alone: it binds both triggers, both
    /// bumpers, all four face buttons, pressing down the right stick, and the two buttons beside them, and anything
    /// past the fourth face button does not exist on a real gamepad. Opening the chat deliberately gets no button:
    /// there is no way to type on a gamepad, so it would only open a box that cannot be filled in.
    ///
    /// The key that paired saves shared it until 0.4.91, when saves stopped being paired. A settings file that still
    /// names that key is read past it (PlayerActionSetConverter).
    /// </summary>
    private const InputControlType CoopButton = InputControlType.LeftStickButton;

    /// <summary>
    /// Keybind to open the chat.
    /// </summary>
    public PlayerAction OpenChat { get; }

    /// <summary>
    /// Keybind that gives up waiting in a cocoon to be pulled back up (CoopSave.Rescue).
    ///
    /// A key rather than a chat command, because a held player has been unable to open the chat at all, and a player
    /// lying in a cocoon can't pause either: the one way out would be shut at the one moment it is needed.
    /// </summary>
    public PlayerAction CoopLeave { get; }

    public Keybinds() {
        OpenChat = CreatePlayerAction("OpenChat");
        OpenChat.AddDefaultBinding(Key.Y);

        CoopLeave = CreatePlayerAction("CoopLeave");
        CoopLeave.AddDefaultBinding(Key.K);
        CoopLeave.AddDefaultBinding(new DeviceBindingSource(CoopButton));
    }
}
