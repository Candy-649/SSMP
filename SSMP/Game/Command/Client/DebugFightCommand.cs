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
/// TEMPORARY debug commands /0 and /1, for trying the fight with the silk boss (see BossRoomCoop.DebugCommands). To be
/// taken out again once they are tried. /1 works only once /0 came with the key after it in this run of the game,
/// which only the players who test with them know (see <see cref="KeyHash"/>).
/// </summary>
internal class DebugFightCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// The SHA-256 of the key that has to follow the first /0 of a run of the game, in lowercase hex. The code is
    /// public and so are the versions that carried these commands before: until then every command does nothing and
    /// says nothing, so nobody else takes the players anywhere (USER 11-09 "看起来下次/0还得带一串密钥才行"; a simpler
    /// key, USER 11-21 "密钥换个简单点的吧").
    /// </summary>
    private const string KeyHash = "57f87ef6d90c2cfacd36767db1db1735cd832aa266c9543195bf71b8e6d681da";

    /// <summary>
    /// Whether /0 came with the key in this run of the game: from then on until the game is closed, /1 needs no key
    /// (USER 11-09 "要不然改成/0后面要加密钥，/1和/2只有本次游戏输入过/0才能用"; 11-21 "/0开启调试").
    /// </summary>
    private static bool _unlocked;

    /// <summary>
    /// The boss rooms, which carry out the command in every player's game.
    /// </summary>
    private readonly BossRoomCoop _bossRoomCoop;

    /// <summary>
    /// The number of the command, 0 or 1.
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
    public string Description => _command == 0
        ? "Temporary debug, with the key: turn the debug commands on until the game is closed."
        : "Temporary debug, after /0 with the key: take every player to the silk boss, whose win skips the ending.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (_command == 0) {
            // The command itself comes first
            if (!_unlocked && (arguments.Length < 2 || !IsTheKey(arguments[1]))) {
                return;
            }

            _unlocked = true;
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: the debug commands are on until the game is closed. Type /1 to go to the silk boss.",
                "调试：调试命令已打开，关游戏之前一直有效。输入 /1 去丝母那里。"
            ));
            return;
        }

        if (_unlocked) {
            _bossRoomCoop.RunDebugTrip();
        }
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
