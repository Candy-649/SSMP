using System;
using SSMP.Game.Client;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// A player held by a creature that grabbed them. For as long as a creature holds the hero, the game hides the hero's
/// own sprite and shows them by the creature instead: a stand-in of the hero that the creature carries, or its own
/// animation of holding them. Only the hero's own animation reached the other players, so they saw the figure of the
/// held player beside the creature's stand-in of them - the same player twice. The figure of a held player now hides
/// while they are held, as their hero does.
/// </summary>
internal class HeroHeld : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroHeld Instance = new();

    /// <summary>
    /// The FSM of the hero that holds it while a creature does: stunned, out of the player's hands and hidden, until
    /// the creature lets go.
    /// </summary>
    private const string HoldFsmName = "Roar and Wound States";

    /// <summary>
    /// The states of <see cref="HoldFsmName"/> in which the hero is held. It leaves them only when the creature lets
    /// go, whatever else the creature tells it meanwhile, and each way out shows the hero again - or not at all, for a
    /// player who dies in the creature's hold and goes down in a cocoon still held (see <see cref="HoldsTheLocalHero"/>).
    /// </summary>
    private static readonly string[] HeldStates = ["Hero Grab Invuln", "Hero Grab"];

    /// <summary>
    /// How often a held player says again that they are held, in seconds. It is said the way animations are, which may
    /// be lost on the way, and so may the word that they were let go (see <see cref="HiddenFor"/>).
    /// </summary>
    internal const float SayAgainAfter = 0.5f;

    /// <summary>
    /// How long the figure of a held player stays hidden after they last said that they are held, in seconds: a word
    /// that they were let go that got lost on the way leaves them hidden no longer than this.
    /// </summary>
    private const float HiddenFor = 1.5f;

    /// <summary>
    /// The effect info that says a player is held.
    /// </summary>
    internal static readonly byte[] Held = [1];

    /// <summary>
    /// The effect info that says a player was let go.
    /// </summary>
    internal static readonly byte[] LetGo = [0];

    /// <summary>
    /// Starts watching whether the local hero is held, which may be a new hero or the same one again. Like the other
    /// watches of the hero, this can run while the hero is still being made (see <see cref="HeroChildEffects.Watch"/>),
    /// so the FSM is looked for once the hero runs.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        if (!hero.gameObject.TryGetComponent<HeroHeldWatcher>(out var watcher)) {
            watcher = hero.gameObject.AddComponent<HeroHeldWatcher>();
        }

        watcher.StartOver(send);
    }

    /// <summary>
    /// The FSM that holds the hero (see <see cref="HoldFsmName"/>) among its FSMs, or null if it has none.
    /// </summary>
    /// <param name="fsms">The FSMs of the hero.</param>
    internal static PlayMakerFSM? FindHoldFsm(PlayMakerFSM[] fsms) {
        foreach (var fsm in fsms) {
            if (fsm.FsmName == HoldFsmName) {
                return fsm;
            }
        }

        Logger.Warn($"The hero has no FSM '{HoldFsmName}' to tell other players that it is held by");
        return null;
    }

    /// <summary>
    /// Whether the local hero is held now: the FSM that holds it does (see <see cref="HoldFsmName"/>), and the hero is
    /// hidden for it. A player lying in a cocoon is not, whatever the FSM says: the partner has to find them there.
    /// </summary>
    /// <param name="fsm">The FSM that holds the hero.</param>
    /// <param name="hero">The hero.</param>
    /// <param name="body">The hero's own sprite, or null if it has none.</param>
    internal static bool HoldsTheLocalHero(PlayMakerFSM fsm, GameObject hero, MeshRenderer? body) {
        return Array.IndexOf(HeldStates, fsm.ActiveStateName) >= 0 && (body == null || !body.enabled) &&
               !PlayerTargetRegistry.IsPlayerDown(hero);
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not [var held]) {
            return;
        }

        if (!playerObject.TryGetComponent<HeldFigure>(out var figure)) {
            if (held == 0) {
                return;
            }

            figure = playerObject.AddComponent<HeldFigure>();
        }

        figure.HiddenUntil = held == 1 ? Time.unscaledTime + HiddenFor : 0f;
        HeroChildEffects.ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Whether the character of another player is held by a creature, as far as that player said.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static bool IsHeld(GameObject playerObject) {
        return playerObject.TryGetComponent<HeldFigure>(out var figure) && figure.HiddenUntil > Time.unscaledTime;
    }

    /// <summary>
    /// Forgets that the character of another player is held, for a character that leaves the room: it may come back,
    /// or be used for another player, long after the word that would have shown it again.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        if (playerObject.TryGetComponent<HeldFigure>(out var figure)) {
            figure.HiddenUntil = 0f;
        }
    }
}

/// <summary>
/// Watches whether the local hero is held by a creature, for <see cref="HeroHeld"/>, and says so to the other players
/// when that changes, and again every so often while it lasts.
/// </summary>
internal class HeroHeldWatcher : MonoBehaviour {
    /// <summary>
    /// Sends the effect info of a change to the other players. Not serialized, so a copy of the hero made for another
    /// player's character, which would have this component too, says nothing.
    /// </summary>
    [NonSerialized]
    private Action<byte[]>? _send;

    /// <summary>
    /// The FSM that holds the hero, found once the hero runs.
    /// </summary>
    [NonSerialized]
    private PlayMakerFSM? _fsm;

    /// <summary>
    /// The hero's own sprite, which the game hides while the hero is held.
    /// </summary>
    [NonSerialized]
    private MeshRenderer? _body;

    /// <summary>
    /// Whether the other players were last told that the hero is held.
    /// </summary>
    [NonSerialized]
    private bool _held;

    /// <summary>
    /// When the other players are told again that the hero is still held, in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _sayAgainAt;

    /// <summary>
    /// Starts watching again, telling the other players with the given callback, for a hero that may have been held
    /// with a callback of before.
    /// </summary>
    public void StartOver(Action<byte[]> send) {
        _send = send;
        _fsm = null;
        if (_held) {
            _held = false;
            send(HeroHeld.LetGo);
        }
    }

    private void LateUpdate() {
        if (_send == null) {
            return;
        }

        if (_fsm == null) {
            _fsm = HeroHeld.FindHoldFsm(GetComponents<PlayMakerFSM>());
            if (_fsm == null) {
                // Looked for once: a hero without it will not grow it
                _send = null;
                return;
            }

            _body = GetComponent<MeshRenderer>();
        }

        var held = HeroHeld.HoldsTheLocalHero(_fsm, gameObject, _body);
        if (held == _held && (!held || Time.unscaledTime < _sayAgainAt)) {
            return;
        }

        _held = held;
        _sayAgainAt = Time.unscaledTime + HeroHeld.SayAgainAfter;
        _send(held ? HeroHeld.Held : HeroHeld.LetGo);
    }
}

/// <summary>
/// Keeps the figure of another player hidden while a creature holds them, for <see cref="HeroHeld"/>, and shows it
/// again when they have not said for a while that they still are.
/// </summary>
internal class HeldFigure : MonoBehaviour {
    /// <summary>
    /// Until when the figure stays hidden, in unscaled time, or 0 once it is shown again.
    /// </summary>
    [NonSerialized]
    public float HiddenUntil;

    private void Update() {
        if (HiddenUntil <= 0f || Time.unscaledTime < HiddenUntil) {
            return;
        }

        HiddenUntil = 0f;
        HeroChildEffects.ShowBodyUnlessHidden(gameObject);
    }
}
