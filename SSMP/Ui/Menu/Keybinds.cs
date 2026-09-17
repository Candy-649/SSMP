using InControl;

namespace SSMP.Ui.Menu;

/// <summary>
/// Class that stores keybinds for HKMP, to allow them to be (de)serialized to the settings file.
/// </summary>
internal class Keybinds : PlayerActionSet {
    /// <summary>
    /// The gamepad button that agrees to a two-player save and that gives up on waiting, so that neither of them
    /// needs a keyboard once the game is running.
    ///
    /// Pressing down the left stick is the only button the game itself leaves alone: it binds both triggers, both
    /// bumpers, all four face buttons, pressing down the right stick, and the two buttons beside them, and anything
    /// past the fourth face button does not exist on a real gamepad.
    ///
    /// The same button serves both because the two can never be waiting at once - a player is offered a two-player
    /// save only before they have one, and can only give up on waiting once they do - so whichever of the two is
    /// being offered on screen is the one it answers. Opening the chat deliberately gets no button: there is no way
    /// to type on a gamepad, so it would only open a box that cannot be filled in.
    /// </summary>
    private const InputControlType CoopButton = InputControlType.LeftStickButton;

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
        CoopPair.AddDefaultBinding(new DeviceBindingSource(CoopButton));

        CoopLeave = CreatePlayerAction("CoopLeave");
        CoopLeave.AddDefaultBinding(Key.K);
        CoopLeave.AddDefaultBinding(new DeviceBindingSource(CoopButton));
    }
}
