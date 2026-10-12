using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries the colours of the sprites that the FSMs on a creature colour (see <see cref="FindSprites"/>)
/// from the game that runs it to the other one. While both players are in the room the copy runs the creature's colour
/// changes itself (see EntityFsmActions.Effects), but not the ones made before the copy was set up, and not all of one
/// that was cut short: the copy eases a colour in only while the scene host's creature is still in the state that eases
/// it when the word of that state arrives, and only while the copy is switched on. Otherwise the copy keeps the colour
/// the easing started from.
///
/// A crow roosting in front of the room is drawn there as a black silhouette and turns its own colour when it flies at
/// the player, so for whoever came in after it took off it attacked in black, and the room's own crow went on in black
/// when that player's game took it over (Entity.TakeTintFromCopy). The creatures of an arena come out of the background
/// black and ease into their own colours in about half a second as they join the fight, and for some kinds nothing
/// colours them again after that: a flying one that the partner's game called out went on fighting in black in the
/// other player's game (USER 10-12). Any creature that colours itself the same way had the same gap, so the sprites are
/// found from the FSMs of each creature rather than listed for one.
///
/// The scene host sends the colours of all of them each time they have changed and then stayed the same for a moment,
/// so a colour that eases in goes as one word, where it ends up - even where it was before it changed, because the copy
/// may not have followed it there - and again when this game starts running the creature. The server keeps the last
/// word for whoever walks in next. Like the word of the parts (PartsComponent), each word carries how many sprites
/// there are and a hash of their paths, so a game that found other sprites on the same creature leaves them alone.
internal class TintComponent : EntityComponent {
    /// <summary>
    /// How long the colours have to stay the same before they are sent, in seconds.
    /// </summary>
    private const float SettleTime = 0.25f;

    /// <summary>
    /// The most sprites of one creature that are looked after.
    /// </summary>
    private const int MaxSprites = 16;

    /// <summary>
    /// The sprites of the room's own creature, each null if it isn't there.
    /// </summary>
    private readonly tk2dBaseSprite?[] _hostSprites;

    /// <summary>
    /// The sprites of the copy, in the same order, each null if it isn't there.
    /// </summary>
    private readonly tk2dBaseSprite?[] _clientSprites;

    /// <summary>
    /// The hash of the paths of the sprites, in their order (see PartsComponent.HashPaths).
    /// </summary>
    private readonly int _pathsHash;

    /// <summary>
    /// The colours the sprites of the room's own creature had the last time they were looked at, and since when.
    /// </summary>
    private readonly Color32[] _lastSeen;

    /// <inheritdoc cref="_lastSeen" />
    private float _seenSince;

    /// <summary>
    /// Whether the colours are to be sent once they have stayed the same for a moment: they changed since they were
    /// sent last, or this game has just started running the creature.
    /// </summary>
    private bool _owed;

    /// <summary>
    /// Whether a word for other sprites than the ones found here has been written down.
    /// </summary>
    private bool _mismatchLogged;

    public TintComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        IReadOnlyList<string> paths
    ) : base(netClient, entityId, gameObject) {
        _hostSprites = new tk2dBaseSprite?[paths.Count];
        _clientSprites = new tk2dBaseSprite?[paths.Count];
        _lastSeen = new Color32[paths.Count];
        for (var i = 0; i < paths.Count; i++) {
            _hostSprites[i] = FindSprite(gameObject.Host, paths[i]);
            _clientSprites[i] = FindSprite(gameObject.Client, paths[i]);
            _lastSeen[i] = _hostSprites[i] is { } sprite && sprite != null ? sprite.color : Color.white;
        }

        _pathsHash = PartsComponent.HashPaths(paths);
    }

    /// <summary>
    /// Finds the sprites of a creature that the FSMs on it colour, by their paths under it, the creature's own sprite
    /// being the empty path. What they colour outside the creature is left out, and so are the FSMs on its children.
    /// </summary>
    /// <param name="root">The room's own creature.</param>
    /// <param name="fsms">The FSMs on it that the scene host runs for both games.</param>
    /// <returns>The paths in ordinal order, which is the order of their colours in both games.</returns>
    public static List<string> FindSprites(GameObject root, IEnumerable<PlayMakerFSM> fsms) {
        var paths = new List<string>();
        var found = new HashSet<GameObject>();
        foreach (var component in fsms) {
            if (component == null || component.Fsm is not { } fsm) {
                continue;
            }

            foreach (var state in component.FsmStates) {
                foreach (var action in state.Actions) {
                    FsmOwnerDefault? coloured = action switch {
                        Tk2dSpriteSetColor { Enabled: true } set => set.gameObject,
                        Tk2dSpriteTweenColor { Enabled: true } tween => tween.gameObject,
                        _ => null
                    };
                    if (coloured == null) {
                        continue;
                    }

                    var target = fsm.GetOwnerDefaultTarget(coloured) ?? PartsComponent.FindNamedTarget(fsm, coloured);
                    if (target == null || !target.transform.IsChildOf(root.transform) || !found.Add(target)) {
                        continue;
                    }

                    // A name that an earlier child shares would find that child instead, here and in the copy
                    var path = PartsComponent.GetPath(root.transform, target.transform);
                    if (FindSprite(root, path) is not { } sprite || sprite.gameObject != target) {
                        continue;
                    }

                    paths.Add(path);
                }
            }
        }

        paths.Sort(StringComparer.Ordinal);
        if (paths.Count > MaxSprites) {
            paths.RemoveRange(MaxSprites, paths.Count - MaxSprites);
        }

        return paths;
    }

    /// <summary>
    /// The sprite at a path under a creature, the creature's own for the empty path, or null if there is none.
    /// </summary>
    private static tk2dBaseSprite? FindSprite(GameObject? root, string path) {
        if (root == null) {
            return null;
        }

        var part = path.Length == 0 ? root.transform : root.transform.Find(path);
        if (part == null) {
            return null;
        }

        var sprite = part.GetComponent<tk2dBaseSprite>();
        return sprite == null ? null : sprite;
    }

    /// <inheritdoc />
    public override void SendAgain() {
        _owed = true;
    }

    /// <summary>
    /// Callback method to check whether the colours of the sprites have changed and settled.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        var changed = false;
        for (var i = 0; i < _hostSprites.Length; i++) {
            if (_hostSprites[i] is not { } sprite || sprite == null) {
                continue;
            }

            Color32 colour = sprite.color;
            if (!IsSame(colour, _lastSeen[i])) {
                _lastSeen[i] = colour;
                changed = true;
            }
        }

        if (changed) {
            _seenSince = Time.unscaledTime;
            _owed = true;
            return;
        }

        if (!_owed || Time.unscaledTime - _seenSince < SettleTime) {
            return;
        }

        _owed = false;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Tint
        };
        data.Packet.Write((byte) _lastSeen.Length);
        data.Packet.Write(_pathsHash);
        foreach (var colour in _lastSeen) {
            data.Packet.Write(colour.r);
            data.Packet.Write(colour.g);
            data.Packet.Write(colour.b);
            data.Packet.Write(colour.a);
        }

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
        // What the server keeps for later players is whatever was sent last, perhaps by the other game, so the colours
        // are sent again once this game runs the creature
        _owed = true;
        for (var i = 0; i < _hostSprites.Length; i++) {
            if (_hostSprites[i] is { } sprite && sprite != null) {
                _lastSeen[i] = sprite.color;
            }
        }

        _seenSince = Time.unscaledTime;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var count = data.Packet.ReadByte();
        var hash = data.Packet.ReadInt();
        var colours = new Color32[count];
        for (var i = 0; i < count; i++) {
            colours[i] = new Color32(data.Packet.ReadByte(), data.Packet.ReadByte(), data.Packet.ReadByte(),
                data.Packet.ReadByte());
        }

        if (count != _clientSprites.Length || hash != _pathsHash) {
            if (!_mismatchLogged) {
                _mismatchLogged = true;
                Logger.Warn(
                    $"The coloured sprites of '{(GameObject.Host != null ? GameObject.Host.name : "?")}' that the " +
                    "other game sent aren't the ones found here, so they are left alone"
                );
            }

            return;
        }

        for (var i = 0; i < count; i++) {
            if (_clientSprites[i] is { } sprite && sprite != null && !IsSame(sprite.color, colours[i])) {
                sprite.color = colours[i];
            }
        }
    }

    /// <summary>
    /// Gives the sprites of the room's own creature the colours of the copy's, as this game takes the creature over
    /// (see Entity.TakeTintFromCopy).
    /// </summary>
    public void TakeFromCopy() {
        for (var i = 0; i < _hostSprites.Length; i++) {
            if (_hostSprites[i] is { } roomSprite && roomSprite != null &&
                _clientSprites[i] is { } copySprite && copySprite != null) {
                roomSprite.color = copySprite.color;
            }
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// Whether two colours are the same.
    /// </summary>
    private static bool IsSame(Color32 one, Color32 other) {
        return one.r == other.r && one.g == other.g && one.b == other.b && one.a == other.a;
    }
}
