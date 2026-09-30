using System;
using SSMP.Game.Client;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// A player whom the game hides. It hides the hero while a creature holds them, and shows them by the creature instead:
/// a stand-in of the hero that the creature carries, or its own animation of holding them. It hides them while the room
/// has them too - on a ride that brings them up from a pit, behind a door they come in by, in a scene it plays, lying
/// in a bed - and shows them there by the room's own figure of them, if it has one (see <see cref="HeroRoomFigure"/>).
/// Only the hero's own animation reached the other players, so they saw the figure of the player where the game had
/// put the hidden hero: beside the creature's stand-in of them, the same player twice, or standing in the pit that the
/// ride was about to bring them up from. The figure of a hidden player now hides for the other players too, as their
/// hero does. The hero's own moves that hide it for effects of their own are left to the effects that show them.
/// </summary>
internal class HeroHidden : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroHidden Instance = new();

    /// <summary>
    /// The FSM of the hero that holds it while a creature does: stunned, out of the player's hands and hidden, until
    /// the creature lets go.
    /// </summary>
    private const string HoldFsmName = "Roar and Wound States";

    /// <summary>
    /// The states of <see cref="HoldFsmName"/> in which the hero is held. It leaves them only when the creature lets
    /// go, whatever else the creature tells it meanwhile, and each way out shows the hero again - or not at all, for a
    /// player who dies in the creature's hold and goes down in a cocoon still held (see <see cref="HidesTheLocalHero"/>).
    /// </summary>
    private static readonly string[] HeldStates = ["Hero Grab Invuln", "Hero Grab"];

    /// <summary>
    /// How often a hidden player says again that they are hidden, in seconds. It is said the way animations are, which
    /// may be lost on the way, and so may the word that they show again (see <see cref="HiddenFor"/>).
    /// </summary>
    internal const float SayAgainAfter = 0.5f;

    /// <summary>
    /// How often a hidden player says again that they are hidden for a while after either player came into the room,
    /// in seconds (see <see cref="SayAgainSoon"/>).
    /// </summary>
    internal const float SayOftenEvery = 0.1f;

    /// <summary>
    /// For how long after either player came into the room a hidden player says so often, in seconds.
    /// </summary>
    internal const float SayOftenFor = 1f;

    /// <summary>
    /// How long the figure of a hidden player stays hidden after they last said that they are hidden, in seconds: a
    /// word that they show again that got lost on the way leaves them hidden no longer than this.
    /// </summary>
    private const float HiddenFor = 1.5f;

    /// <summary>
    /// How long the figure of a player who just came into the room stays hidden until they say whether the game hides
    /// them, in seconds (see <see cref="HideNewcomer"/>).
    /// </summary>
    private const float NewcomerHiddenFor = 0.25f;

    /// <summary>
    /// The effect info that says a player is hidden.
    /// </summary>
    internal static readonly byte[] Hidden = [1];

    /// <summary>
    /// The effect info that says a player shows again.
    /// </summary>
    internal static readonly byte[] Shown = [0];

    /// <summary>
    /// What watches the local hero, which is told when the room hides it.
    /// </summary>
    private static HeroHiddenWatcher? _watcher;

    /// <summary>
    /// Starts watching whether the local hero is hidden, which may be a new hero or the same one again. Like the other
    /// watches of the hero, this can run while the hero is still being made (see <see cref="HeroChildEffects.Watch"/>),
    /// so the FSM that holds it is looked for once the hero runs.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        if (!hero.gameObject.TryGetComponent<HeroHiddenWatcher>(out var watcher)) {
            watcher = hero.gameObject.AddComponent<HeroHiddenWatcher>();
        }

        watcher.StartOver(send);
        _watcher = watcher;
    }

    /// <summary>
    /// Notes that the room or a creature just hid the local hero: an FSM that is not the hero's own switched its sprite
    /// off (see <see cref="HeroRoomFigure"/>). The hero counts as hidden by it until its sprite shows again.
    /// </summary>
    internal static void HiddenByTheRoom() {
        _watcher?.HiddenByTheRoom();
    }

    /// <summary>
    /// Says often for a while whether the local hero is hidden, once the local player or another one came into the
    /// room. A word said in the same update as coming in went to the room the player came from, as the server moves
    /// the player only after it passed on their update, and a room hides a player who comes in by a ride or a door
    /// right as they come. A player who comes in hears the others at once, not at their next say.
    /// </summary>
    internal static void SayAgainSoon() {
        _watcher?.SayAgainSoon();
    }

    /// <summary>
    /// Keeps the figure of a player who just came into the room hidden for a moment, until they have said whether the
    /// game hides them: a room hides a player who comes in by a ride right as they come, and without this the other
    /// players saw them stand where the ride brings them up from until the word arrived.
    /// </summary>
    /// <param name="playerObject">The character of the player who came in.</param>
    internal static void HideNewcomer(GameObject playerObject) {
        if (!playerObject.TryGetComponent<HiddenFigure>(out var figure)) {
            figure = playerObject.AddComponent<HiddenFigure>();
        }

        figure.HiddenUntil = Mathf.Max(figure.HiddenUntil, Time.unscaledTime + NewcomerHiddenFor);
        HeroChildEffects.ShowBodyUnlessHidden(playerObject);
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
    /// Whether the local hero is hidden now: its sprite is off, and either a creature holds it, as the FSM that holds
    /// it says (see <see cref="HoldFsmName"/>), or the room hid it since its sprite last showed. A player lying in a
    /// cocoon is not, whatever hid them: the partner has to find them there.
    /// </summary>
    /// <param name="holdFsm">The FSM that holds the hero, or null if it has none.</param>
    /// <param name="hiddenByTheRoom">Whether the room hid the hero since its sprite last showed.</param>
    /// <param name="hero">The hero.</param>
    /// <param name="body">The hero's own sprite, or null if it has none.</param>
    internal static bool HidesTheLocalHero(
        PlayMakerFSM? holdFsm,
        bool hiddenByTheRoom,
        GameObject hero,
        MeshRenderer? body
    ) {
        return (body == null || !body.enabled) &&
               (hiddenByTheRoom || (holdFsm != null && Array.IndexOf(HeldStates, holdFsm.ActiveStateName) >= 0)) &&
               !PlayerTargetRegistry.IsPlayerDown(hero);
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not [var hidden]) {
            return;
        }

        if (!playerObject.TryGetComponent<HiddenFigure>(out var figure)) {
            if (hidden == 0) {
                return;
            }

            figure = playerObject.AddComponent<HiddenFigure>();
        }

        figure.HiddenUntil = hidden == 1 ? Time.unscaledTime + HiddenFor : 0f;
        HeroChildEffects.ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Whether the character of another player is hidden, as far as that player said.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static bool IsHidden(GameObject playerObject) {
        return playerObject.TryGetComponent<HiddenFigure>(out var figure) && figure.HiddenUntil > Time.unscaledTime;
    }

    /// <summary>
    /// Forgets that the character of another player is hidden, for a character that leaves the room: it may come back,
    /// or be used for another player, long after the word that would have shown it again.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        if (playerObject.TryGetComponent<HiddenFigure>(out var figure)) {
            figure.HiddenUntil = 0f;
        }
    }
}

/// <summary>
/// Watches whether the local hero is hidden, for <see cref="HeroHidden"/>, and says so to the other players when that
/// changes, and again every so often while it lasts.
/// </summary>
internal class HeroHiddenWatcher : MonoBehaviour {
    /// <summary>
    /// Sends the effect info of a change to the other players.
    /// </summary>
    [NonSerialized]
    private Action<byte[]>? _send;

    /// <summary>
    /// Whether the FSM that holds the hero was looked for, which is once the hero runs.
    /// </summary>
    [NonSerialized]
    private bool _lookedForFsm;

    /// <summary>
    /// The FSM that holds the hero, or null if it has none.
    /// </summary>
    [NonSerialized]
    private PlayMakerFSM? _fsm;

    /// <summary>
    /// The hero's own sprite, which the game hides while the hero is hidden.
    /// </summary>
    [NonSerialized]
    private MeshRenderer? _body;

    /// <summary>
    /// Whether the room hid the hero since its sprite last showed.
    /// </summary>
    [NonSerialized]
    private bool _hiddenByTheRoom;

    /// <summary>
    /// Whether the other players were last told that the hero is hidden.
    /// </summary>
    [NonSerialized]
    private bool _hidden;

    /// <summary>
    /// When the other players are told again that the hero is still hidden, in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _sayAgainAt;

    /// <summary>
    /// Until when the other players are told often (see <see cref="HeroHidden.SayAgainSoon"/>), in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _sayOftenUntil;

    /// <summary>
    /// Starts watching again, telling the other players with the given callback, for a hero that may be hidden already:
    /// a player who connects again while hidden is said to be hidden at once. What the room hid stays hidden: the room
    /// does not hide the hero again for the new connection.
    /// </summary>
    public void StartOver(Action<byte[]> send) {
        _send = send;
        _lookedForFsm = false;
        _fsm = null;
        _hidden = false;
    }

    /// <summary>
    /// Notes that the room just hid the hero (see <see cref="HeroHidden.HiddenByTheRoom"/>).
    /// </summary>
    public void HiddenByTheRoom() {
        _hiddenByTheRoom = true;
    }

    /// <summary>
    /// Tells the other players often for a while (see <see cref="HeroHidden.SayAgainSoon"/>), starting now.
    /// </summary>
    public void SayAgainSoon() {
        _sayOftenUntil = Time.unscaledTime + HeroHidden.SayOftenFor;
        _sayAgainAt = 0f;
    }

    private void LateUpdate() {
        if (_send == null) {
            return;
        }

        if (!_lookedForFsm) {
            _lookedForFsm = true;
            _fsm = HeroHidden.FindHoldFsm(GetComponents<PlayMakerFSM>());
            _body = GetComponent<MeshRenderer>();
        }

        // What the room hid lasts until the sprite shows again, whoever shows it
        if (_hiddenByTheRoom && _body != null && _body.enabled) {
            _hiddenByTheRoom = false;
        }

        var hidden = HeroHidden.HidesTheLocalHero(_fsm, _hiddenByTheRoom, gameObject, _body);
        if (hidden == _hidden && (!hidden || Time.unscaledTime < _sayAgainAt)) {
            return;
        }

        _hidden = hidden;
        _sayAgainAt = Time.unscaledTime + (Time.unscaledTime < _sayOftenUntil
            ? HeroHidden.SayOftenEvery
            : HeroHidden.SayAgainAfter);
        _send(hidden ? HeroHidden.Hidden : HeroHidden.Shown);
    }
}

/// <summary>
/// Keeps the figure of another player hidden while the game hides them, for <see cref="HeroHidden"/>, and shows it
/// again when they have not said for a while that they still are.
/// </summary>
internal class HiddenFigure : MonoBehaviour {
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
