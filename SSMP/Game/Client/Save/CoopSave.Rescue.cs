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
        // nothing for the partner to open and the player would wait for something that cannot come
        if (nonLethal || !CanWaitForRescue()) {
            return death;
        }

        return HoldDeath(death);
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

        // The wait is on, so the death's own playing-out has to go now rather than at the end of it. Left alone it
        // holds a black screen over the whole wait, which hides the world, the cocoon and the line that explains
        // what is happening - the wait becomes indistinguishable from the game having frozen.
        ClearDeathSequence();

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

        var rescued = _rescue is { Outcome: RescueOutcome.Rescued };
        EndRescueWait();

        if (rescued && TryRevive(HeroController.instance)) {
            yield break;
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

        _rescue = new PendingRescue(scene, position);
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueOffer,
            Scene = scene,
            Values = [position.x, position.y]
        });
        Logger.Info($"Waiting for {partner.Username} to open the cocoon in '{scene}'");

        return true;
    }

    /// <summary>
    /// Stops waiting to be pulled back up, and tells the partner so that the cocoon stops being shown to them.
    /// </summary>
    private void EndRescueWait() {
        _rescue = null;
        _rescueLeaveHeld = false;
        _uiManager.CoopPrompt.Hide();

        if (GetCheckedPartner() is { } partner) {
            Send(new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.RescueEnd
            });
        }
    }

    /// <summary>
    /// Takes away the object the death spawned to play itself out, which is what darkens the screen.
    ///
    /// The death spawns it and activates it *before* its first yield - the exact point a held death stops at. Holding
    /// the death stops the player being taken to their bench but takes nothing away, so that object carries on
    /// playing the death out over a player who is not going anywhere. That is a black screen for as long as the wait
    /// lasts: up to <see cref="RescueWaitTime"/> seconds of staring at nothing, unable to see the world, their own
    /// cocoon, or the line telling them what is happening.
    ///
    /// Safe to do while the death is still held: the wait the game itself uses was read off this object into the
    /// coroutine before the yield, so taking the object away afterwards cannot change it, and giving up still ends
    /// through <c>GameManager.PlayerDead</c>, which does not need it either.
    ///
    /// Found by its component rather than by the prefab it came from, because a cursed, frost, memory or non-lethal
    /// death each spawn a different prefab and every one of them carries this component. It came out of the game's
    /// pool, so it goes back to the pool: destroying a pooled object leaves the pool believing it is still on loan.
    /// </summary>
    private static void ClearDeathSequence() {
        foreach (var sequence in UnityEngine.Object.FindObjectsByType<HeroDeathSequence>(FindObjectsSortMode.None)) {
            if (sequence == null) {
                continue;
            }

            try {
                sequence.gameObject.Recycle();
            } catch (Exception e) {
                Logger.Warn($"Could not return the death sequence to the pool: {e.Message}");
                UnityEngine.Object.Destroy(sequence.gameObject);
            }
        }
    }

    /// <summary>
    /// Puts the local player back on their feet where they fell, with half of their health. This undoes by hand what
    /// the death did before it was held, in the order the game's own respawn does it, and then hands back what the
    /// cocoon held, which is what makes being pulled up worth anything: the money and the silk are already inside it
    /// by this point, so a rescue that left them there would send the player back for them anyway.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <returns>Whether the player was put back on their feet.</returns>
    private bool TryRevive(HeroController? hero) {
        var playerData = PlayerData.instance;
        if (hero == null || playerData == null) {
            return false;
        }

        try {
            // Again here as well as when the wait started, in case a death got held without going through that path
            ClearDeathSequence();

            hero.gameObject.layer = 9;
            hero.renderer.enabled = true;
            hero.heroBox.HeroBoxNormal();
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
    /// <param name="position">Where the partner died.</param>
    /// <returns>The object, or null if it could not be made.</returns>
    private GameObject? SpawnRescueCocoon(Vector2 position) {
        var gameManager = global::GameManager.instance;
        var sceneManager = gameManager == null ? null : gameManager.GetSceneManager();
        var prefab = sceneManager == null ? null : sceneManager.GetComponent<CustomSceneManager>()?.heroCorpsePrefab;
        if (prefab == null) {
            Logger.Warn("Could not find the cocoon to show for the partner");

            return null;
        }

        var cocoon = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);

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

        var hits = cocoon.AddComponent<RescueCocoonHits>();
        hits.Hits = OnRescueCocoonHit;

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

        if (SpawnRescueCocoon(_partnerCocoonPosition) is not { } cocoon) {
            return;
        }

        _rescueTarget = new RescueTarget(playerId, _partnerCocoonScene, cocoon);
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
