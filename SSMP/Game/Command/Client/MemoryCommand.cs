using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client.Entity.Action;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for a player who missed the memory that the boss of a room sends the players to once it is beaten, like one
/// who lay in their cocoon while their teammate finished the boss. It takes them into it from the room.
/// </summary>
internal class MemoryCommand : IClientCommand, ICommandWithDescription {
    /// <inheritdoc />
    public string Trigger => "/memory";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Go into the memory of the boss of this room, if your save has it beaten.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (!EntityFsmActions.GoIntoMemoryOfBeatenBoss()) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "There is no memory here of a boss you have beaten, or you can't go anywhere right now.",
                "这里没有你打败过的首领的记忆，或者你现在没法离开。"
            ));
        }
    }
}
