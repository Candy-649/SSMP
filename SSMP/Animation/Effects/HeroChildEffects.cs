using System;
using System.Collections.Generic;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The parts of the hero that only show while it does something - the thread of a taunt, the burst of a dash, the puff
/// of a wall jump, the glow of a charge - which the game switches on and off by themselves, beside the hero's own
/// animation. Only that animation reached the other players, so they saw the hero taunt without its thread, dash
/// without its burst and so on. Each of these parts is watched on the local hero, and a copy of it on the character of
/// the player it belongs to is switched on and off with it. The parts whose clip tells when they show go by the clips
/// of the character instead (<see cref="PartsByClips"/>), and are listed here only so that their copies are switched
/// off with the rest as a character leaves the room.
/// </summary>
internal class HeroChildEffects : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroChildEffects Instance = new();

    /// <summary>
    /// The watched parts, by their path under the hero, and the parts that go by the clips of the character (see
    /// <see cref="ByCharacterClips"/>). The place of a watched part in this list is what it is known by over the
    /// network. Left out are the parts that another effect already plays, the parts that
    /// could hurt, touch or be noticed by anything in the room (damagers, colliders, state machines, noise makers),
    /// the parts that show or hide by what the local player has equipped, the parts of the local player's own view
    /// (the light around the hero and the dark of a room), and what the game shows when the hero is hit, which is for
    /// the player who was hit alone: the dark burst of a hit at low health, half a screen wide and drawn over
    /// everything, flashed on the other player's screen as if it were theirs. The parts with animations of their own
    /// play what the hero's parts play by <see cref="HeroPartClips"/>.
    /// </summary>
    private static readonly string[] Paths = [
        "Effects/Taunt Thread",
        "Effects/Taunt Rings Flash",
        "Effects/Dash Burst",
        "Effects/Water Dash Burst",
        "Effects/air_sprint_effect",
        "Effects/Walldash Kickoff",
        "Effects/Wall Puff",
        "Effects/Backflip Puff",
        "Effects/Downspike Burst",
        "Effects/Can Bind Effect",
        "Effects/NA Charge",
        "Effects/NA Charged",
        "Effects/Super Jump Antic Effect L",
        "Effects/Super Jump Antic Effect R",
        "Effects/Super Jump Thread Loop",
        "Effects/Super Jump Thread",
        "Effects/Super Jump Charged",
        "Effects/Super Jump Extra Throw Effect",
        "Effects/Super Jump Extra Ground Effect",
        "Effects/Super Jump Catch Effect",
        "Effects/Hornet_harpoon_grab_effect",
        "Effects/Hornet_harpoon_throw_effect",
        "Effects/Hornet_harpoon_dash",
        "Effects/Harpoon Thread Ring/Thread",
        "Effects/Hunter SprintAttackBurst",
        "Effects/Wanderer DashCombo Burst",
        "Effects/wall_launch_puff",
        "Effects/clamber_impact_effects",
        "Effects/Hornet_Wall_Scramble_Burst_Effect",
        "Effects/umbrella_updraftJump_fx",
        "Effects/Tool_wall_cling_effect",
        "Effects/Needolin Snap Effect",
        "Effects/Needolin Thread",
        "Effects/Swimp Bonk Effect",
        "Effects/Wall Bump/NeedleThrow Effect",
        "Effects/Wall Bump/Particle System",
        "Special Attacks/Sphere Flash",
        "Special Attacks/Silk Charge WallBonk/Slam Effect",
        "Special Attacks/Super Jump Needle Stick",
        "Special Attacks/Parry DashBurst",
        "Attacks/Shaman/Shaman_blade_cast_effect DashSlash",
        "Bind Effects/shaman_focus_halo",
        "Bind Effects/hornet_quick_bind_effect",
        "Tool Effects/Screw Activation Flash",
        "Tool Effects/Syringe Flash poison",
        "Tool Effects/Syringe Flash",
        "Tool Effects/Craft Flash",
        "Tool Effects/Craft Silk",
        "Tool Effects/QuickCraft Silk",
        "Tool Effects/Pt QuickCraft",
        "Tool Effects/Screw Attack Burst",
        "Tool Effects/Screw Burst",
        "Tool Effects/Extractor ChargedEffect",
        "Tool Effects/Scuttle Burst",
        "Tool Effects/Syringe Glow",
        "Tool Effects/Syringe Glow poison",
        "Tool Effects/Screw Attack Impact",
        "Tool Effects/Pt ScrewAttack Poison Trail",
        "Tool Effects/Pt ScrewAttack Poison Impact",
        AmplifyPath,
        AmplifyCopyPath,
        "Tool Effects/Rosary Cannon Point/Shoot Effect",

        // The stand-in that the game shows in place of the hero's own sprite for the tools that need a pose of their
        // own, what goes on top of it, and what those tools show around it
        StandInPath,
        "Tool Effects/Tool Hornet Poison Screw Attack",
        "Tool Effects/WebShot Effects/Gun Sprite",
        "Tool Effects/WebShot Effects/Spit Effect S",
        "Tool Effects/WebShot Effects/Spit Effect W",
        "Tool Effects/WebShot Effects/Flash S",
        "Tool Effects/WebShot_antic_effect"
    ];

    /// <summary>
    /// The path of the stand-in for the hero's own sprite: while it is on, the game hides the hero's own sprite.
    /// </summary>
    private const string StandInPath = "Tool Effects/Tool Hornet";

    /// <summary>
    /// The path of the glow around the hero while it plays the needolin with the tool that enlarges its range. The
    /// game plays every other song with a copy of it (<see cref="AmplifyCopyPath"/>), so that the glow of one song can
    /// still fade out while the next one starts.
    /// </summary>
    private const string AmplifyPath = "Tool Effects/Amplify_Effect";

    /// <summary>
    /// The copy of the glow of <see cref="AmplifyPath"/>, which the hero's song region makes in its Awake, next to it
    /// and under the name Unity gives a copy. That Awake can run after the watching starts.
    /// </summary>
    private const string AmplifyCopyPath = "Tool Effects/Amplify_Effect(Clone)";

    /// <summary>
    /// The value of the second byte of a change for a part that is switched off: it is switched off.
    /// </summary>
    private const byte SwitchedOff = 0;

    /// <summary>
    /// The value of the second byte of a change for a part that is switched on: it is switched on.
    /// </summary>
    private const byte SwitchedOn = 1;

    /// <summary>
    /// The value of the second byte of a change for a part that stays on: it fades out now, and is switched off by a
    /// change of its own once it has. The game fades out the glow of a song by its animation "Disappear" and switches
    /// it off only after that, so the change of it being switched off came late, and the copy went off at once.
    /// </summary>
    private const byte FadesOut = 2;

    /// <summary>
    /// The state of the animator of a glow of a song in which it fades out, as the game plays it.
    /// </summary>
    private static readonly int DisappearState = Animator.StringToHash("Disappear");

    /// <summary>
    /// The hero whose parts are watched, for the copy of the glow of a song, which is watched as a song starts.
    /// </summary>
    private static Transform? _watchedHero;

    /// <summary>
    /// Sends the effect info of a change to the other players, for the copy of the glow of a song, which is watched as
    /// a song starts, and for the fading out of the glow.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// Whether <see cref="OnStartedPerforming"/> and <see cref="OnStoppedPerforming"/> hear the local hero start and
    /// stop a song.
    /// </summary>
    private static bool _hearsSongs;

    /// <summary>
    /// The watched parts that the game switches on together with a flash of the hero's whole sprite, by their path,
    /// with that flash.
    /// </summary>
    private static readonly Dictionary<string, Action<SpriteFlash>> Flashes = new() {
        ["Tool Effects/Syringe Flash"] = flash => flash.flashHealBlue(),
        ["Tool Effects/Syringe Flash poison"] = flash => flash.flashHealPoison()
    };

    /// <summary>
    /// The number of bytes that one bit for each watched part takes.
    /// </summary>
    private static readonly int MaskLength = (Paths.Length + 7) / 8;

    /// <summary>
    /// Which watched parts of the local hero are switched on. It goes along in full with every change, so that a
    /// change that got lost on the way is put right by the next one.
    /// </summary>
    private static readonly byte[] LocalMask = new byte[MaskLength];

    /// <summary>
    /// Which places in <see cref="Paths"/> hold parts that go by the clips of the character instead, like the needles
    /// and threads of a super jump (see <see cref="PartsByClips"/>): they are neither watched nor sent, and the changes
    /// of the other parts leave their copies be. Their places stay, so that their copies go with the rest as a
    /// character leaves the room.
    /// </summary>
    private static readonly bool[] ByCharacterClips = MarkByCharacterClips();

    /// <summary>
    /// Marks the places of the parts that go by the clips of the character (see <see cref="ByCharacterClips"/>).
    /// </summary>
    private static bool[] MarkByCharacterClips() {
        var marks = new bool[Paths.Length];
        for (var i = 0; i < Paths.Length; i++) {
            marks[i] = Array.IndexOf(PartsByClips.Paths, Paths[i]) >= 0;
        }

        return marks;
    }

    /// <summary>
    /// Starts watching the parts of the local hero, which may be a new hero or the same one again.
    ///
    /// This runs when the game first asks for the hero, which can be while the hero is still being made: one of its
    /// own parts asks from its Awake, before the hero's own set-up has run. An exception thrown here comes out of
    /// that part's Awake and leaves the part unset.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        Array.Clear(LocalMask, 0, MaskLength);

        // The transform of the object, not HeroController.transform: the hero keeps a field of that name that hides
        // the component's own, and fills it in only in its set-up. Read before that it is null, and the exception
        // it threw came out of the Awake of the down attack that asked for the hero first, which then never hooked
        // its hits: it bounced off nothing and struck nothing it has to strike itself, like the partner's cocoon.
        var heroTransform = hero.gameObject.transform;
        _watchedHero = heroTransform;
        _send = send;
        for (var i = 0; i < Paths.Length; i++) {
            if (!ByCharacterClips[i] && !WatchPart(heroTransform, i, send) && Paths[i] != AmplifyCopyPath) {
                Logger.Warn($"The hero has no part '{Paths[i]}' to show to other players");
            }
        }

        // The copy of the song glow, which HeroPerformanceRegion.Awake can make after this, is watched as songs start
        if (!_hearsSongs) {
            _hearsSongs = true;
            HeroPerformanceRegion.StartedPerforming += OnStartedPerforming;
            HeroPerformanceRegion.StoppedPerforming += OnStoppedPerforming;
        }
    }

    /// <summary>
    /// Watches one part of the local hero, when the hero has it.
    /// </summary>
    /// <param name="heroTransform">The transform of the local hero.</param>
    /// <param name="index">The place of the part in the watched parts.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    /// <returns>Whether the hero has the part.</returns>
    private static bool WatchPart(Transform heroTransform, int index, Action<byte[]> send) {
        var part = heroTransform.Find(Paths[index]);
        if (part == null) {
            return false;
        }

        if (!part.TryGetComponent<HeroChildEffectWatcher>(out var watcher)) {
            watcher = part.gameObject.AddComponent<HeroChildEffectWatcher>();
        }

        watcher.Index = index;
        watcher.Toggled = (changed, on) => OnToggled(changed, on, send);
        SetBit(LocalMask, index, part.gameObject.activeInHierarchy);
        return true;
    }

    /// <summary>
    /// Called as the local hero starts playing the needolin, right before the game switches on the glow of the song:
    /// watches the copy of the glow if it is not watched yet. The song region makes the copy in its Awake, which can
    /// come after <see cref="Watch"/>, and has made it by the time it plays a song.
    /// </summary>
    private static void OnStartedPerforming() {
        try {
            var hero = _watchedHero;
            var send = _send;
            if (hero == null || send == null) {
                return;
            }

            var part = hero.Find(AmplifyCopyPath);
            if (part == null ||
                part.TryGetComponent<HeroChildEffectWatcher>(out var watcher) && watcher.Toggled != null) {
                return;
            }

            var index = Array.IndexOf(Paths, AmplifyCopyPath);
            if (WatchPart(hero, index, send)) {
                Logger.Info($"The hero's part '{AmplifyCopyPath}' shows to other players from this song on");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not watch the copy of the glow of a song: {e.Message}");
        }
    }

    /// <summary>
    /// Called as the local hero stops playing the needolin, right before the game starts to fade out the glow of
    /// the song, when it has one: tells the other players that their copy of that glow fades out now (see
    /// <see cref="FadesOut"/>). The game fades out only a glow that is on and not fading out already.
    /// </summary>
    private static void OnStoppedPerforming() {
        // This runs inside the game's stop of the song, which fades out the glow and puts the view back after it
        try {
            var region = HeroPerformanceRegion._instance;
            var send = _send;
            if (region == null || send == null) {
                return;
            }

            var isCopy = region.amplifyEffectIsB;
            var glow = isCopy ? region.amplifyEffectB : region.amplifyEffect;
            var fading = isCopy ? region.endAmplifyEffectRoutineB : region.endAmplifyEffectRoutineA;
            if (glow == null || fading != null || !glow.gameObject.activeSelf ||
                !glow.TryGetComponent<HeroChildEffectWatcher>(out var watcher) || watcher.Toggled == null) {
                return;
            }

            Send(watcher.Index, FadesOut, send);
        } catch (Exception e) {
            Logger.Warn($"Could not tell the other players that the glow of the song fades out: {e.Message}");
        }
    }

    /// <summary>
    /// Called when a watched part of the local hero is switched on or off.
    /// </summary>
    private static void OnToggled(int index, bool on, Action<byte[]> send) {
        SetBit(LocalMask, index, on);
        Send(index, on ? SwitchedOn : SwitchedOff, send);
    }

    /// <summary>
    /// Sends a change of a watched part with which of them are on now.
    /// </summary>
    /// <param name="index">The place of the part in the watched parts.</param>
    /// <param name="change">What became of it: <see cref="SwitchedOff"/>, <see cref="SwitchedOn"/> or
    /// <see cref="FadesOut"/>.</param>
    /// <param name="send">Sends the effect info to the other players.</param>
    private static void Send(int index, byte change, Action<byte[]> send) {
        var effectInfo = new byte[2 + MaskLength];
        effectInfo[0] = (byte) index;
        effectInfo[1] = change;
        Array.Copy(LocalMask, 0, effectInfo, 2, MaskLength);
        send(effectInfo);
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length != 2 + MaskLength || effectInfo[0] >= Paths.Length) {
            return;
        }

        var changed = effectInfo[0];
        var on = effectInfo[1] == SwitchedOn;
        if (effectInfo[1] == FadesOut) {
            // Stays on until the change of it being switched off, as the part there does
            var fading = GetCopy(playerObject, Paths[changed], false);
            if (fading != null && fading.activeInHierarchy && fading.TryGetComponent<Animator>(out var animator)) {
                animator.Play(DisappearState);
            }
        } else {
            var part = GetCopy(playerObject, Paths[changed], on);
            if (part != null) {
                if (on) {
                    Restart(part);
                } else {
                    part.SetActive(false);
                }
            }
        }

        if (on && Flashes.TryGetValue(Paths[changed], out var flash) &&
            playerObject.TryGetComponent<SpriteFlash>(out var spriteFlash)) {
            flash(spriteFlash);
        }

        // What the changes that got lost on the way would have done. A part that is off there is off here. A part
        // that stays on until it is switched off is on here too, but a part that ends by itself is only ever started by
        // its own change: it may already have ended here while it is still showing there.
        for (var i = 0; i < Paths.Length; i++) {
            if (i == changed || ByCharacterClips[i]) {
                continue;
            }

            if ((effectInfo[2 + i / 8] & (1 << (i % 8))) == 0) {
                var copy = GetCopy(playerObject, Paths[i], false);
                if (copy != null && copy.activeSelf) {
                    copy.SetActive(false);
                }

                continue;
            }

            var local = HeroController.instance.transform.Find(Paths[i]);
            if (local == null || EndsByItself(local.gameObject)) {
                continue;
            }

            var other = GetCopy(playerObject, Paths[i], true);
            if (other != null && !other.activeSelf) {
                Restart(other);
            }
        }

        ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Switches off every copied part on the character of another player and shows its own sprite, for a character
    /// that leaves the room: it may come back, or be used for another player, long after the change that would have
    /// switched its parts off.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void ResetCopies(GameObject playerObject) {
        foreach (var path in Paths) {
            var copy = GetCopy(playerObject, path, false);
            if (copy != null && copy.activeSelf) {
                copy.SetActive(false);
            }
        }

        ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Hides the own sprite of the character of another player while the game hides the hero's own sprite there - while
    /// the copy of the stand-in is on, which shows in its place, while a creature holds them or the room has them (see
    /// <see cref="HeroHidden"/>), while the room shows them by a figure of its own (see
    /// <see cref="HeroRoomFigure"/>), and while the curse of a bind shows in its place (see
    /// <see cref="Bind.ShowsCurse"/>) - and shows it otherwise.
    /// </summary>
    internal static void ShowBodyUnlessHidden(GameObject playerObject) {
        if (!playerObject.TryGetComponent<MeshRenderer>(out var body)) {
            return;
        }

        var standIn = GetCopy(playerObject, StandInPath, false);
        body.enabled = (standIn == null || !standIn.activeSelf) && !HeroHidden.IsHidden(playerObject) &&
                       !HeroRoomFigure.IsShown(playerObject) && !Bind.ShowsCurse(playerObject);
    }

    /// <summary>
    /// Switches a copied part on from its start, the way the game switches its own part on.
    /// </summary>
    internal static void Restart(GameObject part) {
        part.SetActive(false);
        part.SetActive(true);

        // The game plays the animation of a part that does not end by itself right after switching it on; a part that
        // does end by itself plays it from the start as it is switched on, which this repeats harmlessly
        if (part.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
            animator.PlayFromFrame(0);
        }
    }

    /// <summary>
    /// Whether a part switches itself off when it is done, rather than being switched off by the game.
    /// </summary>
    internal static bool EndsByItself(GameObject part) {
        return part.GetComponent<DeactivateAfter2dtkAnimation>() ||
               part.GetComponent<DeactivateAfterDelay>() ||
               part.GetComponent<DisableAfterTime>() ||
               part.GetComponent<ParticleSystemAutoDisable>() ||
               part.GetComponent<ParticleSystemAutoDeactivate>();
    }

    /// <summary>
    /// The copy of a part of the hero on the character of another player, at the same place under it as the part is
    /// under the local hero, made from the local hero's own part when it is not there yet.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    /// <param name="path">The path of the part under the hero.</param>
    /// <param name="create">Whether to make the copy when there is none yet.</param>
    /// <returns>The copy, or null when there is none and none could or should be made.</returns>
    public static GameObject? GetCopy(GameObject playerObject, string path, bool create) {
        var names = path.Split('/');
        var hero = HeroController.instance;
        var original = hero != null ? hero.transform : null;
        var current = playerObject.transform;

        for (var i = 0; i < names.Length; i++) {
            original = original != null ? original.Find(names[i]) : null;

            var next = current.Find(names[i]);
            if (next == null) {
                if (!create || original == null) {
                    return null;
                }

                next = i < names.Length - 1
                    ? CreateStep(current, original)
                    : CreateCopy(current, original, playerObject.transform);
            }

            current = next;
        }

        return current.gameObject;
    }

    /// <summary>
    /// The copy of a part of the hero on the character of another player that a creature of the room switches on or
    /// off there, on that player's own character (see EntityFsmActions.HeroPartOnFigure), made from the local hero's
    /// part when there is none yet, or null when the local hero has no such part.
    ///
    /// Such a part is not one of the watched parts: it may hold parts of its own that the creature switches on, like
    /// the holder of the strike of a cross slash, which is on under the hero while its strike is off. The copy is on or
    /// off as the local part is, and nothing on it hurts or is hit: the strike's damager would have struck the copies
    /// of creatures here as if this game's player had, beside the strike that their game counts.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    /// <param name="path">The path of the part under the hero.</param>
    public static GameObject? GetFigurePart(GameObject playerObject, string path) {
        var part = GetCopy(playerObject, path, false);
        if (part != null) {
            return part;
        }

        part = GetCopy(playerObject, path, true);
        if (part == null) {
            return null;
        }

        foreach (var damager in part.GetComponentsInChildren<DamageEnemies>(true)) {
            Object.DestroyImmediate(damager);
        }

        foreach (var collider in part.GetComponentsInChildren<Collider2D>(true)) {
            Object.DestroyImmediate(collider);
        }

        part.SetActive(HeroController.instance.transform.Find(path).gameObject.activeSelf);
        return part;
    }

    /// <summary>
    /// Makes an empty object on the way to a copied part, placed like the object on the way to the local part, so that
    /// only the part itself is copied and never the objects around it.
    /// </summary>
    private static Transform CreateStep(Transform parent, Transform original) {
        var step = new GameObject(original.name).transform;
        step.SetParent(parent, false);
        step.localPosition = original.localPosition;
        step.localRotation = original.localRotation;
        step.localScale = original.localScale;
        return step;
    }

    /// <summary>
    /// Copies a part of the local hero, switched off, without whatever on it would shake the camera or the controller
    /// of the local player, and turned towards the character it is on rather than towards the local hero.
    /// </summary>
    /// <param name="parent">Where the copy goes.</param>
    /// <param name="original">The part of the local hero.</param>
    /// <param name="character">The character of the other player, which the copy is on.</param>
    private static Transform CreateCopy(Transform parent, Transform original, Transform character) {
        // Made inside a holder that is switched off, so that nothing on the copy starts before it is switched on the
        // way the other player's part was, even when the local part is showing right now
        var holder = new GameObject("Hero Child Effect Holder");
        holder.SetActive(false);

        var copy = Object.Instantiate(original.gameObject, holder.transform, false);
        copy.name = original.name;
        copy.SetActive(false);

        // In what the other player wears, rather than in what the local player wears (see HeroCopies)
        HeroCopies.DressCopy(character.gameObject, copy);

        // Switched off rather than removed: switched off they do nothing, and the animation events that call some of
        // them still find them
        foreach (var shaker in copy.GetComponentsInChildren<CameraControlAnimationEvents>(true)) {
            shaker.enabled = false;
        }

        foreach (var vibration in copy.GetComponentsInChildren<VibrationPlayer>(true)) {
            vibration.enabled = false;
        }

        foreach (var shaker in copy.GetComponentsInChildren<CameraShakeOnEnable>(true)) {
            shaker.enabled = false;
        }

        // Some parts go by the way the hero faces, and name the local hero for it, which the copy would go by too
        foreach (var flip in copy.GetComponentsInChildren<DeactivateOnParentScaleFlip>(true)) {
            if (flip.parent != null && !flip.parent.IsChildOf(copy.transform)) {
                flip.parent = character;
            }
        }

        foreach (var match in copy.GetComponentsInChildren<MatchXScaleSignOnEnable>(true)) {
            if (match.matchObject != null && !match.matchObject.IsChildOf(copy.transform)) {
                match.matchObject = character;
            }
        }

        copy.transform.SetParent(parent, false);
        Object.Destroy(holder);
        return copy.transform;
    }

    /// <summary>
    /// Sets whether the part with an index is on in a mask.
    /// </summary>
    private static void SetBit(byte[] mask, int index, bool value) {
        if (value) {
            mask[index / 8] |= (byte) (1 << (index % 8));
        } else {
            mask[index / 8] &= (byte) ~(1 << (index % 8));
        }
    }
}

/// <summary>
/// Watches one part of the local hero for being switched on and off, for <see cref="HeroChildEffects"/>.
/// </summary>
internal class HeroChildEffectWatcher : MonoBehaviour {
    /// <summary>
    /// The index of the part in the watched parts.
    /// </summary>
    [NonSerialized]
    public int Index;

    /// <summary>
    /// Called with the index of the part and whether it is on. Not serialized, so a copy of the part made for another
    /// player's character, which has this component too, watches nothing.
    /// </summary>
    [NonSerialized]
    public Action<int, bool>? Toggled;

    private void OnEnable() {
        Toggled?.Invoke(Index, true);
    }

    private void OnDisable() {
        Toggled?.Invoke(Index, false);
    }
}
