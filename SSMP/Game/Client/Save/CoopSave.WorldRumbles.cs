using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The world shaking now and then once it is bound in thread, in the last part of the game: the ground rumbles, a sound
/// rolls through, the screen fades a little, and in some rooms tendrils come up out of the ground for a moment. The
/// game does it by itself (WorldRumbleManager, under the cameras) in almost every room that is on the map, after a wait
/// it rolls each time, from 45 seconds to 280, which starts over in every room a player comes into while it is still;
/// never in dreams, on the peaks, in the fields of clover or on the surface. Each game rolled its own waits, so two
/// players in one room never shook together: the screen of one shook and faded while the other's stood still, and the
/// other's did a minute later (USER 10-31 "时不时演出一段褪色动画加震动").
///
/// Now it goes by the scene host's game, the way the machines of a room go by the host's rounds (see
/// CoopSave.PacedMachines): the host's game shakes when its own wait is over and tells the members in the room, with
/// the dice it rolled for the shaking, so that their games shake with the same sound and for as long. A game whose
/// scene host is a member in the room doesn't shake by itself. The tendrils are creatures that the scene host runs,
/// which come up as the host's world shakes, so they come up with the shaking in every game now. A shaking that a
/// moment of the story sets off - the waking at the start of that part, a character, a cutscene - is left to each game,
/// since each game plays that moment by itself. A game that holds the world still for a while, in a game of the
/// festival or a cutscene, doesn't shake with the host either, as it wouldn't shake by itself.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a game that shakes with the scene host's waits before it looks again, in seconds, should the game not
    /// say how long it would wait: the soonest it would ever shake by itself again.
    /// </summary>
    private const float RumbleLookAgainWait = 45f;

    /// <summary>
    /// The method that starts a shaking of the world and gives how long until the next.
    /// </summary>
    private static readonly MethodInfo? RumbleStartMethod =
        typeof(WorldRumbleManager).GetMethod("DoNewRumble", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// The method that rolls the wait until the next shaking, after the given length of the one that starts now.
    /// </summary>
    private static readonly MethodInfo? RumbleWaitMethod =
        typeof(WorldRumbleManager).GetMethod("GetWaitTime", InstanceFlags, null, [typeof(float)], null);

    /// <summary>
    /// The shaking that is going on, or null for none.
    /// </summary>
    private static readonly FieldInfo? RumbleCurrentField =
        typeof(WorldRumbleManager).GetField("currentRumble", InstanceFlags);

    /// <summary>
    /// The kind of shaking that the room has, or null for a world that doesn't shake.
    /// </summary>
    private static readonly FieldInfo? RumbleRoomGroupField =
        typeof(WorldRumbleManager).GetField("sceneRumbleGroup", InstanceFlags);

    /// <summary>
    /// Whether the game holds the world still now, which it only looks at before it shakes by itself.
    /// </summary>
    private static readonly PropertyInfo? RumblePreventedProperty =
        typeof(WorldRumbleManager).GetProperty("IsRumblesPrevented", InstanceFlags);

    /// <summary>
    /// The hook of the start of a shaking of the world.
    /// </summary>
    private Hook? _rumbleStartHook;

    /// <summary>
    /// The hooks of the shakings that a moment of the story sets off: by the event that the waking at the start of the
    /// last part sends, and by the action of a character or a cutscene.
    /// </summary>
    private Hook? _storyRumbleEventHook;

    /// <inheritdoc cref="_storyRumbleEventHook"/>
    private Hook? _storyRumbleActionHook;

    /// <summary>
    /// How many shakings that a moment of the story set off are starting now.
    /// </summary>
    private int _storyRumbles;

    /// <summary>
    /// Whether the shaking of the scene host's game is being started here.
    /// </summary>
    private bool _hostRumbleHere;

    /// <summary>
    /// Registers the hooks that make the shaking of the world go by the scene host's game.
    /// </summary>
    private void RegisterWorldRumbleHooks() {
        _rumbleStartHook = CreateHook(
            RumbleStartMethod,
            new Func<Func<WorldRumbleManager, float>, WorldRumbleManager, float>(OnRumbleStart)
        );
        _storyRumbleEventHook = CreateHook(
            typeof(WorldRumbleManager).GetMethod("DoRumbleNow", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<WorldRumbleManager>, WorldRumbleManager>(OnStoryRumble)
        );
        _storyRumbleActionHook = CreateHook(
            typeof(WorldRumbleManager).GetMethod("ForceRumble", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<WorldRumbleManager>, WorldRumbleManager>(OnStoryRumble)
        );
    }

    /// <summary>
    /// Marks a shaking that a moment of the story sets off, which each game has by itself.
    /// </summary>
    private void OnStoryRumble(Action<WorldRumbleManager> orig, WorldRumbleManager self) {
        _storyRumbles++;
        try {
            orig(self);
        } finally {
            _storyRumbles--;
        }
    }

    /// <summary>
    /// Starts a shaking of the world as the game's own wait is over. The scene host's game shakes and tells the
    /// members in the room, with its dice; a game whose scene host is a member in the room doesn't shake, and waits as
    /// it would after a shaking. Alone in the room, or for a moment of the story, the game shakes as it does.
    /// </summary>
    private float OnRumbleStart(Func<WorldRumbleManager, float> orig, WorldRumbleManager self) {
        if (_hostRumbleHere) {
            return orig(self);
        }

        var scene = SceneManager.GetActiveScene().name;
        if (_storyRumbles > 0) {
            Logger.Info($"[Rumble] The world shakes in '{scene}' for a moment of the story, in this game by itself");
            return orig(self);
        }

        var memberInRoom = IsMemberInRoom();
        var isSceneHost = IsSceneHost?.Invoke();
        if (memberInRoom && isSceneHost == false) {
            var wait = RumbleWaitMethod?.Invoke(self, [0f]) is float rolled ? rolled : RumbleLookAgainWait;
            Logger.Info(
                $"[Rumble] The world would shake in '{scene}' by itself now, but it shakes with the scene host's game; " +
                $"looking again in {wait:0} s"
            );
            return wait;
        }

        var tells = memberInRoom && isSceneHost == true;
        var next = 0f;
        var dice = tells ? SharedDice.Record(() => next = orig(self), byPlayer: false) : null;
        if (dice == null) {
            next = orig(self);
        }

        // A room whose kind of shaking holds none starts none; with the field gone, the members are told anyway, as
        // they don't shake by themselves while this game runs the room
        if (RumbleCurrentField != null && RumbleCurrentField.GetValue(self) == null) {
            Logger.Info($"[Rumble] The world has nothing to shake with in '{scene}'; looking again in {next:0} s");
            return next;
        }

        if (dice == null) {
            Logger.Info($"[Rumble] The world shakes in '{scene}'; the next in {next:0} s");
            return next;
        }

        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.WorldRumble,
            Scene = scene,
            Amounts = [..dice]
        });
        Logger.Info(
            $"[Rumble] The world shakes in '{scene}', and the members in the room shake with it; the next in {next:0} s"
        );
        return next;
    }

    /// <summary>
    /// Shakes the world with the scene host's game, with the dice it shook with, unless this game runs the room itself
    /// or holds the world still now.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update: in <see cref="CoopSaveUpdate.Scene"/> the room, and in
    /// <see cref="CoopSaveUpdate.Amounts"/> the dice.</param>
    private void OnWorldRumble(ClientPlayerData player, CoopSaveUpdate update) {
        var scene = SceneManager.GetActiveScene().name;
        if (!_checkedMembers.Contains(player.Id) || !player.IsInLocalScene || update.Scene != scene) {
            return;
        }

        if (IsSceneHost?.Invoke() != false) {
            Logger.Info(
                $"[Rumble] The world shook in '{scene}' in {player.Username}'s game, but this game runs the room, so " +
                "it shakes by itself"
            );
            return;
        }

        var cameras = GameCameras.SilentInstance;
        var manager = cameras != null ? cameras.worldRumbleManager : null;
        if (manager == null || RumbleStartMethod == null) {
            return;
        }

        if (RumblePreventedProperty?.GetValue(manager) is true) {
            Logger.Info(
                $"[Rumble] The world shook in '{scene}' in {player.Username}'s game, but this game holds the world " +
                "still now"
            );
            return;
        }

        if (RumbleRoomGroupField != null && RumbleRoomGroupField.GetValue(manager) == null) {
            Logger.Info(
                $"[Rumble] The world shook in '{scene}' in {player.Username}'s game, but it doesn't shake in this one"
            );
            return;
        }

        _hostRumbleHere = true;
        try {
            SharedDice.Throw(update.Amounts, () => RumbleStartMethod.Invoke(manager, null));
        } finally {
            _hostRumbleHere = false;
        }

        Logger.Info($"[Rumble] The world shakes in '{scene}' with {player.Username}'s game, the scene host's");
    }
}
