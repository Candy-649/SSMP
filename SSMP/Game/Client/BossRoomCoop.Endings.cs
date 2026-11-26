using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Animation.Effects;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Skin;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The end of a boss that takes the player's character over to play itself out, as one does with a parry and a last
/// blow. The game that runs the boss plays it on its own player's character. In the other games the boss's copy only
/// shows what it does to that character, which a replay never does to the local one (see
/// EntityFsmActions.ActsOnTheLocalPlayer), so their own characters stood beside it while the other player's played it
/// ("只能看到我朋友在演，但是我在旁边站着"). The user chose that the others watch the one who plays it (USER 11-05 "都看房主演"):
/// while a creature's end has another player's character, the local player's character hides and stays where it is,
/// and what creatures draw of a player in the room is drawn in that player's look, as the stand-in that the end shows
/// of them is (see <see cref="HeldLook.EndingFigure"/>). It shows and moves again once that player has control again,
/// or has left the room.
///
/// A creature that every game runs by itself (see <see cref="OwnCreatures"/>) plays its end in each game, on that
/// game's own player's character, so each player plays it with their own (USER 11-26 "各演各的，把本机外的别的人藏起来"):
/// while such an end has the local player's character, the other players' characters and their attacks are hidden
/// here, those of a player who comes in meanwhile too, and nothing is drawn in their look; they show again once the
/// local player has control again, or the room changes. Nobody is told: each game hides the others by itself.
///
/// An end is an action of a creature's FSM that takes the character's control once the creature has no health left,
/// or, for a creature that every game runs by itself, a machine of the room that plays its end as the character goes
/// into it (see <see cref="RoomEndings"/>). The game gives control back at the end, and a hit gives it back too, as
/// when the player did not parry in time.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The machines of rooms that play the end of the room's boss, which every game runs by itself, as the local
    /// player's character goes into it: by the scene, the object, its state machine and the first state of the end
    /// (see <see cref="NoteRoomEnding"/>).
    /// </summary>
    private static readonly HashSet<(string Scene, string Object, string Fsm, string State)> RoomEndings = [
        ("Abyss_Cocoon", "Superjump Sequence", "Control", "Lift 1")
    ];

    /// <summary>
    /// Whether a creature's end has the local player's character, which the other players in the room were told.
    /// </summary>
    private bool _endingHasLocalPlayer;

    /// <summary>
    /// Whether the end of a creature that every game runs by itself has the local player's character, so that the
    /// other players are hidden here.
    /// </summary>
    private bool _ownEndingHasLocalPlayer;

    /// <summary>
    /// The characters of the other players and their attacks, hidden while the end of a creature that every game runs
    /// by itself has the local player's character.
    /// </summary>
    private readonly KeptFromDrawing _othersHidden = new();

    /// <summary>
    /// The handler that hides the other players every frame while such an end lasts, made once.
    /// </summary>
    private Action? _hideOthers;

    /// <summary>
    /// The ID of the player whose character a creature's end has, whom the local player watches, or null.
    /// </summary>
    private ushort? _endingWatched;

    /// <summary>
    /// The count of the game's taking control of the local player's character as of when the watching took it, or
    /// null if it was taken already. The watching gives it back at its end only if nothing took it since, as holding the
    /// character to watch a round does (see MinigameCoop.LetHeroGo).
    /// </summary>
    private int? _watchControlVersion;

    /// <summary>
    /// The name over the local player's character, which the watching hid, or null.
    /// </summary>
    private GameObject? _watchHidName;

    /// <summary>
    /// The handler that looks every frame whether the watched player is still in the room, made once.
    /// </summary>
    private Action? _checkWatched;

    /// <summary>
    /// The hooks on taking control from the character, by both ways of taking it. Giving it back is noticed by the hook
    /// of the rooms that wait (see OnRegainControl).
    /// </summary>
    private Hook? _relinquishControlHook;

    private Hook? _relinquishControlNotVelocityHook;

    /// <summary>
    /// Registers the hooks that notice a creature's end taking the local player's character.
    /// </summary>
    private void RegisterEndingHooks() {
        _relinquishControlHook = CreateHook(
            typeof(HeroController).GetMethod("RelinquishControl", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<HeroController>, HeroController>(OnRelinquishControl)
        );
        _relinquishControlNotVelocityHook = CreateHook(
            typeof(HeroController).GetMethod("RelinquishControlNotVelocity", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<HeroController>, HeroController>(OnRelinquishControl)
        );
    }

    /// <summary>
    /// Disposes the hooks of <see cref="RegisterEndingHooks"/>.
    /// </summary>
    private void DeregisterEndingHooks() {
        _relinquishControlHook?.Dispose();
        _relinquishControlHook = null;

        _relinquishControlNotVelocityHook?.Dispose();
        _relinquishControlNotVelocityHook = null;
    }

    /// <summary>
    /// Takes control from the local player's character, and tells the other players in the room when a creature's end
    /// takes it: an action of the FSM of a creature that has no health left. The end of a creature that every game
    /// runs by itself hides the other players here instead.
    /// </summary>
    private void OnRelinquishControl(Action<HeroController> orig, HeroController self) {
        orig(self);

        if (_endingHasLocalPlayer || _ownEndingHasLocalPlayer || FsmExecutionStack.ExecutingAction == null ||
            FsmExecutionStack.ExecutingFsm is not { GameObject: { } owner } fsm ||
            owner.GetComponentInParent<HealthManager>() is not { } creature || creature.hp > 0) {
            return;
        }

        if (OwnCreatures.Owning(creature.gameObject) != null) {
            HideOthersForOwnEnding(
                $"'{creature.name}', which every game runs by itself, with no health left, took this player's " +
                $"character in '{fsm.ActiveStateName}' of '{fsm.Name}' to play its end"
            );
            return;
        }

        _endingHasLocalPlayer = true;
        Send(BossRoomUpdateKind.EndingTook, ScenePath.Get(creature.transform), fsm.Name, fsm.ActiveStateName, "", "");
        Logger.Info(
            $"[Ending] '{creature.name}', with no health left, took this player's character in " +
            $"'{fsm.ActiveStateName}' of '{fsm.Name}' to play its end, which the other players in the room watch"
        );
    }

    /// <summary>
    /// Has the other players hide here while the end of a creature that every game runs by itself has the local
    /// player's character (see <see cref="HideOthers"/>).
    /// </summary>
    private void HideOthersForOwnEnding(string what) {
        _ownEndingHasLocalPlayer = true;
        HeldLook.StopAll();
        HeldLook.LocalEnding = true;
        _hideOthers ??= HideOthers;
        MonoBehaviourUtil.Instance.OnUpdateEvent += _hideOthers;
        HideOthers();
        Logger.Info($"[Ending] {what}, so the other players hide here until it is over");
    }

    /// <summary>
    /// Has the other players hide here as a machine of the room that plays the end of a creature that every game runs
    /// by itself takes the local player's character into it (see <see cref="RoomEndings"/>), which no action of the
    /// creature's own does: the lost one of the cocoon, once dead, leaves its end to the sequence of the room, which
    /// the player sets off themselves as they come up to it, and which the game takes the character for as they do.
    /// </summary>
    private void NoteRoomEnding(Fsm fsm, FsmState toState) {
        if (_ownEndingHasLocalPlayer || fsm.GameObject == null ||
            !RoomEndings.Contains((SceneManager.GetActiveScene().name, fsm.GameObject.name, fsm.Name, toState.Name))) {
            return;
        }

        HideOthersForOwnEnding(
            $"'{fsm.GameObject.name}' took this player's character into '{toState.Name}' of '{fsm.Name}' to play " +
            "the end of the boss of the room, which every game runs by itself"
        );
    }

    /// <summary>
    /// Hides the characters of the other players in the room and their attacks this frame, those of a player who came
    /// in meanwhile too, while the end of a creature that every game runs by itself has the local player's character.
    /// </summary>
    private void HideOthers() {
        _othersHidden.Gather();
        foreach (var player in _playerData.Values) {
            if (player.IsInLocalScene && player.PlayerContainer is { } container) {
                _othersHidden.KeepOff(container, true);
            }
        }

        foreach (var attack in RemoteAttackComponent.Active) {
            _othersHidden.KeepOff(attack.gameObject, true);
        }

        _othersHidden.Apply();
    }

    /// <summary>
    /// Shows the other players again once the end of a creature that every game runs by itself no longer has the local
    /// player's character.
    /// </summary>
    private void StopHidingOthers(string why) {
        if (!_ownEndingHasLocalPlayer) {
            return;
        }

        _ownEndingHasLocalPlayer = false;
        HeldLook.LocalEnding = false;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= _hideOthers;
        _othersHidden.ShowEverything();
        Logger.Info($"[Ending] The other players show again here: {why}");
    }

    /// <summary>
    /// Notes that the game gave control back to the local player's character: if a creature's end had it, the other
    /// players show again here, or are told so that they stop watching it.
    /// </summary>
    private void NoteControlGivenBack() {
        StopHidingOthers("this player's character has control again");

        if (!_endingHasLocalPlayer) {
            return;
        }

        _endingHasLocalPlayer = false;
        Send(BossRoomUpdateKind.EndingGaveBack, "", "", "", "", "");
        Logger.Info(
            "[Ending] This player's character has control again after the end of a creature, so the other players " +
            "in the room stop watching it"
        );
    }

    /// <summary>
    /// Hides the local player's character and keeps it where it is while a creature's end has another player's, so that
    /// the local player watches it. A player who is down or dead stays as they are.
    /// </summary>
    private void OnEndingTook(BossRoomUpdate update) {
        if (_endingWatched != null || !_playerData.TryGetValue(update.PlayerId, out var player)) {
            return;
        }

        var hero = HeroController.instance;
        if (hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            Logger.Info(
                $"[Ending] {player.Username} plays the end of '{update.Path}', which this player does not watch, " +
                "being down"
            );
            return;
        }

        _endingWatched = update.PlayerId;
        hero.GetComponent<MeshRenderer>().enabled = false;
        HeroHidden.HiddenByTheRoom();

        var name = hero.transform.Find("Username").gameObject;
        if (name.activeSelf) {
            name.SetActive(false);
            _watchHidName = name;
        }

        // Taking control here is not a room or the game taking it (see _changingHero)
        _watchControlVersion = null;
        if (!hero.controlReqlinquished) {
            _watchControlVersion = HeroController.ControlVersion + 1;
            _changingHero = true;
            try {
                hero.RelinquishControl();
            } finally {
                _changingHero = false;
            }
        }

        HeldLook.EndingFigure = player.PlayerObject;
        _checkWatched ??= CheckWatched;
        MonoBehaviourUtil.Instance.OnUpdateEvent += _checkWatched;
        Logger.Info(
            $"[Ending] {player.Username} plays the end of '{update.Path}' from '{update.FromState}' of " +
            $"'{update.FsmName}', so this player's character hides and waits where it is until it is over" +
            (_watchControlVersion != null ? "" : ", its control taken already")
        );
    }

    /// <summary>
    /// Ends the watching once the watched player has control of their character again.
    /// </summary>
    private void OnEndingGaveBack(BossRoomUpdate update) {
        if (_endingWatched == update.PlayerId) {
            EndWatch($"{NameOf(update.PlayerId)} has control again");
        }
    }

    /// <summary>
    /// Ends the watching once the watched player has left the room, by whatever way.
    /// </summary>
    private void CheckWatched() {
        if (_endingWatched is { } id && (!_playerData.TryGetValue(id, out var player) || !player.IsInLocalScene)) {
            EndWatch($"{NameOf(id)} left the room");
        }
    }

    /// <summary>
    /// Forgets a creature's end as the room changes: the local player's character shows and moves again if it watched
    /// one, the other players show again if it played one of its own, and one that an end had is no longer had by it.
    /// </summary>
    private void ForgetEnding() {
        _endingHasLocalPlayer = false;
        StopHidingOthers("the room changed");
        EndWatch("the room changed");
    }

    /// <summary>
    /// Shows the local player's character again, with its name, and gives back the control that the watching took.
    /// </summary>
    private void EndWatch(string why) {
        if (_endingWatched == null) {
            return;
        }

        _endingWatched = null;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= _checkWatched;
        HeldLook.EndingFigure = null;

        if (_watchHidName != null) {
            _watchHidName.SetActive(true);
            _watchHidName = null;
        }

        // Gone with the room as the game goes back to the menu. Whatever took control since gives it back itself.
        if (HeroController.UnsafeInstance is { } hero) {
            hero.GetComponent<MeshRenderer>().enabled = true;
            if (_watchControlVersion == HeroController.ControlVersion && hero.controlReqlinquished) {
                _changingHero = true;
                try {
                    hero.RegainControl();
                } finally {
                    _changingHero = false;
                }
            }
        }

        _watchControlVersion = null;
        Logger.Info($"[Ending] This player's character shows and moves again: {why}");
    }

    /// <summary>
    /// The name of a player for the log, or their ID once they are gone.
    /// </summary>
    private string NameOf(ushort id) => _playerData.TryGetValue(id, out var player) ? player.Username : $"player {id}";
}
