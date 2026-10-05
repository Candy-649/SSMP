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
/// Being pulled back up by the other player after dying, instead of waking at a bench. While the other player is
/// still standing, a death of the local player is not played out at all: the player goes down where they are, the
/// game's own death is shown over them with what it tells the room taken out, and they lie in a cocoon that the other
/// player can break open to put them back on their feet with half their health. The game's own death runs, from its
/// very first step, only once nobody is left to do that: both players are down, the wait ran out, or the player gave
/// up. Waiting is a choice, and the cocoon stops being shown to the other player the moment waiting ends.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How many hits it takes to open the cocoon of the other player.
    /// </summary>
    private const int RescueHits = 5;

    /// <summary>
    /// How long a player waits to be pulled back up before going to their bench anyway, in seconds. Without this a
    /// player whose partner never noticed would wait for as long as the game runs. It leaves the partner time to finish
    /// what they are in the middle of first - 45 seconds was too short in play - and a player who does not want to wait
    /// gives up on it with a key.
    /// </summary>
    private const float RescueWaitTime = 180f;

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
    /// The layer of the body of the player.
    /// </summary>
    private const int PlayerLayer = (int) GlobalEnums.PhysLayers.PLAYER;

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
    /// How long the screen takes to come back, in seconds.
    /// </summary>
    private const float RescueFadeTime = 0.5f;

    /// <summary>
    /// How long to wait for the death to finish playing itself out before the screen is brought back anyway, in
    /// seconds. The death's own ending takes a little over four.
    /// </summary>
    private const float DeathEffectWaitTime = 10f;

    /// <summary>
    /// The name of the FSM that plays a death out on what the game puts up for it.
    /// </summary>
    private const string DeathAnimFsmName = "Hero Death Anim";

    /// <summary>
    /// The name of the bool of the camera's shake FSM that the death shown over a player keeps the screen rumbling
    /// with, for as long as it is on.
    /// </summary>
    private const string DeathRumbleName = "RumblingMed";

    /// <summary>
    /// The frame the game's own death was started behind a dark screen, for a player who already heard it once while
    /// lying down. What the game puts up for a death plays its sounds as it starts: on that frame when it is taken out
    /// of the pool, which starts it over at once, or on the next when it is made new.
    /// </summary>
    private static int _deathHeardFrame = -2;

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
        /// Whether the wait ended because the partner went down as well, which makes this one of two deaths rather than
        /// a death of one player that the other lived through.
        /// </summary>
        public bool PartnerDown { get; set; }

        /// <summary>
        /// Whether the player died while a lava was chasing the two of them, which leaves no cocoon: they stand up
        /// beside the partner a few seconds later instead (see <see cref="UpdateChaseStandUp"/>).
        /// </summary>
        public bool Chase { get; set; }

        /// <summary>
        /// Where the partner said to stand up, for a death in the chase, or null to stand up where they fell.
        /// </summary>
        public Vector2? StandAt { get; set; }

        /// <summary>
        /// The game's death shown over the player as they went down, while it is still playing.
        /// </summary>
        public GameObject? Effect { get; set; }

        /// <summary>
        /// The last frame the wait itself, or the way to the bench after it, ran on. Both run on every frame for as
        /// long as anything is holding them, so a frame count that has stopped moving means whatever held it is gone.
        /// </summary>
        public int HeldFrame { get; set; } = Time.frameCount;
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
        public Action<HitInstance>? Hits;

        /// <summary>
        /// The frame an effect was last played on, so that an attack which lands twice in one frame does not stack
        /// two of them. The game's own hit effects keep the same guard.
        /// </summary>
        private int _lastEffectFrame = -1;

        /// <inheritdoc/>
        public IHitResponder.HitResponse Hit(HitInstance damageInstance) {
            // Only the local player's own attacks open it. Creatures swing through where it lies as well, and the
            // attacks of some of them hit whatever they touch, which counted towards pulling the partner up and paid
            // this player silk for it. For them there is nothing there.
            if (!damageInstance.IsHeroDamage) {
                return IHitResponder.Response.None;
            }

            Hits?.Invoke(damageInstance);
            PlayHitEffect(damageInstance);

            // Not None. That is the answer for "there was nothing there", so the nail passes straight through with
            // no impact, no sound and nothing to bounce off - which is why hitting the cocoon felt like hitting air
            // and a downward strike would not pogo. GenericHit is what something solid but undamageable answers.
            return IHitResponder.Response.GenericHit;
        }

        /// <summary>
        /// Plays what a thread spinner in the world plays where a hit draws silk out of it
        /// (<see cref="PlaySilkHitEffect"/>).
        ///
        /// It has to be done here because nothing else will. Every hit effect in this game is played by the thing
        /// that was hit - the health of an enemy, or the spinner itself - and a cocoon is neither. So the swing
        /// landed, and counted, and looked and felt like nothing at all.
        /// </summary>
        private void PlayHitEffect(HitInstance hit) {
            if (_lastEffectFrame == Time.frameCount) {
                return;
            }

            _lastEffectFrame = Time.frameCount;

            // Whether the hit draws silk by the game's own rule (HeroController.SilkGain), which is what pays the
            // silk for it (OnRescueCocoonHit)
            PlaySilkHitEffect(
                gameObject,
                hit.SilkGeneration == HitSilkGeneration.Full ||
                hit.SilkGeneration == HitSilkGeneration.FirstHit && hit.IsFirstHit
            );
        }
    }

    /// <summary>
    /// The game's own name for the strike a thread spinner in the world puts up where it is hit.
    /// </summary>
    private const string StrikeEffectName = "Strike Nail R";

    /// <summary>
    /// The game's own name for the threads that burst out of a thread spinner in the world where it is hit.
    /// </summary>
    private const string SilkBurstEffectName = "Silk Break Effect";

    /// <summary>
    /// The strike, once it has been found in the pool the game fills as it starts.
    /// </summary>
    private static GameObject? _strikeEffect;

    /// <summary>
    /// The threads bursting out, once they have been found in the pool the game fills as it starts.
    /// </summary>
    private static GameObject? _silkBurstEffect;

    /// <summary>
    /// Puts up on a cocoon what a thread spinner in the world puts up where a hit draws silk out of it
    /// (ThreadSpinner.Hit): the strike, the threads bursting out, and the flash and sound of silk being gained.
    ///
    /// These are effects the whole game shares, not the spinner's own. The first two wait in the pool the game fills
    /// as it starts, and the third is the one the game plays for a hit that gains the player something
    /// (Effects.RageHitHealthEffectPrefab).
    /// </summary>
    /// <param name="cocoon">The cocoon that was hit.</param>
    /// <param name="silkGained">Whether the local player gained silk by the hit, which the flash and the sound are
    /// about.</param>
    private static void PlaySilkHitEffect(GameObject cocoon, bool silkGained) {
        try {
            if (_strikeEffect == null || _silkBurstEffect == null) {
                foreach (var startup in ObjectPool.instance.startupPools) {
                    if (startup.prefab == null) {
                        continue;
                    }

                    if (startup.prefab.name == StrikeEffectName) {
                        _strikeEffect = startup.prefab;
                    } else if (startup.prefab.name == SilkBurstEffectName) {
                        _silkBurstEffect = startup.prefab;
                    }
                }
            }

            // Where the cocoon is rather than where its feet are. A cocoon is drawn from the ground up, so its own
            // position is the bottom of it, and an effect put there would go off under the blow.
            var at = cocoon.GetComponentInChildren<Collider2D>() is { } body
                ? (Vector3) body.bounds.center
                : cocoon.transform.position;

            if (_strikeEffect != null) {
                _strikeEffect.Spawn(at);
            }

            if (_silkBurstEffect != null) {
                _silkBurstEffect.Spawn(at);
            }

            if (silkGained && Effects.RageHitHealthEffectPrefab is { } silkGet) {
                silkGet.Spawn(at);
            }
        } catch (Exception e) {
            Logger.Warn($"Could not play the effect of a hit on a cocoon: {e.Message}");
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
    /// where they died sees. A player lying down loads nothing, so without this they were lying in a cocoon that was
    /// nowhere on their screen.
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
        Entity.Entity.EachGamePartBegan += EndFightOfFallenBoss;
        _hazardRespawnHook = WatchForTheRoomPuttingThePlayerBack();
        _burnHook = WatchForBurns();
        _respawnResetHook = CreateHook(
            typeof(HeroController).GetMethod("HazardRespawnReset", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<HeroController>, HeroController>(OnRespawnReset)
        );
        _timePassesHook = CreateHook(
            typeof(global::GameManager).GetMethod("TimePasses", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<global::GameManager>, global::GameManager>(OnTimePasses)
        );
        _specialDamageHook = CreateHook(
            typeof(HeroController).GetMethod(
                "DoSpecialDamage",
                InstanceFlags,
                null,
                [typeof(int), typeof(bool), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool)],
                null
            ),
            new Action<Action<HeroController, int, bool, string, bool, bool, bool, bool>, HeroController, int, bool,
                string, bool, bool, bool, bool>(OnSpecialDamage)
        );
        _deathSoundHook = CreateHook(
            typeof(AudioPlayerOneShotSingle).GetMethod("OnEnter", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<AudioPlayerOneShotSingle>, AudioPlayerOneShotSingle>(OnDeathSound)
        );
    }

    /// <summary>
    /// The hook on the sounds FSMs play, which a death is one of.
    /// </summary>
    private Hook? _deathSoundHook;

    /// <summary>
    /// Keeps the sounds of the game's own death quiet when it plays behind a dark screen for a player who already
    /// heard them while lying down (<see cref="_deathHeardFrame"/>). Both players going down made the one who went
    /// down first hear their death twice.
    /// </summary>
    private static void OnDeathSound(Action<AudioPlayerOneShotSingle> orig, AudioPlayerOneShotSingle self) {
        if (Time.frameCount - _deathHeardFrame <= 1 && self.Fsm?.Name == DeathAnimFsmName) {
            self.Finish();

            return;
        }

        orig(self);
    }

    /// <summary>
    /// The hook on the damage that goes around the defences of the player.
    /// </summary>
    private Hook? _specialDamageHook;

    /// <summary>
    /// Keeps the damage that goes around the defences of the player - frost, and what the room marks them with - off
    /// a player lying down.
    ///
    /// Going down takes the player out of reach of hits (<see cref="GoDown"/>), but this damage does not ask about
    /// that: it goes straight to the health, and asks only whether the player is changing rooms
    /// (HeroController.DoSpecialDamage). The game keeps it off the dead by stopping what deals it - the frost stops
    /// building for a dead player - and a player lying down is not dead. So a player lying in the cold lost health they
    /// did not have, broke what they were carrying, and asked for a death again every time the frost came round, each
    /// of which showed the partner a death of theirs.
    /// </summary>
    private static void OnSpecialDamage(
        Action<HeroController, int, bool, string, bool, bool, bool, bool> orig,
        HeroController self,
        int damageAmount,
        bool playEffects,
        string damageEvent,
        bool canDie,
        bool allowFracturedMaskBreak,
        bool justTakeHealth,
        bool isFrostDamage
    ) {
        if (PlayerTargetRegistry.IsPlayerDown(self.gameObject)) {
            return;
        }

        orig(
            self, damageAmount, playEffects, damageEvent, canDie, allowFracturedMaskBreak, justTakeHealth, isFrostDamage
        );
    }

    /// <summary>
    /// Keeps the world where it is for a death that the partner lived through.
    ///
    /// A death is the game's main way of letting time pass: once the save is written and just before the player is
    /// taken to their bench, <c>GameManager.PlayerDead</c> calls this, and it moves characters on, rolls whether
    /// some of them are out, and ends what only lasts a few rooms. In a two-player save only both players going down
    /// counts as time passing - one of them waking at a bench while the other is still out there has not made any time
    /// pass for the world the two of them share. Being pulled back up never gets here at all. Leaving the game and the
    /// other callers are untouched.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The game manager.</param>
    private void OnTimePasses(Action<global::GameManager> orig, global::GameManager self) {
        if (_deathPassesNoTime) {
            _deathPassesNoTime = false;

            if (HeroController.instance is { } hero && hero.cState.dead) {
                Logger.Info("Not letting time pass for this death: the teammate is still standing");

                return;
            }
        }

        orig(self);
    }

    /// <summary>
    /// The hook on the other way a player can die.
    /// </summary>
    private Hook? _hazardRespawnHook;

    /// <summary>
    /// The hook on the game moving its world on after a death.
    /// </summary>
    private Hook? _timePassesHook;

    /// <summary>
    /// Whether the death that is on its way to the bench is one the partner lived through, so the world does not move
    /// on for it. Set when a player lying down is let go to the bench, and used up by the one call that moves the
    /// world on.
    /// </summary>
    private bool _deathPassesNoTime;

    /// <summary>
    /// Says when the room itself kills the player and puts them straight back.
    ///
    /// Only <c>HeroController.Die</c> is wrapped by this mod, and lava, spikes and falls do not go through it: they
    /// take their health, darken the screen and set the player down again at the room's own mark, all without ever
    /// calling it. To the player that is dying and coming back to life; in the log it was nothing at all, and a
    /// player saying they kept dying and reviving could not be matched to a single line. It changes nothing - the
    /// game does this by itself and should - it only stops being invisible.
    /// </summary>
    private Hook? WatchForTheRoomPuttingThePlayerBack() {
        try {
            var method = typeof(HeroController).GetMethod(
                "HazardRespawn",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (method == null) {
                Logger.Warn("Could not find how the room puts a player back, so those deaths stay unsaid");

                return null;
            }

            return new Hook(
                method,
                (Func<Func<HeroController, IEnumerator>, HeroController, IEnumerator>) OnRoomPutThePlayerBack
            );
        } catch (Exception e) {
            Logger.Error($"Could not watch for the room putting a player back:\n{e}");

            return null;
        }
    }

    /// <summary>
    /// Writes down a death the room dealt and undid by itself, and for a burn in the chase of a lava, puts the player
    /// back beside the partner (<see cref="OnBurn"/>).
    /// </summary>
    /// <param name="orig">The original call.</param>
    /// <param name="self">The hero controller.</param>
    private IEnumerator OnRoomPutThePlayerBack(Func<HeroController, IEnumerator> orig, HeroController self) {
        try {
            var playerData = PlayerData.instance;
            TakeBurnRedirect(playerData);
            Logger.Info(
                "The room itself killed the player and is putting them straight back: from " +
                $"{self.transform.position} to {(playerData == null ? "?" : playerData.hazardRespawnLocation.ToString())}, " +
                $"with {(playerData == null ? "?" : playerData.health.ToString())} health left"
            );
        } catch (Exception e) {
            Logger.Warn($"Could not write down the room putting the player back: {e.Message}");
        }

        return orig(self);
    }

    /// <summary>
    /// Keeps a death of the local player from being played out while the partner can still pull them back up, and lays
    /// them down instead. Anything that leaves no cocoon to open, or leaves nobody to open it, plays out untouched.
    /// </summary>
    /// <param name="death">The coroutine of the game that plays out the death.</param>
    /// <param name="nonLethal">Whether the death was non-lethal.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private IEnumerator WrapDeath(IEnumerator death, bool nonLethal, bool frostDeath) {
        // The game makes every death in a memory non-lethal itself, inside the death and so after this is asked
        // (HeroController.Die). It leaves no cocoon there, so none of them is held either.
        nonLethal |= global::GameManager.instance != null && global::GameManager.instance.IsMemoryScene();

        // Before anything is decided about this death, including the deaths this mod then keeps its hands off. One
        // of those is a death that is not lethal, and from the player's chair a death that is not lethal is dying
        // and coming back to life by itself - which is exactly the thing that was reported and that no line in
        // either log could be matched to, because this used to sit below the return.
        SayWhatTheDeathFound(nonLethal, frostDeath);

        // A player lying down is not marked dead, so the game can ask for their death again: a hit that deals its
        // damage directly does so whenever the health is at nothing and the player is not dead
        // (HeroController.CheckDeathCatch). The death they are lying in is the only one there is, and it stays the
        // only one on the way from there to the bench, until the game's own death has marked them dead.
        if (HeroController.instance is { } hero && PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            Logger.Info("Not playing out a death of a player who is already lying down");

            return Array.Empty<object>().GetEnumerator();
        }

        // Nor is a player the game has already marked dead laid down on their way to the bench. Damage that goes
        // around the defences of the player asks for a death without looking at that (HeroController.DoSpecialDamage),
        // and the game's own death answers it by itself.
        if (HeroController.instance is { cState.dead: true }) {
            return death;
        }

        _deathPassesNoTime = false;

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

        // Before the player has gone down, so that anything still showing afterwards can be told apart from what the
        // room was already showing
        NoteWhatIsOnAroundThePlayer();

        return LieDown(death, frostDeath);
    }

    /// <summary>
    /// Writes down what the player was when this death reached them.
    /// </summary>
    /// <param name="nonLethal">Whether the death was non-lethal, which this mod leaves entirely alone.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private void SayWhatTheDeathFound(bool nonLethal, bool frostDeath) {
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
                $"non-lethal: {nonLethal}, " +
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
        rescue.PartnerDown = true;
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

        // Steel Soul keeps the game's own rule, whoever else is still standing: its one death is the end
        if (PlayerData.instance is { permadeathMode: not GlobalEnums.PermadeathModes.Off }) {
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
    /// Lays the player down where they are instead of playing the death out, holds them there while the partner has a
    /// chance to open their cocoon, and then either puts them back on their feet or plays the game's own death after
    /// all, from its first step.
    /// </summary>
    /// <param name="death">The coroutine of the game that plays out the death, not started yet.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private IEnumerator LieDown(IEnumerator death, bool frostDeath) {
        var hero = HeroController.instance;
        if (hero == null || !StartRescueWait(hero) || _rescue is not { } lying) {
            // Nothing is waiting, so the death plays out the way it always did
            while (death.MoveNext()) {
                yield return death.Current;
            }

            yield break;
        }

        try {
            GoDown(hero, lying, frostDeath);
        } catch (Exception e) {
            LogRescueError(e);
        }

        // The death is shown over the player the way the game shows it - it belongs to the game and it is worth
        // seeing - and the screen is given back as soon as it is over, so that the rest of the wait is spent looking at
        // the room, the cocoon and the line that explains what is happening rather than at nothing at all.
        MonoBehaviourUtil.Instance.StartCoroutine(BringScreenBackAfterDeath(lying));

        // Running out of time is decided here rather than only frame by frame next door, because the update that runs
        // frame by frame sits behind early returns of its own: a marker that is gone for a moment is enough to stop it,
        // and then this would keep the player down for as long as the game runs. Going down has already switched the
        // pause menu off, so there would be nothing left to do about it but kill the game. This loop is the one thing
        // that is certainly still running while the player lies there, so the way out that must always work is in it.
        // Seeded with what the key is doing right now rather than with "not held". A player dies in the middle of
        // doing things, and a key that was already down when they died is not them asking to give up on being saved.
        var leaveHeld = _modSettings.Keybinds.CoopLeave.IsPressed;
        var padButtonWasDown = IsPadButtonDown(_modSettings.Keybinds.CoopLeave, out _);

        while (_rescue is { Outcome: RescueOutcome.Waiting } waiting) {
            waiting.HeldFrame = Time.frameCount;

            if (Time.unscaledTime - waiting.StartTime >= RescueWaitTime) {
                Logger.Info("Waited the whole time to be pulled back up and nobody came, going to the bench");
                waiting.Outcome = RescueOutcome.Ended;

                break;
            }

            // The line and the way out live here for the same reason the time limit does: this is the one thing that
            // is certainly still running while the player lies there. They used to live next door, behind those early
            // returns, so a player could be left staring at a cocoon with nothing on screen telling them anything and
            // no way out but to sit through the whole wait.
            ShowTheWayOutOfTheWait(waiting);

            // Every press of the pad button is written down as it happens, whatever comes of it. A wait that ran out
            // with no line in it could not be told apart from a player who never pressed anything, and a player who
            // pressed the way out and was not let out cannot tell us whether the press never reached the game,
            // arrived while the chat had the keys, or came too early to count.
            var padButtonDown = IsPadButtonDown(_modSettings.Keybinds.CoopLeave, out var pad);
            if (padButtonDown && !padButtonWasDown) {
                var into = Time.unscaledTime - waiting.StartTime;
                Logger.Info(
                    $"The pad button that gives up on the wait went down on '{pad}' {into:0.0}s into it, " +
                    (!_modSettings.Keybinds.Enabled
                        ? "while the chat has the keys of this mod switched off"
                        : into < LeaveKeyDeadTime
                            ? "too early into the wait to count"
                            : "with the keys of this mod listening")
                );
            }

            padButtonWasDown = padButtonDown;

            var leaveNow = _modSettings.Keybinds.CoopLeave.IsPressed;
            if (leaveNow && !leaveHeld && Time.unscaledTime - waiting.StartTime >= LeaveKeyDeadTime) {
                Logger.Info("Gave up waiting to be pulled back up and went to the bench");
                waiting.Outcome = RescueOutcome.Ended;

                break;
            }

            leaveHeld = leaveNow;

            yield return null;
        }

        // Said here because this is the one place that knows which of the ways out of a wait was taken, and a player
        // left standing in neither their own body nor at their bench has no way of telling us which it was
        Logger.Info(
            $"The wait to be pulled back up is over as '{lying.Outcome}', with the screen back: {lying.ScreenBack}"
        );

        // No longer down before being put back on their feet, since giving the body back asks whether anything still
        // holds it (IsHeroTakenByGame)
        if (lying.Outcome == RescueOutcome.Rescued) {
            EndRescueWait();

            if (TryRevive(HeroController.instance, lying)) {
                yield break;
            }

            // A partner who went down while this player was being stood up waits for them, and this player is going
            // to their bench after all: two deaths, two benches
            if (_partnerWaitingRescue != null) {
                lying.PartnerDown = true;
                TellPartnerNobodyIsComing();
            }
        }

        // Time only passes when both players are down. A partner who is still connected and did not go down as well
        // lived through this death, whether this player gave up, ran out of time or could not be stood back up
        _deathPassesNoTime = !lying.PartnerDown && GetCheckedPartner() != null;
        Logger.Info(
            "Letting the game's own death take the player to their bench, " +
            (_deathPassesNoTime ? "without time passing: the teammate is still standing" : "with time passing")
        );

        // The player has already watched their death once, so the game's own is played behind a dark screen rather
        // than shown to them a second time. The bench is reached with the screen dark anyway: the game fades back in
        // only once the player is standing there.
        if (lying.Effect != null) {
            UnityEngine.Object.Destroy(lying.Effect);
        }

        ScreenFaderUtils.Fade(ScreenFaderUtils.GetColour(), Color.black, RescueFadeTime);

        // Still down while the screen goes dark, and marked as held all the way, so that nothing in between takes the
        // player for someone on their feet: a second death would be laid down on top of this one, a partner going
        // down now would be told this player is coming for them, and what only moves a player who is standing - back
        // to the door of a fight, over to a delivery - would move one on their way to a death
        for (var faded = 0f; faded < RescueFadeTime; faded += Time.deltaTime) {
            lying.HeldFrame = Time.frameCount;

            yield return null;
        }

        // The one thing of lying down that the way to the bench does not undo by itself: it gives control and the
        // animation back when the bench room is ready (GameManager.OnNextLevelReady), but knows nothing of this
        if (HeroController.instance is { } dying) {
            dying.RemoveInvulnerabilitySource(RescueInvulnerability);
        }

        // Either the player gave up, or putting them back on their feet did not work, and a player left lying there
        // would be far worse than the bench they expected in the first place. From its first step, which is where the
        // player was headed before the partner could do anything about it: it finds them lying where they went down,
        // so what it writes down of the cocoon and the money in it is written about that place, and it tells the room
        // of the death itself. Its sounds were heard once already, while the player went down (OnDeathSound).
        _deathHeardFrame = Time.frameCount;
        var more = death.MoveNext();

        // That first step has marked the player dead, which is where lying down ends
        EndRescueWait();

        if (!more) {
            yield break;
        }

        yield return death.Current;

        while (death.MoveNext()) {
            yield return death.Current;
        }
    }

    /// <summary>
    /// Takes the player down where they are, as the first step of the game's own death does (HeroController.Die),
    /// without the part that makes it a death: they are not marked dead, nothing is told of it, and their money and
    /// silk stay with them. Nothing in the room acts on a death that never happened - a boss can still fall to the
    /// partner, the money on the floor can still be picked up, the dark over hidden places still follows the player -
    /// and nothing has to be put back when they stand up. The game's own death does all of it if it comes to that.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="rescue">The wait the player goes down for.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private static void GoDown(HeroController hero, PendingRescue rescue, bool frostDeath) {
        if (hero.hazardRespawnRoutine != null) {
            hero.StopCoroutine(hero.hazardRespawnRoutine);
            hero.hazardRespawnRoutine = null;
        }

        hero.ResetSilkRegen();
        hero.audioCtrl.StopSound(GlobalEnums.HeroSounds.FOOTSTEPS_WALK, true);
        hero.audioCtrl.StopSound(GlobalEnums.HeroSounds.FOOTSTEPS_RUN, true);

        // Whatever the player was in the middle of - a swing, a skill, a tool - stops, as it does for a death. Before
        // control is taken, since some of what stops hands control back as it goes.
        EventRegister.SendEvent(EventRegisterEvents.FsmCancel, null);
        hero.RelinquishControl();
        hero.StopAnimationControl();
        PlayerData.instance.disablePause = true;

        hero.StopTilemapTest();
        hero.cState.onConveyor = false;
        hero.cState.onConveyorV = false;
        hero.rb2d.linearVelocity = Vector2.zero;
        hero.CancelRecoilHorizontal();
        hero.AffectedByGravity(false);
        hero.cState.falling = false;
        hero.rb2d.bodyType = RigidbodyType2D.Kinematic;
        hero.ResetMotion(true);
        hero.ResetHardLandingTimer();

        // Out of reach of anything that hurts. The layer stays as it is, unlike in a death, so that nothing the player
        // is standing in sees them leave: the camera stays where it was held, and the dark over a hidden place stays
        // off while they lie inside it.
        HeroBox.Inactive = true;
        hero.heroBox.HeroBoxOff();
        hero.AddInvulnerabilitySource(RescueInvulnerability);
        hero.renderer.enabled = false;

        if (hero.vibrationCtrl != null) {
            hero.vibrationCtrl.PlayHeroDeath();
        }

        rescue.Effect = ShowTheDeath(hero, frostDeath);
    }

    /// <summary>
    /// Shows the game's own death over the player going down: a copy of what the game puts up for it, placed and
    /// dressed the way the game does it (HeroController.Die), with the actions that tell the room about a death and
    /// the one that rids the player of what they carry switched off. Everything else plays as it always does - the
    /// fall, the sound, the shake, the music going quiet and the screen going black - and the screen is given back
    /// when it is over (<see cref="BringScreenBackAfterDeath"/>).
    ///
    /// A copy of its own rather than one out of the game's pool, because the pool would hand the same object to the
    /// game's own death later on, and that one must not come with its announcements switched off.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    /// <returns>The copy, which takes itself away once it has played out.</returns>
    private static GameObject ShowTheDeath(HeroController hero, bool frostDeath) {
        var prefab = hero.GetHeroDeathPrefab(false, false, frostDeath);
        var effect = UnityEngine.Object.Instantiate(prefab, hero.transform.position, prefab.transform.rotation);
        effect.transform.localScale = Vector3.Scale(hero.transform.localScale, prefab.transform.localScale);
        effect.SetActive(true);

        // Switched on first, so that its FSMs are set up, but before they start, which is not until the next frame
        foreach (var fsm in effect.GetComponentsInChildren<PlayMakerFSM>(true)) {
            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is SendEventToRegister or SendEventToRegisterDelay or SetHeroMaggoted) {
                        action.Enabled = false;
                    }
                }
            }
        }

        var animator = effect.GetComponent<tk2dSpriteAnimator>();
        if (animator != null) {
            animator.Library = hero.animCtrl.animator.Library;
        }

        return effect;
    }

    /// <summary>
    /// Where the game would leave the cocoon of a death of the player standing here: the room, and the place in it.
    ///
    /// The same choice the first step of the game's own death makes (HeroController.Die), without writing it down,
    /// since the player lying here has not died: a room can send the cocoon somewhere of its choosing, and otherwise it
    /// goes to the nearest of the places the room keeps for one, or where the player is if the room keeps none.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    private static (string Scene, Vector2 Position) WhereTheGameLeavesTheCocoon(HeroController hero) {
        var proxy = HeroCorpseMarkerProxy.Instance;
        if (proxy != null) {
            return (proxy.TargetSceneName, proxy.TargetScenePos);
        }

        var at = (Vector2) hero.transform.position;
        var marker = HeroCorpseMarker.GetClosest(at);

        return (hero.gm.GetSceneNameString(), marker != null ? marker.Position : at);
    }

    /// <summary>
    /// Starts waiting to be pulled back up, and tells the partner where the cocoon is so their game can show it.
    /// </summary>
    /// <returns>Whether a wait was started.</returns>
    /// <param name="hero">The hero controller.</param>
    private bool StartRescueWait(HeroController hero) {
        if (GetCheckedPartner() is not { } partner) {
            return false;
        }

        // Where the game itself would leave the cocoon, so both games point at the same spot
        var (scene, position) = WhereTheGameLeavesTheCocoon(hero);
        if (string.IsNullOrEmpty(scene)) {
            return false;
        }

        // In the chase of a lava nobody can come back for a cocoon: the lava is already over where the player fell.
        // They stand up beside the partner instead, the way a game made for two brings a player back.
        var chase = IsChaseWithPartner(partner);

        var gameManager = global::GameManager.instance;
        _rescue = new PendingRescue(scene, position) {
            // Taken now, before the death shown over the player has played any of itself out, so it is the music of
            // the room rather than the music of the death
            Music = gameManager == null ? null : gameManager.AudioManager.CurrentMusicCue,
            Key = ++_lastRescueKey,
            Chase = chase
        };
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueOffer,
            Scene = scene,
            Values = chase ? [position.x, position.y, 1f] : [position.x, position.y],
            Key = _rescue.Key
        });
        SayTheLocalPlayerIsDown(true);

        if (chase) {
            Logger.Info($"Died in the chase of the lava in '{scene}', so standing up beside {partner.Username} soon");

            return true;
        }

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
    /// Says whether the local player is lying in a cocoon rather than standing.
    ///
    /// A death this mod holds never reloads the room, so the hero goes on existing where it fell for as long as the
    /// player lies there - and the enemies in the room, which were never told any different, went on attacking that
    /// spot. Tied to the wait rather than to the cocoon: the player is down from the moment they go down until they are
    /// back on their feet or the game's own death has marked them dead, and the cocoon can go before that.
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
    /// Ends the fight of a boss that fell, asked when the boss goes into the end that each game plays for its own
    /// player (Entity.EachGamePartBegan), and for any boss once its save has it beaten. Its checkpoint ends: the boss
    /// counts as beaten only once the local player has bound it, and the partner dropping out in the middle of that
    /// took them back to the door with the boss still holding them. And the local player is stood up the way a rescue
    /// does if they lie in their cocoon. What comes after a boss is for both players, and a game whose player still
    /// lay there had them miss it: they neither bound the boss nor followed the other into the memory it sends them
    /// to, and found its room shut when they came back.
    /// </summary>
    private void EndFightOfFallenBoss() {
        if (GetCurrentMarker() is { BossScene: { } bossScene } marker && bossScene == SceneUtil.GetCurrentSceneName()) {
            Logger.Info($"The boss in '{bossScene}' fell, so its checkpoint ends");
            ClearCheckpoint(marker);
        }

        if (_rescue is { Outcome: RescueOutcome.Waiting } rescue) {
            Logger.Info("The boss fell while the local player lay waiting to be pulled up, so they get up");
            rescue.Outcome = RescueOutcome.Rescued;
        }
    }

    /// <summary>
    /// Stops waiting to be pulled back up, so the player is no longer down, and tells the partner so that the cocoon
    /// stops being shown to them.
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
        SayTheLocalPlayerIsDown(false);
        _uiManager.CoopPrompt.Hide();

        if (GetCheckedPartner() is { } partner) {
            Send(new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.RescueEnd
            });
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
    /// the game sends while respawning. A player lying down never respawns, so the plates stayed closed for the whole
    /// wait, over a screen this mod had just deliberately given back: a dark cloud around a player who could otherwise
    /// see the room, their cocoon and their teammate coming.
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
    /// A death is a sequence that switches things on at its start and off again at its end, and a player pulled up
    /// early never sees its end. Which things those are cannot be guessed from a report that says only how many were
    /// dealt with, so the only honest way to name one is to know what was there beforehand.
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
    /// event that ends them, and the thing that sends it is further along the death than a player pulled up early
    /// ever sees. So the screen was still shaking after the player was back on their feet, and would have gone on
    /// shaking until they left the room.
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

        if (rescue.Chase) {
            _uiManager.CoopPrompt.Show(Lang.Pick(
                $"You will stand up beside {partnerName} in a moment. Press {LeaveKeyName} to go to your bench instead",
                $"马上会在 {partnerName} 身边站起来。按 {LeaveKeyName} 直接回长椅"
            ));

            return;
        }

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
    /// standing there. A player lying down never gets that far, so nobody ever fades anything back in - which is the
    /// black screen the waiting player was left staring at, unable to see the world, their own cocoon, or the line
    /// telling them what is happening.
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
        var start = Time.unscaledTime;

        while (rescue.Effect != null && rescue.Effect.activeInHierarchy &&
               Time.unscaledTime - start < DeathEffectWaitTime) {
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
    /// Puts the local player back on their feet where they fell, with half of their health. This undoes what going
    /// down did (<see cref="GoDown"/>) and what the death shown over them left on the screen and in the sound, in the
    /// order the game's own respawn does it. Their money and silk never left them.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="rescue">The wait that is ending, which holds what was playing before the death.</param>
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

            // What is left of the death shown over them, for a player pulled up before it had played out
            if (rescue.Effect != null) {
                UnityEngine.Object.Destroy(rescue.Effect);

                // Cut short before its end, which is where it lets go of the rumble it holds the camera in: its Blow
                // and Explode states switch the camera's RumblingMed on, and only Ended switches it off. Left on, the
                // camera went back to rumbling every time it came to rest, until the room changed.
                var cameras = GameCameras.instance;
                var rumble = cameras == null || cameras.cameraShakeFSM == null
                    ? null
                    : cameras.cameraShakeFSM.FsmVariables.FindFsmBool(DeathRumbleName);
                if (rumble != null) {
                    rumble.Value = false;
                }
            }

            // After the rumble is let go of, since a camera that stops shaking goes back to whatever it is still told
            StopTheScreenShaking();
            RestoreMusic(rescue);

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

            // Put back on their feet where they fell is the whole point of this everywhere else. Where that spot is
            // in lava, spikes or a drop, or over one, it is the one place that cannot be done: a player put back there
            // falls straight in - a creature can kill a player in the middle of a jump across lava - and lava takes
            // two masks, all that a rescue gives back of five. The game keeps a place of its own for exactly this,
            // which is where it would have put them itself.
            //
            // What the death left behind cannot tell this. The game marks a death by the room (cState.hazardDeath)
            // only on the way back to that place after a hit the player lives through; the hit that takes the last
            // of the health goes straight to the death without it (HeroController.TakeDamage).
            // Beside the partner, for a death in the chase: a place their game picked as clear of the lava, while the
            // game's own mark is usually under it by now
            if (rescue.StandAt is { } standAt) {
                Logger.Info($"Standing back up beside the partner at {standAt}");
                hero.transform.position = new Vector3(standAt.x, standAt.y, hero.transform.position.z);
            } else if (IsInOrOverTheRoomsHarm(hero.transform.position)) {
                var safe = playerData.hazardRespawnLocation;
                Logger.Info(
                    $"Standing back up at {safe} instead of {hero.transform.position}, which is in or over lava, " +
                    "spikes, a drop or sand that pulls players under"
                );
                hero.transform.position = safe;
            }

            // Once the player stands where they will play on from, so that the camera locks to the area they are in
            ReleaseHeldCamera();

            hero.cState.onGround = true;
            hero.cState.falling = false;
            hero.cState.hazardDeath = false;
            hero.cState.recoiling = false;

            playerData.disablePause = false;

            hero.ResetMotion();
            hero.ResetHardLandingTimer();
            hero.ResetInput();
            hero.ResetLook();

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

            // A silk for every hit it took to open the cocoon. The one who opened it got one for each hit as well
            // (OnRescueCocoonHit): a whole spool, which is what the game gives for breaking one's own cocoon, made a
            // bind the moment they stood up, and with a bind that nothing interrupts that was all of their health.
            hero.AddSilk(RescueHits, true);

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

            // Wrapped for the same reason: none of this is worth failing a rescue over either
            try {
                // The cold the player went down in is let go of, as the game's own death does a moment in
                // (HeroController.Die). Left alone, a player who froze stood up frozen and started freezing again.
                hero.SetFrostAmount(0f);
                StatusVignette.SetFrostVignetteAmount(0f);

                // The death shown over them turned the rumble of the pad down to nothing, and only the fade into the
                // next room turns it back up (GameManager.FadeSceneIn), so a player pulled back up played on without
                // it until they left the room. This is the call that fade makes.
                VibrationManager.FadeVibration(1f, 0.25f);
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
    /// Whether the given place is inside something of the room that hurts - lava, spikes, a drop - or has one as the
    /// first thing below it rather than ground the player can stand on, so that a player stood up there falls
    /// straight into it.
    /// </summary>
    /// <param name="position">Where the player would stand up.</param>
    private static bool IsInOrOverTheRoomsHarm(Vector2 position) {
        // Inside is asked on its own. This game's physics settings keep a query from seeing a collider it starts in
        // (queriesStartInColliders is off), so the look below would pass the lava the player is sunk in and find the
        // floor under it
        var inside = new List<Collider2D>();
        Physics2D.OverlapPoint(position, new ContactFilter2D().NoFilter(), inside);
        if (inside.Exists(IsTheRoomsHarm)) {
            return true;
        }

        var hits = new List<RaycastHit2D>();
        Physics2D.Raycast(position, Vector2.down, new ContactFilter2D().NoFilter(), hits, Mathf.Infinity);

        // Ground is anything solid that the body of the player collides with, as the game's own table of layers says
        var nearest = Mathf.Infinity;
        var harm = false;
        foreach (var hit in hits) {
            var collider = hit.collider;
            var isGround = !collider.isTrigger &&
                           !Physics2D.GetIgnoreLayerCollision(PlayerLayer, collider.gameObject.layer);
            var isHarm = IsTheRoomsHarm(collider);
            if ((isGround || isHarm) && hit.distance < nearest) {
                nearest = hit.distance;
                harm = isHarm;
            }
        }

        return harm;
    }

    /// <summary>
    /// Whether a collider hurts the player for the room rather than for a creature. The game reads the harm off the
    /// collider's own object (HeroBox.CheckForDamage), and answers every kind of it but a creature's attack and an
    /// explosion by putting the player back at the last safe place (HeroController.TakeDamage).
    ///
    /// The sand that creatures pull a player down into hurts without any of that: its trigger tells the group of
    /// creatures (RangeAttackGroup.OnCustomDamageTriggerEntered), whose event drags the player under and puts them
    /// back at the same safe place, so it is known by being the trigger of such a group.
    /// </summary>
    private static bool IsTheRoomsHarm(Collider2D collider) {
        if (collider.TryGetComponent<DamageHero>(out var damage) && damage.hazardType is not (
                GlobalEnums.HazardType.NON_HAZARD or GlobalEnums.HazardType.ENEMY or GlobalEnums.HazardType.EXPLOSION)) {
            return true;
        }

        return collider.TryGetComponent<TriggerEnterEvent>(out var trigger) &&
               collider.GetComponentInParent<RangeAttackGroup>(true) is { } group &&
               RangeAttackGroupDamageTriggerField?.GetValue(group) is TriggerEnterEvent groupTrigger &&
               groupTrigger == trigger;
    }

    /// <summary>
    /// The trigger through which a group of creatures that live in the ground hurts the player.
    /// </summary>
    private static readonly FieldInfo? RangeAttackGroupDamageTriggerField =
        typeof(RangeAttackGroup).GetField("customDamageTrigger", InstanceFlags);

    /// <summary>
    /// Lets the camera follow the player again if it is still held where they went down. Some of the room's harm holds
    /// it still as it pulls the player under - the sand of the creatures that drag a player in calls FreezeInPlace in
    /// its hit - and only the way back to the room's mark or to the bench lets go of it again, neither of which comes
    /// for a death that is held for a rescue. So the camera stayed where the player went under while they walked
    /// away. This is the game's own way of letting go of it (the CameraStopFreeze action), which also locks it back
    /// to the area of the room the player is in.
    /// </summary>
    private static void ReleaseHeldCamera() {
        var cameras = GameCameras.instance;
        var camera = cameras == null ? null : cameras.cameraController;
        if (camera == null || camera.mode != CameraController.CameraMode.FROZEN) {
            return;
        }

        Logger.Info("The camera was still held where the player went down: letting it follow them again");
        camera.StopFreeze(true);
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
            // The wait marks every frame it runs on, and so does the way to the bench after it, so one that has
            // stopped marking them is gone: the room was loaded again underneath it, or the save was left, and
            // whatever was holding it went away with it. After a room is loaded this is a different hero entirely.
            // Nothing else can end the wait once that has happened, so it ends here, which also takes the cocoon of
            // someone who is walking around again off the screen of the other player.
            if (_rescue is { } held && Time.frameCount - held.HeldFrame > 2) {
                EndRescueWait();

                return;
            }

            if (_rescue is { Outcome: RescueOutcome.Waiting } rescue) {
                if (partner == null) {
                    // Nobody is left to open it
                    rescue.Outcome = RescueOutcome.Ended;
                    return;
                }

                // Only remembered here. Showing the line and reading the key that gives up on the wait both happen in
                // the wait itself, which is the one thing that keeps running while the player lies there
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
        // Said either way, because this news is not sent again, and without a line here a partner who went down
        // unseen could not be told from one whose news never arrived
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            Logger.Info(
                $"Not taking the news that {player.Username} went down in '{update.Scene}': " +
                $"two-player save loaded: {GetCurrentMarker() != null}, " +
                $"saves checked with: {_checkedWith?.ToString() ?? "nobody"}"
            );

            return;
        }

        Logger.Info($"{player.Username} went down in '{update.Scene}' ({update.Key})");

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

        // Both went down before either heard about the other, so each is lying there waiting for someone who cannot
        // come. Two deaths are two benches in whichever order the news arrives, and this wait ends here.
        if (_rescue is { Outcome: RescueOutcome.Waiting }) {
            OnRescueLost(player);
        }

        // This player is down themselves - lying there, on their way to a bench, or dead as far as the game knows - so
        // nobody is coming for the partner either: two deaths, two benches. Told to them even when this player's own
        // cocoon should have told them already, because it may not have reached them: a partner whose news was lost
        // in a stretch of the connection carrying nothing lay there waiting for someone who had long gone to their
        // bench, and let no time pass for a death that both of them died. A player whose cocoon the partner has just
        // broken is standing up rather than down: the partner died right after pulling them up, and the news of both
        // can arrive together, before this player is back on their feet.
        if (HeroController.instance is { } hero && _rescue is not { Outcome: RescueOutcome.Rescued } &&
            (PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || hero.cState.dead)) {
            TellPartnerNobodyIsComing();

            return;
        }

        // A death in the chase of a lava leaves no cocoon to show, now or on walking into that room later
        if (update.Values.Count >= 3 && update.Values[2] > 0f) {
            _partnerCocoonScene = "";
            OnChaseDeath(player, update);

            return;
        }

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

        // The hit the partner landed, shown on this side of it too. The silk it drew went to them.
        if (_rescueOwnCocoon != null) {
            PlaySilkHitEffect(_rescueOwnCocoon, false);
        }

        if (update.Part >= (update.PartCount == 0 ? RescueHits : update.PartCount)) {
            // A death in the chase is stood up beside the partner, at the place their game picked
            if (update.Values.Count >= 2) {
                rescue.StandAt = new Vector2(update.Values[0], update.Values[1]);
            }

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

        ForgetChaseDeath(player.Id);

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
    /// Counts a hit of the local player on the cocoon of the partner, and tells them how far it got. Each one pays
    /// silk the way a hit on a creature does (HealthManager.TakeDamage), by the game's own rule for the attack: what
    /// gives no silk on a creature gives none here, and an attack that hits many times pays for its first.
    /// </summary>
    /// <param name="hit">The hit.</param>
    private void OnRescueCocoonHit(HitInstance hit) {
        if (_rescueTarget is not { } target || GetCheckedPartner() is not { } partner ||
            partner.Id != target.PlayerId) {
            return;
        }

        target.Hits++;

        if (HeroController.instance is { } hero) {
            hero.SilkGain(hit);
        }

        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueHit,
            Part = (ushort) target.Hits,
            PartCount = RescueHits,
            Key = target.Key
        });

        if (target.Hits >= RescueHits) {
            // Counted as standing from here on, as a partner stood up in the chase of a lava is. Their game only says
            // so once this hit has reached it and they are back on their feet, and a player who died in between - in
            // a fight, right after pulling them up - was taken for the second of two deaths and sent to their bench,
            // leaving the partner they had just saved standing alone
            _partnerWaitingRescue = null;
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
    /// Takes the body of a partner who lies in the cocoon shown here off the screen again, now that their character
    /// has been put in the room. Walking into the room where they lie shows the cocoon as soon as the room loads, but
    /// their character only comes a moment later, once the server has said who is already here, and it comes playing
    /// the last thing they did, which is their death. Nothing hid it after that, so their body stood beside their
    /// cocoon for the rest of the wait.
    /// </summary>
    /// <param name="player">The player whose character was just put in the room.</param>
    internal void HideCharacterOfPartnerInCocoon(ClientPlayerData player) {
        if (_rescueTarget is not { } target || target.PlayerId != player.Id) {
            return;
        }

        Logger.Info($"{player.Username} came into view lying in their cocoon here, so their body is hidden again");
        SetPartnerBodyHidden(player.Id, true);
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

                // Nothing should come looking for someone who is lying in a cocoon. Off the list, nothing chooses
                // them from here on; an enemy that had already chosen them is let go of them as well, as one is
                // when this game's own player goes down. Left holding them, it kept them for as long as they lay in
                // its sight, which is all of the wait: a boss went on striking the cocoon of the partner it had
                // been fighting while the player still standing was right there.
                PlayerTargetRegistry.UnregisterRemotePlayer(body);
                GamePatcher.ForgetPlayerAsTarget(body);

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
        if (_chaseHiddenPartner is { } hidden) {
            ForgetChaseDeath(hidden);
        }

        _chaseStandUp = null;
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
