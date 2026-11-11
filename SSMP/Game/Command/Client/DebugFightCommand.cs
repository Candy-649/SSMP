using System;
using System.Security.Cryptography;
using System.Text;
using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// TEMPORARY debug commands /0 to /3, for trying two fights again in a copy of a save (see
/// BossRoomCoop.DebugCommands). To be taken out again once they are tried. They work only once /0 came with the
/// key after it in this run of the game, which only the players who test with them know (see <see cref="KeyHash"/>).
/// </summary>
internal class DebugFightCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The SHA-256 of the key that has to follow the first /0 of a run of the game, in lowercase hex. The code is
    /// public and so is the mod page that 0.4.156 went up on with these commands in it, read of in its install notes:
    /// until then every command does nothing and says nothing, so nobody else takes the players anywhere or writes
    /// into their saves (USER 11-09 "看起来下次/0还得带一串密钥才行", "我们还是需要/2的").
    /// </summary>
    private const string KeyHash = "7671bac6bb85c237b2c4de95ef1012a16f5305608c2afa3fa1cab071dcfe2e3d";

    /// <summary>
    /// Whether /0 came with the key in this run of the game: from then on until the game is closed, /0 to /3 need
    /// no key (USER 11-09 "似乎有点要打的太多了（，要不然改成/0后面要加密钥，/1和/2只有本次游戏输入过/0才能用").
    /// </summary>
    private static bool _unlocked;

    /// <summary>
    /// The boss rooms, which carry out the command in every player's game.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    /// <summary>
    /// The number of the command, 0 to 3.
    /// </summary>
    private readonly int _command;

    public DebugFightCommand(BossRoomCoop bossRoomCoop, int command) {
        _bossRoomCoop = bossRoomCoop;
        _command = command;
    }

    /// <inheritdoc />
    public string Trigger => $"/{_command}";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => _command switch {
        0 => "Temporary debug, with the key the first time: set the fight at the top of the lift back to before the " +
             "first ride up, in every player's save.",
        1 => "Temporary debug, after /0 with the key: take every player to the bottom of the lift.",
        2 => "Temporary debug, after /0 with the key: take every player to the challenge of the other fight, whose " +
             "win then leads back there.",
        _ => "Temporary debug, after /0 with the key: give every player 100 shell shards."
    };

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (!_unlocked) {
            // The command itself comes first
            if (_command != 0 || arguments.Length < 2 || !IsTheKey(arguments[1])) {
                return;
            }

            _unlocked = true;
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: the debug commands are on until the game is closed; /0 to /3 need no key from now on.",
                "调试：调试命令已打开，关游戏之前 /0 到 /3 都不用再加密钥。"
            ));
        }

        _bossRoomCoop.RunDebugCommand(_command);
    }

    /// <summary>
    /// Whether the given text is the key (see <see cref="KeyHash"/>).
    /// </summary>
    private static bool IsTheKey(string text) {
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        return string.Equals(hash, KeyHash, StringComparison.OrdinalIgnoreCase);
    }
}
