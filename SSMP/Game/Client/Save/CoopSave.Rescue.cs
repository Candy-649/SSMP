using System;
using System.Collections;
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

            return IHitResponder.Response.None;
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

            Chat("Your teammate pulled you back up.");
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
                        ? $"{partner.Username} is breaking you out ({rescue.Hits}/{RescueHits}). " +
                          $"Press {LeaveKeyName} to go to your bench instead"
                        : $"Waiting for {partner.Username} to break you out. " +
                          $"Press {LeaveKeyName} to go to your bench instead"
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
                ? $"{player.Username} died. Hit their cocoon {RescueHits} times to break them out."
                : $"{player.Username} died. Their cocoon is where they fell, and {RescueHits} hits break them out."
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
    /// Puts something that stands in for the cocoon of the partner in the room, without the parts of it that pay out
    /// and clear the save: its own FSMs call <c>HeroController.CocoonBroken</c>, which would hand the money and the
    /// silk of whoever is looking at it to them and wipe the record of their own cocoon, and HitResponse is what turns
    /// a hit into the event those FSMs wait for. The hits are counted here instead.
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
        cocoon.DestroyComponentsInChildren<PlayMakerFSM>();
        cocoon.DestroyComponentsInChildren<HitResponse>();
        cocoon.SetActive(true);

        var core = cocoon.FindGameObjectInChildren("Core");
        if (core != null) {
            ActivateAll(core);
        }

        var hits = cocoon.AddComponent<RescueCocoonHits>();
        hits.Hits = OnRescueCocoonHit;

        return cocoon;

        void ActivateAll(GameObject obj) {
            obj.SetActive(true);
            foreach (var child in obj.GetChildren()) {
                ActivateAll(child);
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
            Chat($"You broke {partner.Username} out.");
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
