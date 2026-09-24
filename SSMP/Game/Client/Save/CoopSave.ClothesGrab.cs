using System;
using HutongGames.PlayMaker;
using SSMP.Internals;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Taking back the clothes that the prison took, in a checked two-player save. Each player takes back their own, from
/// the creature that holds them in their own game, but while both of them are without theirs, it only happens once both
/// of them reached for it: whoever reaches first crouches there and waits for the other, and then both go at once. The
/// jump button gets the one who waits out of it again. The user chose this on 2026-09-24 - both players decide to take
/// them back, and only then does it happen. A partner who has theirs already doesn't make anyone wait.
///
/// How it looks to the other player is carried over as well. The game plays the grab with a stand-in: it hides the
/// player, has a second figure leap at the creature and fling itself up, and the player only reappears in the air
/// afterwards. The other game only ever saw the player crouch and then hang in the air where they were hidden, so it
/// now hides them the same way and plays a copy of the same stand-in on them, from the moment the grab starts over
/// there until they reappear.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the object that the player takes their clothes back at.
    /// </summary>
    private const string ClothesGrabObjectName = "Start Inspect";

    /// <summary>
    /// The name of the object above it that only exists while the player is without their clothes. The same room has
    /// another object of the same name for a player who has them.
    /// </summary>
    private const string ClothesGrabParentName = "Battle Cloakless Start Scene";

    /// <summary>
    /// The name of the FSM that plays the grab.
    /// </summary>
    private const string ClothesGrabFsmName = "Control";

    /// <summary>
    /// The state of the grab that waits for the player to reach for their clothes.
    /// </summary>
    private const string ClothesGrabIdleState = "Idle";

    /// <summary>
    /// The state of the grab in which the player crouches first, which is where they wait for the partner.
    /// </summary>
    private const string ClothesGrabCrouchState = "Crouch";

    /// <summary>
    /// The event that ends the crouching.
    /// </summary>
    private const string ClothesGrabGoEvent = "NEXT";

    /// <summary>
    /// The state of the grab that hides the player and brings out the stand-in, which is where the grab starts.
    /// </summary>
    private const string ClothesGrabStartState = "Slam Down";

    /// <summary>
    /// The state of the grab that puts the stand-in away and shows the player again.
    /// </summary>
    private const string ClothesGrabEndState = "Hero Flip";

    /// <summary>
    /// The name of the stand-in, next to the object that the player takes their clothes back at.
    /// </summary>
    private const string StandInName = "Hornet Dummy";

    /// <summary>
    /// The animation of the stand-in leaping at the creature.
    /// </summary>
    private const string StandInAttackClip = "Hornet Attack";

    /// <summary>
    /// The animation of the stand-in flinging itself up again.
    /// </summary>
    private const string StandInFlingClip = "Hornet Fling";

    /// <summary>
    /// How many marked frames of the leap the grab waits for before the fling: it goes on after one, twice.
    /// </summary>
    private const int StandInLeapBeats = 3;

    /// <summary>
    /// How long, in seconds, the grab carries the player to the creature before the leap starts.
    /// </summary>
    private const float StandInLeapDelay = 0.2f;

    /// <summary>
    /// Where the grab moves the stand-in when it flings itself up, from the player it is on.
    /// </summary>
    private static readonly Vector3 StandInFlingOffset = new(-0.3f, -1f, 0f);

    /// <summary>
    /// The number in <see cref="CoopSaveUpdate.PartCount"/> for a partner who no longer waits to take theirs back.
    /// </summary>
    private const ushort ClothesGrabNotReady = 0;

    /// <summary>
    /// The number in <see cref="CoopSaveUpdate.PartCount"/> for a partner who reached for theirs.
    /// </summary>
    private const ushort ClothesGrabReady = 1;

    /// <summary>
    /// The number in <see cref="CoopSaveUpdate.PartCount"/> for the grab of a partner starting.
    /// </summary>
    private const ushort ClothesGrabStarted = 2;

    /// <summary>
    /// The number in <see cref="CoopSaveUpdate.PartCount"/> for the grab of a partner showing them again.
    /// </summary>
    private const ushort ClothesGrabEnded = 3;

    /// <summary>
    /// The grab of the local player while it waits for the partner, or null.
    /// </summary>
    private Fsm? _clothesGrabHeld;

    /// <summary>
    /// Whether the waiting grab already tried to end the crouching, which it does again once the partner is ready.
    /// </summary>
    private bool _clothesGrabGoHeld;

    /// <summary>
    /// Whether the partner reached for their clothes and waits for the local player.
    /// </summary>
    private bool _partnerReadyToGrab;

    /// <summary>
    /// The stand-in that plays the grab of the partner here, or null.
    /// </summary>
    private PartnerStandIn? _partnerStandIn;

    /// <summary>
    /// Follows the grab of the local player: whether it has to wait for the partner, and when it starts and ends.
    /// Called from the hook on changes of FSM state that <see cref="RegisterInteractionHooks"/> puts in place.
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    private void OnClothesGrabSwitch(Fsm fsm, FsmState toState) {
        if (_checkedWith is not { } partnerId || fsm.Name != ClothesGrabFsmName || !IsClothesGrab(fsm)) {
            return;
        }

        switch (toState.Name) {
            case ClothesGrabCrouchState when fsm.ActiveStateName == ClothesGrabIdleState:
                OnLocalClothesGrab(fsm, partnerId);
                break;
            case ClothesGrabStartState:
                _partnerReadyToGrab = false;
                SendClothesGrab(fsm, partnerId, ClothesGrabStarted);
                break;
            case ClothesGrabEndState:
                SendClothesGrab(fsm, partnerId, ClothesGrabEnded);
                break;
        }
    }

    /// <summary>
    /// Whether an FSM plays the grab of a player without their clothes.
    /// </summary>
    private static bool IsClothesGrab(Fsm fsm) {
        return fsm.GameObject is { name: ClothesGrabObjectName } gameObject &&
               gameObject.transform.parent is { name: ClothesGrabParentName };
    }

    /// <summary>
    /// The local player reached for their clothes. If the partner is without theirs as well, the partner is told, and
    /// the local player waits crouching until the partner reached for theirs too.
    /// </summary>
    private void OnLocalClothesGrab(Fsm fsm, ushort partnerId) {
        if (GetCheckedPartner() is not { CrestType: CrestType.Cloakless } partner) {
            return;
        }

        SendClothesGrab(fsm, partnerId, ClothesGrabReady);
        if (_partnerReadyToGrab) {
            Logger.Info($"Reached for the clothes after {partner.Username} did, so both take them back now");
            return;
        }

        _clothesGrabHeld = fsm;
        _clothesGrabGoHeld = false;
        Logger.Info($"Reached for the clothes, waiting for {partner.Username} to reach for theirs");

        var jump = InputHandler.Instance is { } inputHandler ? PromptKeyName(inputHandler.inputActions.Jump) : "Jump";
        Chat(Lang.Pick(
            $"Waiting for {partner.Username} to reach for theirs, to take them back together. {jump} to stop waiting.",
            $"等 {partner.Username} 也伸手，两个人一起抢回来。按 {jump} 不等了。"
        ));
    }

    /// <summary>
    /// Keeps the waiting grab crouching: the event that would end the crouching is held back until the partner is
    /// ready. Called from the hook on the events of FSMs.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool HoldsClothesGrab(Fsm fsm, FsmEvent fsmEvent) {
        if (_clothesGrabHeld != fsm || fsmEvent.Name != ClothesGrabGoEvent ||
            fsm.ActiveStateName != ClothesGrabCrouchState) {
            return false;
        }

        _clothesGrabGoHeld = true;
        return true;
    }

    /// <summary>
    /// Lets the waiting grab go on, now that the partner reached for theirs.
    /// </summary>
    private void ReleaseClothesGrab() {
        if (_clothesGrabHeld is not { } fsm) {
            return;
        }

        _clothesGrabHeld = null;
        if (_clothesGrabGoHeld && fsm.ActiveStateName == ClothesGrabCrouchState) {
            fsm.Event(ClothesGrabGoEvent);
        }

        _clothesGrabGoHeld = false;
    }

    /// <summary>
    /// Tells the partner about the grab of the local player.
    /// </summary>
    private void SendClothesGrab(Fsm fsm, ushort partnerId, ushort part) {
        if (fsm.GameObject is not { } gameObject) {
            return;
        }

        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.ClothesGrab,
            Scene = gameObject.scene.name,
            ObjectPath = ScenePath.Get(gameObject.transform),
            PartCount = part
        });
    }

    /// <summary>
    /// The partner reached for their clothes, stopped waiting, or their grab started or ended.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, with what happened in <see cref="CoopSaveUpdate.PartCount"/>.</param>
    private void OnClothesGrab(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            return;
        }

        switch (update.PartCount) {
            case ClothesGrabReady:
                _partnerReadyToGrab = true;
                if (_clothesGrabHeld != null) {
                    Logger.Info($"{player.Username} reached for their clothes too, so both take them back now");
                    ReleaseClothesGrab();
                } else {
                    Chat(Lang.Pick(
                        $"{player.Username} waits for you to take your clothes back together.",
                        $"{player.Username} 在等你一起去抢衣服。"
                    ));
                }

                break;
            case ClothesGrabNotReady:
                _partnerReadyToGrab = false;
                Chat(Lang.Pick($"{player.Username} stopped waiting.", $"{player.Username} 不等了。"));
                break;
            case ClothesGrabStarted:
                // The partner doesn't wait anymore once their grab started, and a local player who still waits for them
                // goes along: the partner only goes after hearing that the local player reached for theirs too. What
                // they said about waiting may have got lost on the way, like when they lost the connection meanwhile.
                _partnerReadyToGrab = false;
                if (_clothesGrabHeld != null) {
                    Logger.Info($"The grab of {player.Username} started, so the local player goes along");
                    ReleaseClothesGrab();
                }

                StartPartnerStandIn(player, update);
                break;
            case ClothesGrabEnded:
                EndPartnerStandIn();
                break;
        }
    }

    /// <summary>
    /// Lets the local player stop waiting for the partner with the jump button, and keeps the stand-in of the partner
    /// going.
    /// </summary>
    private void UpdateClothesGrab(HeroController hero) {
        try {
            UpdatePartnerStandIn();

            if (_clothesGrabHeld is not { } fsm || InputHandler.Instance is not { } inputHandler ||
                global::GameManager.instance is not { isPaused: false } || !inputHandler.inputActions.Jump.WasPressed) {
                return;
            }

            _clothesGrabHeld = null;
            _clothesGrabGoHeld = false;

            // The grab took the controls and the HUD like a conversation does, so it is ended like one, and then waits
            // to be reached for again. Crouching also took the animations of the player away, which ending a
            // conversation doesn't give back, so they get them back the way the grab gives them back when it is over.
            if (fsm.GameObject is { } gameObject && gameObject.GetComponent<PlayMakerNPC>() is { } npc) {
                npc.ForceEndDialogue();
            }

            hero.StartAnimationControlToIdle();
            fsm.SetState(ClothesGrabIdleState);
            if (_checkedWith is { } partnerId) {
                SendClothesGrab(fsm, partnerId, ClothesGrabNotReady);
            }

            Logger.Info("Stopped waiting for the partner to take the clothes back together");
            Chat(Lang.Pick("You stopped waiting.", "不等了。"));
        } catch (Exception e) {
            Logger.Error($"Could not follow taking the clothes back:\n{e}");
        }
    }

    /// <summary>
    /// Hides the body of the partner and plays a copy of the stand-in on it, the way the grab plays it in their game.
    /// </summary>
    private void StartPartnerStandIn(ClientPlayerData player, CoopSaveUpdate update) {
        EndPartnerStandIn();

        if (SceneUtil.GetCurrentSceneName() != update.Scene || player.PlayerObject is not { } body ||
            body.GetComponent<MeshRenderer>() is not { } bodyRenderer) {
            return;
        }

        // The stand-in of this room, which is there whether the local player is without their clothes or not. The
        // grab of the local player takes it along onto the local player when it starts, and leaves it there, which is
        // where it is when both players grab at once.
        var grab = ScenePath.Find(update.ObjectPath, update.Scene);
        var template = grab != null ? grab.transform.parent?.Find(StandInName) : null;
        if (template == null && HeroController.instance is { } hero) {
            template = hero.transform.Find(StandInName);
        }

        if (template == null) {
            return;
        }

        var figure = Object.Instantiate(template.gameObject, body.transform, false);
        figure.name = "SSMP Partner Stand-In";
        figure.transform.localPosition = Vector3.zero;
        figure.transform.localRotation = Quaternion.identity;
        figure.transform.localScale = Vector3.one;
        figure.SetActive(true);

        var animator = figure.GetComponent<tk2dSpriteAnimator>();
        if (animator == null) {
            Object.Destroy(figure);
            return;
        }

        var standIn = new PartnerStandIn(figure, animator, bodyRenderer, Time.time);
        animator.AnimationEventTriggered = (_, clip, _) => {
            if (clip.name == StandInAttackClip && ++standIn.Beats >= StandInLeapBeats) {
                standIn.Fling();
            }
        };
        animator.AnimationCompleted = (_, clip) => {
            if (clip.name == StandInAttackClip) {
                standIn.Fling();
            }
        };

        // The first frame of the leap while the grab carries them to the creature
        animator.Play(StandInAttackClip);
        animator.Pause();

        bodyRenderer.enabled = false;
        _partnerStandIn = standIn;
        Logger.Info($"Playing the grab of {player.Username} with a copy of the stand-in");
    }

    /// <summary>
    /// Starts the leap of the stand-in once the grab carried the partner to the creature.
    /// </summary>
    private void UpdatePartnerStandIn() {
        if (_partnerStandIn is not { } standIn) {
            return;
        }

        if (standIn.Animator.Paused && Time.time - standIn.StartTime >= StandInLeapDelay) {
            standIn.Animator.Resume();
        }
    }

    /// <summary>
    /// Puts the stand-in of the partner away and shows their body again.
    /// </summary>
    private void EndPartnerStandIn() {
        if (_partnerStandIn is not { } standIn) {
            return;
        }

        _partnerStandIn = null;
        if (standIn.Figure != null) {
            Object.Destroy(standIn.Figure);
        }

        if (standIn.Body != null) {
            standIn.Body.enabled = true;
        }
    }

    /// <summary>
    /// Forgets the grabs of the room that was left. Whether the partner waits is kept: whoever gets there first waits
    /// there, so the other one only comes into the room after they said so.
    /// </summary>
    private void OnClothesGrabSceneChanged() {
        _clothesGrabHeld = null;
        _clothesGrabGoHeld = false;
        EndPartnerStandIn();
    }

    /// <summary>
    /// Forgets the grabs, for a new session.
    /// </summary>
    private void ResetClothesGrab() {
        OnClothesGrabSceneChanged();
        _partnerReadyToGrab = false;
    }

    /// <summary>
    /// The partner left the session. That they waited is forgotten: they may come back with their game loaded anew,
    /// and then they don't wait anymore. If they do still wait when they come back, reaching for the clothes here
    /// frees them, and their grab starting frees the local player in turn. The stand-in of a grab of theirs is put
    /// away, since their character goes back to be used for whoever comes next, the stand-in and all.
    /// </summary>
    private void OnClothesGrabPartnerLeft() {
        _partnerReadyToGrab = false;
        EndPartnerStandIn();
    }

    /// <summary>
    /// A copy of the stand-in that plays the grab of the partner on their body.
    /// </summary>
    private sealed class PartnerStandIn {
        public PartnerStandIn(GameObject figure, tk2dSpriteAnimator animator, MeshRenderer body, float startTime) {
            Figure = figure;
            Animator = animator;
            Body = body;
            StartTime = startTime;
        }

        /// <summary>
        /// The copy of the stand-in.
        /// </summary>
        public GameObject Figure { get; }

        /// <summary>
        /// What plays its animations.
        /// </summary>
        public tk2dSpriteAnimator Animator { get; }

        /// <summary>
        /// What draws the body of the partner, which is hidden while the stand-in plays.
        /// </summary>
        public MeshRenderer Body { get; }

        /// <summary>
        /// When the grab of the partner started, in seconds of game time.
        /// </summary>
        public float StartTime { get; }

        /// <summary>
        /// How many marked frames of the leap have passed.
        /// </summary>
        public int Beats { get; set; }

        /// <summary>
        /// Whether the stand-in flings itself up already.
        /// </summary>
        private bool _flinging;

        /// <summary>
        /// Has the stand-in fling itself up, once.
        /// </summary>
        public void Fling() {
            if (_flinging) {
                return;
            }

            _flinging = true;
            Figure.transform.localPosition = StandInFlingOffset;
            Animator.Play(StandInFlingClip);
        }
    }
}
