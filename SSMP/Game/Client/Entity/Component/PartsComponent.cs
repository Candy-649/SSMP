using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries which parts of a creature are switched on from the game that runs it to the other one. A
/// part is a child that the creature's own FSMs switch on or off for good rather than for the length of a state, and
/// that is only there to be seen: sprites, and nothing that collides, sounds, sparkles or runs (see
/// <see cref="FindParts"/>). While both players are in the room the copy switches its parts as the scene host's
/// creature does (EntityFsmActions), but not the ones that were switched before the copy was set up, and the room's own
/// creature, asleep while the other game runs it, keeps whatever its own first steps switched before it was put to
/// sleep. A game that takes the creature over wakes it with those.
///
/// A tall reed rests wrapped in one of two cobwebs that its first steps pick, and only its first wake takes the cobweb
/// off: whether it woke before is kept in its FSM. A game that took the room over woke the room's own reed with the
/// cobweb it had put on by itself, and with the other game's word that it had woken already, so nothing took it off and
/// the reed got up and fought in its cobweb (USER 10-08). Any creature that dresses itself the same way had the same
/// gap, so the parts are found from the FSMs of each creature rather than listed for one.
///
/// The scene host sends the parts once they have stayed the same for a moment, and again when this game starts running
/// the creature. The server keeps the last word for whoever walks in next. The other game puts every word on the room's
/// own creature, so that the game that takes it over finds its parts the way the other game left them, and the first
/// word it hears on the copy as well. After that the copy follows the creature's own switches as they happen, which a
/// word sent a moment later would only switch back for that moment.
///
/// Each word carries how many parts there are and a hash of their paths, in the order of their bits. A game that found
/// other parts on the same creature, like one whose creature was changed by something else first, would put the word on
/// the wrong parts, so it takes none.
///
/// A part through which a player talks to the creature is one too (see <see cref="IsTalkPart"/>). A knight that rides a
/// bug is talked to through such parts, which its FSM switches on as the room starts, so the player of a game that
/// came in after that could not talk to it at all while the partner could (USER 10-09). Each player talks in their own
/// game, as to any character. Unlike a part that is only seen, every word goes on the copy's talk parts, because one
/// switches itself off in its own FSM, which the copy never hears of - but never one that would cut off the local
/// player while they are talking: that waits until they are done.
internal class PartsComponent : EntityComponent {
    /// <summary>
    /// How long the parts have to stay the same before they are sent, in seconds.
    /// </summary>
    private const float SettleTime = 0.25f;

    /// <summary>
    /// The most parts of one creature that are looked after, one bit each.
    /// </summary>
    private const int MaxParts = 31;

    /// <summary>
    /// The parts of the room's own creature, each null if it isn't there.
    /// </summary>
    private readonly GameObject?[] _hostParts;

    /// <summary>
    /// The parts of the copy, in the same order, each null if it isn't there.
    /// </summary>
    private readonly GameObject?[] _clientParts;

    /// <summary>
    /// Which parts were on when they were sent last, a bit for each, or null if nothing has been sent since this game
    /// started running the creature.
    /// </summary>
    private int? _lastSent;

    /// <summary>
    /// Which parts were on the last time they were looked at, and since when.
    /// </summary>
    private int _lastSeen;

    /// <inheritdoc cref="_lastSeen" />
    private float _seenSince;

    /// <summary>
    /// Whether the copy got the parts since this game last stopped running the creature.
    /// </summary>
    private bool _copyDressed;

    /// <summary>
    /// A hash of the paths of the parts in their order, which tells a word about the same parts from one about others.
    /// </summary>
    private readonly int _pathsHash;

    /// <summary>
    /// Whether a word about other parts was logged already, which is logged once.
    /// </summary>
    private bool _mismatchLogged;

    /// <summary>
    /// Which parts are ones through which a player talks to the creature, a bit for each.
    /// </summary>
    private readonly int _talkParts;

    /// <summary>
    /// Which talk parts of the copy the last word switched off while the local player was talking, a bit for each.
    /// They are switched off once the player is done.
    /// </summary>
    private int _talkPartsToSwitchOff;

    public PartsComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        IReadOnlyList<string> paths
    ) : base(netClient, entityId, gameObject) {
        _hostParts = new GameObject?[paths.Count];
        _clientParts = new GameObject?[paths.Count];
        for (var i = 0; i < paths.Count; i++) {
            _hostParts[i] = FindPart(gameObject.Host, paths[i]);
            _clientParts[i] = FindPart(gameObject.Client, paths[i]);
            if ((_hostParts[i] ?? _clientParts[i]) is { } part && IsTalkPart(part)) {
                _talkParts |= 1 << i;
            }
        }

        _pathsHash = HashPaths(paths);
    }

    /// <summary>
    /// Finds the parts of a creature, by their paths under it: the children that its FSMs switch on or off for good,
    /// whose whole tree is only something to see, or through which a player talks to it. A child that collides, makes a
    /// sound, sends particles, runs an FSM or does anything else is left to the switches that the copy plays as they
    /// happen, where a word that came a moment late could hurt, sound or burst again on its own.
    /// </summary>
    /// <param name="root">The room's own creature.</param>
    /// <param name="fsms">Its FSMs that the scene host runs for both games.</param>
    /// <returns>The paths in ordinal order, which is the order of their bits in both games.</returns>
    public static List<string> FindParts(GameObject root, IEnumerable<PlayMakerFSM> fsms) {
        var paths = new List<string>();
        var found = new HashSet<GameObject>();
        foreach (var component in fsms) {
            if (component == null || component.Fsm is not { } fsm) {
                continue;
            }

            foreach (var state in component.FsmStates) {
                foreach (var action in state.Actions) {
                    // One that switches back when its state is left, or every frame, is only for the moment
                    if (action is not ActivateGameObject { Enabled: true, resetOnExit: false, everyFrame: false } activate) {
                        continue;
                    }

                    var target = fsm.GetOwnerDefaultTarget(activate.gameObject) ?? FindNamedTarget(fsm, activate.gameObject);
                    if (target == null || target == root || !target.transform.IsChildOf(root.transform) ||
                        !found.Add(target) || !IsOnlyShown(target) && !IsTalkPart(target)) {
                        continue;
                    }

                    // A name that an earlier child shares would find that child instead, here and in the copy
                    var path = GetPath(root.transform, target.transform);
                    if (root.transform.Find(path) != target.transform) {
                        continue;
                    }

                    paths.Add(path);
                }
            }
        }

        paths.Sort(StringComparer.Ordinal);
        if (paths.Count > MaxParts) {
            paths.RemoveRange(MaxParts, paths.Count - MaxParts);
        }

        return paths;
    }

    /// <summary>
    /// A hash of paths in their order (32-bit FNV-1a), the same in every game.
    /// </summary>
    private static int HashPaths(IReadOnlyList<string> paths) {
        unchecked {
            var hash = 2166136261u;
            foreach (var path in paths) {
                foreach (var c in path) {
                    hash = (hash ^ c) * 16777619u;
                }

                hash = (hash ^ '\n') * 16777619u;
            }

            return (int) hash;
        }
    }

    /// <summary>
    /// The child that a variable of an FSM is to hold once the FSM has found it by name, for a variable that is still
    /// empty: the creature fills it in its first steps, which have not run yet when it is looked at here. The knight's
    /// talk parts are switched through such variables.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="target">What an action of the FSM is given to work on.</param>
    private static GameObject? FindNamedTarget(HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmOwnerDefault target) {
        if (target.OwnerOption != HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject ||
            target.GameObject is not { UseVariable: true, Name: { Length: > 0 } name }) {
            return null;
        }

        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                // FindNamedChild finds the child that has the name of the variable it fills (its IL: Transform.Find of
                // storeResult.Name)
                if (action is not FindNamedChild { storeResult: { } store } find || store.Name != name ||
                    fsm.GetOwnerDefaultTarget(find.gameObject) is not { } parent) {
                    continue;
                }

                var child = parent.transform.Find(name);
                if (child != null) {
                    return child.gameObject;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a child is one through which a player talks to the creature: it is a character to talk to itself, and
    /// nothing under it hurts, can be hurt, or is picked up or used. What is picked up is each player's own, so a pickup
    /// must stay where each game left it.
    /// </summary>
    private static bool IsTalkPart(GameObject part) {
        if (part.GetComponent<NPCControlBase>() == null) {
            return false;
        }

        foreach (var component in part.GetComponentsInChildren<UnityEngine.Component>(true)) {
            switch (component) {
                case DamageHero:
                case HealthManager:
                case InteractableBase and not NPCControlBase:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the local player may be talking through a talk part right now: its dialogue is running, or the player is
    /// not their own master, as they are not from the moment they start talking until the talk lets them go.
    /// </summary>
    private static bool MayBeTalking(GameObject part) {
        foreach (var npc in part.GetComponentsInChildren<PlayMakerNPC>(true)) {
            if (npc.IsRunningDialogue) {
                return true;
            }
        }

        return HeroController.instance is { controlReqlinquished: true };
    }

    /// <summary>
    /// Whether everything in the tree of a child is only there to be seen: sprites and what draws them, at least one.
    /// </summary>
    private static bool IsOnlyShown(GameObject part) {
        var shows = false;
        foreach (var component in part.GetComponentsInChildren<UnityEngine.Component>(true)) {
            switch (component) {
                case Transform:
                case MeshFilter:
                case tk2dBaseSprite:
                case tk2dSpriteAnimator:
                    break;
                case ParticleSystemRenderer:
                    return false;
                case Renderer:
                    shows = true;
                    break;
                default:
                    // Anything else, a script that is missing among it
                    return false;
            }
        }

        return shows;
    }

    /// <summary>
    /// The path of an object under another, as Transform.Find takes it.
    /// </summary>
    private static string GetPath(Transform root, Transform part) {
        var names = new List<string>();
        for (var current = part; current != null && current != root; current = current.parent) {
            names.Add(current.name);
        }

        names.Reverse();
        var path = new StringBuilder();
        foreach (var name in names) {
            if (path.Length > 0) {
                path.Append('/');
            }

            path.Append(name);
        }

        return path.ToString();
    }

    /// <summary>
    /// Callback method to check whether the parts have changed and settled.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            SwitchOffTalkPartsLeftOver();
            return;
        }

        var parts = ReadParts();
        if (parts != _lastSeen) {
            _lastSeen = parts;
            _seenSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _seenSince < SettleTime || _lastSent == parts) {
            return;
        }

        _lastSent = parts;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Parts
        };
        data.Packet.Write((byte) _hostParts.Length);
        data.Packet.Write(_pathsHash);
        for (var i = 0; i < _hostParts.Length; i++) {
            data.Packet.Write((parts & (1 << i)) != 0);
        }

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
        // What the server keeps for later players is whatever was sent last, perhaps by the other game, so the parts
        // are sent again once this game runs the creature
        _lastSent = null;
        _lastSeen = ReadParts();
        _seenSince = Time.unscaledTime;
    }

    /// <inheritdoc />
    public override void InitializeClient(uint sceneHostEpoch) {
        base.InitializeClient(sceneHostEpoch);

        // The copy was hidden while this game ran the creature, so it is dressed again by the next word
        _copyDressed = false;
        _talkPartsToSwitchOff = 0;
    }

    /// <summary>
    /// Switches off the talk parts of the copy that a word switched off while the local player was talking, once they
    /// are done.
    /// </summary>
    private void SwitchOffTalkPartsLeftOver() {
        if (_talkPartsToSwitchOff == 0) {
            return;
        }

        for (var i = 0; i < _clientParts.Length; i++) {
            if ((_talkPartsToSwitchOff & (1 << i)) == 0 || _clientParts[i] is { } part && part != null &&
                part.activeSelf && MayBeTalking(part)) {
                continue;
            }

            _talkPartsToSwitchOff &= ~(1 << i);
            SetOn(_clientParts[i], false);
        }
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var count = data.Packet.ReadByte();
        var hash = data.Packet.ReadInt();
        var parts = new bool[count];
        for (var i = 0; i < count; i++) {
            parts[i] = data.Packet.ReadBool();
        }

        if (count != _hostParts.Length || hash != _pathsHash) {
            if (!_mismatchLogged) {
                _mismatchLogged = true;
                Logger.Warn(
                    $"The parts of '{(GameObject.Host != null ? GameObject.Host.name : "?")}' that the other game " +
                    "sent aren't the ones found here, so they are left alone"
                );
            }

            return;
        }

        var dressCopy = !_copyDressed;
        _copyDressed = true;
        for (var i = 0; i < count; i++) {
            var bit = 1 << i;
            if ((_talkParts & bit) != 0) {
                SetTalkPartOfCopy(i, parts[i]);
            } else if (dressCopy) {
                SetOn(_clientParts[i], parts[i]);
            }

            SetOn(_hostParts[i], parts[i]);
        }
    }

    /// <summary>
    /// Switches a talk part of the copy as a word says, but leaves one on while the local player may be talking
    /// through it: switched off under them, the conversation and its box would be left without the FSM that ends
    /// them, and the player without control for good (NPCControlBase.OnDisable closes neither).
    /// </summary>
    /// <param name="index">The index of the part.</param>
    /// <param name="isOn">Whether the word says it is on.</param>
    private void SetTalkPartOfCopy(int index, bool isOn) {
        var bit = 1 << index;
        if (isOn) {
            _talkPartsToSwitchOff &= ~bit;
            SetOn(_clientParts[index], true);
            return;
        }

        if (_clientParts[index] is { } part && part != null && part.activeSelf && MayBeTalking(part)) {
            _talkPartsToSwitchOff |= bit;
            return;
        }

        _talkPartsToSwitchOff &= ~bit;
        SetOn(_clientParts[index], false);
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// Which parts of the room's own creature are on, a bit for each.
    /// </summary>
    private int ReadParts() {
        var parts = 0;
        for (var i = 0; i < _hostParts.Length; i++) {
            if (_hostParts[i] is { } part && part != null && part.activeSelf) {
                parts |= 1 << i;
            }
        }

        return parts;
    }

    /// <summary>
    /// Finds a part of a creature by its path under it.
    /// </summary>
    private static GameObject? FindPart(GameObject? root, string path) {
        if (root == null) {
            return null;
        }

        var part = root.transform.Find(path);
        return part == null ? null : part.gameObject;
    }

    /// <summary>
    /// Switches a part on or off, if it is there.
    /// </summary>
    private static void SetOn(GameObject? part, bool isOn) {
        if (part != null && part.activeSelf != isOn) {
            part.SetActive(isOn);
        }
    }
}
