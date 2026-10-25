using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using InControl;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Ui.Chat;

/// <summary>
/// The keys of the game while the chat box is open. The keyboard types into the chat and presses no key of the game or
/// of this mod meanwhile, but for the one that closes the chat; a gamepad can type nothing, so it goes on playing. A key
/// or button that was held down as the chat opened, on either of them, stays held until the chat closes, though the
/// hand lets go of it to type (USER 10-25: "像手柄在长按的时候，按y打开聊天框可以自动延续长按", "当然键盘上的长按也是
/// 一样的"): a player playing the needolin goes on playing, one walking goes on walking. Pressing a held button of the
/// gamepad again, or the opposite way of a held direction on the gamepad, hands it back to the hand, as closing the chat
/// does for every key. A key of the keyboard can't be pressed again meanwhile, as every key typed is a letter of the
/// chat: holding the needle's charge and typing a word with that letter in it would let go of the charge (review).
///
/// The game reads each key of each of its actions every frame, a key of the keyboard and a button of a gamepad each
/// through a binding of its own (InControl's PlayerAction.UpdateBindings, BindingSource.GetValue), which is where this
/// steps in, so the actions themselves stay switched on.
/// </summary>
internal static class ChatHeldKeys {
    /// <summary>
    /// How far down a key, or a way of a stick, has to be, from 0 to 1, to count as held as the chat opens.
    /// </summary>
    private const float HeldFrom = 0.25f;

    /// <summary>
    /// Below this, a key that stays held counts as let go of by the hand.
    /// </summary>
    private const float LetGoBelow = 0.2f;

    /// <summary>
    /// From this on, a key that stays held and that the hand let go of counts as pressed again, which hands it back to
    /// the hand. Apart from <see cref="LetGoBelow"/>, so that a stick resting near the middle does neither.
    /// </summary>
    private const float PressedAgainAt = 0.5f;

    /// <summary>
    /// A key that was held as the chat opened and stays held.
    /// </summary>
    private sealed class Held {
        /// <summary>
        /// How far down it was, which it stays at.
        /// </summary>
        public float Value;

        /// <summary>
        /// Whether the hand let go of it since.
        /// </summary>
        public bool LetGo;
    }

    /// <summary>
    /// Compares the bindings themselves: InControl counts two bindings of the same key as equal, and one key can be
    /// bound to more than one action.
    /// </summary>
    private sealed class SameBinding : IEqualityComparer<BindingSource> {
        public bool Equals(BindingSource? x, BindingSource? y) => ReferenceEquals(x, y);

        public int GetHashCode(BindingSource obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// The keys that stay held while the chat is open, each as the binding of a game action it was held through.
    /// </summary>
    private static readonly Dictionary<BindingSource, Held> HeldKeys = new(new SameBinding());

    /// <summary>
    /// The opposite way of each direction of the game: pressing one hands the other back to the hand, as a held
    /// direction would otherwise cancel the one the hand now presses.
    /// </summary>
    private static readonly Dictionary<PlayerAction, PlayerAction> Opposites = new();

    /// <summary>
    /// The keys being handed back, kept to take them out of <see cref="HeldKeys"/> after going through it.
    /// </summary>
    private static readonly List<BindingSource> HandedBack = [];

    /// <summary>
    /// The hooks on the bindings, kept so they stay.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Whether the hooks were made.
    /// </summary>
    private static bool _registered;

    /// <summary>
    /// Whether the chat is open.
    /// </summary>
    private static bool _open;

    /// <summary>
    /// Hooks the bindings of keys and buttons, once.
    /// </summary>
    public static void Register() {
        if (_registered) {
            return;
        }

        _registered = true;

        // A key of the keyboard reads its value through its state (KeyBindingSource.GetValue calls GetState). The
        // states of the mouse and of an unknown gamepad read their values, so only the values are hooked: both hooks
        // on one read would take a held key as pressed again (review)
        Add<KeyBindingSource, bool>(
            "GetState",
            (orig, self, device) => Read(self, orig(self, device) ? 1f : 0f, true) > 0f
        );
        Add<MouseBindingSource, float>("GetValue", (orig, self, device) => Read(self, orig(self, device), true));
        Add<DeviceBindingSource, float>("GetValue", (orig, self, device) => Read(self, orig(self, device), false));
        Add<DeviceBindingSource, bool>(
            "GetState",
            (orig, self, device) => Read(self, orig(self, device) ? 1f : 0f, false) > 0f
        );
        Add<UnknownDeviceBindingSource, float>(
            "GetValue",
            (orig, self, device) => Read(self, orig(self, device), false)
        );
    }

    /// <summary>
    /// Hooks one method of a binding that reads its key for a device.
    /// </summary>
    private static void Add<TBinding, TResult>(
        string methodName,
        Func<Func<TBinding, InputDevice, TResult>, TBinding, InputDevice, TResult> hook
    ) where TBinding : BindingSource {
        try {
            var method = typeof(TBinding).GetMethod(methodName, [typeof(InputDevice)]);
            if (method == null) {
                Logger.Warn($"Could not find {typeof(TBinding).Name}.{methodName} for the chat's held keys");
                return;
            }

            Hooks.Add(new Hook(method, hook));
        } catch (Exception e) {
            Logger.Error(
                $"Could not hook {typeof(TBinding).Name}.{methodName}, so its keys may not do what they should " +
                $"while the chat is open:\n{e}"
            );
        }
    }

    /// <summary>
    /// Takes down the keys of the game's actions held as the chat opens, which stay held until it closes, and from here
    /// leaves the keyboard to the chat.
    /// </summary>
    /// <param name="actions">The game's actions.</param>
    public static void Open(HeroActions actions) {
        HeldKeys.Clear();
        Opposites.Clear();

        AddOpposites(actions.Left, actions.Right);
        AddOpposites(actions.Up, actions.Down);
        AddOpposites(actions.RsLeft, actions.RsRight);
        AddOpposites(actions.RsUp, actions.RsDown);

        // The actions that play the game. What moves through menus, opens the backpack or pauses is left out
        PlayerAction[] gameActions = [
            actions.Left, actions.Right, actions.Up, actions.Down,
            actions.RsLeft, actions.RsRight, actions.RsUp, actions.RsDown,
            actions.Jump, actions.Evade, actions.Dash, actions.SuperDash, actions.DreamNail, actions.Attack,
            actions.Cast, actions.QuickMap, actions.QuickCast, actions.Taunt
        ];

        List<string>? named = null;
        foreach (var action in gameActions) {
            if (action == null) {
                continue;
            }

            foreach (var binding in action.UnfilteredBindings) {
                float value;
                try {
                    value = binding.GetValue(action.Device);
                } catch (Exception) {
                    continue;
                }

                if (value < HeldFrom) {
                    continue;
                }

                HeldKeys[binding] = new Held { Value = value };
                (named ??= []).Add($"{action.Name} ({binding.Name})");
            }
        }

        _open = true;

        if (named != null) {
            Logger.Info($"[Chat] Opened with {string.Join(", ", named)} held, which stay held while it is open");
        }
    }

    /// <summary>
    /// Hands every key back to the hand as the chat closes.
    /// </summary>
    public static void Close() {
        _open = false;
        HeldKeys.Clear();
        Opposites.Clear();
    }

    /// <summary>
    /// Notes two opposite ways of a direction.
    /// </summary>
    private static void AddOpposites(PlayerAction? one, PlayerAction? other) {
        if (one == null || other == null) {
            return;
        }

        Opposites[one] = other;
        Opposites[other] = one;
    }

    /// <summary>
    /// What a key or button reads, from how far down the hand has it.
    /// </summary>
    /// <param name="binding">The binding of the key to an action.</param>
    /// <param name="hand">How far down the hand has it, from 0 to 1.</param>
    /// <param name="typedWith">Whether it is a key of the keyboard (or the mouse with it), which types into the chat
    /// rather than playing the game while the chat is open.</param>
    private static float Read(BindingSource binding, float hand, bool typedWith) {
        if (!_open) {
            return hand;
        }

        if (HeldKeys.TryGetValue(binding, out var held)) {
            // A held key of the keyboard stays held until the chat closes: every key it presses meanwhile is a letter
            // typed, not a press of that key again
            if (typedWith) {
                return held.Value;
            }

            if (hand < LetGoBelow) {
                held.LetGo = true;
            } else if (held.LetGo && hand >= PressedAgainAt) {
                // Pressed again: from here it is what the hand does with it
                HeldKeys.Remove(binding);
                return ReadLoose(binding, hand, false);
            }

            return Mathf.Max(hand, held.Value);
        }

        return ReadLoose(binding, hand, typedWith);
    }

    /// <summary>
    /// What a key that doesn't stay held reads while the chat is open.
    /// </summary>
    private static float ReadLoose(BindingSource binding, float hand, bool typedWith) {
        if (typedWith) {
            return IsCloseKey(binding) ? hand : 0f;
        }

        // The opposite way of a held direction pressed on the gamepad takes over from it. The arrow keys that move
        // through the text typed don't
        if (hand >= PressedAgainAt && HeldKeys.Count > 0 && binding.BoundTo is { } action &&
            Opposites.TryGetValue(action, out var opposite)) {
            HandBack(opposite);
        }

        return hand;
    }

    /// <summary>
    /// Whether a binding is one of the game's pause, which closes the chat: looked up as it is now, since the game makes
    /// its actions anew when it loads the keys it saved (InputHandler.SetupNonMappableBindings, review).
    /// </summary>
    private static bool IsCloseKey(BindingSource binding) {
        var input = InputHandler.Instance;
        return input != null && input.inputActions != null && binding.BoundTo == input.inputActions.Pause;
    }

    /// <summary>
    /// Hands the keys of an action that stay held back to the hand.
    /// </summary>
    private static void HandBack(PlayerAction action) {
        foreach (var binding in HeldKeys.Keys) {
            if (binding.BoundTo == action) {
                HandedBack.Add(binding);
            }
        }

        foreach (var binding in HandedBack) {
            HeldKeys.Remove(binding);
        }

        HandedBack.Clear();
    }
}
