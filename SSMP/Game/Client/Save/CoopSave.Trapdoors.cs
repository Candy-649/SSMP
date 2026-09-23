using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Hatches that a lever opens and that close again by themselves a moment after the last one left them, in a checked
/// two-player save. Most of them sit in a pipe between two rooms, one hatch at each end, and a player coming through
/// the pipe finds the hatch at their end open already.
///
/// The game only ever opens and holds one for its own hero. When the partner opens a pipe from the next room, the
/// hatch at this end stays shut; when they come through it into this room, their body comes out of a closed hatch;
/// and a hatch they stand in closes on them as if nobody were there.
///
/// So each game says which of its hatches its own player keeps open - opening one, or standing in it - and the other
/// game keeps its copy open for as long as that lasts: the same hatch when both are in one room, or the hatch at the
/// other end of the pipe when the partner is in the next room. Once the partner no longer keeps it open, it closes
/// the way the game closes it for its own hero.
///
/// A game only ever says what its own player does, never what it keeps open for the partner. Two games that each kept
/// a hatch open for as long as the other one had it open would keep it open for good.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Where the save keeps a hatch that stays open for good once opened, or nothing for one that closes again.
    /// </summary>
    private static readonly FieldInfo? TrapdoorPersistentField = typeof(Trapdoor).GetField("persistent", InstanceFlags);

    /// <summary>
    /// Whether a hatch stays open once opened, until its room loads again.
    /// </summary>
    private static readonly FieldInfo? TrapdoorStayOpenField = typeof(Trapdoor).GetField("stayOpen", InstanceFlags);

    /// <summary>
    /// Whether a scene of its own holds a hatch open, which then closes when that scene says so.
    /// </summary>
    private static readonly FieldInfo? TrapdoorCustomOpenedField =
        typeof(Trapdoor).GetField("isCustomOpened", InstanceFlags);

    /// <summary>
    /// Which way a hatch at the end of a pipe opens for a player coming out of it, or 0 for a hatch in the middle of a
    /// room.
    /// </summary>
    private static readonly FieldInfo? TrapdoorStartSignField =
        typeof(Trapdoor).GetField("startOpenSign", InstanceFlags);

    /// <summary>
    /// The area that tells the game that its hero came into the room through the hatch. It covers the way out of the
    /// room that the pipe leads to, which is how the hatch at the other end is found.
    /// </summary>
    private static readonly FieldInfo? TrapdoorEnterTriggerField =
        typeof(Trapdoor).GetField("enterSceneTrigger", InstanceFlags);

    /// <summary>
    /// The area in the hatch that keeps it from closing while something is inside.
    /// </summary>
    private static readonly FieldInfo? TrapdoorCloseTriggerField =
        typeof(Trapdoor).GetField("cannotCloseTrigger", InstanceFlags);

    /// <summary>
    /// Set to keep an open hatch from closing: while it counts down to closing, the game starts the count again on
    /// every frame that finds it set.
    /// </summary>
    private static readonly FieldInfo? TrapdoorResetCloseField =
        typeof(Trapdoor).GetField("resetCloseCounter", InstanceFlags);

    /// <summary>
    /// The hook on a hatch starting to open, whatever opened it.
    /// </summary>
    private Hook? _trapdoorOpenHook;

    /// <summary>
    /// The hatches that opened here and haven't been seen shut again.
    /// </summary>
    private readonly List<OpenTrapdoor> _openTrapdoors = [];

    /// <summary>
    /// The hatches that the partner keeps open in their game, by the scene and path of theirs, whether or not a hatch
    /// here goes with them.
    /// </summary>
    private readonly Dictionary<string, PartnerTrapdoor> _partnerTrapdoors = new();

    /// <summary>
    /// Whether a hatch is being opened because the partner keeps theirs open, which is not the local player keeping it
    /// open and is not sent back to them.
    /// </summary>
    private bool _openingPartnerTrapdoor;

    /// <summary>
    /// Registers the hook on hatches opening.
    /// </summary>
    private void RegisterTrapdoorHooks() {
        _trapdoorOpenHook = CreateHook(
            typeof(Trapdoor).GetMethod("DoOpenDoor", InstanceFlags, null, [typeof(float), typeof(bool)], null),
            new Func<Func<Trapdoor, float, bool, IEnumerator>, Trapdoor, float, bool, IEnumerator>(OnTrapdoorOpen)
        );
    }

    /// <summary>
    /// Notes a hatch that starts to open. The local player opened it, unless it is being opened here because the
    /// partner keeps theirs open.
    /// </summary>
    /// <param name="orig">The original method, which makes the routine that opens and closes the hatch.</param>
    /// <param name="self">The hatch.</param>
    /// <param name="sign">Which way it opens.</param>
    /// <param name="skipOpen">Whether it is open at once, as for a player coming out of it.</param>
    private IEnumerator OnTrapdoorOpen(
        Func<Trapdoor, float, bool, IEnumerator> orig,
        Trapdoor self,
        float sign,
        bool skipOpen
    ) {
        var routine = orig(self, sign, skipOpen);

        try {
            NoticeTrapdoorOpen(self, sign);
        } catch (Exception e) {
            Logger.Warn($"Could not follow a hatch opening for the partner: {e.Message}");
        }

        return routine;
    }

    /// <summary>
    /// Starts following a hatch that opens, and tells the partner if the local player opened it.
    /// </summary>
    private void NoticeTrapdoorOpen(Trapdoor door, float sign) {
        if (_checkedWith == null || !ClosesByItself(door)) {
            return;
        }

        var open = _openTrapdoors.Find(entry => entry.Door == door);
        if (open == null) {
            open = new OpenTrapdoor(door, door.gameObject.scene.name, ScenePath.Get(door.transform));
            _openTrapdoors.Add(open);
        }

        open.Sign = sign;

        // One the partner keeps open is theirs to say. It is still followed, because the local player may step into it
        // afterwards, and then they keep it open too.
        if (!_openingPartnerTrapdoor && !open.Sent) {
            SendTrapdoor(open, true);
        }
    }

    /// <summary>
    /// Tells the partner about the hatches that the local player keeps open, and keeps open the ones here that the
    /// partner keeps open in their game. Called every frame of a checked session.
    /// </summary>
    private void UpdateTrapdoors() {
        for (var i = _openTrapdoors.Count - 1; i >= 0; i--) {
            var open = _openTrapdoors[i];
            var door = open.Door;
            var shut = door == null || !door.isActiveAndEnabled || !door.IsOpen;

            // The partner was told when it opened. After that, what keeps it open for the local player is what the
            // game itself keeps it open for: being in the hatch.
            var kept = !shut && TrapdoorCloseTriggerField?.GetValue(door) is TrackTriggerObjects trigger &&
                       trigger != null && trigger.IsInside;
            if (open.Sent != kept) {
                SendTrapdoor(open, kept);
            }

            if (shut) {
                _openTrapdoors.RemoveAt(i);
            }
        }

        foreach (var partnerDoor in _partnerTrapdoors.Values) {
            HoldPartnerTrapdoor(partnerDoor);
        }
    }

    /// <summary>
    /// Tells the partner that the local player keeps a hatch open, or no longer does.
    /// </summary>
    private void SendTrapdoor(OpenTrapdoor open, bool keptOpen) {
        if (_checkedWith is not { } partnerId) {
            open.Sent = false;
            return;
        }

        var update = new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.Trapdoor,
            Scene = open.Scene,
            ObjectPath = open.Path,
            PartCount = (ushort) (keptOpen ? 1 : 0)
        };

        if (keptOpen) {
            update.Values = [open.Sign];
            if (open.Door != null && FindTrapdoorExit(open.Door) is { } exit) {
                update.Names = [exit.targetScene, exit.entryPoint];
            }
        }

        Send(update);
        open.Sent = keptOpen;

        Logger.Info(
            keptOpen
                ? $"Told the partner that the hatch '{open.Path}' in {open.Scene} is kept open here"
                : $"Told the partner that the hatch '{open.Path}' in {open.Scene} is no longer kept open here"
        );
    }

    /// <summary>
    /// The partner keeps a hatch open in their game, or no longer does.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names their hatch by its scene and path.</param>
    private void OnTrapdoor(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            return;
        }

        var key = update.Scene + "/" + update.ObjectPath;
        if (update.PartCount == 0) {
            // Nothing more to do: the hatch here counts down to closing from the moment it is no longer held open
            if (_partnerTrapdoors.Remove(key)) {
                Logger.Info(
                    $"{player.Username} no longer keeps the hatch '{update.ObjectPath}' in {update.Scene} open"
                );
            }

            return;
        }

        var partnerDoor = new PartnerTrapdoor(
            update.Scene,
            update.ObjectPath,
            update.Values.Count > 0 ? update.Values[0] : 0f,
            update.Names.Count > 1 ? update.Names[0] : "",
            update.Names.Count > 1 ? update.Names[1] : ""
        );
        _partnerTrapdoors[key] = partnerDoor;
        Logger.Info($"{player.Username} keeps the hatch '{update.ObjectPath}' in {update.Scene} open");

        HoldPartnerTrapdoor(partnerDoor);
    }

    /// <summary>
    /// Keeps the hatch here that goes with a hatch the partner keeps open from closing, opening it if it is shut.
    /// </summary>
    private void HoldPartnerTrapdoor(PartnerTrapdoor partnerDoor) {
        if (partnerDoor.Door == null) {
            // Looked for once per room: the same room that didn't have it before won't have it now either. Another
            // room might, as the local player may walk into the room of the partner, or into the next one along the
            // pipe, while the partner still keeps it open.
            var scene = SceneUtil.GetCurrentSceneName();
            if (partnerDoor.SearchedScene == scene) {
                return;
            }

            partnerDoor.SearchedScene = scene;
            partnerDoor.Door = FindPartnerTrapdoorHere(partnerDoor, out var sign);
            partnerDoor.LocalSign = sign;
            if (partnerDoor.Door == null) {
                return;
            }
        }

        var door = partnerDoor.Door;
        if (!door.isActiveAndEnabled) {
            return;
        }

        if (door.IsOpen) {
            TrapdoorResetCloseField?.SetValue(door, true);
            return;
        }

        // Shut, or finished closing just before the partner stepped back into it: it opens the way a lever opens it
        _openingPartnerTrapdoor = true;
        try {
            door.OpenDoor(System.Math.Sign(partnerDoor.LocalSign));
            Logger.Info($"Opened the hatch '{door.name}' that the partner keeps open");
        } catch (Exception e) {
            Logger.Warn($"Could not open the hatch '{door.name}' that the partner keeps open: {e.Message}");
        } finally {
            _openingPartnerTrapdoor = false;
        }
    }

    /// <summary>
    /// Finds the hatch here that goes with a hatch the partner keeps open: the same one if their room is loaded here,
    /// or the one at the other end of their pipe if that is where the local player is.
    /// </summary>
    /// <param name="partnerDoor">The hatch of the partner.</param>
    /// <param name="sign">Which way to open the hatch that is found.</param>
    /// <returns>The hatch, or null if none here goes with it.</returns>
    private static Trapdoor? FindPartnerTrapdoorHere(PartnerTrapdoor partnerDoor, out float sign) {
        sign = partnerDoor.Sign;
        if (ScenePath.Find(partnerDoor.Path, partnerDoor.Scene) is { } target &&
            target.GetComponent<Trapdoor>() is { } same && same != null && ClosesByItself(same)) {
            return same;
        }

        if (partnerDoor.ExitScene.Length == 0) {
            return null;
        }

        // The other end of the pipe is the hatch over the door that the way out of the partner's room comes in at, and
        // it opens the way it opens for a player coming out of it
        foreach (var entrance in TransitionPoint.TransitionPoints) {
            if (entrance == null || entrance.gameObject.name != partnerDoor.Entrance ||
                entrance.gameObject.scene.name != partnerDoor.ExitScene) {
                continue;
            }

            foreach (var door in UnityEngine.Object.FindObjectsByType<Trapdoor>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None
                     )) {
                if (door != null && door.gameObject.scene == entrance.gameObject.scene && ClosesByItself(door) &&
                    FindTrapdoorExit(door) == entrance && TrapdoorStartSignField?.GetValue(door) is float startSign) {
                    sign = startSign;
                    return door;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The way out of the room that a hatch sits over, found as the door inside the area that tells the game that its
    /// hero came in through the hatch. Only a hatch that opens for a player coming out of it has one.
    /// </summary>
    /// <returns>The way out, or null for a hatch in the middle of a room.</returns>
    private static TransitionPoint? FindTrapdoorExit(Trapdoor door) {
        if (TrapdoorStartSignField?.GetValue(door) is not float sign || Mathf.Abs(sign) <= Mathf.Epsilon ||
            TrapdoorEnterTriggerField?.GetValue(door) is not TrackTriggerObjects trigger || trigger == null ||
            trigger.GetComponent<Collider2D>() is not { } area || area == null || !area.isActiveAndEnabled) {
            return null;
        }

        var bounds = area.bounds;
        foreach (var exit in TransitionPoint.TransitionPoints) {
            if (exit == null || exit.gameObject.scene != door.gameObject.scene ||
                string.IsNullOrEmpty(exit.targetScene) || string.IsNullOrEmpty(exit.entryPoint)) {
                continue;
            }

            var position = exit.transform.position;
            if (position.x >= bounds.min.x && position.x <= bounds.max.x &&
                position.y >= bounds.min.y && position.y <= bounds.max.y) {
                return exit;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a hatch closes by itself a moment after the last one left it. The few others stay open once opened,
    /// are written into the save, or are opened and shut by a scene of their own, none of which is about who keeps
    /// them open.
    /// </summary>
    private static bool ClosesByItself(Trapdoor door) {
        if (TrapdoorPersistentField?.GetValue(door) is UnityEngine.Object persistent && persistent != null) {
            return false;
        }

        return TrapdoorStayOpenField?.GetValue(door) is false && TrapdoorCustomOpenedField?.GetValue(door) is false;
    }

    /// <summary>
    /// Forgets the hatches that the partner keeps open, and that the partner was told about, as the check with them
    /// ends. The hatches here close by themselves afterwards.
    /// </summary>
    private void ResetTrapdoors() {
        _partnerTrapdoors.Clear();
        foreach (var open in _openTrapdoors) {
            open.Sent = false;
        }
    }

    /// <summary>
    /// A hatch that opened here.
    /// </summary>
    private sealed class OpenTrapdoor {
        public OpenTrapdoor(Trapdoor door, string scene, string path) {
            Door = door;
            Scene = scene;
            Path = path;
        }

        /// <summary>
        /// The hatch, which is gone once its room is.
        /// </summary>
        public Trapdoor Door { get; }

        /// <summary>
        /// The scene of the hatch, kept for telling the partner after the hatch is gone.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of the hatch in its scene, kept for the same reason.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Which way it opened.
        /// </summary>
        public float Sign { get; set; }

        /// <summary>
        /// Whether the partner was last told that the local player keeps it open.
        /// </summary>
        public bool Sent { get; set; }
    }

    /// <summary>
    /// A hatch that the partner keeps open in their game.
    /// </summary>
    private sealed class PartnerTrapdoor {
        public PartnerTrapdoor(string scene, string path, float sign, string exitScene, string entrance) {
            Scene = scene;
            Path = path;
            Sign = sign;
            ExitScene = exitScene;
            Entrance = entrance;
        }

        /// <summary>
        /// The scene of their hatch.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of their hatch in that scene.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Which way their hatch opened.
        /// </summary>
        public float Sign { get; }

        /// <summary>
        /// The scene that the way out under their hatch leads to, or empty for a hatch in the middle of a room.
        /// </summary>
        public string ExitScene { get; }

        /// <summary>
        /// The door of <see cref="ExitScene"/> that the way out comes in at.
        /// </summary>
        public string Entrance { get; }

        /// <summary>
        /// The hatch here that goes with theirs, or null while none has been found.
        /// </summary>
        public Trapdoor? Door { get; set; }

        /// <summary>
        /// Which way <see cref="Door"/> opens.
        /// </summary>
        public float LocalSign { get; set; }

        /// <summary>
        /// The room in which <see cref="Door"/> was last looked for.
        /// </summary>
        public string? SearchedScene { get; set; }
    }
}
