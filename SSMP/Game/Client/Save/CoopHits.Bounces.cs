using System;
using System.Collections.Generic;
using System.IO;
using GlobalEnums;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Events;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The things of the room that a player bounces off: the pods and bells that fling a player who strikes them from
/// above and swing on a strike from the side, and the balloons. Each is still there for the player who strikes it (see
/// <see cref="PersonalTypes"/>): the fling is theirs, and so is the moment after a strike in which some pods are shut
/// and let no one bounce off them, so that the partner who follows does not find one shut for them (USER 10-13, "样子
/// 同步，能用各管各的", like the floors that crumble and come back). What a strike looks like is no longer theirs alone:
/// the game of the player who struck one tells the other games in the room how, and they show it - its animation, its
/// swing and sound, and of what it sets off around it what only shows, like the dust it puffs, a shake of its own, the
/// platform it hangs from bobbing, or a part switched on that only draws; a pod that closes after a strike opens again
/// after the same time. Not what only the striker has: it does not shut there and flings nobody. Nor what does more
/// than show (see <see cref="OnlyShows"/>): its colliders, a sprite swapped for a part that explodes after a moment,
/// hurts whoever is near and shuts the pod, a blast that shakes the screen and the pad, the screen shake of the
/// strike, its spark, whose effect shakes the pad, and the pollen trail that follows the striker. Before, each game
/// showed its own player's strikes alone. A pod that a creature carries is found by the creature: the game that runs
/// it shows the room's own, the other a copy, which hangs and swings its pods by the physics as well (see
/// EntityInitializer.FindPodSwingBodies). The bells of a music box go their own way (see CoopSave.PodSequences).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// How a thing that a player bounces off was struck, as the other games show it.
    /// </summary>
    private enum BounceLook : byte {
        /// <summary>
        /// A pod struck from above, which flung the striker up.
        /// </summary>
        PodBounce,

        /// <summary>
        /// A pod struck so that it flung no one up, like from the side, which swings it.
        /// </summary>
        PodStrike,

        /// <summary>
        /// A balloon struck so that it raised no one, like from the side.
        /// </summary>
        BalloonStrike,

        /// <summary>
        /// A balloon struck from above, which raised the striker.
        /// </summary>
        BalloonBounce
    }

    /// <summary>
    /// The kinds of parts that a pod tells as it is struck or opens again whose answer only shows: its dust, its sound,
    /// a shake of its own and the bob of the platform it hangs from (see <see cref="OnlyShows"/>).
    /// </summary>
    private static readonly Type[] ShowingPartTypes = [
        typeof(ParticleSystemPool),
        typeof(PlayParticleEffects),
        typeof(AudioSource),
        typeof(PlayRandomAudioEvent),
        typeof(JitterSelfForTime),
        typeof(CurveOffsetAnimation)
    ];

    /// <summary>
    /// The kinds of components of an object that only draw it, so that switching it on or off only shows something
    /// (see <see cref="IsDrawnOnly"/>): what draws and animates it, and what switches it off again once it is drawn.
    /// </summary>
    private static readonly Type[] DrawingTypes = [
        typeof(Transform),
        typeof(Renderer),
        typeof(MeshFilter),
        typeof(tk2dBaseSprite),
        typeof(tk2dSpriteAnimator),
        typeof(Animator),
        typeof(ParticleSystem),
        typeof(DisableAfterTime),
        typeof(DeactivateAfter2dtkAnimation)
    ];

    /// <summary>
    /// Whether the hit that a pod is taking is one of this game's player, while it takes one, or null between hits. A
    /// pod tells of a bounce a moment later, as the frame ends, by the hit that it keeps as its last.
    /// </summary>
    private bool? _podHitByPlayer;

    /// <summary>
    /// The pods here that show a strike of the partner's shut, as the partner's game has them, each with how long until
    /// it shows opening again. They only look shut here, and still take this game's player.
    /// </summary>
    private readonly Dictionary<BouncePod, float> _shownShutPods = new();

    /// <summary>
    /// Whether sending or showing a strike on a thing that a player bounces off threw, which is only logged once.
    /// </summary>
    private bool _bounceLookFailed;

    /// <summary>
    /// Registers the hooks that send the strikes of this game's player on pods and balloons, and that open the pods
    /// shown shut again.
    /// </summary>
    private void RegisterBounceHooks() {
        AddHook(
            typeof(BouncePod).GetMethod(nameof(BouncePod.Hit), InstanceFlags, null, [typeof(HitInstance)], null),
            new Func<Func<BouncePod, HitInstance, IHitResponder.HitResponse>, BouncePod, HitInstance,
                IHitResponder.HitResponse>(OnPodHit)
        );
        AddHook(
            typeof(BouncePod).GetMethod(nameof(BouncePod.SendBounceHit), InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<BouncePod>, BouncePod>(OnPodBounceHit)
        );
        AddHook(
            typeof(BouncePod).GetMethod(nameof(BouncePod.SendInertHit), InstanceFlags, null, [typeof(float)], null),
            new Action<Action<BouncePod, float>, BouncePod, float>(OnPodInertHit)
        );
        AddHook(
            typeof(BouncePod).GetMethod("Update", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<BouncePod>, BouncePod>(OnPodUpdate)
        );
        AddHook(
            typeof(BounceBalloon).GetMethod(
                nameof(BounceBalloon.Hit), InstanceFlags, null, [typeof(HitInstance)], null
            ),
            new Func<Func<BounceBalloon, HitInstance, IHitResponder.HitResponse>, BounceBalloon, HitInstance,
                IHitResponder.HitResponse>(OnBalloonHit)
        );
    }

    /// <summary>
    /// Hook for a pod taking a hit, which notes whose hit it is while it does (see <see cref="TellPodStrike"/>).
    /// </summary>
    private IHitResponder.HitResponse OnPodHit(
        Func<BouncePod, HitInstance, IHitResponder.HitResponse> orig,
        BouncePod self,
        HitInstance hit
    ) {
        var last = _podHitByPlayer;
        _podHitByPlayer = hit.IsHeroDamage && !RemoteAttackComponent.IsRemoteAttack(hit.Source);
        try {
            return orig(self, hit);
        } finally {
            _podHitByPlayer = last;
        }
    }

    /// <summary>
    /// Hook for a pod telling that it was struck from above and flung the striker up, which goes to the other games in
    /// the room if this game's player struck it, with the way the player faced, which swings it.
    /// </summary>
    private void OnPodBounceHit(Action<BouncePod> orig, BouncePod self) {
        orig(self);

        var hero = HeroController.instance;
        TellPodStrike(self, BounceLook.PodBounce, hero != null && hero.cState.facingRight ? -1f : 1f);
    }

    /// <summary>
    /// Hook for a pod telling that it was struck so that it flung no one up, which goes to the other games in the room
    /// if this game's player struck it, with the way it swings.
    /// </summary>
    private void OnPodInertHit(Action<BouncePod, float> orig, BouncePod self, float direction) {
        orig(self, direction);
        TellPodStrike(self, BounceLook.PodStrike, direction);
    }

    /// <summary>
    /// Tells the other games in the room how this game's player struck a pod: by the hit it is taking, or, as it tells
    /// of one at the end of the frame, by the hit it keeps as its last. Not a bell of a music box, which is told
    /// otherwise (CoopSave.PodSequences), and which also rings here for a strike of the partner.
    /// </summary>
    /// <param name="pod">The pod.</param>
    /// <param name="look">How it was struck.</param>
    /// <param name="direction">The way it swings, as a factor of how fast it swings across.</param>
    private void TellPodStrike(BouncePod pod, BounceLook look, float direction) {
        try {
            if (!(_podHitByPlayer ?? pod.lastHit.IsHeroDamage) || !HasMembers || !_netClient.IsConnected ||
                pod.GetComponentInParent<BouncePodSequence>(true) != null) {
                return;
            }

            SendBounceLook(pod, look, direction, 0f);
        } catch (Exception e) {
            LogBounceLookError(e);
        }
    }

    /// <summary>
    /// Hook for a balloon taking a hit, which goes to the other games in the room if this game's player struck it while
    /// it was up and it answered, with where the player was across: what it shows of raising a player is drawn there.
    /// It raises the player who struck it from above with the needle, if that hit no enemy, as the game has it.
    /// </summary>
    private IHitResponder.HitResponse OnBalloonHit(
        Func<BounceBalloon, HitInstance, IHitResponder.HitResponse> orig,
        BounceBalloon self,
        HitInstance hit
    ) {
        var wasUp = self.isInflated;
        var response = orig(self, hit);
        if (!wasUp || response.response == IHitResponder.Response.None || !hit.IsHeroDamage) {
            return response;
        }

        try {
            if (HasMembers && _netClient.IsConnected && !RemoteAttackComponent.IsRemoteAttack(hit.Source)) {
                var raised = hit.AttackType == AttackTypes.Nail &&
                             hit.GetHitDirection(HitInstance.TargetType.Regular) == HitInstance.HitDirection.Down &&
                             (hit.Source == null || !hit.Source.TryGetComponent<DamageEnemies>(out var damager) ||
                              !damager.DidHitEnemy);
                var hero = HeroController.instance;
                SendBounceLook(
                    self,
                    raised ? BounceLook.BalloonBounce : BounceLook.BalloonStrike,
                    0f,
                    hero != null ? hero.transform.position.x : self.transform.position.x
                );
            }
        } catch (Exception e) {
            LogBounceLookError(e);
        }

        return response;
    }

    /// <summary>
    /// Sends the members in the room how this game's player struck a pod or a balloon of the room. Not one in a place
    /// that each player has to themselves, nor one that a room spawned or a part of a creature that both games keep a
    /// copy of, which the others don't find by its place or have switched off there.
    /// </summary>
    /// <param name="thing">The pod or the balloon.</param>
    /// <param name="look">How it was struck.</param>
    /// <param name="direction">For a pod, the way it swings.</param>
    /// <param name="x">For a balloon, where the player was across.</param>
    private void SendBounceLook(Component thing, BounceLook look, float direction, float x) {
        var target = thing.gameObject;
        if (!IsAnyMemberInRoom() || PersonalPlaces.Contains(target)) {
            return;
        }

        // A part of a creature that both games keep goes by the creature and its place under it: the game that runs
        // the creature shows the room's own, the other the copy, which is another object. Anything else that a room
        // spawned may not be found by its place in the others' games
        Entity.Entity? creature = null;
        var path = "";
        for (var current = target.transform; current != null; current = current.parent) {
            if (TryGetEntity(current.gameObject, out var entity, out _)) {
                creature = entity;
                path = ScenePath.GetUnder(target.transform, current);
                break;
            }

            if (current.name.Contains("(Clone)") || EntityProcessor.IsRegistered(current.gameObject)) {
                return;
            }
        }

        var scene = target.scene.name;
        if (creature == null && !TryGetPlace(target, out scene, out path)) {
            return;
        }

        var type = thing.GetType();
        var index = Array.IndexOf(target.GetComponents(type), thing);
        if (index is < 0 or > byte.MaxValue || type.FullName == null) {
            return;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) look);
        writer.Write(direction);
        writer.Write(x);
        writer.Write(creature != null);
        writer.Flush();

        SendToRoomMembers(new CoopHitUpdate {
            Kind = CoopHitKind.BounceLook,
            EntityId = creature?.Id ?? 0,
            Scene = scene,
            Path = path,
            Responder = type.FullName,
            Index = (byte) index,
            Hit = stream.ToArray()
        });
        NoteTraffic(scene, path, $"sent a strike ({look}) on {type.FullName}");
    }

    /// <summary>
    /// Shows a strike of the partner on a pod or a balloon here, the way it looked in their game, without what only
    /// the striker has (see <see cref="CoopHits"/>).
    /// </summary>
    private void ShowBounceLook(CoopHitUpdate update) {
        Type? type = null;
        if (update.Responder == typeof(BouncePod).FullName) {
            type = typeof(BouncePod);
        } else if (update.Responder == typeof(BounceBalloon).FullName) {
            type = typeof(BounceBalloon);
        }

        if (type == null) {
            NotReplayed(update, "strike", "that kind of object is unknown here");
            return;
        }

        BounceLook look;
        float direction, x;
        bool onCreature;
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            look = (BounceLook) reader.ReadByte();
            direction = reader.ReadSingle();
            x = reader.ReadSingle();
            onCreature = reader.ReadBoolean();
        } catch (Exception e) {
            Logger.Warn($"Could not read a strike of the partner on {update.Path}: {e.Message}");
            return;
        }

        // A part of a creature is found under the creature on show here: the room's own where this game runs it, the
        // copy where the other game does
        GameObject? target;
        if (onCreature) {
            if (FindEntity(update.EntityId) is not { } entity) {
                NotReplayed(update, "strike", $"the creature {update.EntityId} is not here");
                return;
            }

            var shown = entity.Object.Host != null && entity.Object.Host.activeInHierarchy
                ? entity.Object.Host
                : entity.Object.Client;
            target = shown != null ? ScenePath.FindUnder(shown.transform, update.Path) : null;
        } else {
            target = FindPlace(update);
        }

        if (target == null) {
            NotReplayed(update, "strike", "the object is not here");
            return;
        }

        var components = target.GetComponents(type);
        if (update.Index >= components.Length) {
            NotReplayed(update, "strike", $"the object has only {components.Length} parts of that kind here");
            return;
        }

        string? notShown;
        try {
            notShown = components[update.Index] switch {
                BouncePod pod => ShowPodStrike(pod, look, direction),
                BounceBalloon balloon => ShowBalloonStrike(balloon, look, x),
                _ => "that kind of object is unknown here"
            };
        } catch (Exception e) {
            LogBounceLookError(e);
            NoteTraffic(update.Scene, update.Path, $"got a strike on {update.Responder}, whose showing failed");
            return;
        }

        if (notShown != null) {
            NotReplayed(update, "strike", notShown);
            return;
        }

        NoteReplayed(update, "strike", true);
    }

    /// <summary>
    /// Shows a pod struck by the partner: its animation and sound, of what it sets off around it as it is struck what
    /// only shows (see <see cref="OnlyShows"/>), and its swing, if it swings freely here. One that closes after a
    /// strike shows opening again after the same time (see <see cref="OnPodUpdate"/>), unless it is shut for this
    /// game's player already, which it opens by itself from.
    /// </summary>
    /// <returns>Why it was not shown, or null if it was.</returns>
    private string? ShowPodStrike(BouncePod pod, BounceLook look, float direction) {
        if (!pod.gameObject.activeInHierarchy || pod.isBroken || !pod.IsActive) {
            return "it is put away here";
        }

        if (pod.GetComponentInParent<BouncePodSequence>(true) != null) {
            return "it is a bell of a music box";
        }

        if (pod.isHarpoonHooked) {
            return "this game's player is hooked onto it";
        }

        var bounce = look == BounceLook.PodBounce;
        pod.PlayAnim(bounce ? pod.bounceAnim : pod.hitAnim, false);
        pod.bounceSounds.SpawnAndPlayOneShot(pod.transform.position);
        ShowEffects(bounce ? pod.onBounceHit : pod.onInertHit);

        // A body that is held where it is, like one of a copy, would go on at that speed for good
        if (pod.bounceReactBody != null && pod.bounceReactBody.bodyType == RigidbodyType2D.Dynamic) {
            var velocity = bounce ? pod.bounceReactVelocity : pod.hitReactVelocity;
            velocity.x *= direction;
            pod.bounceReactBody.linearVelocity = velocity;
        }

        if (pod.hitInertTime > 0f && pod.inertTimeLeft <= 0f) {
            ForgetGoneShutPods();
            _shownShutPods[pod] = pod.hitInertTime;
        }

        return null;
    }

    /// <summary>
    /// Hook for a pod's frame, which has a pod shown shut for a strike of the partner show opening again once as long
    /// has gone by as it is shut in their game, counted while it runs, as the game counts its own and opens it, also
    /// one switched off meanwhile. One struck by this game's player since is shut for them too, and opens by itself.
    /// </summary>
    private void OnPodUpdate(Action<BouncePod> orig, BouncePod self) {
        orig(self);
        if (_shownShutPods.Count == 0 || !_shownShutPods.TryGetValue(self, out var left)) {
            return;
        }

        if (self.inertTimeLeft > 0f) {
            _shownShutPods.Remove(self);
            return;
        }

        left -= Time.deltaTime;
        if (left > 0f) {
            _shownShutPods[self] = left;
            return;
        }

        _shownShutPods.Remove(self);
        ShowPodOpening(self);
    }

    /// <summary>
    /// Shows a pod opening again: its animation, and of what it sets off what only shows.
    /// </summary>
    private void ShowPodOpening(BouncePod pod) {
        try {
            pod.PlayAnim(pod.inertRecoverAnim, false);
            ShowEffects(pod.onRecover);
        } catch (Exception e) {
            LogBounceLookError(e);
        }
    }

    /// <summary>
    /// Shows the pods shown shut for a strike of the partner opening at once, as the games part, rather than leaving
    /// them shut-looking until their room loads again. Not those shut for this game's player, which open by themselves.
    /// </summary>
    private void OpenShownShutPods() {
        foreach (var pod in _shownShutPods.Keys) {
            if (pod != null && pod.inertTimeLeft <= 0f) {
                ShowPodOpening(pod);
            }
        }

        _shownShutPods.Clear();
    }

    /// <summary>
    /// Lets go of the pods shown shut that went with their room.
    /// </summary>
    private void ForgetGoneShutPods() {
        if (_shownShutPods.Count == 0) {
            return;
        }

        List<BouncePod>? gone = null;
        foreach (var pod in _shownShutPods.Keys) {
            if (pod == null) {
                (gone ??= []).Add(pod!);
            }
        }

        if (gone == null) {
            return;
        }

        foreach (var pod in gone) {
            _shownShutPods.Remove(pod);
        }
    }

    /// <summary>
    /// Shows a balloon struck by the partner while it is up here: its bob, sound and dust, and, if it raised them, what
    /// it shows of that (see <see cref="OnlyShows"/>) where they were across.
    /// </summary>
    /// <returns>Why it was not shown, or null if it was.</returns>
    private static string? ShowBalloonStrike(BounceBalloon balloon, BounceLook look, float x) {
        if (!balloon.gameObject.activeInHierarchy || !balloon.isInflated) {
            return "it is not up here";
        }

        if (balloon.isHarpoonHooked) {
            return "this game's player is hooked onto it";
        }

        if (balloon.animator != null) {
            balloon.animator.Play(BounceBalloon._bounceAnim, 0, 0f);
        }

        balloon.bounceSound.SpawnAndPlayOneShot(balloon.transform.position);
        if (balloon.hitParticles != null) {
            balloon.hitParticles.PlayParticles();
        }

        if (look != BounceLook.BalloonBounce) {
            return null;
        }

        ShowEffects(balloon.OnBounce);
        if (balloon.bounceEffects != null) {
            var effects = balloon.bounceEffects.transform;
            var place = effects.position;
            effects.position = new Vector3(x, place.y, place.z);
        }

        return null;
    }

    /// <summary>
    /// Plays of what a thing sets off as it is struck or opens again what only shows (see <see cref="OnlyShows"/>):
    /// what else it tells is held off while it does, and then put back the way it was.
    /// </summary>
    private static void ShowEffects(UnityEvent? look) {
        if (look == null) {
            return;
        }

        List<(int Index, UnityEventCallState State)>? held = null;
        for (var i = 0; i < look.GetPersistentEventCount(); i++) {
            if (OnlyShows(look.GetPersistentTarget(i))) {
                continue;
            }

            (held ??= []).Add((i, look.GetPersistentListenerState(i)));
            look.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        try {
            look.Invoke();
        } finally {
            if (held != null) {
                foreach (var (index, state) in held) {
                    look.SetPersistentListenerState(index, state);
                }
            }
        }
    }

    /// <summary>
    /// Whether what a pod or a balloon tells a part of as it is struck or opens again only shows: a part that plays
    /// dust or a sound, shakes itself or bobs a platform (see <see cref="ShowingPartTypes"/>), or an object switched on
    /// or off that only draws. Not what switches a collider, which is whether it takes a player; nor a sprite switched
    /// off, which some swap for a part that does more; nor a state machine told something; nor an object that does
    /// more than draw, like the part of a pod that explodes after a moment, hurts whoever is near and shuts the pod, or
    /// a blast that shakes the screen and the pad as it plays.
    /// </summary>
    private static bool OnlyShows(Object? target) {
        return target switch {
            GameObject gameObject => IsDrawnOnly(gameObject),
            Component component => Array.Exists(ShowingPartTypes, type => type.IsInstanceOfType(component)),
            _ => false
        };
    }

    /// <summary>
    /// Whether everything on an object and under it only draws it (see <see cref="DrawingTypes"/>).
    /// </summary>
    private static bool IsDrawnOnly(GameObject gameObject) {
        foreach (var component in gameObject.GetComponentsInChildren<Component>(true)) {
            if (component == null || !Array.Exists(DrawingTypes, type => type.IsInstanceOfType(component))) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Logs that sending or showing a strike on a pod or a balloon threw, the first time.
    /// </summary>
    private void LogBounceLookError(Exception e) {
        if (_bounceLookFailed) {
            return;
        }

        _bounceLookFailed = true;
        Logger.Error($"Could not show a strike on a pod or a balloon in the other games:\n{e}");
    }
}
