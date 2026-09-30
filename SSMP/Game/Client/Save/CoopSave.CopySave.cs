using System;
using GlobalEnums;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Copying the save that the local player is in to an empty save slot, with the chat command "/copy". The game saves
/// first, as it does when the player quits to the menu, so that the copy holds everything up to the command. A slot
/// that holds a save is never written over.
///
/// The copy is a normal save, even the copy of a two-player save: two slots paired with the same partner would each be
/// merged with the partner's one save. The game's save menu is built anew each time the player quits to it, so the copy
/// shows there without restarting the game.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a copy may take before another one may start, in seconds. The game's save and its reads and writes of
    /// save slots each answer a frame or more after they are asked, and one that never answers would otherwise keep
    /// every later copy from starting.
    /// </summary>
    private const float CopyTimeout = 30f;

    /// <summary>
    /// The number of slots in the game's save menu, which are slots 1 to 4. Platform.SaveSlotCount is 5, because the
    /// platform also counts a slot 0 that the menu never shows.
    /// </summary>
    private const int MenuSaveSlots = 4;

    /// <summary>
    /// When the copy under way started, by Time.unscaledTime, or null if no copy is under way.
    /// </summary>
    private float? _copyStarted;

    /// <summary>
    /// Saves the game and copies the save to the first empty slot after its own, going round to the first slot after
    /// the last one.
    /// </summary>
    public void CopySave() {
        if (_copyStarted is { } started && Time.unscaledTime - started < CopyTimeout) {
            Chat(Lang.Pick("The save is being copied already.", "存档正在复制中。"));
            return;
        }

        if (!IsInGame()) {
            Chat(Lang.Pick("Load a save first, then copy it.", "先进入一个存档，再复制它。"));
            return;
        }

        // Not in the middle of a room change, a death or a scene that the game won't let the player pause in, where
        // the game itself never saves
        var gameManager = global::GameManager.instance;
        var hero = HeroController.instance;
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning || hero.cState.hazardRespawning ||
            PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || PlayerData.instance.disablePause) {
            Chat(Lang.Pick(
                "The game can't be saved right now. Try again when you could pause it.",
                "现在没法存档，等能暂停游戏的时候再试。"
            ));
            return;
        }

        var slot = gameManager.profileID;
        if (slot < 1 || slot > MenuSaveSlots) {
            Chat(Lang.Pick("This save isn't in a save slot that can be copied.", "这个存档不在能复制的存档槽里。"));
            return;
        }

        _copyStarted = Time.unscaledTime;
        Logger.Info($"Copying save slot {slot}: saving it first");
        gameManager.SaveGame(saved => RunCopyStep(() => OnCopySaved(slot, saved)));
    }

    /// <summary>
    /// Looks for the slot to copy a save to, once the game has saved it.
    /// </summary>
    private void OnCopySaved(int slot, bool saved) {
        if (!saved) {
            EndCopy($"The game did not save slot {slot}", Lang.Pick(
                "The game could not save, so nothing was copied.",
                "游戏没能存档，所以什么都没有复制。"
            ));
            return;
        }

        FindEmptySlot(slot, 1, target => RunCopyStep(() => OnCopyTargetFound(slot, target)));
    }

    /// <summary>
    /// Reads the save to copy, once the slot to copy it to is known.
    /// </summary>
    /// <param name="slot">The slot of the save.</param>
    /// <param name="target">The empty slot to copy it to, or 0 if every other slot holds a save.</param>
    private void OnCopyTargetFound(int slot, int target) {
        if (target == 0) {
            EndCopy($"No save slot is empty to copy slot {slot} to", Lang.Pick(
                "Every other save slot holds a save, and none is written over. Clear one in the game's menu first.",
                "其他存档槽都有存档，不会覆盖任何一个。请先在游戏菜单里清空一个存档槽。"
            ));
            return;
        }

        Platform.Current.ReadSaveSlot(slot, bytes => RunCopyStep(() => OnCopyRead(slot, target, bytes)));
    }

    /// <summary>
    /// Writes the copy, once the save is read.
    /// </summary>
    private void OnCopyRead(int slot, int target, byte[]? bytes) {
        if (bytes == null || bytes.Length == 0) {
            EndCopy($"Could not read save slot {slot} to copy it", Lang.Pick(
                $"Could not read save {slot}, so nothing was copied.",
                $"读不到存档 {slot}，所以什么都没有复制。"
            ));
            return;
        }

        Platform.Current.WriteSaveSlot(
            target,
            bytes,
            written => RunCopyStep(() => OnCopyWritten(slot, target, written))
        );
    }

    /// <summary>
    /// Tells the player how the copy went, once it is written.
    /// </summary>
    private void OnCopyWritten(int slot, int target, bool written) {
        if (!written) {
            EndCopy($"Could not write the copy of save slot {slot} to slot {target}", Lang.Pick(
                $"Could not write the copy to save slot {target}.",
                $"没能把副本写进存档槽 {target}。"
            ));
            return;
        }

        // A pairing left behind by a save that was deleted outside the game would make the copy a two-player save
        if (RemoveMarker(target)) {
            Logger.Info($"Save slot {target} had a pairing from an old save, which was dropped for the copy");
        }

        var message = Lang.Pick(
            $"Copied save {slot} to save slot {target}. It shows in the menu the next time you go there.",
            $"已把存档 {slot} 复制到存档槽 {target}，回到主菜单就能看到。"
        );
        if (GetMarker(slot) != null) {
            message += Lang.Pick(
                " The copy is a normal save: to play it together, pair it with /coopsave.",
                "副本是普通存档，要两人一起玩得再用 /coopsave 配对。"
            );
        }

        EndCopy($"Copied save slot {slot} to slot {target}", message);
    }

    /// <summary>
    /// Asks the platform about the slots after a save's own in turn, and hands on the first empty one, or 0 if every
    /// other slot holds a save.
    /// </summary>
    /// <param name="slot">The slot of the save.</param>
    /// <param name="step">How many slots after the save's own the slot to ask about is.</param>
    /// <param name="found">What to do with the slot found.</param>
    private static void FindEmptySlot(int slot, int step, Action<int> found) {
        if (step >= MenuSaveSlots) {
            found(0);
            return;
        }

        var candidate = (slot - 1 + step) % MenuSaveSlots + 1;
        Platform.Current.IsSaveSlotInUse(candidate, inUse => {
            if (inUse) {
                FindEmptySlot(slot, step + 1, found);
            } else {
                found(candidate);
            }
        });
    }

    /// <summary>
    /// Runs a step of a copy, and ends the copy if the step throws.
    /// </summary>
    private void RunCopyStep(Action step) {
        try {
            step();
        } catch (Exception e) {
            EndCopy($"Could not copy the save:\n{e}", Lang.Pick(
                "Something went wrong while copying the save. Nothing was written over.",
                "复制存档时出了错，没有覆盖任何存档。"
            ));
        }
    }

    /// <summary>
    /// Ends the copy under way, logging how it went and telling the player.
    /// </summary>
    private void EndCopy(string log, string message) {
        _copyStarted = null;
        Logger.Info(log);
        Chat(message);
    }
}
