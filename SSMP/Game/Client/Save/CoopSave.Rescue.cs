using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Hooks;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using GlobalSettings;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Being pulled back up by the other player after dying, instead of waking at a bench. A death holds just before the
/// game takes the player to their bench, which is late enough that the cocoon has been placed and the money and silk
/// have been moved into it. The other player sees that cocoon in their own game and can break it open, and the player
/// who died is put back on their feet where they fell with half their health. Waiting is a choice: giving up goes to
/// the bench as always, and the cocoon stops being shown to the other player the moment waiting ends.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How many hits it takes to open the cocoon of the other player.
    /// </summary>
    private const int RescueHits = 5;

    /// <summary>
    /// How long a player waits to be pulled back up before going to their bench anyway, in seconds. Without this a
    /// player whose partner never noticed would wait for as long as the game runs.
    /// </summary>
    private const float RescueWaitTime = 45f;

    /// <summary>
    /// How long, in seconds, at the start of a wait during which the key that gives up on it is not listened to at
    /// all. A death happens in the middle of a fight, and the first moments after one are the likeliest to carry a
    /// press that was meant for something else entirely.
    /// </summary>
    private const float LeaveKeyDeadTime = 0.5f;

    /// <summary>
    /// The name of the partner whose hits could still pull the local player back up, remembered so that the line
    /// shown during a wait can name them even on a frame when the rest of the two-player save update does not run.
    /// </summary>
    private string? _rescuePartnerName;

    /// <summary>
    /// The source of the short invulnerability of a player who was just pulled back up, so that the hit that killed
    /// them cannot land again in the same instant on half the health.
    /// </summary>
    private static readonly object RescueInvulnerability = new();

    /// <summary>
    /// How long that invulnerability lasts, in seconds.
    /// </summary>
    private const float RescueInvulnerabilityTime = 2f;

    /// <summary>
    /// The name of the one FSM on a cocoon that is kept. It is the game's own name for it, matched at runtime
    /// rather than written into the prefab, so the cocoon shown for the partner decides how it looks the same way
    /// the player's own cocoon does.
    /// </summary>
    private const string BreakFsmName = "Break";

    /// <summary>
    /// The name of the state in that FSM which hands out the money and silk the cocoon holds. It is taken out of
    /// the cocoon shown for the partner, because what it pays out belongs to the player lying in it and not to the
    /// one looking at it.
    /// </summary>
    private const string ReturnCurrencyStateName = "Return Currency";

    /// <summary>
    /// The sprite the mod already uses elsewhere to take a player off the screen without touching anything else about
    /// them. It is a stray one-pixel sprite out of the game's own collection.
    /// </summary>
    private const string HiddenSpriteName = "wall_puff0004";

    /// <summary>
    /// The clip played on a body that was taken off the screen to bring it back, for the case where the player it
    /// belongs to never sends another animation of their own.
    /// </summary>
    private const string IdleClipName = "Idle";

    /// <summary>
    /// The name of the FSM that plays a death out on the object the death spawns for it. Every one of the five of
    /// those objects - the ordinary one, the cursed one, the non-lethal one, the memory one and the frost one -
    /// carries one FSM by this name, which is what makes it the way to find the object at all.
    /// </summary>
    private const string DeathAnimFsmName = "Hero Death Anim";

    /// <summary>
    /// How long the screen takes to come back, in seconds.
    /// </summary>
    private const float RescueFadeTime = 0.5f;

    /// <summary>
    /// How long to wait for the death to finish playing itself out before the screen is brought back anyway, in
    /// seconds. The death's own ending takes a little over four.
    /// </summary>
    private const float DeathEffectWaitTime = 10f;

    /// <summary>
    /// How the wait of the local player ended.
    /// </summary>
    private enum RescueOutcome {
        /// <summary>
        /// Still waiting to be pulled back up.
        /// </summary>
        Waiting,

        /// <summary>
        /// The other player opened the cocoon.
        /// </summary>
        Rescued,

        /// <summary>
        /// The player gave up, nobody could reach them, or waiting ran out of time.
        /// </summary>
        Ended
    }

    /// <summary>
    /// The death of the local player that is waiting to be undone.
    /// </summary>
    private sealed class PendingRescue {
        public PendingRescue(string scene, Vector2 position) {
            Scene = scene;
            Position = position;
        }

        /// <summary>
        /// The room the player died in.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// Where the cocoon was left.
        /// </summary>
        public Vector2 Position { get; }

        /// <summary>
        /// When the wait started.
        /// </summary>
        public float StartTime { get; } = Time.unscaledTime;

        /// <summary>
        /// How far the other player got in opening the cocoon.
        /// </summary>
        public int Hits { get; set; }

        /// <summary>
        /// How the wait ended, or <see cref="RescueOutcome.Waiting"/> while it has not.
        /// </summary>
        public RescueOutcome Outcome { get; set; } = RescueOutcome.Waiting;

        /// <summary>
        /// Whether the screen has been given back to the player since the death darkened it.
        /// </summary>
        public bool ScreenBack { get; set; }

        /// <summary>
        /// What was playing in the room when the player died, so that it can be put back on if they are pulled up.
        /// A death changes the music to its own, and what normally changes it back is the loading of the room the
        /// bench is in - which a player who never goes there never gets.
        /// </summary>
        public MusicCue? Music { get; set; }

        /// <summary>
        /// What names this death apart from every other one, so that what the partner says about opening a cocoon
        /// can be told to be about this cocoon and not the last one.
        ///
        /// These messages are resent until they arrive and are never dropped in favour of a newer one, which is
        /// right for them - none of them may be lost - but it means one written about a death that is already over
        /// can still turn up afterwards. Without a name on it, the last hit of the last cocoon opened the next one
        /// the moment it appeared, which in a room that kills a player where they stand is a loop with no way out.
        /// </summary>
        public ulong Key { get; set; }

        /// <summary>
        /// Whether what killed the player was the room rather than a creature - lava, spikes, a fall.
        ///
        /// It decides where they are put when they are pulled back up. Everywhere else that is where they fell, which
        /// is what makes being pulled up worth anything; here it is the inside of the thing that killed them, and
        /// they would die again in the time it takes to stand up.
        /// </summary>
        public bool KilledByTheRoom { get; set; }
    }

    /// <summary>
    /// The cocoon of the partner, shown in this game only while they are waiting to be pulled back up.
    /// </summary>
    private sealed class RescueTarget {
        public RescueTarget(ushort playerId, string scene, GameObject cocoon) {
            PlayerId = playerId;
            Scene = scene;
            Cocoon = cocoon;
        }

        /// <summary>
        /// The player who is waiting.
        /// </summary>
        public ushort PlayerId { get; }

        /// <summary>
        /// The room their cocoon is in.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The object that stands in for their cocoon.
        /// </summary>
        public GameObject Cocoon { get; }

        /// <summary>
        /// How many hits it has taken.
        /// </summary>
        public int Hits { get; set; }

        /// <summary>
        /// What the player who is waiting called this death of theirs, sent back with every hit so that a hit cannot
        /// be taken as being about a later death of the same player.
        /// </summary>
        public ulong Key { get; set; }
    }

    /// <summary>
    /// Counts the hits of the local player on the cocoon of the partner. The cocoon of the game's own player is never
    /// this: that one keeps its own components and belongs to the player it was left by.
    /// </summary>
    private sealed class RescueCocoonHits : MonoBehaviour, IHitResponder {
        /// <summary>
        /// Called for every hit that lands.
        /// </summary>
        public Action? Hits;

        /// <summary>
        /// The frame an effect was last played on, so that an attack which lands twice in one frame does not stack
        /// two of them. The game's own hit effects keep the same guard.
        /// </summary>
        private int _lastEffectFrame = -1;

        /// <inheritdoc/>
        public IHitResponder.HitResponse Hit(HitInstance damageInstance) {
            Hits?.Invoke();
            PlayHitEffect(damageInstance);

            // Not None. That is the answer for "there was nothing there", so the nail passes straight through with
            // no impact, no sound and nothing to bounce off - which is why hitting the cocoon felt like hitting air
            // and a downward strike would not pogo. GenericHit is what something solid but undamageable answers.
            return IHitResponder.Response.GenericHit;
        }

        /// <summary>
        /// Plays what the game plays when a hit lands on something that takes no damage from it: the shake the
        /// controller rumbles along with, and the spark, thrown the way the blow was.
        ///
        /// It has to be done here because nothing else will. Every hit effect in this game is played by the health
        /// of the thing that was hit - it is <c>HealthManager.TakeDamage</c> that asks the effects to play, through
        /// the receiver written into each enemy - and a cocoon has no health and is no enemy. So the swing landed,
        /// and counted, and looked and felt like nothing at all.
        ///
        /// What is played is the game's own, not an imitation: both come from the one set of effects the whole game
        /// shares for a hit that does no damage, which is exactly what a hit on this is. The stronger effects are no
        /// use here - each enemy carries its own, written into it one by one, and a cocoon has none to carry.
        /// </summary>
        private void PlayHitEffect(HitInstance hit) {
            if (_lastEffectFrame == Time.frameCount) {
                return;
            }

            _lastEffectFrame = Time.frameCount;

            try {
                Effects.WeakHitEffectShake.DoShake(this, true);

                if (Effects.WeakHitEffectPrefab is not { } spark) {
                    return;
                }

                // Zero is what the game passes here, and there is no name for it to pass: the answer is the angle
                // the blow came in at, which is what the spark is turned to.
                var angle = hit.GetHitDirectionAsAngle((HitInstance.TargetType) 0);

                // Where the thing is rather than where its feet are. A cocoon is drawn from the ground up, so its
                // own position is the bottom of it, and a spark struck there would go off under the blow.
                var at = GetComponentInChildren<Collider2D>() is { } body
                    ? (Vector3) body.bounds.center
                    : transform.position;

                spark.Spawn(at, Quaternion.Euler(0f, 0f, angle));
            } catch (Exception e) {
                Logger.Warn($"Could not play the effect of a hit on the cocoon of the partner: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The death of the local player that waits to be undone, or null while no death is waiting.
    /// </summary>
    private PendingRescue? _rescue;

    /// <summary>
    /// The name given to the last death of the local player that waited to be pulled back up. Every death takes the
    /// next one, so that what the partner says about one cocoon is never taken as being about another.
    /// </summary>
    private ulong _lastRescueKey;

    /// <summary>
    /// The cocoon of the partner that the local player can open, or null while they are not waiting.
    /// </summary>
    private RescueTarget? _rescueTarget;

    /// <summary>
    /// The cocoon shown to the player who is lying in it, or null while none is.
    ///
    /// The game makes no such object at the moment of a death. A death writes down where the cocoon is and what it
    /// holds, and the object itself is made when the room is next loaded - which is what a player walking back to
    /// where they died sees. A held death never loads anything, so the player waiting to be pulled up was lying in
    /// a cocoon that was nowhere on their screen.
    /// </summary>
    private GameObject? _rescueOwnCocoon;

    /// <summary>
    /// The partner who is lying dead waiting to be pulled back up, or null while they are not. This is kept apart from
    /// <see cref="_rescueTarget"/> on purpose: that one only exists while their cocoon is in the room the local player
    /// is standing in, and a partner who died somewhere else is still in no position to pull anyone up.
    /// </summary>
    private ushort? _partnerWaitingRescue;

    /// <summary>
    /// The room the waiting partner died in, or empty while none is waiting. Remembered even while the local player is
    /// somewhere else, so that walking into that room later still shows their cocoon: an offer that was thrown away
    /// because it arrived while the player was elsewhere could never be shown at all.
    /// </summary>
    private string _partnerCocoonScene = "";

    /// <summary>
    /// Where in that room their cocoon is.
    /// </summary>
    private Vector2 _partnerCocoonPosition;

    /// <summary>
    /// What the partner called the death their cocoon belongs to, sent back with every hit on it.
    /// </summary>
    private ulong _partnerRescueKey;

    /// <summary>
    /// Whether a failure of this has been logged already.
    /// </summary>
    private bool _rescueFailed;

    /// <summary>
    /// Takes over how a death of the local player plays out.
    /// </summary>
    private void RegisterRescueHooks() {
        EventHooks.HeroControllerDieWrapper = WrapDeath;
        _deathAnnouncementHook = HoldBackTheNewsOfADeath();
    }

    /// <summary>
    /// What a death of the player announces to everything in the room that answers to one. Fifty-seven objects in the
    /// game listen for it and almost all of them are bosses; nothing on the player themselves does, so holding it
    /// back takes nothing away from the death itself.
    /// </summary>
    private const string DeathAnnouncement = "HORNET DEATH";

    /// <summary>
    /// The hook on the one call that carries that announcement.
    /// </summary>
    private Hook? _deathAnnouncementHook;

    /// <summary>
    /// Stops one player's death from telling the room the fight is over while the other player is still in it.
    ///
    /// A boss that hears this stops fighting and celebrates, and what ends the celebration is the loading of the room
    /// the bench is in. In two players that room is never loaded while one of them is still standing, so the boss
    /// stood there celebrating over a player who was being pulled back up, and the one still fighting had nothing
    /// left to fight. Held back, the boss simply carries on with whoever is left.
    /// </summary>
    private Hook? HoldBackTheNewsOfADeath() {
        try {
            var method = typeof(EventRegister).GetMethod(
                nameof(EventRegister.SendEvent),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(string), typeof(GameObject)],
                null
            );

            if (method == null) {
                Logger.Error("Could not find how a death tells the room, so a boss will celebrate over one player");

                return null;
            }

            return new Hook(method, (Action<Action<string, GameObject>, string, GameObject>) OnDeathAnnounced);
        } catch (Exception e) {
            Logger.Error($"Could not hold back the news of a death:\n{e}");

            return null;
        }
    }

    /// <summary>
    /// Lets the news of a death through only when there is nobody left in the room it would be news to.
    /// </summary>
    /// <remarks>
    /// Decided by the very thing that decides where this death is going: a death that can still wait to be pulled
    /// back up is not the end of anything, and a death that cannot is the one that goes to the bench, which is the
    /// same as saying both players are down. Keeping one answer rather than two means the boss and the bench can
    /// never disagree about whether the fight is over.
    ///
    /// Only the death of the player at this screen ever reaches here. The death of the partner is played out as
    /// particles and a cocoon and nothing else - it never puts up the object that carries this announcement - so
    /// there is no second case to get right.
    /// </remarks>
    /// <param name="orig">The original call.</param>
    /// <param name="eventName">The announcement.</param>
    /// <param name="excludeTarget">What is not to be told, which is the caller's own business.</param>
    private void OnDeathAnnounced(Action<string, GameObject> orig, string eventName, GameObject excludeTarget) {
        try {
            if (eventName == DeathAnnouncement && CanWaitForRescue()) {
                Logger.Info("Not telling the room the player died, because their teammate is still fighting in it");

                return;
            }
        } catch (Exception e) {
            // Whatever goes wrong in deciding this, the game's own call has to happen: the alternative is a death
            // that half the room never hears about for a reason that was never about the room
            Logger.Warn($"Could not decide whether to hold back the news of a death: {e.Message}");
        }

        orig(eventName, excludeTarget);
    }



    /// <summary>
    /// Holds a death of the local player before it takes them to their bench, so that the partner has a chance to pull
    /// them back up. Anything that leaves no cocoon to open, or leaves nobody to open it, plays out untouched.
    /// </summary>
    /// <param name="death">The coroutine of the game that plays out the death.</param>
    /// <param name="nonLethal">Whether the death was non-lethal.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private IEnumerator WrapDeath(IEnumerator death, bool nonLethal, bool frostDeath) {
        // A non-lethal death leaves no cocoon: the game skips that whole part of its own sequence, so there would be
        // nothing for the partner to open and the player would wait for something that cannot come. It also takes
        // nobody to a bench, so a player who is waiting is not told anything: whoever this is can still reach them.
        if (nonLethal) {
            return death;
        }

        // What the player was when they died, because a death that follows another one too closely to have been
        // dealt by anything is a different problem from a death that something dealt, and from the outside the two
        // look exactly alike. Invulnerability lasts two seconds after standing up, so anything under that was not a
        // hit, whatever it looked like.
        SayWhatTheDeathFound(frostDeath);

        if (!CanWaitForRescue()) {
            TellPartnerNobodyIsComing();

            return death;
        }

        // Before the death has run a single frame of itself, so that anything it is still showing afterwards can be
        // told apart from what the room was already showing
        NoteWhatIsOnAroundThePlayer();

        return HoldDeath(death);
    }

    /// <summary>
    /// Writes down what the player was when this death reached them.
    /// </summary>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private void SayWhatTheDeathFound(bool frostDeath) {
        try {
            var hero = HeroController.instance;
            var playerData = PlayerData.instance;
            var since = _lastStoodBackUpTime > 0f
                ? $"{Time.unscaledTime - _lastStoodBackUpTime:0.00}s after standing up"
                : "having not been pulled up before";

            Logger.Info(
                $"A death reached the player {since}, with " +
                $"{(playerData == null ? "?" : playerData.health.ToString())}/" +
                $"{(playerData == null ? "?" : playerData.maxHealth.ToString())} health, " +
                $"by the room: {(hero == null ? "?" : hero.cState.hazardDeath.ToString())}, " +
                $"by frost: {frostDeath}, " +
                $"already dead: {(hero == null ? "?" : hero.cState.dead.ToString())}, " +
                $"at {(hero == null ? "?" : hero.transform.position.ToString())}"
            );
        } catch (Exception e) {
            Logger.Warn($"Could not write down what the death found: {e.Message}");
        }
    }

    /// <summary>
    /// Tells a partner who is lying in their cocoon that nobody is coming, because the player who was to open it has
    /// just died themselves.
    ///
    /// Two deaths means two benches - that is the rule - and this is the half of it that was missing. The death of
    /// the second player already goes straight to their bench, because a player whose partner is waiting cannot wait
    /// themselves, but the first player was never told and lay there until their wait ran out of time: watching an
    /// empty room, with the enemies that killed them both still swinging at the spot they fell on.
    /// </summary>
    private void TellPartnerNobodyIsComing() {
        try {
            if (_partnerWaitingRescue is not { } playerId || GetCheckedPartner() is not { } partner ||
                partner.Id != playerId) {
                // Two players who both die and are both left lying there is exactly what this exists to stop, so
                // which of these three it was is worth a line
                Logger.Info(
                    "Not telling anybody that nobody is coming: " +
                    $"waiting partner: {_partnerWaitingRescue?.ToString() ?? "none"}, " +
                    $"checked partner: {GetCheckedPartner()?.Id.ToString() ?? "none"}"
                );

                return;
            }

            Logger.Info($"Telling {partner.Username} that nobody is coming, because this player has died as well");

            Send(new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.RescueLost
            });

            // Their cocoon goes now rather than when they answer: this player is on their way to a bench either way
            RemoveRescueTarget();
            _partnerWaitingRescue = null;
        } catch (Exception e) {
            LogRescueError(e);
        }
    }

    /// <summary>
    /// The partner died while this player was waiting to be pulled back up, so the wait ends and this player goes to
    /// their bench as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    private void OnRescueLost(ClientPlayerData player) {
        if (_rescue is not { Outcome: RescueOutcome.Waiting } rescue || GetCheckedPartner()?.Id != player.Id) {
            return;
        }

        rescue.Outcome = RescueOutcome.Ended;
        Logger.Info($"{player.Username} died as well, so this wait is over and both go to their benches");
        Chat(
            Lang.Pick(
                $"{player.Username} died as well, so you are both going back to your bench.",
                $"{player.Username} 也死了，两个人一起回长椅。"
            )
        );
    }

    /// <summary>
    /// Whether the local player can be pulled back up at all: the saves are checked with a partner who is here, and
    /// that partner is not lying dead themselves, since two players waiting for each other would wait forever.
    /// </summary>
    private bool CanWaitForRescue() {
        if (!_netClient.IsConnected || !IsInGame() || GetCurrentMarker() == null) {
            return false;
        }

        if (GetCheckedPartner() is not { } partner) {
            return false;
        }

        // Both players dead means nobody is left to open a cocoon, so this one goes to the bench the way it always did
        if (_partnerWaitingRescue == partner.Id) {
            return false;
        }

        return true;
    }

    /// <summary>
    /// When the local player was last pulled back up, in unscaled seconds, or 0 if they never were.
    /// </summary>
    private float _lastStoodBackUpTime;

    /// <summary>
    /// Plays the death out to the point where the cocoon has been placed, holds there while the partner has a chance
    /// to open it, and then either puts the player back on their feet or lets the death finish as it always did.
    /// </summary>
    /// <param name="death">The coroutine of the game that plays out the death.</param>
    private IEnumerator HoldDeath(IEnumerator death) {
        // One step covers everything the game does before its first wait: the cocoon is placed, the money and the silk
        // are moved into it and the body is hidden. Taking the player to their bench is the first thing after it, so
        // this is the last moment at which a death can still be undone.
        bool running;
        try {
            running = death.MoveNext();
        } catch (Exception e) {
            LogRescueError(e);
            yield break;
        }

        if (!running) {
            yield break;
        }

        if (!StartRescueWait()) {
            // Nothing is waiting, so the death finishes the way it started
            yield return death.Current;

            while (death.MoveNext()) {
                yield return death.Current;
            }

            yield break;
        }

        // The death is allowed to play its own ending out - it belongs to the game and it is worth seeing - and the
        // screen is given back as soon as it is over, so that the rest of the wait is spent looking at the room, the
        // cocoon and the line that explains what is happening rather than at nothing at all.
        if (_rescue is { } started) {
            MonoBehaviourUtil.Instance.StartCoroutine(BringScreenBackAfterDeath(started));
        }

        // Running out of time is decided here rather than only frame by frame next door, because the update that runs
        // frame by frame sits behind early returns of its own: a marker that is gone for a moment is enough to stop it,
        // and then this would hold the death for as long as the game runs. A held death has already switched the pause
        // menu off, so there would be nothing left to do about it but kill the game. This loop is the one thing that is
        // certainly still running while a death is held, so the way out that must always work lives in it.
        // Seeded with what the key is doing right now rather than with "not held". A player dies in the middle of
        // doing things, and a key that was already down when they died is not them asking to give up on being saved.
        var leaveHeld = _modSettings.Keybinds.CoopLeave.IsPressed;

        while (_rescue is { Outcome: RescueOutcome.Waiting } waiting) {
            if (Time.unscaledTime - waiting.StartTime >= RescueWaitTime) {
                Logger.Info("Waited the whole time to be pulled back up and nobody came, going to the bench");
                waiting.Outcome = RescueOutcome.Ended;

                break;
            }

            // The line and the way out live here for the same reason the time limit does: this is the one thing that
            // is certainly still running while a death is held. They used to live next door, behind those early
            // returns, so a player could be left staring at a cocoon with nothing on screen telling them anything and
            // no way out but to sit through the whole wait.
            ShowTheWayOutOfTheWait(waiting);

            var leaveNow = _modSettings.Keybinds.CoopLeave.IsPressed;
            if (leaveNow && !leaveHeld && Time.unscaledTime - waiting.StartTime >= LeaveKeyDeadTime) {
                Logger.Info("Gave up waiting to be pulled back up and went to the bench");
                waiting.Outcome = RescueOutcome.Ended;

                break;
            }

            leaveHeld = leaveNow;

            yield return null;
        }

        var waited = _rescue;
        var rescued = waited is { Outcome: RescueOutcome.Rescued };
        var screenBack = waited is { ScreenBack: true };

        // Said here because this is the one place that knows which of the ways out of a wait was taken, and a player
        // left standing in neither their own body nor at their bench has no way of telling us which it was
        Logger.Info($"The wait to be pulled back up is over as '{waited?.Outcome}', with the screen back: {screenBack}");
        EndRescueWait();

        if (rescued && waited != null && TryRevive(HeroController.instance, waited)) {
            yield break;
        }

        Logger.Info("Letting the death finish and take the player to their bench");

        // The bench this death is about to take the player to is reached with the screen already black: the game
        // leaves it that way on purpose across the load of the room it is in, and only fades back in once the player
        // is standing there. Giving the screen back during the wait took that away, so it goes dark again here.
        if (screenBack) {
            ScreenFaderUtils.Fade(ScreenFaderUtils.GetColour(), Color.black, RescueFadeTime);

            yield return new WaitForSeconds(RescueFadeTime);
        }

        // Either the player gave up, or putting them back on their feet did not work, and a death that is left half
        // played out would be far worse than the bench they expected in the first place
        yield return death.Current;

        while (death.MoveNext()) {
            yield return death.Current;
        }
    }

    /// <summary>
    /// Starts waiting to be pulled back up, and tells the partner where the cocoon is so their game can show it.
    /// </summary>
    /// <returns>Whether a wait was started.</returns>
    private bool StartRescueWait() {
        var playerData = PlayerData.instance;
        if (playerData == null || GetCheckedPartner() is not { } partner) {
            return false;
        }

        // The position the game itself wrote for its own cocoon, so both games point at the same spot
        var scene = playerData.HeroCorpseScene;
        var position = playerData.HeroDeathScenePos;
        if (string.IsNullOrEmpty(scene)) {
            return false;
        }

        var gameManager = global::GameManager.instance;
        var hero = HeroController.instance;
        _rescue = new PendingRescue(scene, position) {
            // Taken now, before the death has played any of itself out, so it is the music of the room rather than
            // the music of the death
            Music = gameManager == null ? null : gameManager.AudioManager.CurrentMusicCue,
            Key = ++_lastRescueKey,
            KilledByTheRoom = hero != null && hero.cState.hazardDeath
        };
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueOffer,
            Scene = scene,
            Values = [position.x, position.y],
            Key = _rescue.Key
        });
        // Shown to the player themselves as well, not only to the one who can open it. Watching the room you died in
        // with nothing where you fell reads as the game having lost you, rather than as you lying there waiting.
        _rescueOwnCocoon = SpawnRescueCocoon(position, false);
        SayTheLocalPlayerIsDown(true);

        Logger.Info($"Waiting for {partner.Username} to open the cocoon in '{scene}'");

        return true;
    }

    /// <summary>
    /// Takes away the cocoon the local player was shown of their own.
    /// </summary>
    private void RemoveOwnCocoon() {
        if (_rescueOwnCocoon != null) {
            UnityEngine.Object.Destroy(_rescueOwnCocoon);
        }

        _rescueOwnCocoon = null;
        SayTheLocalPlayerIsDown(false);
    }

    /// <summary>
    /// Says whether the local player is lying in a cocoon rather than standing.
    ///
    /// A death this mod holds never reloads the room, so the hero goes on existing where it fell for as long as the
    /// player lies there - and the enemies in the room, which were never told any different, went on attacking that
    /// spot. Tied to the cocoon rather than to the death, because the cocoon is there for exactly as long as the
    /// player is down, however the wait ends.
    /// </summary>
    /// <param name="down">Whether the local player is down.</param>
    private static void SayTheLocalPlayerIsDown(bool down) {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        PlayerTargetRegistry.SetPlayerDown(hero.gameObject, down);

        if (down) {
            GamePatcher.ForgetPlayerAsTarget(hero.gameObject);
        }
    }

    /// <summary>
    /// Stops waiting to be pulled back up, and tells the partner so that the cocoon stops being shown to them.
    /// </summary>
    private void EndRescueWait() {
        // Marked as over on the way out, not just dropped. What is watching the death play out so it can give the
        // screen back holds on to this and asks it whether the wait is still on before it touches anything, and one
        // way out of a wait - the room being loaded again underneath it - ends it from here without going anywhere
        // near the outcome.
        if (_rescue is { Outcome: RescueOutcome.Waiting } waiting) {
            waiting.Outcome = RescueOutcome.Ended;
        }

        _rescue = null;
        RemoveOwnCocoon();
        _uiManager.CoopPrompt.Hide();

        if (GetCheckedPartner() is { } partner) {
            Send(new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.RescueEnd
            });
        }
    }

    /// <summary>
    /// The object the death spawned to play itself out, or null if it is no longer playing.
    ///
    /// This used to be looked for by a <c>HeroDeathSequence</c> component, on the belief that every kind of death
    /// carries one. It does not: of the five objects a death can spawn, exactly one has that component, and it is not
    /// the one an ordinary death uses - so the search came back empty every single time and everything built on it
    /// quietly did nothing. What all five do carry is one FSM named <see cref="DeathAnimFsmName"/>, which is the
    /// thing that plays the death out, so that is what they are found by now.
    ///
    /// Only objects that are switched on are found, which is exactly right: the death's own ending puts this object
    /// back in the pool, and a pooled object is switched off. Finding nothing therefore means the death has already
    /// played itself out.
    /// </summary>
    private static GameObject? FindDeathEffect() {
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
            if (fsm != null && fsm.FsmName == DeathAnimFsmName) {
                return fsm.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes away the object the death spawned to play itself out, so that it cannot darken the screen after the
    /// player has been put back on their feet.
    ///
    /// Its last step sets the screen to black and puts itself back in the pool, and nothing in a held death ever
    /// reaches the place where the game clears that again. A player pulled up before the death finished would
    /// therefore be walking around for a second or two and then have the screen go black on them.
    ///
    /// It came out of the game's pool, so it goes back to the pool: destroying a pooled object leaves the pool
    /// believing it is still on loan.
    /// </summary>
    private static void ClearDeathEffect() {
        if (FindDeathEffect() is not { } effect) {
            return;
        }

        try {
            effect.Recycle();
        } catch (Exception e) {
            Logger.Warn($"Could not return the death effect to the pool: {e.Message}");
            UnityEngine.Object.Destroy(effect);
        }
    }

    /// <summary>
    /// The value of <see cref="AutoRecycleSelf.afterEvent"/> that has the effect taken away by a timer running out.
    /// </summary>
    private const int EffectEndedByTimer = 0;

    /// <summary>
    /// The value of <see cref="AutoRecycleSelf.afterEvent"/> that has the effect taken away when its animation stops.
    /// </summary>
    private const int EffectEndedByAnimation = 1;

    /// <summary>
    /// The value of <see cref="AutoRecycleSelf.afterEvent"/> that has nothing take the effect away at all.
    /// </summary>
    private const int EffectEndedByNothing = 2;

    /// <summary>
    /// Whether an effect that is playing now has any way of ending itself.
    ///
    /// Read from <c>AutoRecycleSelf.RecycleUpdate</c>, which is one switch over <see cref="AutoRecycleSelf.afterEvent"/>
    /// and nothing else. The names of that enumeration are not in the game's own libraries for us to write here, so
    /// the numbers stand, and what each of them does is written next to it.
    ///
    /// The two values not named here count frames instead, and are left alone: an effect counting frames is an
    /// effect that is going to end, and this is not the place to decide it has waited long enough.
    /// </summary>
    /// <param name="recycler">The effect to judge.</param>
    /// <returns>Whether it can still put itself back in the pool.</returns>
    private static bool CanEffectEndItself(AutoRecycleSelf recycler) {
        return (int) recycler.afterEvent switch {
            // A timer that was never started never runs out, and the branch reads the flag before the clock
            EffectEndedByTimer => recycler.recycleTimerRunning,
            // Likewise: without an animator there is no animation to end
            EffectEndedByAnimation => recycler.hasTk2dAnimator,
            EffectEndedByNothing => false,
            _ => true
        };
    }

    /// <summary>
    /// Takes away the effects that were playing when the player died and that nothing will ever take away by itself.
    ///
    /// Every effect the game draws from its pool carries an <see cref="AutoRecycleSelf"/> saying what ends it: a
    /// timer, the end of an animation, a count of frames - or, for one of the values, nothing at all. The ones ended
    /// by nothing are not a mistake and are not leaks. They are held up by the one thing that clears all of them at
    /// once, which is the change of room: <c>GameManager.SetupGameRefs</c> hands
    /// <c>AutoRecycleSelf.RecycleActiveRecyclers</c> to <c>NextSceneWillActivate</c>, and that recycles every effect
    /// that is playing, whatever it says about itself. A pooled effect outlives the unloading of a scene - the pool it
    /// belongs to is not part of the scene - so without that hook they would follow the player into the next room.
    ///
    /// An ordinary death changes rooms, so an ordinary death clears them. A death that is held for the other player to
    /// answer never changes rooms, and so it never clears them: whatever was covering the player when they died is
    /// still covering them when they are pulled back up, and stays there until they walk out of the room. That was the
    /// screen full of smoke.
    ///
    /// Only the ones with no way of ending themselves are taken, not the whole list the room change takes. Everything
    /// else was going to be gone within a second or two on its own, and taking it as well would mean answering a death
    /// by wiping the room - including whatever the other player has in the air at that moment.
    /// </summary>
    private static void ClearStuckEffects() {
        var stuck = new List<AutoRecycleSelf>();

        // Copied before any of it is touched, because putting one back in the pool takes it out of this same list
        try {
            foreach (var recycler in AutoRecycleSelf.activeRecyclers) {
                if (recycler != null && !CanEffectEndItself(recycler)) {
                    stuck.Add(recycler);
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not look over the effects the death left playing: {e.Message}");

            return;
        }

        var cleared = new List<string>();
        foreach (var recycler in stuck) {
            try {
                if (recycler == null) {
                    continue;
                }

                cleared.Add(recycler.name);
                recycler.ForceRecycle();
            } catch (Exception e) {
                Logger.Warn($"Could not return an effect the death left playing to the pool: {e.Message}");
            }
        }

        if (cleared.Count > 0) {
            // By name, because something a player can see is still there afterwards cannot be told from something
            // that was never here at all unless what was taken away is written down
            Logger.Info(
                $"Took away {cleared.Count} effect(s) the death left playing that nothing would have ended: " +
                string.Join(", ", cleared)
            );
        }

    }

    /// <summary>
    /// The name of the FSM on the hero that holds the dark plates around them.
    /// </summary>
    private const string DarknessFsmName = "Darkness Control";

    /// <summary>
    /// The state a death puts that FSM into, which has no way out of its own.
    /// </summary>
    private const string DarknessDeathStateName = "Death";

    /// <summary>
    /// What that FSM is waiting to hear before it opens the plates again.
    /// </summary>
    private const string DarknessOpenEvent = "HERO RESPAWNED";

    /// <summary>
    /// Opens the dark plates a death closed around the player.
    ///
    /// The hero carries the vignette of the game on them: two plates larger than the screen with the player's own
    /// place cut out of the middle, which is how a dark room is drawn. A death draws them closed, and the state it
    /// does that in is a dead end - it has no transition of its own at all, and the only ways out of it are events
    /// the game sends while respawning. A held death never respawns, so the plates stayed closed for the whole wait,
    /// over a screen this mod had just deliberately given back: a dark cloud around a player who could otherwise see
    /// the room, their cocoon and their teammate coming.
    ///
    /// The event sent is the one whose state grows the plates back to the size the room itself asked for, so a dark
    /// room stays as dark as it was rather than being thrown open by a death.
    /// </summary>
    /// <remarks>
    /// Sent to that one FSM rather than announced to everything that listens for it: this is undoing one piece of a
    /// death that is still being waited out, not declaring the player alive.
    /// </remarks>
    private static void OpenTheDarknessAroundTheHero() {
        try {
            var hero = HeroController.instance;
            if (hero == null) {
                return;
            }

            PlayMakerFSM? darkness = null;
            foreach (var fsm in hero.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (fsm.FsmName == DarknessFsmName) {
                    darkness = fsm;

                    break;
                }
            }

            if (darkness == null) {
                Logger.Info("The hero has no plates to draw the dark with, so the death left none of them closed");

                return;
            }

            // Said either way, because the whole difficulty of a thing left on the screen is telling what it was:
            // silence here would only mean the next report is another guess.
            var state = darkness.ActiveStateName;
            if (state != DarknessDeathStateName) {
                Logger.Info($"The dark plates around the player were in '{state}', so the death left them open");

                return;
            }

            darkness.SendEvent(DarknessOpenEvent);
            Logger.Info($"Opened the dark plates the death closed around the player; they are now in '{darkness.ActiveStateName}'");
        } catch (Exception e) {
            Logger.Warn($"Could not open the dark plates the death closed around the player: {e.Message}");
        }
    }

    /// <summary>
    /// How many leftovers are worth naming before the line stops being readable.
    /// </summary>
    private const int MostLeftoversWorthNaming = 25;

    /// <summary>
    /// Everything around the player that was already switched on when the death began.
    /// </summary>
    private static HashSet<GameObject>? _whatWasOnBeforeTheDeath;

    /// <summary>
    /// The two things a death draws on: the player, who carries their own effects around with them, and the cameras,
    /// which carry the screen's.
    /// </summary>
    private static IEnumerable<GameObject> WhereADeathDraws() {
        var hero = HeroController.instance;
        if (hero != null) {
            yield return hero.gameObject;
        }

        var cameras = GameCameras.instance;
        if (cameras != null) {
            yield return cameras.gameObject;
        }
    }

    /// <summary>
    /// Everything switched on around the player at this moment.
    /// </summary>
    private static HashSet<GameObject> WhatIsOnAroundThePlayer() {
        var on = new HashSet<GameObject>();

        foreach (var root in WhereADeathDraws()) {
            foreach (var child in root.GetComponentsInChildren<Transform>(true)) {
                if (child.gameObject.activeInHierarchy) {
                    on.Add(child.gameObject);
                }
            }
        }

        return on;
    }

    /// <summary>
    /// Writes down what was switched on around the player before the death began playing.
    ///
    /// A death is a sequence that switches things on at its start and off again at its end, and a held death never
    /// reaches its end. Which things those are cannot be guessed from a report that says only how many were dealt
    /// with, so the only honest way to name one is to know what was there beforehand.
    /// </summary>
    private static void NoteWhatIsOnAroundThePlayer() {
        try {
            _whatWasOnBeforeTheDeath = WhatIsOnAroundThePlayer();
        } catch (Exception e) {
            _whatWasOnBeforeTheDeath = null;

            Logger.Warn($"Could not write down what was on around the player before the death: {e.Message}");
        }
    }

    /// <summary>
    /// Names whatever the death switched on around the player and is still drawing.
    /// </summary>
    /// <param name="when">Which moment this is being asked at, for the log to say.</param>
    private static void SayWhatTheDeathLeftSwitchedOn(string when) {
        try {
            if (_whatWasOnBeforeTheDeath == null) {
                return;
            }

            var left = new List<string>();
            foreach (var thing in WhatIsOnAroundThePlayer()) {
                if (_whatWasOnBeforeTheDeath.Contains(thing)) {
                    continue;
                }

                // Only what actually puts something on the screen: a death switches plenty of bookkeeping on as
                // well, and a list nobody can read through is the same as no list at all.
                var drawn = thing.GetComponent<Renderer>();
                if (drawn == null || !drawn.enabled) {
                    continue;
                }

                left.Add(PathOf(thing.transform));
            }

            if (left.Count == 0) {
                Logger.Info($"The death left nothing of its own drawing around the player, {when}");

                return;
            }

            left.Sort(StringComparer.Ordinal);

            Logger.Info(
                $"The death switched these on around the player and they are still drawing, {when} " +
                $"({left.Count}): " +
                string.Join(", ", left.Count > MostLeftoversWorthNaming ? left.GetRange(0, MostLeftoversWorthNaming) : left)
            );
        } catch (Exception e) {
            Logger.Warn($"Could not look over what the death left around the player: {e.Message}");
        }
    }

    /// <summary>
    /// Where something sits in the world, written out in full so that it can be found again.
    /// </summary>
    /// <param name="thing">The object to name.</param>
    private static string PathOf(Transform thing) {
        var path = thing.name;
        for (var parent = thing.parent; parent != null; parent = parent.parent) {
            path = parent.name + "/" + path;
        }

        return path;
    }

    /// <summary>
    /// The beginning of the name of every state of the camera that shakes the screen until told to stop.
    /// </summary>
    private const string ShakingOnStateNamePrefix = "Rumbling";

    /// <summary>
    /// The name of the one state of that FSM in which the screen is not being shaken at all.
    /// </summary>
    private const string RestingStateName = "Normal";

    /// <summary>
    /// The event that ends a shake which runs for a set time rather than until it is told to stop.
    /// </summary>
    private const string DoneShakingEvent = "DoneShaking";

    /// <summary>
    /// What the camera is waiting to hear before it stops shaking the screen.
    /// </summary>
    private const string StopShakingEvent = "StopRumble";

    /// <summary>
    /// The state the camera sits in when the screen has been told to hold still.
    /// </summary>
    private const string HoldingStillStateName = "CancelAllShake";

    /// <summary>
    /// What the camera is waiting to hear before it will shake the screen again.
    /// </summary>
    private const string ResumeShakingEvent = "RESUME SHAKE";

    /// <summary>
    /// Stops the screen shaking when the death left it shaking with nothing coming to stop it.
    ///
    /// The camera tells the two kinds of shake apart by how they end. The short ones count themselves out and stop.
    /// The long ones - the ground going, the deep rumble under a death - do not: they run until something sends the
    /// event that ends them, and the thing that sends it is further along the death than a held death ever gets. So
    /// the screen was still shaking after the player was back on their feet, and would have gone on shaking until
    /// they left the room.
    ///
    /// The other way round is covered as well, because it has the same shape: a screen told to hold still is waiting
    /// on an event too, and being pulled up should not cost the player every shake for the rest of the room.
    /// </summary>
    /// <remarks>
    /// The states that rumble leave only on <see cref="StopShakingEvent"/>, the ones that shake for a set time leave
    /// only on <see cref="DoneShakingEvent"/>, and the state they all rest in answers to neither - so both are sent
    /// and the resting state alone is left alone.
    /// </remarks>
    private static void StopTheScreenShaking() {
        try {
            var shake = GameCameras.instance?.cameraShakeFSM;
            if (shake == null) {
                return;
            }

            var state = shake.ActiveStateName;
            if (state == null) {
                return;
            }

            // Every state that shakes the screen leaves on one of two events and on nothing else: the ones that
            // rumble on and on until told to stop, and the ones that shake for a while and announce their own end.
            // Both are sent, because the state resting between them has no answer to either and a death can leave
            // the screen in one of them just as easily as in the other.
            if (state == HoldingStillStateName) {
                shake.SendEvent(ResumeShakingEvent);
                Logger.Info("Let the screen shake again, which the death had switched off with nothing to switch on");
            } else if (state != RestingStateName) {
                shake.SendEvent(StopShakingEvent);
                shake.SendEvent(DoneShakingEvent);

                Logger.Info(
                    $"Told the screen to stop shaking, which the death left in '{state}' with nothing to end it; " +
                    $"it is now in '{shake.ActiveStateName}'"
                );
            }
        } catch (Exception e) {
            Logger.Warn($"Could not stop the screen shaking after the death: {e.Message}");
        }
    }

    /// <summary>
    /// Puts the line on screen that says what the wait is and how to leave it.
    /// </summary>
    /// <param name="rescue">The death being waited on.</param>
    private void ShowTheWayOutOfTheWait(PendingRescue rescue) {
        var partnerName = string.IsNullOrEmpty(_rescuePartnerName)
            ? Lang.Pick("your teammate", "队友")
            : _rescuePartnerName;

        _uiManager.CoopPrompt.Show(
            rescue.Hits > 0
                ? Lang.Pick(
                    $"{partnerName} is breaking you out ({rescue.Hits}/{RescueHits}). " +
                    $"Press {LeaveKeyName} to go to your bench instead",
                    $"{partnerName} 正在打你的茧（{rescue.Hits}/{RescueHits}）。" +
                    $"按 {LeaveKeyName} 直接回长椅"
                )
                : Lang.Pick(
                    $"Waiting for {partnerName} to break you out. " +
                    $"Press {LeaveKeyName} to go to your bench instead",
                    $"等 {partnerName} 来打破你的茧。" +
                    $"按 {LeaveKeyName} 直接回长椅"
                )
        );
    }

    /// <summary>
    /// Gives the player their screen back after the death darkened it.
    ///
    /// A death ends with the whole screen black and the heads-up display slid away, and it stays that way on purpose:
    /// the game leaves it black across the load of the room the bench is in and only fades back in once the player is
    /// standing there. A held death never gets that far, so nobody ever fades anything back in - which is the black
    /// screen the waiting player was left staring at, unable to see the world, their own cocoon, or the line telling
    /// them what is happening.
    /// </summary>
    private static void RestoreScreen() {
        // From whatever the screen is now rather than from black. A fade always starts by putting its first colour
        // up, so a fade written as "from black" over a screen that happens not to be black blacks it out for the
        // length of the fade - a blink that would be this feature's own doing.
        ScreenFaderUtils.Fade(ScreenFaderUtils.GetColour(), Color.clear, RescueFadeTime);

        var cameras = GameCameras.instance;
        if (cameras != null) {
            // The death slid this away as it started
            cameras.HUDIn();
        }

        // The black over the screen is not the only dark a death leaves. Fading that off in front of plates still
        // drawn shut around the player only reveals the plates.
        OpenTheDarknessAroundTheHero();
        SayWhatTheDeathLeftSwitchedOn("with the screen just given back");
    }

    /// <summary>
    /// Puts the music of the room back on after a death changed it to its own.
    ///
    /// A death ends on its own music and the game changes it back by loading the room the bench is in. A player who
    /// is pulled back up never loads anything, so without this they would walk out of their own death into a room
    /// that has gone quiet, and stay in it until they left the room.
    /// </summary>
    /// <param name="rescue">The wait that is ending, which holds what was playing before the death.</param>
    private static void RestoreMusic(PendingRescue rescue) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null) {
            return;
        }

        if (rescue.Music != null) {
            var audio = gameManager.AudioManager;
            if (audio.CurrentMusicCue != rescue.Music) {
                audio.ApplyMusicCue(rescue.Music, 0f, 0f, false);
            }
        }

        RestoreAudioSnapshots(gameManager);
    }

    /// <summary>
    /// Puts the sound of the room back the way the room itself says it should be.
    ///
    /// A death moves the whole mixer onto its own settings four separate times while it plays out - that muffled,
    /// closing-in sound a death has - and what puts them back is the loading of the room the bench is in. A player
    /// who is pulled back up loads nothing, so they stood up into a world that stayed muffled for as long as they
    /// stayed in the room.
    ///
    /// Asking the room is the only honest answer available: what a mixer is currently set to cannot be read back,
    /// so there is nothing to save when the wait starts the way the music is saved. These are the same five
    /// settings the room applies to itself when it loads.
    /// </summary>
    /// <param name="gameManager">The game manager.</param>
    private static void RestoreAudioSnapshots(global::GameManager gameManager) {
        var sceneManager = gameManager.GetSceneManager();
        var customSceneManager = sceneManager == null ? null : sceneManager.GetComponent<CustomSceneManager>();
        if (customSceneManager == null) {
            return;
        }

        foreach (var snapshot in new[] {
                     customSceneManager.musicSnapshot,
                     customSceneManager.atmosSnapshot,
                     customSceneManager.enviroSnapshot,
                     customSceneManager.actorSnapshot,
                     customSceneManager.shadeSnapshot
                 }) {
            if (snapshot != null) {
                snapshot.TransitionTo(RescueFadeTime);
            }
        }
    }

    /// <summary>
    /// Lets the death play its own ending out - the animation, the sound, the shake and the screen going black, all
    /// of which is the game's and should be seen - and gives the screen back the moment it is over.
    ///
    /// Waits for the object rather than for a length of time, because the deaths do not all take the same length of
    /// time, and puts a limit on the waiting anyway: the one thing that must never happen is a player left staring at
    /// a black screen because something we expected to end did not.
    /// </summary>
    /// <param name="rescue">The wait this belongs to.</param>
    private IEnumerator BringScreenBackAfterDeath(PendingRescue rescue) {
        GameObject? effect = null;
        try {
            effect = FindDeathEffect();
        } catch (Exception e) {
            LogRescueError(e);
        }

        var start = Time.unscaledTime;

        while (effect != null && effect.activeInHierarchy && Time.unscaledTime - start < DeathEffectWaitTime) {
            yield return null;
        }

        // Being pulled back up in the meantime gives the screen back by itself, and does it in the same breath as
        // everything else about a rescue rather than after this has noticed
        if (rescue.ScreenBack || rescue.Outcome != RescueOutcome.Waiting) {
            yield break;
        }

        // Anything thrown in here would leave the screen black for the rest of the wait, which is the very thing
        // this exists to stop, so it is caught and said out loud rather than ending the coroutine quietly.
        try {
            rescue.ScreenBack = true;
            RestoreScreen();
        } catch (Exception e) {
            LogRescueError(e);
        }
    }

    /// <summary>
    /// Puts the local player back on their feet where they fell, with half of their health. This undoes by hand what
    /// the death did before it was held, in the order the game's own respawn does it, and then hands back what the
    /// cocoon held, which is what makes being pulled up worth anything: the money and the silk are already inside it
    /// by this point, so a rescue that left them there would send the player back for them anyway.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="rescue">The wait that is ending, which holds what the death took away.</param>
    /// <returns>Whether the player was put back on their feet.</returns>
    private bool TryRevive(HeroController? hero, PendingRescue rescue) {
        var playerData = PlayerData.instance;
        if (hero == null || playerData == null) {
            return false;
        }

        try {
            // The screen comes first, and before the death effect is taken away: taking the effect away pulls the
            // black it holds over the screen off in one step, and the fade is what makes that a fade. Whoever was
            // pulled up after the death had already finished playing out has had their screen back for a while.
            if (!rescue.ScreenBack) {
                RestoreScreen();
            }

            ClearDeathEffect();
            ClearStuckEffects();
            StopTheScreenShaking();
            RestoreMusic(rescue);

            hero.gameObject.layer = 9;
            hero.renderer.enabled = true;
            hero.heroBox.HeroBoxNormal();

            // The hit box of the player answers to two things: the collider that HeroBoxNormal switches back on, and
            // one switch shared by the whole game that a death turns on and only the loading of a room ever turns off
            // again. Leaving it on makes a player who was pulled back up unable to be touched by anything at all
            // until they change rooms - which is not a mercy, it is the fight stopping to mean anything.
            HeroBox.Inactive = false;
            // The death made the body kinematic so that it would stop where it fell. Rigidbody2D.isKinematic, which
            // the game's own respawn still writes, is obsolete in the Unity this game runs on, so the body type it
            // stands for is set instead - the file next door only gets away with the old one behind a blanket
            // suppression of the warning, and a new file should not start life with one of those.
            hero.rb2d.bodyType = RigidbodyType2D.Dynamic;
            hero.AffectedByGravity(true);

            // Put back on their feet where they fell is the whole point of this everywhere else. Where the room
            // itself did the killing it is the one place that cannot be done: that spot is the inside of the lava,
            // the spikes or the drop, and a player put back into it dies again before they can move - which is a
            // death, a cocoon, a rescue and a death again, with nothing either player can do to break out of it.
            // The game keeps a place of its own for exactly this, which is where it would have put them itself.
            if (rescue.KilledByTheRoom) {
                var safe = playerData.hazardRespawnLocation;
                Logger.Info($"The room itself did the killing, so standing back up happens at {safe} instead");
                hero.transform.position = safe;
            }

            hero.cState.dead = false;
            hero.cState.isFrostDeath = false;
            hero.cState.onGround = true;
            hero.cState.falling = false;
            hero.cState.hazardDeath = false;
            hero.cState.recoiling = false;

            playerData.disablePause = false;

            hero.ResetMotion();
            hero.ResetHardLandingTimer();
            hero.ResetInput();
            hero.ResetLook();

            // Hands back the money and the silk the cocoon holds and clears what the save keeps of it, all of which
            // belongs to this player and is done by their own game, so none of it depends on a hit crossing over
            hero.CocoonBroken();

            // Half of the maximum, rounded down, but never nothing: waking up already dead would be absurd.
            //
            // Given the game's own way rather than written into the number, because the number is only half of what
            // health is here. The row of masks in the corner is not drawn from the field - it is redrawn when the
            // game says health has changed - so writing the field left a player standing up with health they had no
            // way of seeing, and a corner that still said they were dead. Writing it is kept for the case where
            // there is nothing to add, which cannot happen to someone who has just died but costs nothing to hold.
            var health = Mathf.Max(1, playerData.maxHealth / 2);
            var missing = health - playerData.health;
            if (missing > 0) {
                hero.AddHealth(missing);
            } else {
                playerData.health = health;
            }

            hero.AddInvulnerabilitySource(RescueInvulnerability);
            MonoBehaviourUtil.Instance.StartCoroutine(EndRescueInvulnerability(hero));

            if (!IsHeroTakenByGame(hero)) {
                if (GiveBackHeroControl != null) {
                    GiveBackHeroControl(hero);
                } else {
                    hero.RegainControl();
                }
            }

            // What the game itself runs when the hero is alive again. Everything above undoes the death by hand, one
            // field at a time, and this is the half that cannot be written that way: it lets go of the swing the
            // death froze part-way through, and it tells the FSMs of the hero that the death was called off. The
            // death told every one of them to cancel, and nothing else ever tells them otherwise - so without this a
            // player who was pulled back up walks, looks and swings, and touches nothing.
            //
            // Wrapped on its own because of what is on the other end of it: that event is handed to the whole stack
            // of the hero's own FSMs and every one of them runs there and then. If any one of them throws, the
            // rescue above it has already given the player their body, their health and their control back - and
            // failing at this point would report the rescue as having failed and let the death carry on over the
            // top of it, which is worse than anything this line can fix.
            try {
                hero.HeroRespawned();
            } catch (Exception e) {
                LogRescueError(e);
            }

            // Giving control back writes the state of the player straight into the field, without telling the part
            // that decides which animation belongs to that state. Left alone it stays on the last thing it was told,
            // which is the death - so a player who is pulled up and then stands still is still lying there dead on
            // the screen of the other player. This is what the game's own respawn does about it. Wrapped for the
            // same reason as above: standing in the wrong pose is not worth failing a rescue over.
            try {
                hero.StartAnimationControlToIdle();
            } catch (Exception e) {
                LogRescueError(e);
            }

            // Asked again here and not only when the screen came back, because this is the moment the player is
            // walking around looking at whatever is left, and everything the rescue itself undoes has now run
            SayWhatTheDeathLeftSwitchedOn("with the player back on their feet");

            _lastStoodBackUpTime = Time.unscaledTime;

            Chat(Lang.Pick("Your teammate pulled you back up.", "队友把你拉起来了。"));
            Logger.Info("Pulled back up after a death instead of going to the bench");

            return true;
        } catch (Exception e) {
            LogRescueError(e);

            return false;
        }
    }

    /// <summary>
    /// Takes the short invulnerability of a rescue away again.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    private static IEnumerator EndRescueInvulnerability(HeroController hero) {
        yield return new WaitForSeconds(RescueInvulnerabilityTime);

        if (hero != null) {
            hero.RemoveInvulnerabilitySource(RescueInvulnerability);
        }
    }

    /// <summary>
    /// Keeps a wait for a rescue and a cocoon of the partner in step with the game each frame: the line that tells the
    /// waiting player what is happening, the key that gives up on it, and the cocoon that stops being shown once the
    /// partner is no longer waiting.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="partner">The partner whose save is checked with this one, or null.</param>
    private void UpdateRescue(HeroController hero, ClientPlayerData? partner) {
        try {
            if (_rescue is { Outcome: RescueOutcome.Waiting } rescue) {
                // A death that is being held always leaves the player marked dead - that is set in the part of the
                // death that has already been played out by the time a wait starts - so finding them alive means the
                // death itself is gone: the room was loaded again underneath it, or the save was left, and whatever
                // was holding it went away with it. After a room is loaded this is a different hero entirely. Nothing
                // else can end the wait once that has happened, so it ends here, which also takes the cocoon of
                // someone who is walking around again off the screen of the other player.
                if (!hero.cState.dead) {
                    EndRescueWait();

                    return;
                }

                if (partner == null) {
                    // Nobody is left to open it
                    rescue.Outcome = RescueOutcome.Ended;
                    return;
                }

                // Only remembered here. Showing the line and reading the key that gives up on the wait both happen in
                // the wait itself, which is the one thing that keeps running while a death is held
                _rescuePartnerName = partner.Username;

                return;
            }

            if (_rescueTarget is { } target && (partner == null || partner.Id != target.PlayerId ||
                                                SceneUtil.GetCurrentSceneName() != target.Scene)) {
                RemoveRescueTarget();
            }
        } catch (Exception e) {
            LogRescueError(e);
        }
    }

    /// <summary>
    /// The partner died and is waiting to be pulled back up, so their cocoon is shown here for the local player to
    /// break open. Their own game never sent one before, which is why a room where both players died only ever held
    /// one cocoon.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnRescueOffer(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            return;
        }

        RemoveRescueTarget();

        if (update.Values.Count < 2) {
            return;
        }

        // Noted whichever room they died in: it decides whether a death of this player can wait for them at all, and
        // it is what lets their cocoon appear when this player walks into that room later
        _partnerWaitingRescue = player.Id;
        _partnerCocoonScene = update.Scene;
        _partnerCocoonPosition = new Vector2(update.Values[0], update.Values[1]);
        _partnerRescueKey = update.Key;

        Chat(
            SceneUtil.GetCurrentSceneName() == update.Scene
                ? Lang.Pick(
                    $"{player.Username} died. Hit their cocoon {RescueHits} times to break them out.",
                    $"{player.Username} 死了。攻击他们的茧 {RescueHits} 次就能把人救出来。"
                )
                : Lang.Pick(
                    $"{player.Username} died. Their cocoon is where they fell, and {RescueHits} hits break them out.",
                    $"{player.Username} 死了。茧就在他们倒下的地方，打 {RescueHits} 次能把人救出来。"
                )
        );

        ShowPartnerCocoon();
    }

    /// <summary>
    /// The partner hit the cocoon of the local player, so the player who is waiting sees how far it got, and is put
    /// back on their feet on the last hit.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnRescueHit(ClientPlayerData player, CoopSaveUpdate update) {
        if (_rescue is not { Outcome: RescueOutcome.Waiting } rescue || GetCheckedPartner()?.Id != player.Id) {
            return;
        }

        // A hit about some earlier death of this player is not a hit on the cocoon lying here now. These messages
        // are resent until they arrive and are never dropped for a newer one, so one written about a death that is
        // already over can still turn up - and taken at face value it opened this cocoon the instant it appeared.
        if (update.Key != rescue.Key) {
            Logger.Info(
                $"Ignoring a hit on a cocoon of an earlier death ({update.Key}), this one being {rescue.Key}"
            );

            return;
        }

        rescue.Hits = update.Part;
        if (update.Part >= (update.PartCount == 0 ? RescueHits : update.PartCount)) {
            rescue.Outcome = RescueOutcome.Rescued;
        }
    }

    /// <summary>
    /// The partner is no longer waiting to be pulled back up, so their cocoon goes away again.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnRescueEnd(ClientPlayerData player, CoopSaveUpdate update) {
        if (_partnerWaitingRescue == player.Id) {
            _partnerWaitingRescue = null;
        }

        if (_rescueTarget is { } target && target.PlayerId == player.Id) {
            RemoveRescueTarget();
        }
    }

    /// <summary>
    /// Puts something that stands in for the cocoon of the partner in the room, keeping the part of it that decides
    /// how it looks and dropping the parts that pay out, clear the save, or make it fade away again.
    ///
    /// Destroying every FSM was too blunt. The cocoon's own "Break" FSM starts in "Next Frame" and walks straight
    /// into "Appearance?", which reads <c>HeroCorpseType</c> and switches on the matching child of its "Appearances
    /// parent" - no hit needed. With every FSM gone that step never ran, nothing was switched on, and the cocoon
    /// showed the form it has after it has already burst.
    /// </summary>
    /// <param name="position">Where the player died.</param>
    /// <param name="openable">Whether hits on it count towards pulling the player in it back up.</param>
    /// <returns>The object, or null if it could not be made.</returns>
    private GameObject? SpawnRescueCocoon(Vector2 position, bool openable) {
        var gameManager = global::GameManager.instance;
        var sceneManager = gameManager == null ? null : gameManager.GetSceneManager();
        var prefab = sceneManager == null ? null : sceneManager.GetComponent<CustomSceneManager>()?.heroCorpsePrefab;
        if (prefab == null) {
            Logger.Warn("Could not find the cocoon to show for the partner");

            return null;
        }

        // The depth the game puts its own cocoon at, rather than zero: the position that travels is where the player
        // fell, which is a place in the room and says nothing about what the cocoon should be drawn in front of.
        var cocoon = UnityEngine.Object.Instantiate(
            prefab,
            new Vector3(position.x, position.y, prefab.transform.position.z),
            Quaternion.identity
        );

        // Only the root's "Break" FSM survives. The one on the "Core" child is called "Dissolve" and starts in
        // "Delay", which fades the cocoon out and leaves: keeping it would trade a cocoon that looks wrong for one
        // that quietly disappears a moment after it arrives.
        foreach (var fsm in cocoon.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm == null || (fsm.FsmName == BreakFsmName && fsm.gameObject == cocoon)) {
                continue;
            }

            UnityEngine.Object.DestroyImmediate(fsm);
        }

        // HitResponse still goes. It is what turns a hit into the event the FSM breaks on, and how many hits open
        // this cocoon is a rule of ours, not the game's, so the counting stays in RescueCocoonHits.
        cocoon.DestroyComponentsInChildren<HitResponse>();

        // Without HitResponse the state that pays out cannot be reached at all - but that state is the entire
        // reason these FSMs used to be destroyed wholesale, so the call is taken out as well. Then the cocoon
        // cannot hand this player the money and silk of the one lying in it, or wipe their own cocoon's record,
        // even if something later finds another way into that state.
        RemoveCocoonPayout(cocoon);

        cocoon.SetActive(true);

        // The one a player is shown of their own takes no hits. They are lying in it, they cannot swing at anything
        // while they are, and the way out of it is the other player - not themselves.
        if (openable) {
            var hits = cocoon.AddComponent<RescueCocoonHits>();
            hits.Hits = OnRescueCocoonHit;
        }

        return cocoon;
    }

    /// <summary>
    /// Takes the <c>HeroController.CocoonBroken</c> call out of a cocoon, which is the one thing on it that would
    /// pay out and clear a save.
    /// </summary>
    /// <param name="cocoon">The cocoon to take it out of.</param>
    private static void RemoveCocoonPayout(GameObject cocoon) {
        foreach (var fsm in cocoon.GetComponentsInChildren<PlayMakerFSM>(true)) {
            // Asked for by name first: RemoveFirstAction throws when the state is missing, and a cocoon that the
            // game one day ships without this state should still be shown rather than swallowed by an exception.
            if (fsm == null || fsm.GetStateOrNull(ReturnCurrencyStateName) == null) {
                continue;
            }

            try {
                fsm.RemoveFirstAction<CallMethodProper>(ReturnCurrencyStateName);
            } catch (Exception e) {
                Logger.Warn($"Could not take the payout out of the cocoon of the partner: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Counts a hit of the local player on the cocoon of the partner, and tells them how far it got.
    /// </summary>
    private void OnRescueCocoonHit() {
        if (_rescueTarget is not { } target || GetCheckedPartner() is not { } partner ||
            partner.Id != target.PlayerId) {
            return;
        }

        target.Hits++;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueHit,
            Part = (ushort) target.Hits,
            PartCount = RescueHits,
            Key = target.Key
        });

        if (target.Hits >= RescueHits) {
            RemoveRescueTarget();
            Chat(Lang.Pick($"You broke {partner.Username} out.", $"你把 {partner.Username} 拉起来了。"));
        }
    }

    /// <summary>
    /// Puts the cocoon of a waiting partner in the room once the local player is standing in the room it is in. It is
    /// shown on arriving as well as on hearing about it, since a player who was elsewhere when their partner died can
    /// still be the one who walks in and opens it.
    /// </summary>
    private void ShowPartnerCocoon() {
        if (_partnerWaitingRescue is not { } playerId || _rescueTarget != null ||
            _partnerCocoonScene.Length == 0 || SceneUtil.GetCurrentSceneName() != _partnerCocoonScene) {
            return;
        }

        if (SpawnRescueCocoon(_partnerCocoonPosition, true) is not { } cocoon) {
            return;
        }

        _rescueTarget = new RescueTarget(playerId, _partnerCocoonScene, cocoon) { Key = _partnerRescueKey };

        // Their body is in the cocoon now, so it stops standing beside it. Nothing ever took it away before: the
        // death that crosses over is an animation like any other, and the animation of a death simply stops on its
        // last frame. Their own game hides their body the moment they die, and until a death could be held they
        // were gone from the room a few seconds later anyway, so a body frozen mid-death was never on screen long
        // enough to be noticed. A wait lasts up to three quarters of a minute.
        SetPartnerBodyHidden(playerId, true);
    }

    /// <summary>
    /// Takes the body of the partner off the screen while they are lying in their cocoon, and brings it back when
    /// they are not.
    ///
    /// Hidden the way the mod hides a player everywhere else: the animation is stopped and the sprite is replaced by
    /// one that cannot be seen. Nothing else about them is touched, so where they are, what they can be hit by and
    /// everything the rest of the mod knows about them stays exactly as it was.
    /// </summary>
    /// <param name="playerId">The player whose body it is.</param>
    /// <param name="hidden">Whether to take it off the screen.</param>
    private void SetPartnerBodyHidden(ushort playerId, bool hidden) {
        try {
            if (!_playerData.TryGetValue(playerId, out var player)) {
                return;
            }

            var body = player.PlayerObject;
            if (body == null) {
                return;
            }

            var animator = body.GetComponent<tk2dSpriteAnimator>();
            if (animator == null) {
                return;
            }

            if (hidden) {
                var sprite = body.GetComponent<tk2dSprite>();
                if (sprite == null) {
                    return;
                }

                animator.Stop();
                sprite.SetSprite(HiddenSpriteName);

                // Nothing should come looking for someone who is lying in a cocoon. This stops anything choosing
                // them from here on; an enemy that had already fixed on them keeps swinging at the spot until it
                // loses interest of its own accord, because the hold it has is kept somewhere this cannot reach.
                PlayerTargetRegistry.UnregisterRemotePlayer(body);

                return;
            }

            PlayerTargetRegistry.RegisterRemotePlayer(body);

            // Any animation of theirs puts their own sprite back, so this only has to cover the player who is put
            // back on their feet and then stands perfectly still: their game would send nothing, and a body that
            // was taken off the screen for the wait would stay that way.
            var idle = animator.GetClipByName(IdleClipName);
            if (idle != null) {
                animator.Play(idle);
            }
        } catch (Exception e) {
            LogRescueError(e);
        }
    }

    /// <summary>
    /// Shows or takes away the cocoon of a waiting partner when the local player changes rooms.
    /// </summary>
    private void OnRescueSceneChanged() {
        try {
            if (_rescueTarget is { } target && SceneUtil.GetCurrentSceneName() != target.Scene) {
                RemoveRescueTarget();
            }

            ShowPartnerCocoon();
        } catch (Exception e) {
            LogRescueError(e);
        }
    }

    /// <summary>
    /// Forgets everything about a rescue when the loaded save is left, so that a cocoon of a partner does not stay
    /// behind in the world and a partner who is remembered as waiting cannot keep the next death of this player from
    /// waiting for a rescue of its own.
    /// </summary>
    private void ResetRescue() {
        if (_rescue is { Outcome: RescueOutcome.Waiting } rescue) {
            rescue.Outcome = RescueOutcome.Ended;
        }

        RemoveRescueTarget();
        RemoveOwnCocoon();
        _partnerWaitingRescue = null;
        _partnerCocoonScene = "";
        _uiManager.CoopPrompt.Hide();
    }

    /// <summary>
    /// Takes the cocoon of the partner out of the room again.
    /// </summary>
    private void RemoveRescueTarget() {
        if (_rescueTarget is { } target) {
            if (target.Cocoon != null) {
                UnityEngine.Object.Destroy(target.Cocoon);
            }

            // Whatever took the cocoon away - they were pulled up, they gave up, or this player walked out of the
            // room - their body belongs back on the screen. Anything they do puts it there by itself, so this only
            // has to cover someone who does nothing at all.
            SetPartnerBodyHidden(target.PlayerId, false);

            _rescueTarget = null;
        }
    }

    /// <summary>
    /// Logs a failure of a rescue once, so that a room does not fill the log with the same line.
    /// </summary>
    /// <param name="e">The exception.</param>
    private void LogRescueError(Exception e) {
        if (_rescueFailed) {
            return;
        }

        _rescueFailed = true;
        Logger.Error($"Could not hold a death for a rescue:\n{e}");
    }
}
