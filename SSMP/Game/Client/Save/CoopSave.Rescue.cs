using System;
using System.Collections;
using HutongGames.PlayMaker.Actions;
using SSMP.Hooks;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
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

        /// <inheritdoc/>
        public IHitResponder.HitResponse Hit(HitInstance damageInstance) {
            Hits?.Invoke();

            // Not None. That is the answer for "there was nothing there", so the nail passes straight through with
            // no impact, no sound and nothing to bounce off - which is why hitting the cocoon felt like hitting air
            // and a downward strike would not pogo. GenericHit is what something solid but undamageable answers.
            return IHitResponder.Response.GenericHit;
        }
    }

    /// <summary>
    /// The death of the local player that waits to be undone, or null while no death is waiting.
    /// </summary>
    private PendingRescue? _rescue;

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
    /// Whether the give-up key was held on the previous frame, so that holding it counts once.
    /// </summary>
    private bool _rescueLeaveHeld;

    /// <summary>
    /// Whether a failure of this has been logged already.
    /// </summary>
    private bool _rescueFailed;

    /// <summary>
    /// Takes over how a death of the local player plays out.
    /// </summary>
    private void RegisterRescueHooks() {
        EventHooks.HeroControllerDieWrapper = WrapDeath;
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

        if (!CanWaitForRescue()) {
            TellPartnerNobodyIsComing();

            return death;
        }

        return HoldDeath(death);
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
                return;
            }

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
        return _partnerWaitingRescue != partner.Id;
    }

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
        while (_rescue is { Outcome: RescueOutcome.Waiting } waiting) {
            if (Time.unscaledTime - waiting.StartTime >= RescueWaitTime) {
                waiting.Outcome = RescueOutcome.Ended;

                break;
            }

            yield return null;
        }

        var waited = _rescue;
        var rescued = waited is { Outcome: RescueOutcome.Rescued };
        var screenBack = waited is { ScreenBack: true };
        EndRescueWait();

        if (rescued && waited != null && TryRevive(HeroController.instance, waited)) {
            yield break;
        }

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
        _rescue = new PendingRescue(scene, position) {
            // Taken now, before the death has played any of itself out, so it is the music of the room rather than
            // the music of the death
            Music = gameManager == null ? null : gameManager.AudioManager.CurrentMusicCue
        };
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueOffer,
            Scene = scene,
            Values = [position.x, position.y]
        });
        // Shown to the player themselves as well, not only to the one who can open it. Watching the room you died in
        // with nothing where you fell reads as the game having lost you, rather than as you lying there waiting.
        _rescueOwnCocoon = SpawnRescueCocoon(position, false);

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
        _rescueLeaveHeld = false;
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

            // Half of the maximum, rounded down, but never nothing: waking up already dead would be absurd
            playerData.health = Mathf.Max(1, playerData.maxHealth / 2);

            hero.AddInvulnerabilitySource(RescueInvulnerability);
            MonoBehaviourUtil.Instance.StartCoroutine(EndRescueInvulnerability(hero));

            if (!IsHeroTakenByGame(hero)) {
                if (GiveBackHeroControl != null) {
                    GiveBackHeroControl(hero);
                } else {
                    hero.RegainControl();
                }
            }

            // Giving control back writes the state of the player straight into the field, without telling the part
            // that decides which animation belongs to that state. Left alone it stays on the last thing it was told,
            // which is the death - so a player who is pulled up and then stands still is still lying there dead on
            // the screen of the other player. This is what the game's own respawn does about it.
            hero.StartAnimationControlToIdle();

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

                _uiManager.CoopPrompt.Show(
                    rescue.Hits > 0
                        ? Lang.Pick(
                            $"{partner.Username} is breaking you out ({rescue.Hits}/{RescueHits}). " +
                            $"Press {LeaveKeyName} to go to your bench instead",
                            $"{partner.Username} 正在打你的茧（{rescue.Hits}/{RescueHits}）。" +
                            $"按 {LeaveKeyName} 直接回长椅"
                        )
                        : Lang.Pick(
                            $"Waiting for {partner.Username} to break you out. " +
                            $"Press {LeaveKeyName} to go to your bench instead",
                            $"等 {partner.Username} 来打破你的茧。" +
                            $"按 {LeaveKeyName} 直接回长椅"
                        )
                );

                var leaveHeld = _modSettings.Keybinds.CoopLeave.IsPressed;
                if (leaveHeld && !_rescueLeaveHeld) {
                    rescue.Outcome = RescueOutcome.Ended;
                }

                _rescueLeaveHeld = leaveHeld;

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
            PartCount = RescueHits
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

        _rescueTarget = new RescueTarget(playerId, _partnerCocoonScene, cocoon);

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
        _rescueLeaveHeld = false;
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
