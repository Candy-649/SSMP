using System;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Deleting a save with the chat command "/delete" and the number of its slot, as the game's save menu counts them from
/// the top (USER 11-08 "能不能加个删存档的功能，感觉删存档对于我朋友来说太困难了"). The first command tells what the save is
/// and asks; the same command again within 15 seconds deletes it the way the game's menu does, after a copy of it is
/// kept among the deleted saves in the config folder, where the game's own delete keeps none. The save being played is
/// never deleted, and only the local player's own saves are touched. The chat opens in the main menu too.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long, in seconds, the same /delete typed again goes ahead after the first one.
    /// </summary>
    private const float DeleteConfirmTime = 15f;

    /// <summary>
    /// The folder in the config folder that the copies of deleted saves go to.
    /// </summary>
    private const string DeletedFolderName = "deleted-saves";

    /// <summary>
    /// The slot that a /delete waits to be typed again for, and when, by Time.unscaledTime, it asked.
    /// </summary>
    private (int Slot, float At)? _deleteAsked;

    /// <summary>
    /// Tells what the save of a slot is and asks, or deletes it if the same command asked within the last 15 seconds.
    /// </summary>
    /// <param name="arguments">The words of the command, the first one being the command itself.</param>
    public void DeleteSave(string[] arguments) {
        if (arguments.Length != 2 || !int.TryParse(arguments[1], out var slot) || slot < 1 || slot > MenuSaveSlots) {
            Chat(Lang.Pick(
                "Type /delete and the number of the save, 1 to 4, counting from the top of the game's save menu.",
                "输入 /delete 加存档的编号 1~4（选存档界面里从上往下数）。"
            ));
            return;
        }

        if (IsInGame() && global::GameManager.instance.profileID == slot) {
            Chat(Lang.Pick($"You're playing save {slot}, so it can't be deleted.", $"你正在玩存档 {slot}，不能删。"));
            return;
        }

        if (_deleteAsked is { } asked && asked.Slot == slot && Time.unscaledTime - asked.At <= DeleteConfirmTime) {
            _deleteAsked = null;
            DeleteNow(slot);
            return;
        }

        _deleteAsked = null;
        Platform.Current.IsSaveSlotInUse(slot, inUse => {
            if (!inUse) {
                Chat(Lang.Pick($"Save {slot} is empty.", $"存档 {slot} 是空的。"));
                return;
            }

            global::GameManager.instance.GetSaveStatsForSlot(slot, (stats, _) => AskToDelete(slot, stats));
        });
    }

    /// <summary>
    /// Tells what the save of a slot is, and waits for the same command again.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="stats">What the game read of the save, or null if it couldn't read it.</param>
    private void AskToDelete(int slot, SaveStats? stats) {
        _deleteAsked = (slot, Time.unscaledTime);
        string english, chinese;
        if (stats == null) {
            english = "the game can't read it";
            chinese = "游戏读不出它的内容";
        } else {
            var played = TimeSpan.FromSeconds(stats.PlayTime);
            english = $"{stats.CompletionPercentage:0}% done, played {(int) played.TotalHours} h {played.Minutes} min";
            chinese = $"完成度 {stats.CompletionPercentage:0}%，玩了 {(int) played.TotalHours} 小时 {played.Minutes} 分";
        }

        if (GetMarker(slot) != null) {
            english += ", a co-op save";
            chinese += "，是联机存档";
        }

        Logger.Info($"Asked whether to delete save slot {slot}: {english}");
        Chat(Lang.Pick(
            $"Save {slot}: {english}. Type /delete {slot} again within 15 seconds to delete it.",
            $"存档 {slot}：{chinese}。确定要删的话，15 秒内再输一次 /delete {slot}。"
        ));
    }

    /// <summary>
    /// Keeps a copy of the save of a slot and deletes it the way the game's save menu does.
    /// </summary>
    private void DeleteNow(int slot) {
        if (!BackUpSave(slot, DeletedFolderName)) {
            Chat(Lang.Pick(
                $"Save {slot} wasn't deleted, as no copy of it could be kept first.",
                $"存档 {slot} 没删：没能先留一份备份。"
            ));
            return;
        }

        Logger.Info($"Deleting save slot {slot}, a copy of which was kept");
        global::GameManager.instance.ClearSaveFile(slot, cleared => {
            if (!cleared) {
                Logger.Warn($"The game did not delete save slot {slot}");
                Chat(Lang.Pick($"The game didn't delete save {slot}.", $"游戏没能删掉存档 {slot}。"));
                return;
            }

            // The save menu in the title menu reads the slot anew the next time it shows; in a save, it is built anew
            // as the player quits to it
            if (!IsInGame()) {
                global::UIManager.instance.ClearSaveCache();
            }

            Logger.Info($"Deleted save slot {slot}");
            Chat(Lang.Pick(
                $"Save {slot} is deleted. A copy of it was kept first, so it can be brought back.",
                $"存档 {slot} 删掉了。删之前留了一份备份，删错了能找回来。"
            ));
        });
    }
}
