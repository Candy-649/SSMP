using System;
using System.IO;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The deaths of the creatures that each player has to themselves (see PersonalPlaces), the flies that a player
/// bounces off, which one blow kills. Each game keeps its own, so a fly that the partner bounced off and killed is
/// still there for this game's player to bounce off, rather than gone for both until one of them is put back at a safe
/// place (USER 10-23, "本来时间就很赶了还要等"). What the death looked like is no longer the killer's alone: their game
/// tells the other games in the room, and those show the body flung away, its sound and what else only shows of it,
/// while their own creature stays as it is (USER 10-23, "演碎裂特效但是不碎", like a strike of the partner on a pod, see
/// CoopHits.Bounces). Not on one that is gone there already, and not what only the killer has: the kill in the journal,
/// and the shake of the screen with the moment the game stops for a kill.
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// Whether sending or showing the death of a creature that each player has to themselves threw, which is only
    /// logged once.
    /// </summary>
    private bool _deathLookFailed;

    /// <summary>
    /// Whether the death of the partner's creature is being shown here, which shakes nothing (see
    /// <see cref="OnDeathCameraShake"/>).
    /// </summary>
    private bool _showingDeathLook;

    /// <summary>
    /// Registers the hooks that send the deaths of the creatures that each player has to themselves, and that keep
    /// the screen still as one of the partner's is shown here.
    /// </summary>
    private void RegisterDeathLookHooks() {
        AddHook(
            typeof(EnemyDeathEffectsRegular.SimulateDeath).GetMethod(
                nameof(EnemyDeathEffectsRegular.SimulateDeath.OnEnter), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<EnemyDeathEffectsRegular.SimulateDeath>, EnemyDeathEffectsRegular.SimulateDeath>(
                OnSimulateDeath
            )
        );
        AddHook(
            typeof(EnemyDeathEffects).GetMethod("ShakeCameraIfVisible", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<EnemyDeathEffects>, EnemyDeathEffects>(OnDeathCameraShake)
        );
    }

    /// <summary>
    /// Hook for a creature shaking the screen as it dies, where it is seen, which also stops the game for a moment:
    /// left out for the death of the partner's creature shown here.
    /// </summary>
    private void OnDeathCameraShake(Action<EnemyDeathEffects> orig, EnemyDeathEffects self) {
        if (!_showingDeathLook) {
            orig(self);
        }
    }

    /// <summary>
    /// Hook for a creature playing a death that leaves it where it is, put away until it comes back, as the flies that
    /// a player bounces off do. The death of one that each player has to themselves goes to the other games in the
    /// room.
    /// </summary>
    private void OnSimulateDeath(
        Action<EnemyDeathEffectsRegular.SimulateDeath> orig,
        EnemyDeathEffectsRegular.SimulateDeath self
    ) {
        orig(self);

        try {
            if (HasMembers && _netClient.IsConnected && FSMUtility.GetSafe(self.target, self) is { } creature &&
                creature != null) {
                TellPersonalDeath(creature);
            }
        } catch (Exception e) {
            LogDeathLookError(e);
        }
    }

    /// <summary>
    /// Sends the members in the room the death of a creature here that each player has to themselves, with the way
    /// its body is flung, which the game works out from the blow that killed it. Only this game's player kills one
    /// here: the copies of the partner's attacks leave it alone.
    /// </summary>
    /// <param name="creature">The creature.</param>
    private void TellPersonalDeath(GameObject creature) {
        if (!IsAnyMemberInRoom() || !PersonalPlaces.Contains(creature) ||
            creature.GetComponent<EnemyDeathEffectsRegular>() == null ||
            !TryGetPlace(creature, out var scene, out var path)) {
            return;
        }

        var angle = creature.TryGetComponent<HealthManager>(out var health)
            ? DirectionUtils.GetAngle(health.GetAttackDirection())
            : float.NaN;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(angle);
        writer.Flush();

        SendToRoomMembers(new CoopHitUpdate {
            Kind = CoopHitKind.DeathLook,
            Scene = scene,
            Path = path,
            Responder = nameof(EnemyDeathEffectsRegular),
            Index = 0,
            Hit = stream.ToArray()
        });
        NoteTraffic(scene, path, "sent its death");
    }

    /// <summary>
    /// Shows the death of the partner's creature on this game's own (see <see cref="CoopHits"/>), which stays as it is:
    /// its body flung away the way it was there, its sound and what else only shows of it. Not on one that is gone
    /// here already, dead or not back yet, which can't be struck here.
    /// </summary>
    private void ShowDeathLook(CoopHitUpdate update) {
        var creature = FindPlace(update);
        if (creature == null) {
            NotReplayed(update, "death", "the creature is not here");
            return;
        }

        if (!PersonalPlaces.Contains(creature) ||
            creature.GetComponent<EnemyDeathEffectsRegular>() is not { } deathEffects) {
            NotReplayed(update, "death", "it is not one that each player has to themselves here");
            return;
        }

        if (!creature.activeInHierarchy || creature.GetComponent<Collider2D>() is not { enabled: true }) {
            NotReplayed(update, "death", "it is gone here already");
            return;
        }

        float angle;
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            angle = reader.ReadSingle();
        } catch (Exception e) {
            Logger.Warn($"Could not read the death of the partner's creature on {update.Path}: {e.Message}");
            return;
        }

        _showingDeathLook = true;
        try {
            // What the game plays for a death that leaves the creature where it is, but for the kill in the journal
            var corpse = deathEffects.IsCorpseRecyclable
                ? deathEffects.EmitCorpse(
                    float.IsNaN(angle) ? null : angle, 1f, AttackTypes.Generic, NailElements.None, null, null, out _
                )
                : null;
            deathEffects.EmitEffects(corpse);
        } catch (Exception e) {
            LogDeathLookError(e);
            NoteTraffic(update.Scene, update.Path, "got its death, whose showing failed");
            return;
        } finally {
            _showingDeathLook = false;
        }

        NoteReplayed(update, "death", true);
    }

    /// <summary>
    /// Logs that sending or showing the death of a creature that each player has to themselves threw, the first time.
    /// </summary>
    private void LogDeathLookError(Exception e) {
        if (_deathLookFailed) {
            return;
        }

        _deathLookFailed = true;
        Logger.Error($"Could not show the death of a creature in the other games:\n{e}");
    }
}
