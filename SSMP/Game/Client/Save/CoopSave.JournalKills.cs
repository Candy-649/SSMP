using System;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The kills of the journal of creatures for the members who are in another room as a creature dies. One that dies in a
/// room counts in the journal of every player there, as each of their games has it die, but a member in another room
/// took no kill of it, and nor did one who came in after it died: a creature that is met once, like the flea that a
/// cage holds, was missing from their journal for good, unless they joined a host who had it, whose kills a joining
/// save takes as it loads (see CoopHostSync.MergeJournal), and the host never had it (11-27 research; USER "第二条补").
/// Every kill that a game records now goes to the members in other rooms. One whose journal has no kill of that
/// creature yet takes one, and one whose entry of it is short of whole takes the rest when the kill gave the whole
/// entry at once, as the deaths of some creatures and some state machines do. Nothing else is taken: what the others
/// kill does not add up in a member's own journal, which counts the kills of their own room by itself. It is taken
/// quietly, without the game's word of a new entry.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The hook that sees the game record a kill in the journal.
    /// </summary>
    private Hook? _journalKillHook;

    /// <summary>
    /// The hooks that see the game give an entry of the journal whole at once: by the death of a creature that gives
    /// its whole entry, and by the state machines that complete an entry.
    /// </summary>
    private Hook? _deathJournalHook;

    private Hook? _completeJournalHook;

    private Hook? _completeJournalV2Hook;

    /// <summary>
    /// Whether the game gives an entry of the journal whole at once now, which is told once, as it is given (see
    /// <see cref="GiveWholeEntry"/>), rather than kill by kill.
    /// </summary>
    private bool _givingWholeEntry;

    /// <summary>
    /// Whether a kill that a member in another room told is being written into the local journal, which is told to
    /// nobody.
    /// </summary>
    private bool _takingJournalKill;

    /// <summary>
    /// Watches the game record kills in the journal and give entries whole.
    /// </summary>
    private void RegisterJournalKillHooks() {
        _journalKillHook = CreateHook(
            typeof(EnemyJournalManager).GetMethod(
                nameof(EnemyJournalManager.RecordKill),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(EnemyJournalRecord), typeof(bool), typeof(bool)],
                null
            ),
            new Action<Action<EnemyJournalRecord, bool, bool>, EnemyJournalRecord, bool, bool>(OnJournalKill)
        );
        _deathJournalHook = CreateHook(
            typeof(EnemyDeathEffects).GetMethod("RecordKillForJournal", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<EnemyDeathEffects>, EnemyDeathEffects>(OnDeathJournalKill)
        );
        _completeJournalHook = CreateHook(
            typeof(CompleteJournalRecord).GetMethod(nameof(CompleteJournalRecord.OnEnter)),
            new Action<Action<CompleteJournalRecord>, CompleteJournalRecord>(OnCompleteJournalRecord)
        );
        _completeJournalV2Hook = CreateHook(
            typeof(CompleteJournalRecordV2).GetMethod(nameof(CompleteJournalRecordV2.OnEnter)),
            new Action<Action<CompleteJournalRecordV2>, CompleteJournalRecordV2>(OnCompleteJournalRecordV2)
        );
    }

    /// <summary>
    /// Tells the members in other rooms of a kill that the game recorded in the journal, one kill at a time, unless it
    /// is a part of an entry given whole, or a kill that a member told.
    /// </summary>
    private void OnJournalKill(
        Action<EnemyJournalRecord, bool, bool> orig,
        EnemyJournalRecord record,
        bool showPopup,
        bool forcePopup
    ) {
        orig(record, showPopup, forcePopup);
        if (!_takingJournalKill && !_givingWholeEntry) {
            TellJournalKill(record, false);
        }
    }

    /// <summary>
    /// The death of a creature, which gives its whole entry at once if it says so (awardFullJournalEntry).
    /// </summary>
    private void OnDeathJournalKill(Action<EnemyDeathEffects> orig, EnemyDeathEffects self) {
        if (self.awardFullJournalEntry) {
            GiveWholeEntry(() => orig(self), self.journalRecord);
        } else {
            orig(self);
        }
    }

    private void OnCompleteJournalRecord(Action<CompleteJournalRecord> orig, CompleteJournalRecord self) {
        GiveWholeEntry(() => orig(self), self.Record.Value as EnemyJournalRecord);
    }

    private void OnCompleteJournalRecordV2(Action<CompleteJournalRecordV2> orig, CompleteJournalRecordV2 self) {
        GiveWholeEntry(() => orig(self), self.Record.Value as EnemyJournalRecord);
    }

    /// <summary>
    /// Lets the game give an entry of the journal whole, kill by kill, and tells the members in other rooms of it once,
    /// if it gave any kill: the death of a creature that the local player had no part in gives none.
    /// </summary>
    private void GiveWholeEntry(Action give, EnemyJournalRecord? record) {
        var before = record != null ? record.KillCount : 0;
        _givingWholeEntry = true;
        try {
            give();
        } finally {
            _givingWholeEntry = false;
        }

        if (record != null && !_takingJournalKill && record.KillCount > before) {
            TellJournalKill(record, true);
        }
    }

    /// <summary>
    /// Tells the members in other rooms of a kill recorded in the journal here, or of an entry given whole.
    /// </summary>
    private void TellJournalKill(EnemyJournalRecord record, bool whole) {
        var scene = SceneUtil.GetCurrentSceneName();
        var told = SendToMembersElsewhere(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.JournalKill,
            Scene = scene,
            ObjectPath = record.name,
            PartCount = (ushort) (whole ? 1 : 0)
        });
        if (told.Count > 0) {
            Logger.Info(
                $"[Journal] '{record.name}' " + (whole ? "given whole" : "killed") + $" here in '{scene}', " +
                $"{record.KillCount} of {record.KillsRequired}: told to " +
                $"{JoinNames(told.Select(member => member.Username))}, in other rooms"
            );
        }
    }

    /// <summary>
    /// Takes a kill that a member in another room recorded in their journal: one kill of a creature that the local
    /// journal has none of, or the rest of an entry that their kill gave whole.
    /// </summary>
    private void OnJournalKillElsewhere(ClientPlayerData player, CoopSaveUpdate update) {
        if (!IsFromMemberInSave(player)) {
            return;
        }

        var whole = update.PartCount == 1;
        var told = $"{player.Username} " + (whole ? "was given the whole entry of" : "killed") +
                   $" '{update.ObjectPath}' in '{update.Scene}'";
        if (EnemyJournalManager.GetRecord(update.ObjectPath) is not { } record) {
            Logger.Info($"[Journal] {told}, which this game's journal has no entry of");
            return;
        }

        var had = record.KillCount;
        if (had > 0 && !(whole && had < record.KillsRequired)) {
            Logger.Info($"[Journal] {told}, of which this journal has {had} of {record.KillsRequired}: nothing taken");
            return;
        }

        _takingJournalKill = true;
        try {
            do {
                EnemyJournalManager.RecordKill(record, false);
            } while (whole && record.KillCount < record.KillsRequired);
        } finally {
            _takingJournalKill = false;
        }

        Logger.Info($"[Journal] {told}: taken here, {had} -> {record.KillCount} of {record.KillsRequired}");
    }
}
