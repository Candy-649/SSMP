using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Moments that each player has for themselves as they arrive somewhere: what plays as they come through a door, or
/// what a room decides to show as it loads. A scene that starts as a player walks into it waits for every player
/// (BossRoomCoop.Story), but these have no start that could wait, so each game plays them as its own player arrives.
/// The game marks such a moment as done with a flag or a wish that both saves share, and the player who got there
/// second found it done by the partner and never saw it. Now the second player's game plays it too, leaving out only
/// what the partner's had already done for both of them.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The scene in which a quest begins as the player first arrives through one of its doors, in any case.
    /// </summary>
    private const string CitadelStartSceneName = "Coral_10";

    /// <summary>
    /// The path of the object whose FSM begins that quest.
    /// </summary>
    private const string CitadelStartPath = "Citadel Quest Starter";

    /// <summary>
    /// The FSM that begins the quest.
    /// </summary>
    private const string CitadelStartFsmName = "Start Citadel Investigate Quest";

    /// <summary>
    /// The state of <see cref="CitadelStartFsmName"/> that asks whether the quest began already.
    /// </summary>
    private const string CitadelQuestCheckState = "Quest Started?";

    /// <summary>
    /// The state it goes to when the quest began already, in which it does nothing more.
    /// </summary>
    private const string CitadelStartSkippedState = "Inert 2";

    /// <summary>
    /// The state it goes on with when the quest hasn't begun: it checks the door that the player came through, and only
    /// some doors begin the quest.
    /// </summary>
    private const string CitadelDoorCheckState = "Get Entry Gate";

    /// <summary>
    /// The flag of the player data that marks the place as visited, which the start sets along with the quest. Only
    /// the local player's own arrival sets it, so its being unset tells that the quest came from the partner.
    /// </summary>
    private const string CitadelVisitedFlag = "visitedCitadel";

    /// <summary>
    /// Flags of the player data that mark an arrival as the local player's own: the visit above, and the scene of the
    /// first town that the scene begun at its door walks the player into ('Churchkeeper Intro Scene' in bonetown),
    /// which can't wait for both players while that walk steers them. Dialogue writes both, and they went to the
    /// partner with it, whose game then skipped the arrival. They never go to the partner, nor come from them.
    /// </summary>
    private static readonly HashSet<string> OwnArrivalMarks = new(StringComparer.Ordinal) {
        CitadelVisitedFlag, "churchKeeperIntro"
    };

    /// <summary>
    /// The flag of the player data that the landing in the room above sets once it played. Both saves share it.
    /// </summary>
    private const string CrashLandedFlag = "crashedIntoGreymoor";

    /// <summary>
    /// The flag of the player data that the way up which crashes in sets as the player goes through it, which the
    /// landing room reads together with <see cref="CrashLandedFlag"/>. Both saves share it, and nothing clears it
    /// after a landing.
    /// </summary>
    private const string CrashingFlag = "crashingIntoGreymoor";

    /// <summary>
    /// The scene of the landing, in any case.
    /// </summary>
    private const string CrashLandingSceneName = "Greymoor_01";

    /// <summary>
    /// The path of the object whose FSM decides as the room loads whether the landing plays.
    /// </summary>
    private const string CrashLandingPath = "Floor Control Scene";

    /// <summary>
    /// The name of that FSM.
    /// </summary>
    private const string CrashLandingFsmName = "Control";

    /// <summary>
    /// The states of that FSM that read both flags: the first one as the room loads, and the one that chooses between
    /// the landing and the room as it is after the landing.
    /// </summary>
    private static readonly string[] CrashLandingStates = ["Activate Entry CamLock?", "Check State"];

    /// <summary>
    /// The state of that FSM in which the player has landed, which sets <see cref="CrashLandedFlag"/>.
    /// </summary>
    private const string CrashLandedState = "Resume";

    /// <summary>
    /// The door of the landing room that both ways up out of the room below lead to.
    /// </summary>
    private const string CrashLandingGate = "bot1";

    /// <summary>
    /// The door of the room below that the way down through the broken floor leads to.
    /// </summary>
    private const string CrashHoleGate = "top1";

    /// <summary>
    /// The scene that plays as a player first comes into the town of the bell, in any case. Each game plays it for its
    /// own player, since its flag is set only by the scene itself, and it ends by beginning a quest.
    /// </summary>
    private const string TownArrivalSceneName = "Belltown_cutscene";

    /// <summary>
    /// The path of the object whose FSM plays that scene.
    /// </summary>
    private const string TownArrivalPath = "Cinematic Player";

    /// <summary>
    /// The FSM that plays that scene.
    /// </summary>
    private const string TownArrivalFsmName = "Cutscene Control";

    /// <summary>
    /// The FSM that plays an arrival of the local player which the partner had first, leaving out what the partner's
    /// arrival already did for both players.
    /// </summary>
    private Fsm? _arrivalReplay;

    /// <summary>
    /// The hook on the part of the room below the landing that shows the way up which crashes in.
    /// </summary>
    private Hook? _crashWayHook;

    /// <summary>
    /// The hook on the part of the room below the landing that shows the plain door up through the broken floor.
    /// </summary>
    private Hook? _holeWayHook;

    /// <summary>
    /// Registers the hooks that show the room below the landing as it is for a player who hasn't landed yet.
    /// </summary>
    private void RegisterArrivalHooks() {
        _crashWayHook = CreateHook(
            typeof(DeactivateIfPlayerdataTrue).GetMethod("ForceEvaluate", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<DeactivateIfPlayerdataTrue>, DeactivateIfPlayerdataTrue>((orig, self) =>
                DecideCrashWay(self.boolName, () => orig(self))
            )
        );
        _holeWayHook = CreateHook(
            typeof(DeactivateIfPlayerdataFalse).GetMethod("ForceEvaluate", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<DeactivateIfPlayerdataFalse>, DeactivateIfPlayerdataFalse>((orig, self) =>
                DecideCrashWay(self.boolName, () => orig(self))
            )
        );
    }

    /// <summary>
    /// Before a state switch: the start of the quest that the local player's first arrival begins, which its FSM skips
    /// because the partner's arrival began the quest, goes on to the check of the door they came through instead, the
    /// way it goes for a player whose quest hasn't begun. Through the doors that begin it, it then plays as it did for
    /// the partner: what it unlocks on this computer, the mark of the visit and the prompt of the quest, which the
    /// partner's game had and this one never got. What the partner's start already did for both is left out (see
    /// <see cref="IsArrivalReplay"/>).
    /// </summary>
    /// <returns>The state to switch to.</returns>
    private FsmState RedirectArrival(Fsm fsm, FsmState toState) {
        if (toState.Name != CitadelStartSkippedState || fsm.Name != CitadelStartFsmName ||
            fsm.ActiveStateName != CitadelQuestCheckState || PlayerData.instance is not { } playerData ||
            playerData.GetBool(CitadelVisitedFlag) || !IsFsmAt(fsm, CitadelStartSceneName, CitadelStartPath) ||
            fsm.GetState(CitadelDoorCheckState) is not { } doorCheck) {
            return toState;
        }

        _arrivalReplay = fsm;
        Logger.Info(
            $"The partner began the quest of '{CitadelStartPath}' before the local player arrived; the door they came " +
            "through decides their own start, which leaves out what the partner's did for both"
        );
        return doorCheck;
    }

    /// <summary>
    /// Whether a flag of the player data marks an arrival as the local player's own (see <see cref="OwnArrivalMarks"/>).
    /// </summary>
    private static bool IsOwnArrivalMark(string name) {
        return OwnArrivalMarks.Contains(name);
    }

    /// <summary>
    /// Whether the FSM that runs now plays an arrival of the local player which the partner may have had first: the
    /// start above, or the scene of the first arrival in the town of the bell (<see cref="TownArrivalSceneName"/>).
    /// Such an arrival lets no time pass, which the partner's already did and which would move the characters of the
    /// world on again, and doesn't begin a quest that the partner finished since, which would open it again in both
    /// games.
    /// </summary>
    private bool IsArrivalReplay() {
        return FsmExecutionStack.ExecutingFsm is { } fsm &&
               (fsm == _arrivalReplay ||
                fsm.Name == TownArrivalFsmName && IsFsmAt(fsm, TownArrivalSceneName, TownArrivalPath));
    }

    /// <summary>
    /// Remembers that the partner had an arrival first, from the flag that their game set for it, so that the local
    /// player still sees it on their own arrival.
    /// </summary>
    private void NoteArrivalToSee(string flagName) {
        if (flagName != CrashLandedFlag || GetCurrentMarker() is not { } marker ||
            marker.ArrivalsToSee.Contains(flagName)) {
            return;
        }

        marker.ArrivalsToSee.Add(flagName);
        Logger.Info($"The partner had the arrival '{flagName}' first; the local player sees it on their own arrival");
        try {
            SaveMarkers();
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Whether the local player is still to see an arrival that the partner had first.
    /// </summary>
    private bool IsArrivalToSee(string flagName) {
        return GetCurrentMarker() is { } marker && marker.ArrivalsToSee.Contains(flagName);
    }

    /// <summary>
    /// Forgets an arrival that the local player was still to see.
    /// </summary>
    private void ForgetArrivalToSee(string flagName, string reason) {
        if (GetCurrentMarker() is not { } marker || !marker.ArrivalsToSee.Remove(flagName)) {
            return;
        }

        Logger.Info($"The arrival '{flagName}' is no longer the local player's to see, since {reason}");
        try {
            SaveMarkers();
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Decides the two ways up out of the room below the landing as they are for a player who hasn't landed yet, while
    /// the landing is still the local player's to see: the way that crashes in stays, and the plain door that the
    /// broken floor leaves is gone. A player who arrives through that door came down through the broken floor, so the
    /// floor is broken for them now, and the ways are decided from the save.
    /// </summary>
    /// <param name="boolName">The flag that the part of the room is shown by.</param>
    /// <param name="evaluate">Decides whether the part is shown.</param>
    private void DecideCrashWay(string boolName, Action evaluate) {
        if (boolName != CrashLandedFlag || !_everChecked) {
            evaluate();
            return;
        }

        var landingToSee = false;
        try {
            if (IsArrivalToSee(CrashLandedFlag)) {
                if (global::GameManager.instance != null &&
                    global::GameManager.instance.GetEntryGateName() == CrashHoleGate) {
                    ForgetArrivalToSee(CrashLandedFlag, "they came down through the broken floor");
                } else {
                    landingToSee = true;
                }
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }

        if (landingToSee) {
            WithoutFlag(CrashLandedFlag, evaluate);
        } else {
            evaluate();
        }
    }

    /// <summary>
    /// Which flag the FSM of the landing has to find unset as it switches into a state, for the local player's own
    /// arrival in its room.
    /// - Through the door that both ways up lead to, while the landing is still theirs to see, they came the way that
    ///   crashes in, since the plain door has been taken away (see <see cref="DecideCrashWay"/>). The landing plays
    ///   for them as it did for the partner: the landed flag is hidden.
    /// - Through any other door the landing never plays, as in a game alone. The crashing flag of a partner who just
    ///   went up the way that crashes in, and hasn't landed yet, is hidden.
    /// Their first arrival in the room ends the landing that was theirs to see, whichever way they came, and so does
    /// their own landing: a landed flag from the partner that arrives while they land changes nothing.
    /// </summary>
    /// <returns>The flag to hide, or null.</returns>
    private string? GetLandingFlagToHide(Fsm fsm, FsmState toState) {
        if (fsm.Name != CrashLandingFsmName ||
            (Array.IndexOf(CrashLandingStates, toState.Name) < 0 && toState.Name != CrashLandedState) ||
            !IsFsmAt(fsm, CrashLandingSceneName, CrashLandingPath)) {
            return null;
        }

        if (toState.Name == CrashLandedState) {
            ForgetArrivalToSee(CrashLandedFlag, "they landed");
            return null;
        }

        string? hidden;
        if (global::GameManager.instance == null ||
            global::GameManager.instance.GetEntryGateName() != CrashLandingGate) {
            hidden = CrashingFlag;
        } else {
            hidden = IsArrivalToSee(CrashLandedFlag) ? CrashLandedFlag : null;
        }

        // The second state decides the room
        if (toState.Name == CrashLandingStates[1]) {
            ForgetArrivalToSee(
                CrashLandedFlag,
                hidden == CrashLandedFlag ? "it plays for them now" : "they came into the room another way"
            );
        }

        return hidden;
    }

    /// <summary>
    /// Runs something of the game with a set boolean of the player data unset. The flags are shared ones, and each is
    /// set back before anything else can read it, so the partner's game never hears of it.
    /// </summary>
    private static void WithoutFlag(string flagName, Action action) {
        var playerData = PlayerData.instance;
        var field = GetPlayerDataField(flagName);
        if (playerData == null || field?.FieldType != typeof(bool) || !(bool) field.GetValue(playerData)) {
            action();
            return;
        }

        field.SetValue(playerData, false);
        try {
            action();
        } finally {
            field.SetValue(playerData, true);
        }
    }

    /// <summary>
    /// Whether an FSM belongs to the object at a path in a scene.
    /// </summary>
    private static bool IsFsmAt(Fsm fsm, string sceneName, string path) {
        return fsm.GameObject is { } gameObject &&
               string.Equals(gameObject.scene.name, sceneName, StringComparison.OrdinalIgnoreCase) &&
               ScenePath.Get(gameObject.transform) == path;
    }
}
