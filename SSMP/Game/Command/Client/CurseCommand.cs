using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command that puts the curse back on the local player, or lifts it again, the way the game does both: the cursed
/// crest is equipped as the room that curses the player equips it, and the crest worn before it as the cure equips
/// its own, each followed by the hero's update of the cursed silk.
/// The crest a player wears is their own (2026-11-13), and the fight at the end of the second act ends by the crest of
/// each game's own player, so two players who want the ending of the cursed one together both need the curse, and one
/// who had it lifted had no way back (2026-11-24, the user: "有没有什么办法给她存档调回诅咒状态", then "加个聊天命令吧").
/// It works on a bench, where the game lets crests change, and only in a save that has the crest that lifting the
/// curse gives, so it never skips the story's own curse or its cure.
/// </summary>
internal class CurseCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The name of the crest that the game equips when it curses the player.
    /// </summary>
    private const string CursedCrest = "Cursed";

    /// <summary>
    /// The name of the crest that lifting the curse gives: a save has it once the curse was lifted in it, by its own
    /// player or by the host whose world a co-op save takes.
    /// </summary>
    private const string LiftedCrest = "Witch";

    /// <inheritdoc />
    public string Trigger => "/curse";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        "Put the curse back on, or lift it again, sitting on a bench; only in a save that has had it lifted once.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        var hero = HeroController.instance;
        var playerData = PlayerData.instance;
        if (hero == null || playerData is not { atBench: true }) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick("Sit on a bench to use this.", "要坐在椅子上才能用。"));
            return;
        }

        if (ToolItemManager.GetCrestByName(LiftedCrest) is not { IsUnlocked: true }) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Only in a save that has had the curse lifted once.",
                "要这个存档里解除过一次诅咒才能用。"
            ));
            return;
        }

        var lifting = playerData.CurrentCrestID == CursedCrest;
        var crest = ToolItemManager.GetCrestByName(lifting ? playerData.PreviousCrestID : CursedCrest);
        ToolItemManager.AutoEquip(crest, false, true);
        hero.UpdateSilkCursed();

        UiManager.InternalChatBox.AddMessage(lifting
            ? Lang.Pick(
                "The curse is lifted: you wear the crest you had before it.",
                "已解除诅咒状态，换回了之前戴的纹章。"
            )
            : Lang.Pick(
                "You are cursed again. Type /curse again on a bench to lift it.",
                "已回到诅咒状态。坐在椅子上再打一次 /curse 就解除。"
            )
        );
    }
}
