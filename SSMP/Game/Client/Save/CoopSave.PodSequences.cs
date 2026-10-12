using System;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The music boxes, in a checked two-player save: bells that the box rings in a tune once a player steps on its plate,
/// which the player then strikes in the same order, three rounds of a longer tune each, with a reward at the end
/// (BouncePodSequence, in three rooms). The bells are bounce pods, which in a room with the partner are each player's
/// own: the copy of the partner's attack leaves the one here alone, so that a bell flings only the player who struck it
/// (CoopHits.PersonalTypes, GamePatcher.IgnoreRemoteHit). So each game heard only its own player's strikes and judged
/// them by itself, and each player played a tune of their own (USER 10-12, "有个跟随演示的音调敲不同的钟的机制没有同步，
/// 我俩各敲各的").
///
/// A strike now counts for the one music box of the room. The game of the player who struck a bell tells the other
/// games in the room, which ring that bell - the sound and the swing of the bell alone, no fling and no hit effects -
/// and the game that runs the room judges it like a strike of its own player, so every strike is judged there, in the
/// order they reach it. That game tells the others what came of each - the right bell, a wrong one - and they play it
/// out the way it played out there: the box turns, a round ends and the next tune starts, or it all stops and the
/// plate comes up again. They judge nothing by themselves, so the striker sees what came of their strike a moment late.
/// The rounds won are saved as the game saves them, and the save of a partner in another room gets them too, so that
/// both find the box where it was left, and each can take the reward from it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Hook for a bell telling that it was struck from above.
    /// </summary>
    private Hook? _podBounceHitHook;

    /// <summary>
    /// Hook for a bell telling that it was struck from the side, or rung here for a strike of the partner.
    /// </summary>
    private Hook? _podInertHitHook;

    /// <summary>
    /// Hook for a music box judging the strikes of a frame.
    /// </summary>
    private Hook? _podSequenceUpdateHook;

    /// <summary>
    /// Hook for a music box taking a strike on the right bell.
    /// </summary>
    private Hook? _podHitSuccessHook;

    /// <summary>
    /// Hook for a music box taking a strike on a wrong bell.
    /// </summary>
    private Hook? _podHitFailHook;

    /// <summary>
    /// Whether a bell is being rung here for a strike in another game, which is not told back.
    /// </summary>
    private bool _ringingPartnerStrike;

    /// <summary>
    /// Whether what came of a strike in the game that runs the room is being played out here, which is not told back.
    /// </summary>
    private bool _playingPodOutcome;

    /// <summary>
    /// Whether keeping a music box together threw, which is only logged once.
    /// </summary>
    private bool _podSequenceFailed;

    /// <summary>
    /// Registers the hooks on the bells and the music boxes.
    /// </summary>
    private void RegisterPodSequenceHooks() {
        _podBounceHitHook = CreateHook(
            typeof(BouncePod).GetMethod(nameof(BouncePod.SendBounceHit), InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<BouncePod>, BouncePod>(OnPodBounceHit)
        );
        _podInertHitHook = CreateHook(
            typeof(BouncePod).GetMethod(nameof(BouncePod.SendInertHit), InstanceFlags, null, [typeof(float)], null),
            new Action<Action<BouncePod, float>, BouncePod, float>(OnPodInertHit)
        );
        _podSequenceUpdateHook = CreateHook(
            typeof(BouncePodSequence).GetMethod(
                nameof(BouncePodSequence.Update), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<BouncePodSequence>, BouncePodSequence>(OnPodSequenceUpdate)
        );
        _podHitSuccessHook = CreateHook(
            typeof(BouncePodSequence).GetMethod(
                nameof(BouncePodSequence.PodHitSuccess), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<BouncePodSequence>, BouncePodSequence>(OnPodHitSuccess)
        );
        _podHitFailHook = CreateHook(
            typeof(BouncePodSequence).GetMethod(
                nameof(BouncePodSequence.PodHitFail), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<BouncePodSequence>, BouncePodSequence>(OnPodHitFail)
        );
    }

    /// <summary>
    /// Whether the music boxes here are kept together with the other games: with a member that the saves were checked
    /// with in the room.
    /// </summary>
    private bool ArePodSequencesShared() => _checkedMembers.Count > 0 && IsMemberInRoom();

    /// <summary>
    /// A bell telling that it was struck from above, which goes to the other games in the room if it is a bell of a
    /// music box (see <see cref="TellPodStrike"/>).
    /// </summary>
    private void OnPodBounceHit(Action<BouncePod> orig, BouncePod self) {
        orig(self);
        TellPodStrike(self, 0);
    }

    /// <summary>
    /// A bell telling that it was struck from the side, which goes to the other games in the room if it is a bell of a
    /// music box (see <see cref="TellPodStrike"/>).
    /// </summary>
    private void OnPodInertHit(Action<BouncePod, float> orig, BouncePod self, float direction) {
        orig(self, direction);
        TellPodStrike(self, direction < 0f ? -1 : 1);
    }

    /// <summary>
    /// Tells the other games in the room that this game's player struck a bell of a music box. Only the player's own
    /// strikes come here: the copy of the partner's attack leaves the bells here alone.
    /// </summary>
    /// <param name="pod">The bell.</param>
    /// <param name="side">The side it was struck from: -1, 1, or 0 from above.</param>
    private void TellPodStrike(BouncePod pod, int side) {
        if (_ringingPartnerStrike || !ArePodSequencesShared()) {
            return;
        }

        try {
            var sequence = pod.GetComponentInParent<BouncePodSequence>(true);
            if (sequence == null || sequence.pods == null) {
                return;
            }

            var index = Array.IndexOf(sequence.pods, pod);
            if (index < 0) {
                return;
            }

            var update = new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.PodStrike,
                Scene = sequence.gameObject.scene.name
            };
            update.Amounts.Add((int) GetPodSequenceKey(sequence));
            update.Amounts.Add(index);
            update.Amounts.Add(side);
            SendToRoomMembers(update);
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// Rings the bell that the partner struck: its swing and its sound, and the music box here takes the strike like
    /// one of this game's player, which only the game that runs the room judges (see
    /// <see cref="OnPodSequenceUpdate"/>). The partner's own game flings them off it and shows the hit; here it does
    /// neither.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, with the key of the music box, the bell and the side it was struck
    /// from.</param>
    private void OnPodStrike(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !player.IsInLocalScene || update.Amounts.Count < 3) {
            return;
        }

        try {
            if (FindPodSequence(update.Scene, (uint) update.Amounts[0]) is not { pods: { } pods }) {
                return;
            }

            // Not a bell that is put away here, which takes no strike from this game's player either
            var index = update.Amounts[1];
            if (index < 0 || index >= pods.Length || pods[index] is not { } pod || !pod.isActiveAndEnabled ||
                !pod.IsActive) {
                return;
            }

            _ringingPartnerStrike = true;
            try {
                pod.Ring(true);
                pod.SendInertHit(update.Amounts[2] < 0 ? -1f : 1f);
            } finally {
                _ringingPartnerStrike = false;
            }
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// A music box judging the strikes of a frame. With the partner in the room, only the game that runs the room
    /// judges them, and the other games let go of what the strikes here set (see <see cref="OnPodOutcome"/>). A strike
    /// on a bell of no round there fails the round like a wrong bell, which the game that runs the room tells first.
    /// </summary>
    private void OnPodSequenceUpdate(Action<BouncePodSequence> orig, BouncePodSequence self) {
        if (ArePodSequencesShared()) {
            if (IsSceneHost?.Invoke() != true) {
                self.anonPodWasHit = false;
                self.queuedSuccess = false;
                self.queuedFail = false;
            } else if (self.anonPodWasHit) {
                TellPodOutcome(self, false);
            }
        }

        orig(self);
    }

    /// <summary>
    /// A music box taking a strike on the right bell, which the game that runs the room tells the other games in it,
    /// and which saves a round won, also for the partner (see <see cref="TellPodProgress"/>).
    /// </summary>
    private void OnPodHitSuccess(Action<BouncePodSequence> orig, BouncePodSequence self) {
        var round = self.currentSequenceIndex;
        TellPodOutcome(self, true);
        orig(self);

        if (self.currentSequenceIndex != round) {
            TellPodProgress(self);
        }
    }

    /// <summary>
    /// A music box taking a strike on a wrong bell, which the game that runs the room tells the other games in it.
    /// </summary>
    private void OnPodHitFail(Action<BouncePodSequence> orig, BouncePodSequence self) {
        TellPodOutcome(self, false);
        orig(self);
    }

    /// <summary>
    /// Tells the other games in the room what a strike came to in this game, which runs the room: with where the music
    /// box was before it, so that one whose box got to somewhere else plays out the same.
    /// </summary>
    /// <param name="sequence">The music box.</param>
    /// <param name="right">Whether the bell was the right one.</param>
    private void TellPodOutcome(BouncePodSequence sequence, bool right) {
        if (_playingPodOutcome || !ArePodSequencesShared() || IsSceneHost?.Invoke() != true) {
            return;
        }

        try {
            var update = new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.PodOutcome,
                Scene = sequence.gameObject.scene.name
            };
            update.Amounts.Add((int) GetPodSequenceKey(sequence));
            update.Amounts.Add(right ? 1 : 0);
            update.Amounts.Add(sequence.targetPodIndex);
            update.Amounts.Add(sequence.currentSequenceIndex);
            SendToRoomMembers(update);
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// Plays out here what a strike came to in the game that runs the room: the music box is put where it was there
    /// before the strike, and takes the right or a wrong bell the same way, with all that the game does for it.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, with the key of the music box, whether the bell was the right one, and the bell
    /// and the round that the box was at before it.</param>
    private void OnPodOutcome(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !player.IsInLocalScene || IsSceneHost?.Invoke() != false ||
            update.Amounts.Count < 4) {
            return;
        }

        try {
            if (FindPodSequence(update.Scene, (uint) update.Amounts[0]) is not { } sequence || sequence.isComplete) {
                return;
            }

            var round = update.Amounts[3];
            if (round < 0 || round >= sequence.sequences.Count) {
                return;
            }

            _playingPodOutcome = true;
            try {
                sequence.targetPodIndex = update.Amounts[2];
                if (sequence.currentSequenceIndex != round) {
                    sequence.currentSequenceIndex = round;
                    sequence.UpdateProgressIndicator(true);
                }

                if (update.Amounts[1] != 0) {
                    sequence.PodHitSuccess();
                } else {
                    sequence.PodHitFail();
                }
            } finally {
                _playingPodOutcome = false;
            }
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// Tells the other members how many rounds of a music box are won, for their saves: with a member in the room only
    /// from the game that runs it, whose box the others follow already, and with none from this game, where the round
    /// was won.
    /// </summary>
    private void TellPodProgress(BouncePodSequence sequence) {
        if (_checkedMembers.Count == 0 || sequence.persistent == null ||
            IsMemberInRoom() && IsSceneHost?.Invoke() != true) {
            return;
        }

        try {
            GetItemSceneAndId(sequence.persistent, out var scene, out var id);
            var update = new CoopSaveUpdate { TargetId = CoopTargets.Everyone, Kind = CoopSaveUpdateKind.PodProgress };
            update.ItemScenes.Add(scene);
            update.ItemIds.Add(id);
            update.Amounts.Add(sequence.currentSequenceIndex);
            update.Amounts.Add(sequence.persistent.ItemData.IsSemiPersistent ? 1 : 0);
            Send(update);
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// Keeps the rounds of a music box won in the partner's game in this save, and on the box here where this game has
    /// it, as far as it got: rounds are only ever won. Not on a box in a room that this game runs with the partner in
    /// it, which the partner's box follows.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, with the saved number and the rounds.</param>
    private void OnPodProgress(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player) ||
            SceneData.instance is not { } sceneData || update.ItemScenes.Count < 1 || update.ItemIds.Count < 1 ||
            update.Amounts.Count < 2 || player.IsInLocalScene && IsSceneHost?.Invoke() == true) {
            return;
        }

        try {
            var scene = update.ItemScenes[0];
            var id = update.ItemIds[0];
            var rounds = update.Amounts[0];
            if (!sceneData.PersistentInts.TryGetValue(scene, id, out var saved) || saved.Value < rounds) {
                sceneData.PersistentInts.SetValue(new PersistentItemData<int> {
                    ID = id,
                    SceneName = scene,
                    Value = rounds,
                    IsSemiPersistent = update.Amounts[1] != 0
                });
            }

            foreach (var sequence in Object.FindObjectsByType<BouncePodSequence>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None
                     )) {
                if (sequence.persistent == null || sequence.currentSequenceIndex >= rounds) {
                    continue;
                }

                GetItemSceneAndId(sequence.persistent, out var sequenceScene, out var sequenceId);
                if (sequenceId == id && string.Equals(sequenceScene, scene, StringComparison.OrdinalIgnoreCase)) {
                    PutPodSequenceAt(sequence, rounds);
                }
            }
        } catch (Exception e) {
            LogPodSequenceError(e);
        }
    }

    /// <summary>
    /// Puts a music box at a number of rounds won, the way the game does as its room loads: the box turned that far,
    /// and open, with its reward out, once all of them are.
    /// </summary>
    private static void PutPodSequenceAt(BouncePodSequence sequence, int rounds) {
        sequence.currentSequenceIndex = Mathf.Min(rounds, sequence.sequences.Count);
        sequence.UpdateProgressIndicator(true);
        if (sequence.isComplete || sequence.currentSequenceIndex < sequence.sequences.Count) {
            return;
        }

        sequence.isComplete = true;
        if (sequence.startSequencePlate != null) {
            sequence.startSequencePlate.ActivateSilent();
        }

        sequence.Unlock(true);
    }

    /// <summary>
    /// Finds the music box of a loaded room by its key (see <see cref="GetPodSequenceKey"/>).
    /// </summary>
    private static BouncePodSequence? FindPodSequence(string scene, uint key) {
        foreach (var sequence in Object.FindObjectsByType<BouncePodSequence>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            if (sequence.gameObject.scene.name == scene && GetPodSequenceKey(sequence) == key) {
                return sequence;
            }
        }

        return null;
    }

    /// <summary>
    /// The key of a music box, which is the same in every game: the hash of its scene and its path in it.
    /// </summary>
    private static uint GetPodSequenceKey(BouncePodSequence sequence) {
        return HashText(sequence.gameObject.scene.name + ":" + ScenePath.Get(sequence.transform));
    }

    /// <summary>
    /// Logs that keeping a music box together threw, the first time.
    /// </summary>
    private void LogPodSequenceError(Exception e) {
        if (_podSequenceFailed) {
            return;
        }

        _podSequenceFailed = true;
        Logger.Error($"Could not keep a music box together with the partner:\n{e}");
    }
}
