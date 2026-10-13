using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The rooms of the maze, which lay themselves out anew each time a player comes in (FsmScan --hazards, class 6): which
/// of a few ways each part of a room is built (ActivateRandomChildren, as the room is switched on), which of its places
/// get traps and where its creatures and silk flies go (the maze controller's GetRandomChildSceneSeed, a few frames
/// later, as many as the player's way through the maze calls for), and where each door leads and which of them lead on
/// (MazeController.LinkDoors, as the room starts). Each game rolled all of it by itself, so two players who went
/// through the same door came out in different rooms, and a room was built differently for each of them.
///
/// The user decided (10-13): two players in the same room who go through the same door come out in the same room; once
/// apart, they meet again only by chance, and a room they meet in is built the same for both; the first player in a
/// room decides how it is built; and a room that everyone left is forgotten, so whoever comes in next decides anew,
/// also when one player ran through a room before the other set off into it. How far each player is through the maze
/// stays their own.
///
/// So the game of the first player in a room writes down what it rolled for the room - the results, not dice: which way
/// each part was built, which places were picked, what the controller read of the player's way through the maze before
/// it picked, and where each door leads - and sends it to the others once the room is laid out. A game that comes into
/// a room that another player is in, or on the way into, waits for that before the room is switched on: the screen
/// stays black a moment longer, as the load waits to switch the room on (SceneLoad.IsActivationAllowed). Each game says
/// which room of the maze it is going into as its load begins, so that every game knows who is in or on the way into
/// each room. Two players who set off into the same room before either heard of the other leave it to the one with the
/// larger save key, as for other things that two games chose at once (MemberKeyWins). A room that two games laid out
/// each by itself, when a word came too late, takes the doors of the larger key in both, which is what keeps the
/// players together; the rest of it stays as each game built it.
///
/// A door of a room also decides, as a player goes through it, whether it sends them back to the way in after too many
/// doors that did not lead on, by that player's own count; so two players whose counts differ could part at the same
/// door. While a room has someone in it, a door that a player went through takes everyone after them where it took
/// that player: the game of the first one through tells the others where it led (see <see cref="AfterMazeDoor"/>).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The scenes of the maze, each with a MazeController (FsmScan --dump MazeController): its eight rooms, the room to
    /// rest in, the hall at its end and the way in.
    /// </summary>
    private static readonly HashSet<string> LayoutSceneNames = new(StringComparer.OrdinalIgnoreCase) {
        "Dust_Maze_01", "Dust_Maze_02", "Dust_Maze_03", "Dust_Maze_04", "Dust_Maze_05", "Dust_Maze_06", "Dust_Maze_07",
        "Dust_Maze_08", "Dust_Maze_crossing", "Dust_Maze_Last_Hall", "Dust_Maze_09_entrance"
    };

    /// <summary>
    /// How long a load into a room of the maze waits for the layout of a player who is in it or on the way into it, in
    /// seconds, before the room is laid out here by itself.
    /// </summary>
    private const float LayoutWaitLimit = 5f;

    /// <summary>
    /// How long the game that lays a room out writes it down for at most, in seconds from when the room was switched
    /// on.
    /// </summary>
    private const float LayoutRecordLimit = 3f;

    /// <summary>
    /// How long a room with no state machine that picks is written down for, in seconds from when it was switched on:
    /// the way into the maze, which only links its door.
    /// </summary>
    private const float LayoutQuietTime = 1f;

    /// <summary>
    /// The layout of each room of the maze that a player is in or on the way into, by its scene.
    /// </summary>
    private readonly Dictionary<string, LayoutRecord> _layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The room of the maze that each other member is in or on the way into, as their last word said.
    /// </summary>
    private readonly Dictionary<ushort, LayoutPlace> _memberLayoutPlaces = new();

    /// <summary>
    /// The room of the maze that the local player is in or on the way into, or none.
    /// </summary>
    private LayoutPlace _localLayoutPlace = LayoutPlace.None;

    /// <summary>
    /// How many words of where players are in the maze this game made or heard, which orders them.
    /// </summary>
    private int _layoutWordCount;

    /// <summary>
    /// The load into a room of the maze that is under way, or null.
    /// </summary>
    private LayoutLoad? _layoutLoad;

    /// <summary>
    /// The layout that this game writes down for a room that the local player came into first, or null.
    /// </summary>
    private LayoutRecord? _recordingLayout;

    /// <summary>
    /// The layout of another game that this game lays the room of the local player out by, or null.
    /// </summary>
    private LayoutRecord? _followedLayout;

    /// <summary>
    /// How deep the linking of doors goes, which starts over by calling itself when no door led on.
    /// </summary>
    private int _layoutLinkDepth;

    /// <summary>
    /// Where each door of a room of the maze took the player who went through it first, by the path of the door, for
    /// each room that a player is in or on the way into, by its scene.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, TakenDoor>> _takenDoors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For each state machine of a room of the maze that picks, the states from which it still comes to a pick.
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _layoutPickingStates = new();

    /// <summary>
    /// The IL hooks of the layouts.
    /// </summary>
    private readonly List<ILHook> _layoutILHooks = [];

    /// <summary>
    /// Registers the hooks that write down and lay out the rooms of the maze.
    /// </summary>
    private void RegisterRoomLayoutHooks() {
        // The flag that lets a load switch its room on is read by the routine of the load, which is changed rather than
        // the getter of the flag, since that is small enough to be inlined into it
        var routine = typeof(SceneLoad).GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith("<BeginRoutine>", StringComparison.Ordinal))
            ?.GetMethod("MoveNext", InstanceFlags);
        if (routine == null) {
            Logger.Error("Could not find the routine of a scene load, so the maze is laid out by each game");
        } else {
            try {
                _layoutILHooks.Add(new ILHook(routine, HoldActivationForLayout));
            } catch (Exception e) {
                Logger.Error($"Could not hook the routine of a scene load:\n{e}");
            }
        }

        AddInteractionHook(
            typeof(SceneLoad).GetMethod("Begin", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<SceneLoad>, SceneLoad>(OnLayoutLoadBegin)
        );
        AddInteractionHook(
            typeof(ActivateRandomChildren).GetMethod("OnEnable", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ActivateRandomChildren>, ActivateRandomChildren>(OnLayoutSetEnable)
        );
        AddInteractionHook(
            typeof(GetRandomChildSceneSeed).GetMethod("DoGetRandomChild", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<GetRandomChildSceneSeed>, GetRandomChildSceneSeed>(OnLayoutPick)
        );
        AddInteractionHook(
            typeof(GetPlayerDataInt).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<GetPlayerDataInt>, GetPlayerDataInt>(OnLayoutPlayerDataInt)
        );
        AddInteractionHook(
            typeof(MazeController).GetMethod("LinkDoors", InstanceFlags, null, [typeof(List<TransitionPoint>)], null),
            new Action<Action<MazeController, List<TransitionPoint>>, MazeController, List<TransitionPoint>>(
                OnLayoutLinkDoors
            )
        );

        // What a door of the maze does as a player goes through it is a lambda of SubscribeDoorEntered, a method of a
        // type that the compiler made, which holds the door and the controller
        var doorEntered = typeof(MazeController).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(InstanceFlags | BindingFlags.DeclaredOnly))
            .FirstOrDefault(method => method.Name.StartsWith("<SubscribeDoorEntered>", StringComparison.Ordinal));
        var doorField = doorEntered?.DeclaringType?.GetField("door", InstanceFlags);
        var controllerField = doorEntered?.DeclaringType?.GetField("<>4__this", InstanceFlags);
        if (doorEntered == null || doorField == null || controllerField == null) {
            Logger.Error("Could not find what a door of the maze does as a player goes through it");
        } else {
            try {
                _layoutILHooks.Add(
                    new ILHook(doorEntered, il => FollowFirstThroughDoor(il, doorField, controllerField))
                );
            } catch (Exception e) {
                Logger.Error($"Could not hook what a door of the maze does as a player goes through it:\n{e}");
            }
        }
    }

    /// <summary>
    /// IL hook for the routine of a scene load, which keeps a room of the maze switched off while its load waits for
    /// the layout of another player (see <see cref="DecideLayout"/>).
    /// </summary>
    private void HoldActivationForLayout(ILContext il) {
        try {
            var c = new ILCursor(il);
            var found = false;
            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchCallOrCallvirt(typeof(SceneLoad), "get_IsActivationAllowed")
                   )) {
                c.Remove();
                c.EmitDelegate<Func<SceneLoad, bool>>(load => load.IsActivationAllowed && !HoldsLayoutActivation(load));
                found = true;
            }

            if (!found) {
                Logger.Warn("Could not find where a scene load waits to switch its room on");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change where a scene load waits to switch its room on:\n{e}");
        }
    }

    /// <summary>
    /// IL hook for what a door of the maze does as a player goes through it, which then lets
    /// <see cref="AfterMazeDoor"/> say where the door takes the player.
    /// </summary>
    private void FollowFirstThroughDoor(ILContext il, FieldInfo doorField, FieldInfo controllerField) {
        try {
            var c = new ILCursor(il);
            var found = false;
            while (c.TryGotoNext(MoveType.Before, i => i.MatchRet())) {
                c.MoveAfterLabels();
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, doorField);
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, controllerField);
                c.EmitDelegate<Action<TransitionPoint, MazeController>>(AfterMazeDoor);
                c.Index++;
                found = true;
            }

            if (!found) {
                Logger.Warn("Could not find where a door of the maze is done with a player going through it");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change what a door of the maze does as a player goes through it:\n{e}");
        }
    }

    /// <summary>
    /// Runs as a door of a room of the maze is done with a player going through it, before it takes them anywhere. A
    /// door that a player went through earlier, while the room has had someone in it since, takes this player where it
    /// took that one, rather than where this player's own count of doors would send them (USER 10-13: two players who
    /// go through the same door come out in the same room). Otherwise where it takes this player is told to the
    /// members that the room has, for when they go through it after them.
    /// </summary>
    private void AfterMazeDoor(TransitionPoint door, MazeController controller) {
        try {
            if (door == null || controller == null || controller.isCapScene) {
                return;
            }

            var scene = controller.gameObject.scene.name;
            if (!IsLayoutScene(scene)) {
                return;
            }

            var path = ScenePath.Get(door.transform);
            if (_takenDoors.TryGetValue(scene, out var doors) && doors.TryGetValue(path, out var taken)) {
                if (taken.Scene != door.targetScene || taken.Entry != door.entryPoint) {
                    door.SetTargetScene(taken.Scene);
                    door.entryPoint = taken.Entry;

                    // As the door sets it for the way back from the next room, which it set before this
                    PlayerData.instance.PreviousMazeTargetDoor = taken.Entry;
                    Logger.Info(
                        $"'{door.name}' of '{scene}' takes this player to '{taken.Scene}', where it took the player " +
                        "who went through it first"
                    );
                }

                return;
            }

            // Only while a member has the room: a room that the local player leaves alone is forgotten
            if (!_memberLayoutPlaces.Values.Any(place => IsSameScene(place.Scene, scene)) &&
                !GetCheckedMembers().Any(member => IsSameScene(PlaceOf(member).Scene, scene))) {
                return;
            }

            if (doors == null) {
                _takenDoors[scene] = doors = new Dictionary<string, TakenDoor>(StringComparer.Ordinal);
            }

            var record = new TakenDoor(
                door.targetScene ?? "", door.entryPoint ?? "", LocalKey, LayoutKeyOf(scene) ?? ""
            );
            doors[path] = record;
            SendToMembers(WriteTakenDoor(scene, path, record));
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Takes where a door of a room of the maze took a member who went through it first. Of two who went through it
    /// at once, each before hearing of the other, the one with the larger key counts in every game. One made under
    /// another layout of the room than the one known here is old, from before the room was left and laid out anew, or
    /// of a layout that did not count.
    /// </summary>
    private void OnRoomDoorTaken(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player) ||
            !IsLayoutScene(update.Scene) || update.Records.Count != 4 || update.PartnerKey.Length == 0 ||
            update.Records[1].Length == 0 || update.Records[2].Length == 0 || !IsLayoutHeld(update.Scene)) {
            return;
        }

        var layoutKey = update.Records[3];
        if (LayoutKeyOf(update.Scene) is { } knownLayout && layoutKey.Length > 0 && knownLayout != layoutKey) {
            return;
        }

        if (!_takenDoors.TryGetValue(update.Scene, out var doors)) {
            _takenDoors[update.Scene] = doors = new Dictionary<string, TakenDoor>(StringComparer.Ordinal);
        }

        var path = update.Records[0];
        if (doors.TryGetValue(path, out var known) && string.CompareOrdinal(known.Key, update.PartnerKey) >= 0) {
            return;
        }

        doors[path] = new TakenDoor(update.Records[1], update.Records[2], update.PartnerKey, layoutKey);
    }

    /// <summary>
    /// The key of the layout of a room of the maze known here: the one that counts, or the one this game writes down
    /// or follows for the room of the local player, or null for none.
    /// </summary>
    private string? LayoutKeyOf(string scene) {
        if (_layouts.TryGetValue(scene, out var layout)) {
            return layout.DeciderKey;
        }

        if (_recordingLayout is { } recording && IsSameScene(recording.Scene, scene)) {
            return recording.DeciderKey;
        }

        return _followedLayout is { } followed && IsSameScene(followed.Scene, scene) ? followed.DeciderKey : null;
    }

    /// <summary>
    /// Forgets where the doors of a room took the first players through them under another layout than the one that
    /// counts now, as the doors of the room are linked again the way that one has them.
    /// </summary>
    private void ForgetTakenDoorsOfOtherLayouts(string scene, string layoutKey) {
        if (!_takenDoors.TryGetValue(scene, out var doors)) {
            return;
        }

        foreach (var pair in doors.ToList()) {
            if (pair.Value.LayoutKey.Length > 0 && pair.Value.LayoutKey != layoutKey) {
                doors.Remove(pair.Key);
            }
        }
    }

    /// <summary>
    /// Writes where a door of a room of the maze took the player who went through it first into an update:
    /// <see cref="CoopSaveUpdate.Records"/> holds the path of the door, the scene it took them to, the door they came in
    /// at there and the key of the layout of the room it was made under, and <see cref="CoopSaveUpdate.PartnerKey"/>
    /// the save key of that player.
    /// </summary>
    private static CoopSaveUpdate WriteTakenDoor(string scene, string path, TakenDoor door) {
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.RoomDoorTaken,
            Scene = scene,
            PartnerKey = door.Key
        };
        update.Records.Add(path);
        update.Records.Add(door.Scene);
        update.Records.Add(door.Entry);
        update.Records.Add(door.LayoutKey);
        return update;
    }

    /// <summary>
    /// Whether a load that could switch its room on now waits for the layout of another player instead.
    /// </summary>
    private bool HoldsLayoutActivation(SceneLoad load) {
        try {
            return DecideLayout(load.TargetSceneName);
        } catch (Exception e) {
            LogInteractionError(e);
            return false;
        }
    }

    /// <summary>
    /// Whether a scene is a room of the maze.
    /// </summary>
    private static bool IsLayoutScene(string? sceneName) {
        return sceneName is { Length: > 0 } && LayoutSceneNames.Contains(sceneName);
    }

    /// <summary>
    /// Hook for the start of a scene load, which tells the other members which room of the maze the local player is
    /// going into, or that they are leaving the maze.
    /// </summary>
    private void OnLayoutLoadBegin(Action<SceneLoad> orig, SceneLoad self) {
        try {
            SetOffIntoLayout(self.TargetSceneName);
        } catch (Exception e) {
            LogInteractionError(e);
        }

        orig(self);
    }

    /// <summary>
    /// Takes the local player into a room of the maze, or out of the maze: what they wrote down or followed for the
    /// room they leave ends here, a room that nobody is in any more is forgotten, and the other members hear where they
    /// go, with whether anyone was known to be there.
    /// </summary>
    private void SetOffIntoLayout(string target) {
        var scene = IsLayoutScene(target) ? target : "";
        _layoutLoad = scene.Length > 0 ? new LayoutLoad(scene) : null;
        if (scene.Length == 0 && _localLayoutPlace.Scene.Length == 0) {
            return;
        }

        var members = GetCheckedMembers();
        var founder = scene.Length > 0 && !members.Any(member => IsSameScene(PlaceOf(member).Scene, scene));
        _localLayoutPlace = new LayoutPlace(scene, founder, ++_layoutWordCount);
        _recordingLayout = null;
        _followedLayout = null;
        ForgetEmptyLayouts();

        if (members.Count > 0) {
            SendToMembers(new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.RoomLayoutPlace,
                Scene = scene,
                Part = (ushort) (founder ? 0 : 1)
            });
        }

        Logger.Info(
            scene.Length == 0
                ? "Left the maze, which the other members hear"
                : $"Going into '{scene}' of the maze, " + (founder ? "where nobody is known to be" : "where someone is")
        );
    }

    /// <summary>
    /// Decides, as a load into a room of the maze could switch it on, how the room is laid out here: the way the layout
    /// known for it says, or after waiting for the layout of a member who is in it or on the way into it, or by this
    /// game, which writes it down for the others.
    /// </summary>
    /// <param name="scene">The scene that the load would switch on.</param>
    /// <returns>Whether the load waits.</returns>
    private bool DecideLayout(string scene) {
        if (_layoutLoad is not { Decided: false } load || !IsSameScene(load.Scene, scene)) {
            return false;
        }

        if (_layouts.TryGetValue(scene, out var known)) {
            load.Decided = true;
            _followedLayout = known.ForFollowing();
            Logger.Info(
                $"Laying out '{scene}' the way the game that came into it first did" +
                (load.Said ? $", after waiting {Time.unscaledTime - load.Since:0.00} s for it" : "")
            );
            return false;
        }

        var waitFor = GetCheckedMembers().FirstOrDefault(member => WaitsForLayoutOf(member, scene));
        if (waitFor != null && Time.unscaledTime - load.Since < LayoutWaitLimit) {
            if (!load.Said) {
                load.Said = true;
                Logger.Info($"Waiting for the layout of '{scene}' from {waitFor.Username} before switching it on");
            }

            return true;
        }

        load.Decided = true;
        // Also written down while nobody else is in the save yet, like as it loads into a room of the maze: a member
        // who comes in later gets the layout, and of two that loaded into the same room, the doors of both lead where
        // those of the larger key lead
        _recordingLayout = new LayoutRecord(scene, LocalKey);
        Logger.Info(
            waitFor != null
                ? $"The layout of '{scene}' from {waitFor.Username} did not come, so this game lays it out itself"
                : $"Laying out '{scene}' for the others, as the first in it"
        );
        return false;
    }

    /// <summary>
    /// Whether the local player, going into a room of the maze, waits for the layout of a member: one who is in it as
    /// far as the server says, or whose word into it came before the local player's, or who set off into it at the same
    /// time as the local player, before either heard of the other, and has the larger save key.
    /// </summary>
    private bool WaitsForLayoutOf(ClientPlayerData member, string scene) {
        var place = PlaceOf(member);
        if (!IsSameScene(place.Scene, scene)) {
            return false;
        }

        if (!_memberLayoutPlaces.ContainsKey(member.Id) || place.Order < _localLayoutPlace.Order) {
            return true;
        }

        // By the key that their game puts on its layouts, so that the wait and the layout that counts go by the same
        return _localLayoutPlace.Founder && place.Founder && string.CompareOrdinal(member.SaveKey, LocalKey) > 0;
    }

    /// <summary>
    /// The room of the maze that a member is in or on the way into: by their last word, or else by the room the server
    /// says they are in, as one they were in before any word of the local game.
    /// </summary>
    private LayoutPlace PlaceOf(ClientPlayerData member) {
        if (_memberLayoutPlaces.TryGetValue(member.Id, out var place)) {
            return place;
        }

        return IsLayoutScene(member.Room) ? new LayoutPlace(member.Room, true, 0) : LayoutPlace.None;
    }

    /// <summary>
    /// Whether two scene names are of the same scene.
    /// </summary>
    private static bool IsSameScene(string first, string second) {
        return first.Length > 0 && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the local player or a member is in a room of the maze or on the way into it: by the word of a member in
    /// the save, also while a new round of the check runs and nobody is checked, or by the room the server says a
    /// checked member is in.
    /// </summary>
    private bool IsLayoutHeld(string scene) {
        return IsSameScene(_localLayoutPlace.Scene, scene) ||
               _memberLayoutPlaces.Values.Any(place => IsSameScene(place.Scene, scene)) ||
               GetCheckedMembers().Any(member => IsSameScene(PlaceOf(member).Scene, scene));
    }

    /// <summary>
    /// Forgets the layout of each room of the maze that nobody is in or on the way into any more, so that whoever comes
    /// in next lays it out anew (USER 10-13, "只要房间空了就当断了").
    /// </summary>
    private void ForgetEmptyLayouts() {
        foreach (var scene in _layouts.Keys.ToList()) {
            if (IsLayoutHeld(scene)) {
                continue;
            }

            _layouts.Remove(scene);
            Logger.Info($"Forgot the layout of '{scene}', which nobody is in any more");
        }

        foreach (var scene in _takenDoors.Keys.ToList()) {
            if (!IsLayoutHeld(scene)) {
                _takenDoors.Remove(scene);
            }
        }
    }

    /// <summary>
    /// Takes the word of a member about which room of the maze they are going into, or that they left the maze. A
    /// member going into the room of the local player gets its layout at once.
    /// </summary>
    private void OnRoomLayoutPlace(ClientPlayerData player, CoopSaveUpdate update) {
        // Also from a member who is in the save while this game still finishes the round: the word that a check
        // sends at its end comes as the member's game finishes it
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player)) {
            return;
        }

        // A word said again after a check, of the room the member is in already, keeps its place in the order: the
        // member did not go anywhere, and coming later would let a player who is to wait for them stop waiting
        var scene = IsLayoutScene(update.Scene) ? update.Scene : "";
        var order = _memberLayoutPlaces.TryGetValue(player.Id, out var said) && IsSameScene(said.Scene, scene)
            ? said.Order
            : ++_layoutWordCount;
        _memberLayoutPlaces[player.Id] = new LayoutPlace(scene, update.Part == 0, order);
        ForgetEmptyLayouts();

        if (scene.Length > 0 && IsSameScene(scene, _localLayoutPlace.Scene)) {
            if (_layouts.TryGetValue(scene, out var layout)) {
                SendLayout(layout, player.Id);
            }

            SendTakenDoors(scene, player.Id);
        }
    }

    /// <summary>
    /// Takes the layout that another game wrote down for a room of the maze. One for a room that nobody is in or on the
    /// way into any more is old. Of two games that laid the same room out, each by itself, the one with the larger key
    /// counts in every game, and the doors of the room of the local player go where its doors go.
    /// </summary>
    private void OnRoomLayout(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player) ||
            !IsLayoutScene(update.Scene) || ReadLayout(update) is not { } layout || !IsLayoutHeld(layout.Scene)) {
            return;
        }

        // The layout that this game still writes down for the room counts as known before it is sent: one of a smaller
        // key that came in the meantime would otherwise take the doors here, and this game would then send its own
        // and keep the other's doors
        var known = _layouts.TryGetValue(layout.Scene, out var have) ? have : null;
        if (known == null && _recordingLayout is { Sent: false } recording &&
            IsSameScene(recording.Scene, layout.Scene)) {
            known = recording;
        }

        if (known != null && (known.DeciderKey == layout.DeciderKey ||
                              string.CompareOrdinal(layout.DeciderKey, known.DeciderKey) < 0)) {
            return;
        }

        _layouts[layout.Scene] = layout;
        Logger.Info(
            $"Got the layout of '{layout.Scene}' from {player.Username} ({layout.Rolls.Count} rolls, " +
            $"{layout.Doors.Count} doors)"
        );

        // The local player is in the room already, laid out another way: its doors now lead where the others' lead,
        // also if they are yet to be linked
        if (IsSameScene(_localLayoutPlace.Scene, layout.Scene) && _layoutLoad is not { Decided: false } &&
            (known != null || _recordingLayout != null)) {
            if (_followedLayout is { } followed && IsSameScene(followed.Scene, layout.Scene)) {
                followed.Doors.Clear();
                foreach (var pair in layout.Doors) {
                    followed.Doors[pair.Key] = pair.Value;
                }
            }

            LinkDoorsLive(layout);
            ForgetTakenDoorsOfOtherLayouts(layout.Scene, layout.DeciderKey);
        }
    }

    /// <summary>
    /// Tells the members of a round that just finished which room of the maze the local player is in, with its layout,
    /// or that they are not in the maze, and where the doors of each room that someone holds took the first players
    /// through them: a member who came into the save later never heard the words, and a word that went while the round
    /// ran was not taken, also by a game whose player went on to another room since.
    /// </summary>
    private void TellRoomLayoutAfterCheck(List<ClientPlayerData> members) {
        var scene = _localLayoutPlace.Scene;
        foreach (var member in members) {
            Send(new CoopSaveUpdate {
                TargetId = member.Id,
                Kind = CoopSaveUpdateKind.RoomLayoutPlace,
                Scene = scene,
                Part = (ushort) (_localLayoutPlace.Founder ? 0 : 1)
            });
            if (scene.Length > 0 && _layouts.TryGetValue(scene, out var layout)) {
                SendLayout(layout, member.Id);
            }

            foreach (var held in _takenDoors.Keys) {
                SendTakenDoors(held, member.Id);
            }
        }
    }

    /// <summary>
    /// Forgets which room of the maze a member who left the save was in.
    /// </summary>
    private void ForgetLayoutPlaceOf(ushort id) {
        if (_memberLayoutPlaces.Remove(id)) {
            ForgetEmptyLayouts();
        }
    }

    /// <summary>
    /// Forgets what is known of the rooms of the maze from the members, as the save is left or another loads. The room
    /// of the local player stays as it is, with what this game wrote down or followed for it: a save loads its room
    /// before it counts as loaded, so that a session begins with the room laid out already, and a member who comes
    /// into it later takes its layout.
    /// </summary>
    private void ResetRoomLayouts() {
        _memberLayoutPlaces.Clear();
        foreach (var scene in _layouts.Keys.ToList()) {
            if (!IsSameScene(_localLayoutPlace.Scene, scene)) {
                _layouts.Remove(scene);
            }
        }

        foreach (var scene in _takenDoors.Keys.ToList()) {
            if (!IsSameScene(_localLayoutPlace.Scene, scene)) {
                _takenDoors.Remove(scene);
            }
        }
    }

    /// <summary>
    /// Forgets the room of the maze of the local player as well, as the game goes to the main menu.
    /// </summary>
    private void ForgetLocalLayout() {
        _layouts.Clear();
        _takenDoors.Clear();
        _localLayoutPlace = LayoutPlace.None;
        _layoutLoad = null;
        _recordingLayout = null;
        _followedLayout = null;
    }

    /// <summary>
    /// Sends the layout of a room once this game, which came into it first, laid it out: once every state machine that
    /// picks for the room went past its last pick and its doors are linked, or after a while at most. Of two games that
    /// laid it out at the same time, the one with the larger key counts, and the doors here go where its doors go.
    /// </summary>
    private void UpdateRoomLayouts() {
        if (_recordingLayout is not { Sent: false } layout ||
            !IsSameScene(SceneUtil.GetCurrentSceneName(), layout.Scene)) {
            return;
        }

        if (layout.SwitchedOnAt < 0f) {
            layout.SwitchedOnAt = Time.unscaledTime;
        }

        var time = Time.unscaledTime - layout.SwitchedOnAt;
        var laidOut = layout.DoorsLinked && (layout.Machines.Count > 0
            ? layout.Machines.All(fsm => !StillPicks(fsm))
            : time > LayoutQuietTime);
        if (!laidOut && time < LayoutRecordLimit) {
            return;
        }

        layout.Sent = true;
        if (_layouts.TryGetValue(layout.Scene, out var other) &&
            string.CompareOrdinal(other.DeciderKey, layout.DeciderKey) > 0) {
            Logger.Info($"Another game laid out '{layout.Scene}' at the same time, and its layout counts");
            LinkDoorsLive(other);
            ForgetTakenDoorsOfOtherLayouts(layout.Scene, other.DeciderKey);
            return;
        }

        _layouts[layout.Scene] = layout;
        Logger.Info(
            $"Laid out '{layout.Scene}' ({layout.Rolls.Count} rolls, {layout.Doors.Count} doors" +
            (laidOut ? ")" : ", not all of it in time)") + ", sent to the others"
        );
        if (_checkedMembers.Count > 0) {
            SendToMembers(WriteLayout(layout));
            if (_takenDoors.TryGetValue(layout.Scene, out var doors)) {
                foreach (var pair in doors) {
                    SendToMembers(WriteTakenDoor(layout.Scene, pair.Key, pair.Value));
                }
            }
        }
    }

    /// <summary>
    /// Sends the layout of a room to one member.
    /// </summary>
    private void SendLayout(LayoutRecord layout, ushort targetId) {
        var update = WriteLayout(layout);
        update.TargetId = targetId;
        Send(update);
    }

    /// <summary>
    /// Sends one member where the doors of a room took the players who went through them first.
    /// </summary>
    private void SendTakenDoors(string scene, ushort targetId) {
        if (!_takenDoors.TryGetValue(scene, out var doors)) {
            return;
        }

        foreach (var pair in doors) {
            var taken = WriteTakenDoor(scene, pair.Key, pair.Value);
            taken.TargetId = targetId;
            Send(taken);
        }
    }

    /// <summary>
    /// The layout that a game following another's lays the scene of an object out by, or the one this game writes down
    /// for it, or null for neither.
    /// </summary>
    private LayoutRecord? LayoutOf(GameObject gameObject) {
        if (_recordingLayout == null && _followedLayout == null) {
            return null;
        }

        var scene = gameObject.scene.name;
        if (_recordingLayout is { } recording && IsSameScene(recording.Scene, scene)) {
            return recording;
        }

        return _followedLayout is { } followed && IsSameScene(followed.Scene, scene) ? followed : null;
    }

    /// <summary>
    /// Hook for a part of a room that is built one of a few ways, which it picks as it is switched on.
    /// </summary>
    private void OnLayoutSetEnable(Action<ActivateRandomChildren> orig, ActivateRandomChildren self) {
        if (LayoutOf(self.gameObject) is not { } layout) {
            orig(self);
            return;
        }

        RollForLayout(layout, "a|" + ScenePath.Get(self.transform), () => orig(self));
    }

    /// <summary>
    /// Hook for a state machine picking a place of a room of the maze, like where a trap goes.
    /// </summary>
    private void OnLayoutPick(Action<GetRandomChildSceneSeed> orig, GetRandomChildSceneSeed self) {
        if (self.Fsm is not { FsmComponent: { } machine } fsm || machine == null ||
            LayoutOf(machine.gameObject) is not { } layout) {
            orig(self);
            return;
        }

        if (layout == _recordingLayout) {
            layout.Machines.Add(fsm);
        }

        RollForLayout(layout, "p|" + ActionKey(machine, self), () => orig(self));
    }

    /// <summary>
    /// Hook for a state machine reading a number of the player's save. One of a room of the maze that is yet to pick
    /// reads how far the player is through the maze, which decides how many places it picks: the reading is written
    /// down for the others, or the one of the game that laid the room out is taken. What it reads after its last pick,
    /// like for its music, is the player's own.
    /// </summary>
    private void OnLayoutPlayerDataInt(Action<GetPlayerDataInt> orig, GetPlayerDataInt self) {
        orig(self);
        try {
            if (self.Fsm is not { FsmComponent: { } machine } fsm || machine == null ||
                LayoutOf(machine.gameObject) is not { } layout || self.storeValue is not { IsNone: false } store ||
                !StillPicks(fsm)) {
                return;
            }

            var key = "i|" + ActionKey(machine, self);
            if (layout == _recordingLayout) {
                layout.Machines.Add(fsm);
                if (!layout.Rolls.TryGetValue(key, out var values)) {
                    layout.Rolls[key] = values = [];
                }

                values.Add(store.Value);
                return;
            }

            if (layout.Rolls.TryGetValue(key, out var recorded)) {
                var next = layout.Taken.TryGetValue(key, out var taken) ? taken : 0;
                if (next < recorded.Count) {
                    store.Value = recorded[next];
                    layout.Taken[key] = next + 1;
                }
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Hook for linking the doors of a room of the maze, which the game that came into the room first does and writes
    /// down, and a game following it takes from what it wrote down.
    /// </summary>
    private void OnLayoutLinkDoors(
        Action<MazeController, List<TransitionPoint>> orig,
        MazeController self,
        List<TransitionPoint> doors
    ) {
        var layout = LayoutOf(self.gameObject);
        if (layout != null && layout != _recordingLayout) {
            if (layout.Doors.Count == 0) {
                Logger.Info($"The layout of '{layout.Scene}' followed here has no doors, so this game links them");
            } else {
                try {
                    LinkDoorsAs(self, doors, layout);
                    return;
                } catch (Exception e) {
                    LogInteractionError(e);
                }
            }
        }

        _layoutLinkDepth++;
        try {
            orig(self, doors);
        } finally {
            _layoutLinkDepth--;
        }

        // The outer call only, since it calls itself to start over when no door led on
        if (layout != _recordingLayout || layout == null || _layoutLinkDepth > 0) {
            return;
        }

        try {
            layout.Doors.Clear();
            foreach (var door in doors) {
                if (door != null) {
                    layout.Doors[ScenePath.Get(door.transform)] = new LayoutDoor(
                        door.targetScene ?? "", door.entryPoint ?? "", self.correctDoors.IndexOf(door)
                    );
                }
            }

            layout.DoorsLinked = true;
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Links the doors of a room the way a layout has them: where each leads, at which door it comes in there, and
    /// which of them lead on through the maze, in the order that the game which laid the room out has them, since what
    /// looks for the way on takes the first. A door that the layout has nothing of keeps where it leads.
    /// </summary>
    private static void LinkDoorsAs(MazeController controller, IEnumerable<TransitionPoint> doors,
        LayoutRecord layout) {
        var leadingOn = new List<(int Order, TransitionPoint Door)>();
        foreach (var door in doors) {
            if (door == null || !layout.Doors.TryGetValue(ScenePath.Get(door.transform), out var link)) {
                continue;
            }

            door.SetTargetScene(link.Scene);
            door.entryPoint = link.Entry;
            if (link.LeadsOn >= 0) {
                leadingOn.Add((link.LeadsOn, door));
            }
        }

        controller.correctDoors.Clear();
        controller.correctDoors.AddRange(leadingOn.OrderBy(pair => pair.Order).Select(pair => pair.Door));
    }

    /// <summary>
    /// Links the doors of the room of the local player again, the way the layout that counts has them, after this game
    /// laid the room out by itself.
    /// </summary>
    private static void LinkDoorsLive(LayoutRecord layout) {
        var controller = MazeController.NewestInstance;
        if (controller == null || !IsSameScene(controller.gameObject.scene.name, layout.Scene)) {
            return;
        }

        var scene = controller.gameObject.scene;
        var doors = TransitionPoint.TransitionPoints.Where(door => door != null && door.gameObject.scene == scene);
        LinkDoorsAs(controller, doors.ToList(), layout);
        Logger.Info($"The doors of '{layout.Scene}' lead where those of the layout that counts lead");
    }

    /// <summary>
    /// The name of an action of a state machine for a layout: its object, state machine, state and place in the state.
    /// </summary>
    private static string ActionKey(PlayMakerFSM machine, FsmStateAction action) {
        var state = action.State;
        var index = state?.Actions != null ? Array.IndexOf(state.Actions, action) : -1;
        return $"{ScenePath.Get(machine.transform)}|{machine.FsmName}|{state?.Name}|{index}";
    }

    /// <summary>
    /// Lets something of a room of the maze roll with the random numbers of the scene, written down as results for the
    /// layout this game writes, or taken from the layout it follows, under a name of what rolls.
    /// </summary>
    private void RollForLayout(LayoutRecord layout, string key, Action roll) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null) {
            roll();
            return;
        }

        var recording = layout == _recordingLayout;
        List<int>? results;
        if (recording) {
            if (!layout.Rolls.TryGetValue(key, out results)) {
                layout.Rolls[key] = results = [];
            }
        } else {
            layout.Rolls.TryGetValue(key, out results);
        }

        var own = gameManager.SceneSeededRandom;
        var random = new LayoutRandom(
            own ?? new System.Random(), results, !recording, layout.Taken.TryGetValue(key, out var taken) ? taken : 0
        );
        gameManager.SceneSeededRandom = random;
        try {
            roll();
        } finally {
            gameManager.SceneSeededRandom = own;
            if (!recording) {
                layout.Taken[key] = random.Taken;
                if (random.Missed) {
                    Logger.Info($"'{key}' of '{layout.Scene}' rolled more here than in the layout it follows");
                }
            }
        }
    }

    /// <summary>
    /// Whether a state machine of a room of the maze can still come to a pick from the state it is in.
    /// </summary>
    private bool StillPicks(Fsm fsm) {
        return fsm.FsmComponent != null && fsm.ActiveState is { } state && PickingStatesOf(fsm).Contains(state.Name);
    }

    /// <summary>
    /// The states of a state machine from which it comes, by its own ways out, to a state that picks a place of the
    /// room, those included. Found once for each state machine of the room.
    /// </summary>
    private HashSet<string> PickingStatesOf(Fsm fsm) {
        if (_layoutPickingStates.TryGetValue(fsm, out var states)) {
            return states;
        }

        states = new HashSet<string>(StringComparer.Ordinal);
        var comingInto = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var next = new Queue<string>();
        foreach (var state in fsm.States) {
            foreach (var transition in state.Transitions) {
                if (transition.ToState is not { Length: > 0 } to) {
                    continue;
                }

                if (!comingInto.TryGetValue(to, out var from)) {
                    comingInto[to] = from = [];
                }

                from.Add(state.Name);
            }

            if (state.Actions.Any(action => action is GetRandomChildSceneSeed { Enabled: true })) {
                next.Enqueue(state.Name);
            }
        }

        while (next.Count > 0) {
            var name = next.Dequeue();
            if (!states.Add(name) || !comingInto.TryGetValue(name, out var from)) {
                continue;
            }

            foreach (var earlier in from) {
                next.Enqueue(earlier);
            }
        }

        _layoutPickingStates[fsm] = states;
        return states;
    }

    /// <summary>
    /// Writes a layout into an update: <see cref="CoopSaveUpdate.Names"/> holds what rolled, and
    /// <see cref="CoopSaveUpdate.Amounts"/> how many results each has followed by them;
    /// <see cref="CoopSaveUpdate.Records"/> holds the path of each door, where it leads and at which door it comes in
    /// there, and <see cref="CoopSaveUpdate.Values"/> 0 for each that does not lead on through the maze, else 1 more
    /// than its place among those that do.
    /// </summary>
    private static CoopSaveUpdate WriteLayout(LayoutRecord layout) {
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.RoomLayout,
            Scene = layout.Scene,
            PartnerKey = layout.DeciderKey
        };
        foreach (var pair in layout.Rolls) {
            update.Names.Add(pair.Key);
            update.Amounts.Add(pair.Value.Count);
            update.Amounts.AddRange(pair.Value);
        }

        foreach (var pair in layout.Doors) {
            update.Records.Add(pair.Key);
            update.Records.Add(pair.Value.Scene);
            update.Records.Add(pair.Value.Entry);
            update.Values.Add(pair.Value.LeadsOn + 1);
        }

        return update;
    }

    /// <summary>
    /// Reads a layout that <see cref="WriteLayout"/> wrote, or null for a broken one.
    /// </summary>
    private static LayoutRecord? ReadLayout(CoopSaveUpdate update) {
        if (update.PartnerKey.Length == 0 || update.Records.Count != update.Values.Count * 3) {
            return null;
        }

        var layout = new LayoutRecord(update.Scene, update.PartnerKey);
        var at = 0;
        foreach (var key in update.Names) {
            if (at >= update.Amounts.Count) {
                return null;
            }

            var count = update.Amounts[at++];
            if (count < 0 || count > update.Amounts.Count - at) {
                return null;
            }

            layout.Rolls[key] = update.Amounts.GetRange(at, count);
            at += count;
        }

        for (var i = 0; i < update.Values.Count; i++) {
            layout.Doors[update.Records[i * 3]] = new LayoutDoor(
                update.Records[i * 3 + 1], update.Records[i * 3 + 2], Mathf.RoundToInt(update.Values[i]) - 1
            );
        }

        return layout;
    }

    /// <summary>
    /// Where a player is in the maze.
    /// </summary>
    /// <param name="Scene">The room of the maze they are in or on the way into, or empty for none.</param>
    /// <param name="Founder">Whether nobody was known to be in it or on the way into it as they set off.</param>
    /// <param name="Order">When this game made or heard the word, which orders the words.</param>
    private readonly record struct LayoutPlace(string Scene, bool Founder, int Order) {
        /// <summary>
        /// Out of the maze.
        /// </summary>
        public static readonly LayoutPlace None = new("", false, 0);
    }

    /// <summary>
    /// A load into a room of the maze.
    /// </summary>
    private sealed class LayoutLoad(string scene) {
        /// <summary>
        /// The room.
        /// </summary>
        public string Scene { get; } = scene;

        /// <summary>
        /// When the load began.
        /// </summary>
        public float Since { get; } = Time.unscaledTime;

        /// <summary>
        /// Whether how the room is laid out here was decided.
        /// </summary>
        public bool Decided { get; set; }

        /// <summary>
        /// Whether the wait for the layout of another player was written to the log.
        /// </summary>
        public bool Said { get; set; }
    }

    /// <summary>
    /// Where a door of a room of the maze leads.
    /// </summary>
    /// <param name="Scene">The scene it leads to.</param>
    /// <param name="Entry">The door of that scene it comes in at.</param>
    /// <param name="LeadsOn">Its place among the doors that lead on through the maze, or -1 if it does not.</param>
    private readonly record struct LayoutDoor(string Scene, string Entry, int LeadsOn);

    /// <summary>
    /// Where a door of a room of the maze took the player who went through it first.
    /// </summary>
    /// <param name="Scene">The scene it took them to.</param>
    /// <param name="Entry">The door of that scene they came in at.</param>
    /// <param name="Key">The save key of that player.</param>
    /// <param name="LayoutKey">The key of the layout of the room it was made under, or empty if none was known.</param>
    private readonly record struct TakenDoor(string Scene, string Entry, string Key, string LayoutKey);

    /// <summary>
    /// How a room of the maze is laid out, as the game that came into it first wrote it down.
    /// </summary>
    private sealed class LayoutRecord(string scene, string deciderKey) {
        /// <summary>
        /// The room.
        /// </summary>
        public string Scene { get; } = scene;

        /// <summary>
        /// The save key of the member whose game laid it out.
        /// </summary>
        public string DeciderKey { get; } = deciderKey;

        /// <summary>
        /// The results of each thing that rolled for the room, in order, by its name: "a|" and a path for a part built
        /// one of a few ways, "p|" and an action for a pick of a place, "i|" and an action for a reading of the save.
        /// </summary>
        public Dictionary<string, List<int>> Rolls { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Where each door leads, by its path.
        /// </summary>
        public Dictionary<string, LayoutDoor> Doors { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// For a layout being followed, how many results of each were taken.
        /// </summary>
        public Dictionary<string, int> Taken { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// For a layout being written down, the state machines that read or picked for it.
        /// </summary>
        public HashSet<Fsm> Machines { get; } = [];

        /// <summary>
        /// For a layout being written down, whether the doors were linked.
        /// </summary>
        public bool DoorsLinked { get; set; }

        /// <summary>
        /// For a layout being written down, when the room was switched on here, or less than 0 before that.
        /// </summary>
        public float SwitchedOnAt { get; set; } = -1f;

        /// <summary>
        /// For a layout being written down, whether it was sent.
        /// </summary>
        public bool Sent { get; set; }

        /// <summary>
        /// A copy to follow, which counts what it took by itself.
        /// </summary>
        public LayoutRecord ForFollowing() {
            var copy = new LayoutRecord(Scene, DeciderKey);
            foreach (var pair in Rolls) {
                copy.Rolls[pair.Key] = pair.Value;
            }

            foreach (var pair in Doors) {
                copy.Doors[pair.Key] = pair.Value;
            }

            return copy;
        }
    }

    /// <summary>
    /// The random numbers of a scene while something of a room of the maze rolls: the game's own, each written down as
    /// it comes, or in place of each the one at the same place of what was written down in another game, as long as it
    /// can be one here.
    /// </summary>
    private sealed class LayoutRandom(System.Random own, List<int>? results, bool follows, int taken) : System.Random {
        /// <summary>
        /// How many of the results written down were taken.
        /// </summary>
        public int Taken { get; private set; } = taken;

        /// <summary>
        /// Whether one was rolled here that had nothing written down for it.
        /// </summary>
        public bool Missed { get; private set; }

        /// <inheritdoc />
        public override int Next() => Take(own.Next(), 0, int.MaxValue);

        /// <inheritdoc />
        public override int Next(int maxValue) => Take(own.Next(maxValue), 0, maxValue);

        /// <inheritdoc />
        public override int Next(int minValue, int maxValue) => Take(own.Next(minValue, maxValue), minValue, maxValue);

        /// <summary>
        /// Writes a number down, or takes the next one written down in its place if it can be one here.
        /// </summary>
        private int Take(int rolled, int min, int max) {
            if (!follows) {
                results?.Add(rolled);
                return rolled;
            }

            if (results != null && Taken < results.Count) {
                var value = results[Taken++];
                if (value >= min && (value < max || value == min)) {
                    return value;
                }
            }

            Missed = true;
            return rolled;
        }
    }
}
