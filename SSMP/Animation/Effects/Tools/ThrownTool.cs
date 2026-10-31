using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Shows the things that the partner's tools throw and spawn. Each copy starts where the thrower's own thing was a
/// moment after it came out, moving the way it moved, and then runs as the game runs it: it flies, bounces, sticks and
/// bursts. What only the thrower's game can know of - what the thing touched there, what hit it, what broke it - comes
/// from there as it happens. A copy is only there to be seen: see <see cref="ToolCopies"/> for all it is kept out of.
/// </summary>
internal class ThrownTool : BaseAttackTool {
    /// <summary>
    /// The copies that are out, by the character of the player who threw them and the number the thrower gave them.
    /// </summary>
    private static readonly Dictionary<(int Player, byte Id), RemoteToolCopy> Copies = new();

    /// <summary>
    /// The seconds that a new copy is kept whatever the list of what is out says.
    /// </summary>
    private const float LiveListGrace = 1.5f;

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length < 2) {
            return;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(effectInfo));
            var kind = (ToolMessageKind) reader.ReadByte();
            var key = (playerObject.GetInstanceID(), reader.ReadByte());
            switch (kind) {
                case ToolMessageKind.Spawn:
                    Spawn(playerObject, key, reader);
                    break;
                case ToolMessageKind.State:
                    ChangeState(key, reader);
                    break;
                case ToolMessageKind.Hit:
                    LandHit(key, reader.ReadByte());
                    break;
                case ToolMessageKind.Motion:
                    Move(key, reader);
                    break;
                case ToolMessageKind.Break:
                    var how = reader.ReadByte();
                    if (Copies.TryGetValue(key, out var broken) && broken != null) {
                        ToolCopyRules.GetState(broken.PrefabName)?.Break(broken.gameObject, how);
                        Logger.Info(
                            $"[Tools] The copy of the partner's '{broken.PrefabName}' ({key.Item2}) breaks ({how}) as " +
                            $"the partner's did; {DescribePictures(broken.gameObject)}"
                        );
                    }

                    break;
                case ToolMessageKind.End:
                    if (Copies.TryGetValue(key, out var copy) && copy != null) {
                        Logger.Info(
                            $"[Tools] The copy of the partner's '{copy.PrefabName}' ({key.Item2}) goes, as the " +
                            $"partner's is gone; it was drawn as {DescribePictures(copy.gameObject)}"
                        );
                        Object.Destroy(copy.gameObject);
                    }

                    Copies.Remove(key);
                    break;
                case ToolMessageKind.Beam:
                    BeamShot.Play(playerObject, reader);
                    break;
                case ToolMessageKind.Imbue:
                    ImbuedNail.Play(playerObject, reader);
                    break;
                case ToolMessageKind.LiveList:
                    KeepLive(playerObject, reader);
                    break;
            }
        } catch (IOException) {
            Logger.Warn("Could not read a message about a tool of the partner");
        } catch (Exception e) {
            Logger.Warn($"Could not show a tool of the partner: {e.Message}");
        }
    }

    /// <summary>
    /// Makes the copy of a thing that the partner threw.
    /// </summary>
    private static void Spawn(GameObject playerObject, (int, byte) key, BinaryReader reader) {
        var spawn = ToolMessages.ReadSpawn(reader);

        // A thing whose state goes all the time sends this again and again, for a copy that is already there; any
        // other copy under the number is of a thing that is gone, whose end got lost on the way
        if (Copies.TryGetValue(key, out var old) && old != null && old.PrefabName == spawn.PrefabName &&
            ToolCopyRules.GetStreamInterval(spawn.PrefabName) > 0f) {
            spawn.Snapshot.ApplyTo(old.gameObject);
            var scale = old.transform.localScale;
            old.transform.localScale = new Vector3(spawn.Scale.x, spawn.Scale.y, scale.z);
            if (spawn.Extra.Length > 0) {
                ToolCopyRules.GetState(spawn.PrefabName)?.Apply(old.gameObject, spawn.Extra);
            }

            return;
        }

        var prefab = ToolCopies.FindPrefab(spawn.PrefabName);
        if (prefab == null) {
            Logger.Warn($"There is no tool thing '{spawn.PrefabName}' to show for the partner");
            return;
        }

        // A number comes round again only long after the thing that had it is gone
        if (old != null) {
            Object.Destroy(old.gameObject);
        }

        var copy = HeroCopies.Make(
            ToolCopies.GetCopyPrefab(prefab),
            playerObject,
            new Vector3(spawn.Snapshot.Position.x, spawn.Snapshot.Position.y, spawn.Z),
            Quaternion.Euler(0f, 0f, spawn.Snapshot.Rotation)
        );
        copy.name = prefab.name;
        copy.transform.localScale = new Vector3(spawn.Scale.x, spawn.Scale.y, prefab.transform.localScale.z);

        var marker = copy.AddComponentIfNotPresent<RemoteToolCopy>();
        marker.Poisoned = spawn.Poisoned;
        marker.FromThrower = true;
        marker.PrefabName = spawn.PrefabName;
        marker.SpawnTime = Time.time;
        marker.Destroyed = gone => {
            if (Copies.TryGetValue(key, out var current) && current == gone) {
                Copies.Remove(key);
            }
        };

        copy.SetActive(true);
        ToolCopies.PrepareCopy(copy, spawn.Poisoned);
        var state = ToolCopyRules.GetState(spawn.PrefabName);
        state?.PrepareCopy(copy, playerObject, spawn.Poisoned);
        ToolCopies.LeaveToThrower(copy, spawn.PrefabName, marker);
        spawn.Snapshot.ApplyTo(copy);
        if (spawn.Extra.Length > 0) {
            state?.Apply(copy, spawn.Extra);
        }

        if (spawn.Follows) {
            copy.AddComponent<FollowPlayer>().Follow(playerObject.transform, spawn.Offset);
        }

        Copies[key] = marker;
        var place = copy.transform.position;
        Logger.Info(
            $"[Tools] The copy of the partner's '{spawn.PrefabName}' ({key.Item2}) comes out at ({place.x:0.00}, " +
            $"{place.y:0.00}, z {place.z:0.000}), scale ({spawn.Scale.x:0.00}, {spawn.Scale.y:0.00}), turned " +
            $"{spawn.Snapshot.Rotation:0}, poisoned: {spawn.Poisoned}; {DescribePictures(copy)}"
        );
        SayPicturesLater(copy, $"The copy of the partner's '{spawn.PrefabName}' ({key.Item2})");
    }

    /// <summary>
    /// How long after it comes out a copy is described again: a mod that colours what is drawn, like Player Color,
    /// chooses the material of an object as it is first drawn, after it came out.
    /// </summary>
    private const float DescribedAgainAfter = 0.3f;

    /// <summary>
    /// Says again what the pictures of a copy are drawn from, a moment after it came out, if it is still there. The
    /// spike ball looked wrong only some of the times it was thrown (USER 10-31: "刺团并不是每次都花的，而是某几次
    /// 花"), so a throw that looks wrong is told apart from the others by what it was drawn with.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="what">What it is, as the log names it.</param>
    internal static void SayPicturesLater(GameObject copy, string what) {
        MonoBehaviourUtil.Instance.StartCoroutine(SayLater());

        IEnumerator SayLater() {
            yield return new WaitForSeconds(DescribedAgainAfter);
            if (copy != null && copy.activeInHierarchy) {
                Logger.Info($"[Tools] {what} a moment later: {DescribePictures(copy)}");
            }
        }
    }

    /// <summary>
    /// What each picture of a copy of a partner's thing is drawn from: the frame, its sheet, and the texture and shader
    /// of its material. A picture drawn from the wrong sheet or texture is a broken look, and nothing else can tell
    /// afterwards which it was (USER 10-31: "这个渲染问题实际上是道具刺团上出现的").
    /// </summary>
    internal static string DescribePictures(GameObject copy) {
        var parts = new List<string>();
        var bySheets = new HashSet<Renderer>();
        foreach (var sprite in copy.GetComponentsInChildren<tk2dBaseSprite>(true)) {
            try {
                var material = sprite.TryGetComponent<Renderer>(out var renderer) ? renderer.sharedMaterial : null;
                if (renderer != null) {
                    bySheets.Add(renderer);
                }

                var texture = material != null ? material.mainTexture : null;
                var sheet = sprite.Collection;
                parts.Add(
                    $"'{(sprite.gameObject == copy ? "itself" : sprite.name)}' shows " +
                    $"'{sprite.CurrentSprite?.name ?? "nothing"}' of '{(sheet != null ? sheet.name : "no sheet")}' on " +
                    (texture != null ? $"'{texture.name}' {texture.width}x{texture.height}" : "no texture") +
                    $" with '{(material != null && material.shader != null ? material.shader.name : "no shader")}'" +
                    (sprite.gameObject.activeInHierarchy ? "" : " (off)")
                );
            } catch (Exception e) {
                parts.Add($"'{sprite.name}' could not be read ({e.GetType().Name})");
            }
        }

        // What draws without a sheet: a sprite of one of the game's atlases, particles, a line. A texture with no name
        // is the image of a skin
        foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true)) {
            if (bySheets.Contains(renderer)) {
                continue;
            }

            try {
                var material = renderer.sharedMaterial;
                var picture = renderer is SpriteRenderer { sprite: { } shown }
                    ? $"'{shown.name}' of '{(shown.texture != null ? shown.texture.name : "no texture")}'"
                    : renderer.GetType().Name;
                var texture = material != null ? material.mainTexture : null;
                parts.Add(
                    $"'{(renderer.gameObject == copy ? "itself" : renderer.name)}' draws {picture} with " +
                    $"'{(material != null ? material.name : "no material")}'" +
                    (texture != null ? $" on '{texture.name}' {texture.width}x{texture.height}" : "") +
                    $" and '{(material != null && material.shader != null ? material.shader.name : "no shader")}'" +
                    (renderer.enabled && renderer.gameObject.activeInHierarchy ? "" : " (off)")
                );
            } catch (Exception e) {
                parts.Add($"'{renderer.name}' could not be read ({e.GetType().Name})");
            }
        }

        return parts.Count == 0 ? "no pictures" : string.Join("; ", parts);
    }

    /// <summary>
    /// Changes the state of a state machine of a copy the way it changed for the thrower's thing, with the numbers the
    /// thrower's had, and puts the copy where the thing was after it.
    /// </summary>
    private static void ChangeState((int, byte) key, BinaryReader reader) {
        var fsmIndex = reader.ReadByte();
        var state = reader.ReadString();
        var snapshot = ToolSnapshot.Read(reader);
        var floats = new float[reader.ReadByte()];
        for (var i = 0; i < floats.Length; i++) {
            floats[i] = reader.ReadSingle();
        }

        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        var fsms = copy.GetComponentsInChildren<PlayMakerFSM>(true);
        if (fsmIndex >= fsms.Length || !fsms[fsmIndex].isActiveAndEnabled || fsms[fsmIndex].Fsm == null) {
            return;
        }

        var fsm = fsms[fsmIndex];
        var names = ToolCopyRules.GetCarriedFloats(copy.PrefabName, fsm.FsmName);
        for (var i = 0; i < names.Length && i < floats.Length; i++) {
            var variable = fsm.FsmVariables.FindFsmFloat(names[i]);
            if (variable != null) {
                variable.Value = floats[i];
            }
        }

        // A copy made this frame has not started its state machine yet, which would then enter the state a second
        // time as it starts; and what the state spawns as it starts comes out where the thrower's thing was
        if (!fsm.Fsm.Started) {
            fsm.Fsm.Start();
        }

        snapshot.ApplyTo(copy.gameObject);
        fsm.Fsm.SetState(state);
        snapshot.ApplyTo(copy.gameObject);

        var place = copy.transform.position;
        Logger.Info(
            $"[Tools] The copy of the partner's '{copy.PrefabName}' ({key.Item2}) goes to '{state}' of " +
            $"'{fsm.FsmName}' as the partner's did, at ({place.x:0.00}, {place.y:0.00}); " +
            DescribePictures(copy.gameObject)
        );
    }

    /// <summary>
    /// Takes away the copies of a player's things that are gone in their game but whose end got lost on the way, and
    /// puts the look of their needle's element right. A copy only just made is left alone, as the list may have been
    /// written before its thing came out.
    /// </summary>
    private static void KeepLive(GameObject playerObject, BinaryReader reader) {
        ImbuedNail.Keep(playerObject, (NailElements) reader.ReadByte());

        var live = new HashSet<byte>();
        for (var count = reader.ReadByte(); count > 0; count--) {
            live.Add(reader.ReadByte());
        }

        var player = playerObject.GetInstanceID();
        List<(int, byte)>? gone = null;
        foreach (var pair in Copies) {
            if (pair.Key.Player != player || live.Contains(pair.Key.Id)) {
                continue;
            }

            if (pair.Value != null) {
                if (Time.time - pair.Value.SpawnTime < LiveListGrace) {
                    continue;
                }

                Object.Destroy(pair.Value.gameObject);
            }

            (gone ??= []).Add(pair.Key);
        }

        if (gone == null) {
            return;
        }

        foreach (var key in gone) {
            Copies.Remove(key);
        }
    }

    /// <summary>
    /// Puts a copy of a thing that its own code moves where the thrower's thing was and in its state.
    /// </summary>
    private static void Move((int, byte) key, BinaryReader reader) {
        var snapshot = ToolSnapshot.Read(reader);
        var state = reader.ReadBytes(reader.ReadByte());

        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        snapshot.ApplyTo(copy.gameObject);
        ToolCopyRules.GetState(copy.PrefabName)?.Apply(copy.gameObject, state);
    }

    /// <summary>
    /// Has a damager of a copy react to a hit that the thrower's thing landed.
    /// </summary>
    private static void LandHit((int, byte) key, byte damagerIndex) {
        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        var damagers = copy.GetComponentsInChildren<DamageEnemies>(true);
        if (damagerIndex < damagers.Length) {
            ToolCopies.LandHit(damagers[damagerIndex]);
        }
    }

    /// <summary>
    /// Takes away the copies of the things of a player who leaves the room, as their things are gone with the room
    /// they were in.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    public static void RemoveCopies(GameObject playerObject) {
        var player = playerObject.GetInstanceID();
        List<(int, byte)>? gone = null;
        foreach (var pair in Copies) {
            if (pair.Key.Player != player) {
                continue;
            }

            if (pair.Value != null) {
                Object.Destroy(pair.Value.gameObject);
            }

            (gone ??= []).Add(pair.Key);
        }

        if (gone == null) {
            return;
        }

        foreach (var key in gone) {
            Copies.Remove(key);
        }
    }

    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        return [];
    }

    /// <summary>
    /// Marks a copy as the partner's, with hits that play on enemies without harming them.
    /// </summary>
    public static void MarkRemote(GameObject copy) {
        FixRemoteAttack(copy);
    }
}

/// <summary>
/// Keeps a copy of a thing that stays with the hero of the thrower, like an effect around them, with the character of
/// the thrower here.
/// </summary>
internal class FollowPlayer : MonoBehaviour {
    /// <summary>
    /// The character of the thrower.
    /// </summary>
    private Transform? _target;

    /// <summary>
    /// Where the thing is from the character.
    /// </summary>
    private Vector2 _offset;

    /// <summary>
    /// Puts the thing where it is from the character, and keeps it there.
    /// </summary>
    public void Follow(Transform target, Vector2 offset) {
        _target = target;
        _offset = offset;
        Move();
    }

    private void LateUpdate() {
        Move();
    }

    private void Move() {
        if (_target == null) {
            return;
        }

        var position = _target.position;
        transform.position = new Vector3(position.x + _offset.x, position.y + _offset.y, transform.position.z);
    }
}
