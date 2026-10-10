using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Checking shared saves: once the players of a save have loaded it, each backs up its save file and sends the bosses it
/// has beaten, the saved objects of the world that it changed, its wish log and its story flags. Each save gets the
/// changed objects and accepted wishes that it lacks, and hears whether the beaten bosses and completed wishes differ,
/// which are never copied because they come with rewards. In a two-player save the other player is the only member.
///
/// The members check together, in rounds. Every round has a key. A player who starts a round picks a key larger than
/// every key they have seen, so a newer round always has a larger key, and the hellos, world progress and leaves of
/// older rounds can be told apart and dropped even when they arrive late. When players start rounds at once, the
/// largest key goes ahead: a player who gets a larger key takes it and starts over in that round, and a player who gets
/// a start with a smaller key than their unfinished round sends their start again. A round finishes once every member on
/// the server has said hello and the world progress has gone both ways between the local player and each of them, and
/// all of it is added at once, so that every game decides from the same saves. A member who comes into the save after a
/// round finished, like one who joined late or came back, starts a new round for everyone.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The embedded list of saved objects of the world.
    /// </summary>
    private const string WorldItemsFilePath = "SSMP.Resource.coop-world-items.json";

    /// <summary>
    /// The folder in the config folder that backups of two-player saves go to.
    /// </summary>
    private const string BackupFolderName = "coop-backups";

    /// <summary>
    /// How many backups each two-player save keeps.
    /// </summary>
    private const int MaxBackups = 10;

    /// <summary>
    /// How many beaten bosses and saved objects go in one part of the world progress.
    /// </summary>
    private const int EntriesPerPart = 64;

    /// <summary>
    /// The value of a hello that starts a check, rather than one that answers the hello of the other player.
    /// </summary>
    private const ushort HelloStart = 0;

    /// <summary>
    /// The value of a hello that answers the hello of the other player.
    /// </summary>
    private const ushort HelloAnswer = 1;

    /// <summary>
    /// The boolean fields of the player data that record a beaten boss.
    /// </summary>
    private static FieldInfo[]? _defeatFields;

    /// <summary>
    /// The names of the boolean fields of the player data.
    /// </summary>
    private static HashSet<string>? _playerDataBoolNames;

    /// <summary>
    /// Whether reading the saved objects of the save failed, so that the reason is logged once rather than on every
    /// poll of the world.
    /// </summary>
    private static bool _worldItemReadFailed;

    /// <summary>
    /// The saved booleans of the world, as keys from <see cref="GetItemKey"/>.
    /// </summary>
    private HashSet<string>? _worldBools;

    /// <summary>
    /// The booleans of the player data that saved objects of the world set together with their own state, by the key
    /// of the object.
    /// </summary>
    private Dictionary<string, List<string>>? _worldPlayerData;

    /// <summary>
    /// The check with each member in the current round, by their ID. A member stays in it once the round finished, for
    /// as long as they are in the save.
    /// </summary>
    private readonly Dictionary<ushort, MemberCheck> _memberChecks = new();

    /// <summary>
    /// The key of the current round, or 0 if no round started.
    /// </summary>
    private ulong _checkKey;

    /// <summary>
    /// The largest key of a round that the local player picked or got from a member since connecting.
    /// </summary>
    private ulong _highestCheckKey;

    /// <summary>
    /// Whether the world progress of the members of the current round was added, which waits until the local save sent
    /// its own to each of them, so that every game compares what every save sent.
    /// </summary>
    private bool _stateAdded;

    /// <summary>
    /// Whether the current round finished. Its members are checked from then on, and a member who wasn't in it makes a
    /// new round.
    /// </summary>
    private bool _roundFinished;

    /// <summary>
    /// What the local save sends in the current round, made the first time it goes to a member, so that every member,
    /// and every part that goes again, gets the same.
    /// </summary>
    private RoundState? _roundState;

    /// <summary>
    /// The saved objects of the world that the local save sent for the current check.
    /// </summary>
    private List<(string Scene, string Id)> _sentWorldItems = [];

    /// <summary>
    /// How many saved objects the last check added.
    /// </summary>
    private int _addedChanges;

    /// <summary>
    /// How many bosses only the saves of the other members have beaten, and how many the local save has beaten that
    /// one of theirs hasn't.
    /// </summary>
    private int _onlyPartnerDefeats;

    private int _onlyLocalDefeats;

    /// <summary>
    /// Whether the players have already been told that their pairings point at different saves. The games keep
    /// exchanging hellos for as long as a check cannot finish, so without this a mismatch would say the same thing
    /// again on every single one of them.
    /// </summary>
    private bool _helloKeyMismatchTold;

    /// <summary>
    /// The players told about once that their pairing names this save while this save's pairing doesn't name them.
    /// Their game says hello every couple of seconds and waits for good, which nothing else would show.
    /// </summary>
    private readonly HashSet<ushort> _strangerHellosTold = [];

    /// <summary>
    /// The players whose world progress was turned away and has been reported already, so that one who keeps sending
    /// it says so once rather than every couple of seconds.
    /// </summary>
    private readonly HashSet<ushort> _stateDropTold = [];

    /// <summary>
    /// How long a hello goes unanswered before it is sent again.
    /// </summary>
    private static readonly TimeSpan HelloRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The check with one member in a round.
    /// </summary>
    private sealed class MemberCheck {
        public MemberCheck(string name) {
            Name = name;
        }

        /// <summary>
        /// The username of the member, which stays after they left the server, for messages.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Whether the member said hello for the round, so they have loaded the save that is paired with the local one.
        /// </summary>
        public bool Hello { get; set; }

        /// <summary>
        /// Whether the local save was backed up and its world progress sent to the member.
        /// </summary>
        public bool StateSent { get; set; }

        /// <summary>
        /// Whether all parts of the world progress of the member arrived.
        /// </summary>
        public bool StateReceived { get; set; }

        /// <summary>
        /// The parts of the world progress of the member that arrived for the round, by part.
        /// </summary>
        public Dictionary<ushort, CoopSaveUpdate> Parts { get; } = new();

        /// <summary>
        /// When the last hello went to the member, so that one which they never answered is sent again.
        /// </summary>
        public DateTime LastHelloUtc { get; set; }

        /// <summary>
        /// Whether sending the world progress to the member again has already been reported, so that a member who keeps
        /// asking says so once.
        /// </summary>
        public bool ResentTold { get; set; }
    }

    /// <summary>
    /// The world progress of the local save in a round.
    /// </summary>
    private sealed class RoundState {
        public List<string> Defeats { get; init; } = [];

        public List<(string Scene, string Id)> Items { get; init; } = [];

        public List<(string Name, int Value)> Wishes { get; init; } = [];

        public List<(string Name, int Value)> StoryFlags { get; init; } = [];

        public int PartCount { get; init; }
    }

    /// <summary>
    /// Forgets the current round and who the saves were checked with. The largest key stays, so the next round gets a
    /// larger one.
    /// </summary>
    private void ResetCheck() {
        _checkedMembers.Clear();
        _memberChecks.Clear();
        _checkKey = 0;
        _stateAdded = false;
        _roundFinished = false;
        _roundState = null;
        _stateDropTold.Clear();
        _sentWorldItems = [];
        _sentWishEntries = [];
        _mergedWishKeys.Clear();
        _giftedWishKeys.Clear();
        _differentWishNames.Clear();
        _agreedStoryValues = null;
        ResetWishProgress();
        _addedChanges = 0;
        _onlyPartnerDefeats = 0;
        _onlyLocalDefeats = 0;
        _differentWishes = 0;
        _differentStoryFlags = 0;
        _storyFlagsFrom = null;
        _checkPlayTime = null;
        _flagSequences.Clear();
        _wishSequences.Clear();
        ResetTrapdoors();
        ResetTollBenches();
    }

    /// <summary>
    /// Moves the round with the members on the server along: start a round, say hello to each member, back up and send
    /// the world progress to each member who said hello, and once the progress of every member is in, add all of it.
    /// </summary>
    /// <param name="marker">The pairing of the loaded save.</param>
    /// <param name="members">The members on the server.</param>
    private void UpdateCheck(CoopSaveMarker marker, List<ClientPlayerData> members) {
        // A member who wasn't in a round that finished starts a new one once their game says hello (OnHello)
        if (_roundFinished) {
            return;
        }

        if (_checkKey == 0) {
            _checkKey = NewCheckKey();
        }

        foreach (var member in members) {
            if (!_memberChecks.TryGetValue(member.Id, out var check)) {
                check = _memberChecks[member.Id] = new MemberCheck(member.Username);

                // Printed once per member and round, because a check that never finishes used to leave nothing behind
                // to say which of the three keys disagreed - which is what made two players wait with nothing to go on
                Logger.Info(
                    $"Starting a check with {member.Username}: this save has the key '{LocalKey}', the pairing is " +
                    $"addressed to '{GetMemberKey(marker, member)}', the server says they are '{member.SaveKey}', and " +
                    $"this check is {_checkKey}"
                );

                SendHello(member, marker, HelloStart, check);
            } else if ((!check.Hello || !check.StateReceived) &&
                       DateTime.UtcNow - check.LastHelloUtc >= HelloRetryDelay) {
                // The first hello is sent the moment the member is there, which can be before their game has loaded
                // the save it belongs to. Their game drops a hello that early without a trace, and this used to be sent
                // exactly once, so a single early or lost hello left both players waiting for ever.
                SendHello(member, marker, HelloStart, check);
            }

            if (check.Hello && !check.StateSent) {
                check.StateSent = true;
                SendWorldState(member);
            }
        }

        if (members.Count == 0 || !members.All(member =>
                _memberChecks.TryGetValue(member.Id, out var check) && check.StateSent && check.StateReceived)) {
            return;
        }

        if (!_stateAdded) {
            _stateAdded = true;
            AddWorldStates(members);
        }

        FinishCheck(marker, members);
    }

    /// <summary>
    /// A key for a new round that is larger than every key seen so far. The lower bits are random, so two rounds that
    /// start at once almost never get the same key; if they do, both players take them as the same round, which works
    /// too.
    /// </summary>
    private ulong NewCheckKey() {
        // Through uint, so that the lower bits are laid in as they are. Random.Range never returns a negative here,
        // but a signed value widened to ulong would fill the whole upper half with ones and bury the count above
        var key = (((_highestCheckKey >> 16) + 1) << 16) | (uint) UnityEngine.Random.Range(1, 0x10000);
        _highestCheckKey = key;
        return key;
    }

    private void SendHello(ClientPlayerData member, CoopSaveMarker marker, ushort kind, MemberCheck check) {
        check.LastHelloUtc = DateTime.UtcNow;

        Send(new CoopSaveUpdate {
            TargetId = member.Id,
            Kind = CoopSaveUpdateKind.Hello,
            Key = _checkKey,
            PartnerKey = GetMemberKey(marker, member),
            PartCount = kind
        });
    }

    /// <summary>
    /// A member said hello for a round of the loaded save.
    /// </summary>
    private void OnHello(ClientPlayerData player, CoopSaveUpdate update) {
        var marker = GetCurrentMarker();
        if (marker == null || !IsMember(player, marker)) {
            if (marker != null && update.PartnerKey == LocalKey && _strangerHellosTold.Add(player.Id)) {
                Logger.Warn(
                    $"Turned down a hello from {player.Username}, whose pairing names this save, while the pairing of " +
                    $"this save is with {string.Join(", ", marker.Members.Select(member => member.Name))}"
                );
                Chat(Lang.Pick(
                    $"{player.Username} plays a save that is paired with yours, but yours isn't paired with theirs, so " +
                    "their game waits for yours. Pair again with /coopsave to play together.",
                    $"{player.Username} 的存档和你的配过对，但你的存档没有和对方配对，所以对方那边会一直等你。" +
                    "想一起玩就用 /coopsave 重新配对。"
                ));
            }

            return;
        }

        if (update.PartnerKey != LocalKey) {
            // Only the first one. Hellos are sent again until they are answered, so a mismatch that stays would
            // otherwise say this, and start a fresh check, every couple of seconds for as long as both games run.
            if (_helloKeyMismatchTold) {
                return;
            }

            _helloKeyMismatchTold = true;

            // Logged for both kinds. An answer turned down here used to say nothing at all, anywhere, so both players
            // sat waiting with neither log showing that a hello had been thrown away - exactly the state two players
            // ended up in and could not diagnose.
            Logger.Warn(
                $"Turned down a hello from {player.Username}: it is addressed to the save key " +
                $"'{update.PartnerKey}', but this save has the key '{LocalKey}'."
            );

            if (update.PartCount == HelloStart) {
                PartnerLeft(player.Id, null);

                Chat(
                    Lang.Pick(
                        $"{player.Username} loaded a save that isn't paired with yours. Your {SaveWord(marker)} waits " +
                        "for them to load the paired save, or to pair again with /coopsave.",
                        $"{player.Username} 进的存档和你的没有配对。你们的{SaveWord(marker)}会一直等对方进入已配对的那个存档，" +
                        "或者用 /coopsave 重新配对。"
                    )
                );
            } else {
                // The member is on the paired save and answered for it, so telling them to load a different one
                // would be wrong: the games disagree about the key itself, which only pairing again settles
                Chat(
                    marker.Members.Count <= 1
                        ? Lang.Pick(
                            $"Your game turned down an answer from {player.Username}, because it is addressed to a " +
                            "different save key than this save has. Both of you have to pair again with /coopsave.",
                            $"你的游戏拒绝了 {player.Username} 的一条回应，因为它指向的存档标识和当前存档的对不上。" +
                            "你们两个都要用 /coopsave 重新配对。"
                        )
                        : Lang.Pick(
                            $"Your game turned down an answer from {player.Username}, because it is addressed to a " +
                            "different save key than this save has. All of you have to pair again with /coopsave.",
                            $"你的游戏拒绝了 {player.Username} 的一条回应，因为它指向的存档标识和当前存档的对不上。" +
                            "你们所有人都要用 /coopsave 重新配对。"
                        )
                );
            }

            return;
        }

        _highestCheckKey = System.Math.Max(_highestCheckKey, update.Key);
        _memberChecks.TryGetValue(player.Id, out var check);

        if (update.PartCount == HelloAnswer) {
            if (update.Key == _checkKey && check != null) {
                check.Hello = true;
                ResendWorldState(player, check);
            }

            return;
        }

        if (update.Key > _checkKey) {
            // A newer round of a member, like after they loaded their save again or another member came in
            ResetCheck();
            _checkKey = update.Key;
            check = _memberChecks[player.Id] = new MemberCheck(player.Username) { Hello = true };
            SendHello(player, marker, HelloAnswer, check);
            return;
        }

        if (_roundFinished && !_checkedMembers.Contains(player.Id)) {
            // A member who wasn't in the round that finished, like one who came into the save since: every member
            // checks again with them, in a round whose key is larger than any of them has seen, theirs included
            Logger.Info($"{player.Username} came into the save after the last check, so a new check starts for everyone");
            ResetCheck();
            return;
        }

        if (update.Key == _checkKey) {
            // The same round, like when two players started one with the same key, or a member still asking about
            // a round that finished here
            if (check == null) {
                check = _memberChecks[player.Id] = new MemberCheck(player.Username);
            }

            check.Hello = true;
            SendHello(player, marker, HelloAnswer, check);
            ResendWorldState(player, check);
            return;
        }

        // An older round. If the round of the local player hasn't finished with them, they may not know it yet.
        if (!_checkedMembers.Contains(player.Id)) {
            if (check == null) {
                check = _memberChecks[player.Id] = new MemberCheck(player.Username);
            }

            SendHello(player, marker, HelloStart, check);
        }
    }

    /// <summary>
    /// A part of the world progress of a member arrived. Once all parts of every member of the round are in, the progress
    /// is added after the local save sent its own.
    /// </summary>
    private void OnWorldState(ClientPlayerData player, CoopSaveUpdate update) {
        _memberChecks.TryGetValue(player.Id, out var check);
        if (check is { StateReceived: true }) {
            // Every part is already in. A member who asks again lands here, which is not a problem
            return;
        }

        string? turnedAway = null;
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker)) {
            turnedAway = "no save paired with theirs is loaded here";
        } else if (_checkKey == 0) {
            turnedAway = "no check is running here";
        } else if (update.Key != _checkKey) {
            turnedAway = $"it belongs to check {update.Key}, and this game is on check {_checkKey}";
        } else if (check == null) {
            turnedAway = "the check of this game is without them";
        }

        if (turnedAway != null) {
            // This said nothing at all until now. A part turned away here used to be gone for good, so the player
            // it was meant for waited for ever with no line anywhere saying why - which is the state two players
            // ended up in and could not get out of. They are asked for again now, and it says when one is dropped.
            if (_stateDropTold.Add(player.Id)) {
                Logger.Warn(
                    $"Turned away part {update.Part} of the world progress of {player.Username}: {turnedAway}"
                );
            }

            return;
        }

        check!.Parts[update.Part] = update;
        if (check.Parts.Count < update.PartCount) {
            return;
        }

        check.StateReceived = true;
    }

    /// <summary>
    /// A member left the shared save, unless the leave is older than the current round.
    /// </summary>
    private void OnLeft(ClientPlayerData player, CoopSaveUpdate update) {
        if (_memberChecks.ContainsKey(player.Id) && _checkKey > update.Key) {
            return;
        }

        PartnerLeft(player.Id, Lang.Pick("left your two-player save", "退出了你们的双人存档"));
    }

    /// <summary>
    /// Finishes a round: remembers it, and lets the local player move once every member of the save was in it.
    /// </summary>
    /// <param name="marker">The pairing of the loaded save.</param>
    /// <param name="members">The members of the round, every member on the server.</param>
    private void FinishCheck(CoopSaveMarker marker, List<ClientPlayerData> members) {
        _roundFinished = true;
        _checkedMembers.Clear();
        foreach (var member in members) {
            _checkedMembers.Add(member.Id);
        }

        _helloKeyMismatchTold = false;
        var complete = IsGroupComplete(marker, members);
        if (complete) {
            _everChecked = true;
        }

        ResetWorldChanges();
        AddKnownWorldItems();
        RememberWishes();
        RememberStoryFlags();
        SendPendingWishTurnIns(members[0]);
        AskAboutWishesRead(members[0]);

        foreach (var member in members) {
            if (FindMarkerMember(marker, member) is { } entry) {
                entry.Name = member.Username;
                if (member.SaveKey.Length > 0) {
                    entry.Key = member.SaveKey;
                }
            }
        }

        marker.TakeOverPartner();
        marker.LastCheckUtc = DateTime.UtcNow;

        // The play time counts from when every member last played together, so only a round with all of them sets it
        if (complete) {
            marker.CheckedPlayTime = PlayerData.instance != null ? PlayerData.instance.playTime : marker.CheckedPlayTime;
            _nextCheckedPlayTimeSave = UnityEngine.Time.unscaledTime + CheckedPlayTimeSaveInterval;
        }

        SaveMarkers();

        var names = JoinNames(members.Select(member => member.Username));
        Logger.Info(
            $"Checked two-player save with {string.Join(", ", members.Select(member => member.Username))}, " +
            $"{_addedChanges} changes added"
        );
        Chat(members.Count == 1 ? GetCheckSummary(names) : GetGroupCheckSummary(names));
    }

    /// <summary>
    /// What a player hears once the round of a two-player save finished.
    /// </summary>
    private string GetCheckSummary(string partner) {
        // Every sentence is picked whole, and the four ways the last one can read are written out in full rather
        // than built from a translated fragment dropped into a translated frame. A fragment reads as a sentence in
        // English and as debris in Chinese, and that is exactly how this summary reached a player half-translated
        // while the shorter lines around it came out fine.
        var message = _addedChanges == 0
            ? Lang.Pick(
                $"Two-player save with {partner}: backed up, and your worlds match.",
                $"和 {partner} 的双人存档：已备份，你们的世界是一致的。"
            )
            : Lang.Pick(
                $"Two-player save with {partner}: backed up, and {_addedChanges} changes from their world " +
                "were added to yours. Changes in the room you are in show once you enter it again.",
                $"和 {partner} 的双人存档：已备份，并把对方世界里的 {_addedChanges} 处改动合进了你这边。" +
                "你当前所在房间的改动，要重新进这个房间才会显示。"
            );
        if (_onlyPartnerDefeats > 0 || _onlyLocalDefeats > 0) {
            message += Lang.Pick(
                $" Your saves have beaten different bosses ({partner} beat {_onlyPartnerDefeats} " +
                $"that you haven't, you beat {_onlyLocalDefeats} that they haven't). Beaten bosses aren't " +
                "copied, so nobody misses a reward.",
                $"你们两边打过的 Boss 不一样（{partner} 打过 {_onlyPartnerDefeats} 个你没打的，" +
                $"你打过 {_onlyLocalDefeats} 个他们没打的）。打过的 Boss 不会互相复制，免得谁少拿奖励。"
            );
        }

        if (_differentWishes > 0) {
            message += Lang.Pick(
                _differentWishes == 1
                    ? " 1 wish is completed in only one of your saves. Completed wishes aren't copied, so nobody " +
                      "misses a reward."
                    : $" {_differentWishes} wishes are completed in only one of your saves. Completed wishes aren't " +
                      "copied, so nobody misses a reward.",
                $"有 {_differentWishes} 个心愿只在你们其中一边完成了。完成的心愿不会互相复制，免得谁少拿奖励。"
            );
        }

        if (_differentStoryFlags > 0) {
            message += _storyFlagsFrom != null
                ? _checkPlayTimeSinceTogether
                    ? Lang.Pick(
                        $" {_differentStoryFlags} story changes came from the save of {partner}, which was " +
                        "played longer since you last played together.",
                        $"有 {_differentStoryFlags} 处剧情改动来自 {partner} 的存档，因为上次一起玩之后他们玩得更久。"
                    )
                    : Lang.Pick(
                        $" {_differentStoryFlags} story changes came from the save of {partner}, which was " +
                        "played for longer.",
                        $"有 {_differentStoryFlags} 处剧情改动来自 {partner} 的存档，因为他们玩得更久。"
                    )
                : _checkPlayTimeSinceTogether
                    ? Lang.Pick(
                        $" {_differentStoryFlags} story changes went from your save to {partner}, because " +
                        "yours was played longer since you last played together.",
                        $"有 {_differentStoryFlags} 处剧情改动从你的存档传给了 {partner}，因为上次一起玩之后你玩得更久。"
                    )
                    : Lang.Pick(
                        $" {_differentStoryFlags} story changes went from your save to {partner}, because " +
                        "yours was played for longer.",
                        $"有 {_differentStoryFlags} 处剧情改动从你的存档传给了 {partner}，因为你玩得更久。"
                    );
        }

        return message;
    }

    /// <summary>
    /// What a player hears once the round of a save of more than two players finished.
    /// </summary>
    private string GetGroupCheckSummary(string members) {
        var message = _addedChanges == 0
            ? Lang.Pick(
                $"Shared save with {members}: backed up, and your worlds match.",
                $"和 {members} 的多人存档：已备份，大家的世界是一致的。"
            )
            : Lang.Pick(
                $"Shared save with {members}: backed up, and {_addedChanges} changes from their worlds were " +
                "added to yours. Changes in the room you are in show once you enter it again.",
                $"和 {members} 的多人存档：已备份，并把其他人世界里的 {_addedChanges} 处改动合进了你这边。" +
                "你当前所在房间的改动，要重新进这个房间才会显示。"
            );
        if (_onlyPartnerDefeats > 0 || _onlyLocalDefeats > 0) {
            message += Lang.Pick(
                $" Your saves have beaten different bosses (the others beat {_onlyPartnerDefeats} that you haven't, " +
                $"and you beat {_onlyLocalDefeats} that not all of them have). Beaten bosses aren't copied, so " +
                "nobody misses a reward.",
                $"大家打过的 Boss 不一样（其他人打过 {_onlyPartnerDefeats} 个你没打的，你打过 {_onlyLocalDefeats} 个" +
                "不是每个人都打过的）。打过的 Boss 不会互相复制，免得谁少拿奖励。"
            );
        }

        if (_differentWishes > 0) {
            message += Lang.Pick(
                _differentWishes == 1
                    ? " 1 wish is completed in only some of your saves. Completed wishes aren't copied, so nobody " +
                      "misses a reward."
                    : $" {_differentWishes} wishes are completed in only some of your saves. Completed wishes aren't " +
                      "copied, so nobody misses a reward.",
                $"有 {_differentWishes} 个心愿只在部分人那边完成了。完成的心愿不会互相复制，免得谁少拿奖励。"
            );
        }

        if (_differentStoryFlags > 0) {
            var from = _storyFlagsFrom;
            message += from != null
                ? _checkPlayTimeSinceTogether
                    ? Lang.Pick(
                        $" {_differentStoryFlags} story changes came from the save of {from}, which was played " +
                        "longest since you all last played together.",
                        $"有 {_differentStoryFlags} 处剧情改动来自 {from} 的存档，因为上次大家一起玩之后 {from} 玩得最久。"
                    )
                    : Lang.Pick(
                        $" {_differentStoryFlags} story changes came from the save of {from}, which was played " +
                        "for longest.",
                        $"有 {_differentStoryFlags} 处剧情改动来自 {from} 的存档，因为 {from} 玩得最久。"
                    )
                : _checkPlayTimeSinceTogether
                    ? Lang.Pick(
                        $" {_differentStoryFlags} story changes went from your save to the others, because yours was " +
                        "played longest since you all last played together.",
                        $"有 {_differentStoryFlags} 处剧情改动从你的存档传给了其他人，因为上次大家一起玩之后你玩得最久。"
                    )
                    : Lang.Pick(
                        $" {_differentStoryFlags} story changes went from your save to the others, because yours was " +
                        "played for longest.",
                        $"有 {_differentStoryFlags} 处剧情改动从你的存档传给了其他人，因为你玩得最久。"
                    );
        }

        return message;
    }

    #region World progress

    /// <summary>
    /// Sends the bosses that the local save has beaten, the saved objects of the world that are set in it, its wish log
    /// and its story flags to a member: the same that every member of the round gets.
    /// </summary>
    private void SendWorldState(ClientPlayerData member, bool again = false) {
        var state = GetRoundState();
        var defeatIndex = 0;
        var itemIndex = 0;
        var wishIndex = 0;
        var storyIndex = 0;
        for (var part = 0; part < state.PartCount; part++) {
            var update = new CoopSaveUpdate {
                TargetId = member.Id,
                Kind = CoopSaveUpdateKind.WorldState,
                Key = _checkKey,
                Part = (ushort) part,
                PartCount = (ushort) state.PartCount,
                PlayTime = GetCheckPlayTime()
            };

            for (var entries = 0; entries < EntriesPerPart; entries++) {
                if (defeatIndex < state.Defeats.Count) {
                    update.Records.Add(state.Defeats[defeatIndex++]);
                } else if (itemIndex < state.Items.Count) {
                    update.ItemScenes.Add(state.Items[itemIndex].Scene);
                    update.ItemIds.Add(state.Items[itemIndex].Id);
                    itemIndex++;
                } else if (wishIndex < state.Wishes.Count) {
                    update.WishNames.Add(state.Wishes[wishIndex].Name);
                    update.WishValues.Add(state.Wishes[wishIndex].Value);
                    wishIndex++;
                } else if (storyIndex < state.StoryFlags.Count) {
                    update.FlagNames.Add(state.StoryFlags[storyIndex].Name);
                    update.FlagValues.Add(state.StoryFlags[storyIndex].Value);
                    storyIndex++;
                } else {
                    break;
                }
            }

            Send(update);
        }

        if (!again) {
            Logger.Info(
                $"Sent world progress to {member.Username}: {state.Defeats.Count} beaten bosses, {state.Items.Count} " +
                $"saved objects, {state.Wishes.Count} entries of the wish log and {state.StoryFlags.Count} story " +
                $"flags in {state.PartCount} parts"
            );
        }
    }

    /// <summary>
    /// The world progress of the local save for the current round, read, and the save backed up, the first time it goes
    /// to a member. Every member of the round gets the same, and so does a member who asks for it again: a part that
    /// went again used to be read anew, so the parts that one member held could come from two different reads, and the
    /// local game compared with the later one.
    /// </summary>
    private RoundState GetRoundState() {
        if (_roundState != null) {
            return _roundState;
        }

        BackUpSave(_sessionSlot);

        var defeats = GetDefeatRecords();
        var items = GetWorldItems();
        var wishes = GetWishEntries();
        var storyFlags = GetStoryEntries();
        _sentWorldItems = items;
        _sentWishEntries = wishes;
        _agreedStoryValues = storyFlags.Select(entry => entry.Value).ToArray();
        return _roundState = new RoundState {
            Defeats = defeats,
            Items = items,
            Wishes = wishes,
            StoryFlags = storyFlags,
            PartCount = System.Math.Max(
                1, (defeats.Count + items.Count + wishes.Count + storyFlags.Count + EntriesPerPart - 1) / EntriesPerPart
            )
        };
    }

    /// <summary>
    /// Sends the world progress again to a member who is still asking about this round. The progress used to go out
    /// exactly once: a part that was turned away, or that never arrived, was never sent again, and the player left
    /// waiting could not ask for it either, because the game that finished its own check stops looking at the check
    /// at all. A hello that keeps arriving means they are still waiting, so it is answered with the progress too.
    /// The parts are held by their number until they are all in, so sending them twice changes nothing.
    /// </summary>
    /// <param name="member">The member still asking about the round.</param>
    /// <param name="check">The check with them.</param>
    private void ResendWorldState(ClientPlayerData member, MemberCheck check) {
        if (!check.StateSent || _checkKey == 0) {
            return;
        }

        if (!check.ResentTold) {
            check.ResentTold = true;
            Logger.Info(
                $"Sending the world progress to {member.Username} again, because they are still asking about " +
                $"check {_checkKey}"
            );
        }

        SendWorldState(member, true);
    }

    /// <summary>
    /// Adds what the saves of the members sent for the round to the local save, all at once: the saved objects of the
    /// world and the wishes that one of them has and the local save lacks, with the player data that they set, and the
    /// story flags that differ from the save that was played longest. It compares the beaten bosses and completed
    /// wishes. Only what the local player also counts as the world is added. Every game of the round adds the same
    /// saves, so they all end up agreeing.
    /// </summary>
    /// <param name="members">The members of the round.</param>
    private void AddWorldStates(List<ClientPlayerData> members) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        if (playerData == null || sceneData == null) {
            return;
        }

        var states = members
            .Select(member => (
                Member: member,
                Parts: _memberChecks[member.Id].Parts.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList()
            ))
            .ToList();

        var localDefeats = new HashSet<string>(GetDefeatRecords());
        var memberDefeats = states
            .Select(state => new HashSet<string>(state.Parts.SelectMany(part => part.Records)))
            .ToList();
        var onlyPartner = memberDefeats.SelectMany(defeats => defeats).Distinct()
            .Where(name => !localDefeats.Contains(name)).ToList();
        var onlyLocal = localDefeats.Where(name => memberDefeats.Any(defeats => !defeats.Contains(name))).ToList();
        _onlyPartnerDefeats = onlyPartner.Count;
        _onlyLocalDefeats = onlyLocal.Count;
        var names = string.Join(", ", members.Select(member => member.Username));
        if (onlyPartner.Count > 0 || onlyLocal.Count > 0) {
            Logger.Info(
                $"Beaten bosses differ from {names}. Only theirs: {string.Join(", ", onlyPartner)}; " +
                $"only local: {string.Join(", ", onlyLocal)}"
            );
        }

        var loadedScenes = GetLoadedSceneNames();
        var loadedItems = new HashSet<string>(StringComparer.Ordinal);
        var items = 0;
        var flags = 0;

        foreach (var (_, parts) in states) {
            foreach (var part in parts) {
                for (var i = 0; i < part.ItemIds.Count && i < part.ItemScenes.Count; i++) {
                    if (AddWorldItem(part.ItemScenes[i], part.ItemIds[i], loadedScenes, loadedItems, ref flags)) {
                        items++;
                    }
                }
            }
        }

        OverrideLoadedItems(loadedItems);
        var memberWishes = states
            .Select(state => state.Parts
                .SelectMany(part => part.WishNames.Zip(part.WishValues, (name, value) => (name, value)))
                .ToList())
            .ToList();
        var wishes = AddWishes(memberWishes);
        var acceptTalks = GiveBackAcceptTalks();
        ForgetSharedAcceptTalks(memberWishes);
        var storyFlags = AddStoryFlags(states);

        _addedChanges = items + wishes + storyFlags;
        Logger.Info(
            $"Added world progress of {names}: {items} saved objects, {flags} player data flags, " +
            $"{wishes} entries of the wish log with what {acceptTalks} dialogues gave along with them, and " +
            $"{storyFlags} story flags, with {_differentWishes} wishes completed in only some saves and " +
            $"{_differentStoryFlags} story flags that differed"
        );
    }

    /// <summary>
    /// Adds a saved object of the world that the partner set to the local save, if the local save lacks it and also
    /// counts it as the world, with the player data that the object sets together with its own state.
    /// </summary>
    /// <param name="scene">The scene of the object.</param>
    /// <param name="id">The ID of the object.</param>
    /// <param name="loadedScenes">The names of the loaded scenes in lower case.</param>
    /// <param name="loadedItems">The keys of the added objects in loaded scenes, which this adds to.</param>
    /// <param name="flags">The number of player data flags that were set, which this adds to.</param>
    /// <returns>Whether the object was added.</returns>
    private bool AddWorldItem(
        string scene,
        string id,
        HashSet<string> loadedScenes,
        HashSet<string> loadedItems,
        ref int flags
    ) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        var key = GetItemKey(scene, id);
        if (!GetWorldBools().Contains(key) ||
            (sceneData.PersistentBools.TryGetValue(scene, id, out var existing) && existing.Value)) {
            return false;
        }

        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = id,
            SceneName = scene,
            Value = true,
            IsSemiPersistent = false
        });

        if (loadedScenes.Contains(scene.ToLowerInvariant())) {
            loadedItems.Add(key);
        }

        if (_worldPlayerData!.TryGetValue(key, out var names)) {
            var boolNames = GetPlayerDataBoolNames();
            foreach (var name in names) {
                if (boolNames.Contains(name) && !BossRoomCoop.IsHeroStateName(name) && !playerData.GetBool(name)) {
                    playerData.SetBool(name, true);
                    flags++;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a boolean of the player data records a beaten boss.
    /// </summary>
    private static bool IsDefeatRecord(string name) {
        return name.StartsWith("defeated", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Defeated", StringComparison.Ordinal);
    }

    /// <summary>
    /// The bosses that the local save has beaten, by the names of their records.
    /// </summary>
    internal static List<string> GetDefeatRecords() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return [];
        }

        _defeatFields ??= typeof(PlayerData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(bool) && IsDefeatRecord(field.Name))
            .ToArray();
        return _defeatFields
            .Where(field => field.GetValue(playerData) is true)
            .Select(field => field.Name)
            .ToList();
    }

    private static HashSet<string> GetPlayerDataBoolNames() {
        return _playerDataBoolNames ??= new HashSet<string>(
            typeof(PlayerData)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => field.FieldType == typeof(bool))
                .Select(field => field.Name)
        );
    }

    /// <summary>
    /// The saved objects of the world that are set in the local save.
    /// </summary>
    private List<(string Scene, string Id)> GetWorldItems() {
        var result = new List<(string Scene, string Id)>();
        var worldBools = GetWorldBools();
        var collection = SceneData.instance?.PersistentBools;
        if (collection == null || worldBools.Count == 0) {
            return result;
        }

        // The collection is a PersistentBoolCollection, which inherits 'scenes' from the generic base it derives from.
        // Reflection never gives back a private field of a base class, so asking the type of the object alone found
        // nothing every single time, and the read below gave up. Walk up to the class that declares the field.
        FieldInfo? scenesField = null;
        for (var type = collection.GetType(); type != null && scenesField == null; type = type.BaseType) {
            scenesField = type.GetField("scenes", InstanceFlags);
        }

        var scenesValue = scenesField?.GetValue(collection);
        if (scenesValue is not Dictionary<string, Dictionary<string, PersistentItemData<bool>>> scenes) {
            // This says which of the three ways it can fail this was: the field is gone, the game has not built the
            // dictionary yet, or it is not the shape expected here. Guessing between them from the outside is what
            // this exists to stop. It used to report the first one every time, which the lookup above now fixes.
            if (!_worldItemReadFailed) {
                _worldItemReadFailed = true;
                Logger.Warn(
                    "Could not read the saved objects of the save: " +
                    (scenesField == null
                        ? "the collection has no 'scenes' field"
                        : scenesValue == null
                            ? "'scenes' is null, so the game has not built it yet"
                            : $"'scenes' is a {scenesValue.GetType().FullName}, which is not the expected shape")
                );
            }

            return result;
        }

        _worldItemReadFailed = false;

        foreach (var scene in scenes) {
            foreach (var item in scene.Value.Values) {
                if (item.Value && !item.IsSemiPersistent && worldBools.Contains(GetItemKey(scene.Key, item.ID))) {
                    result.Add((scene.Key, item.ID));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The saved booleans of the world, loaded from the embedded list when first needed.
    /// </summary>
    private HashSet<string> GetWorldBools() {
        if (_worldBools != null) {
            return _worldBools;
        }

        _worldBools = new HashSet<string>(StringComparer.Ordinal);
        _worldPlayerData = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var worldItems = FileUtil.LoadObjectFromEmbeddedJson<CoopWorldItems>(WorldItemsFilePath);
        if (worldItems == null) {
            Logger.Warn("Could not load the saved objects of the world for two-player saves");
            return _worldBools;
        }

        foreach (var scene in worldItems.Bools) {
            foreach (var id in scene.Value) {
                _worldBools.Add(GetItemKey(scene.Key, id));
            }
        }

        foreach (var scene in worldItems.PlayerData) {
            foreach (var item in scene.Value) {
                _worldPlayerData[GetItemKey(scene.Key, item.Key)] = item.Value;
            }
        }

        return _worldBools;
    }

    /// <summary>
    /// The key of a saved object: the scene in lower case, because the list comes from bundle names, and the ID.
    /// </summary>
    private static string GetItemKey(string scene, string id) => scene.ToLowerInvariant() + "\n" + id;

    /// <summary>
    /// The names of the loaded scenes in lower case.
    /// </summary>
    private static HashSet<string> GetLoadedSceneNames() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) {
            names.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.ToLowerInvariant());
        }

        return names;
    }

    /// <summary>
    /// Makes the saved objects of the loaded scenes that the partner changed keep that change. They still look the old
    /// way until the player enters the scene again, and without this they would save the old way when the player leaves.
    /// </summary>
    private static void OverrideLoadedItems(HashSet<string> keys) {
        if (keys.Count == 0) {
            return;
        }

        foreach (var item in UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(
                     UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None
                 )) {
            GetItemSceneAndId(item, out var scene, out var id);
            if (keys.Contains(GetItemKey(scene, id))) {
                item.SetValueOverride(true);
            }
        }
    }

    /// <summary>
    /// Gets the scene and the ID that a saved object is saved under.
    /// </summary>
    private static void GetItemSceneAndId(PersistentBoolItem item, out string scene, out string id) {
        var data = item.ItemData;
        id = string.IsNullOrEmpty(data?.ID) ? item.gameObject.name : data!.ID;
        scene = string.IsNullOrEmpty(data?.SceneName) ? item.gameObject.scene.name : data!.SceneName;
    }

    #endregion

    #region Backups

    /// <summary>
    /// Copies the save file of a slot to the backups of two-player saves, keeping the newest backups.
    /// </summary>
    private static void BackUpSave(int slot) {
        try {
            var path = GetSaveFilePath(slot);
            if (path == null || !File.Exists(path)) {
                Logger.Warn($"Could not find the save file of slot {slot} to back up");
                return;
            }

            var folder = Path.Combine(FileUtil.GetConfigPath(), BackupFolderName, GetSaveFolderName(), $"slot{slot}");
            Directory.CreateDirectory(folder);
            var backup = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}"
            );
            File.Copy(path, backup, true);
            Logger.Info($"Backed up two-player save to {backup}");

            foreach (var old in new DirectoryInfo(folder).GetFiles()
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(MaxBackups)) {
                old.Delete();
            }
        } catch (Exception e) {
            Logger.Error($"Could not back up the save file of slot {slot}:\n{e}");
        }
    }

    /// <summary>
    /// The path of the save file of a slot, or null if the platform doesn't say.
    /// </summary>
    private static string? GetSaveFilePath(int slot) {
        var platform = Platform.Current;
        var method = platform?.GetType().GetMethod("GetSaveSlotPath", InstanceFlags);
        var parameters = method?.GetParameters();
        if (platform == null || method == null || parameters is not { Length: 2 } ||
            parameters[0].ParameterType != typeof(int)) {
            return null;
        }

        // The first file name usage is the save file itself rather than one of its backups
        var usage = parameters[1].ParameterType.IsEnum ? Enum.ToObject(parameters[1].ParameterType, 0) : null;
        return method.Invoke(platform, [slot, usage]) as string;
    }

    #endregion
}
