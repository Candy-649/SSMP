using System.Collections.Generic;
using SSMP.Math;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for the player update data.
/// </summary>
internal class PlayerUpdate : GenericClientData {
    /// <summary>
    /// The number of distinct update types in the <see cref="PlayerUpdateType"/> enumeration.
    /// Used during packet serialization to avoid Enum.GetNames allocations.
    /// </summary>
    private static readonly int PlayerUpdateTypeCount = Util.EnumCache<PlayerUpdateType>.Count;

    /// <summary>
    /// Set containing the update types that this packet contains.
    /// </summary>
    public HashSet<PlayerUpdateType> UpdateTypes { get; }

    /// <summary>
    /// The position of the player.
    /// </summary>
    public Vector2 Position { get; set; } = Vector2.Zero;

    /// <summary>
    /// The sequence number of the packet this arrived in, filled in on reading rather than sent: it belongs to the
    /// packet that carried it, and is kept here so that whoever applies the position can tell a newer one from an
    /// older one on a transport that does not order what it carries.
    /// </summary>
    public ushort ReceivedSequence { get; set; }

    /// <summary>
    /// The scale of the player.
    /// </summary>
    public bool Scale { get; set; }

    /// <summary>
    /// The map position of the player.
    /// </summary>
    public Vector2 MapPosition { get; set; } = Vector2.Zero;

    /// <summary>
    /// List of animation info instances.
    /// </summary>
    public List<AnimationInfo> AnimationInfos { get; }

    /// <summary>
    /// How many clip changes of their character the player had sent when they sent this, counted round (see
    /// <see cref="PlayerUpdateType.ClipState"/>).
    /// </summary>
    public ushort ClipChanges { get; private set; }

    /// <summary>
    /// The clip that the player's character plays: the one of the last of those changes.
    /// </summary>
    public ushort StateClipId { get; private set; }

    /// <summary>
    /// How long ago the player's character started that clip, in milliseconds.
    /// </summary>
    public ushort StateClipTime { get; private set; }

    /// <summary>
    /// The effect info of that clip when what its effect sets holds while the clip plays, such as whether a player who
    /// plays the needolin wears the charm that widens its ranges (AnimationEffect.HoldsWhileClipPlays); otherwise null.
    /// </summary>
    public byte[]? StateEffectInfo { get; private set; }

    /// <summary>
    /// Construct the player update data.
    /// </summary>
    public PlayerUpdate() {
        UpdateTypes = new HashSet<PlayerUpdateType>();
        AnimationInfos = new List<AnimationInfo>();

        IsReliable = false;
        DropReliableDataIfNewerExists = false;
    }

    /// <summary>
    /// Says what the player's character plays now (see <see cref="PlayerUpdateType.ClipState"/>), unless this update
    /// says something newer already: one that carries what two updates of the player said keeps the newer.
    /// </summary>
    /// <param name="clipChanges">How many clip changes the player had sent, counted round.</param>
    /// <param name="clipId">The clip of the last of them.</param>
    /// <param name="clipTime">How long ago the character started it, in milliseconds.</param>
    /// <param name="effectInfo">Its effect info, when what its effect sets holds while it plays.</param>
    public void SetClipState(ushort clipChanges, ushort clipId, ushort clipTime, byte[]? effectInfo) {
        if (UpdateTypes.Contains(PlayerUpdateType.ClipState) && (short) (clipChanges - ClipChanges) < 0) {
            return;
        }

        UpdateTypes.Add(PlayerUpdateType.ClipState);
        ClipChanges = clipChanges;
        StateClipId = clipId;
        StateClipTime = clipTime;
        StateEffectInfo = effectInfo;
    }

    /// <summary>
    /// Says in another update what this one says the player's character plays, if it says it.
    /// </summary>
    public void CopyClipStateTo(PlayerUpdate other) {
        if (UpdateTypes.Contains(PlayerUpdateType.ClipState)) {
            other.SetClipState(ClipChanges, StateClipId, StateClipTime, StateEffectInfo);
        }
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        // Write the player update information
        packet.Write(Id);

        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < PlayerUpdateTypeCount; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (UpdateTypes.Contains((PlayerUpdateType) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);

        // Conditionally write the position, scale, map position and animation info
        if (UpdateTypes.Contains(PlayerUpdateType.Position)) {
            packet.Write(Position);
        }

        if (UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            packet.Write(Scale);
        }

        if (UpdateTypes.Contains(PlayerUpdateType.MapPosition)) {
            packet.Write(MapPosition);
        }

        if (UpdateTypes.Contains(PlayerUpdateType.Animation)) {
            // First write the number of infos we are writing
            // We also limit this to a byte, if the list is larger than 255 animations,
            // we just don't send them the rest ¯\_(ツ)_/¯
            var numAnimations = (byte) System.Math.Min(AnimationInfos.Count, 255);

            packet.Write(numAnimations);

            for (var i = 0; i < numAnimations; i++) {
                var animationInfo = AnimationInfos[i];

                packet.Write(animationInfo.ClipId);
                packet.Write(animationInfo.Frame);

                // Check whether there is effect info to write
                if (animationInfo.EffectInfo == null) {
                    packet.Write((byte) 0);
                } else {
                    // Again, we first write the length of the effect info array
                    var numEffects = animationInfo.EffectInfo.Length;
                    packet.Write((byte) numEffects);

                    for (var j = 0; j < numEffects; j++) {
                        packet.Write(animationInfo.EffectInfo[j]);
                    }
                }
            }
        }

        if (UpdateTypes.Contains(PlayerUpdateType.ClipState)) {
            packet.Write(ClipChanges);
            packet.Write(StateClipId);
            packet.Write(StateClipTime);

            // Its length first, as with the effect info of a clip change
            var numEffects = (byte) System.Math.Min(StateEffectInfo?.Length ?? 0, byte.MaxValue);
            packet.Write(numEffects);
            for (var i = 0; i < numEffects; i++) {
                packet.Write(StateEffectInfo![i]);
            }
        }
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Id = packet.ReadUShort();

        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < PlayerUpdateTypeCount; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                UpdateTypes.Add((PlayerUpdateType) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }

        // Based on the update types, we read the corresponding values
        if (UpdateTypes.Contains(PlayerUpdateType.Position)) {
            Position = packet.ReadVector2();
        }

        if (UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            Scale = packet.ReadBool();
        }

        if (UpdateTypes.Contains(PlayerUpdateType.MapPosition)) {
            MapPosition = packet.ReadVector2();
        }

        if (UpdateTypes.Contains(PlayerUpdateType.Animation)) {
            // We first read how many animations are in the packet
            var numAnimations = packet.ReadByte();

            for (var i = 0; i < numAnimations; i++) {
                // Create a new animation info instance
                var animationInfo = new AnimationInfo {
                    ClipId = packet.ReadUShort(),
                    Frame = packet.ReadByte()
                };

                // Now we read how many effect are in the packet and
                // create an array with that length
                var numEffects = packet.ReadByte();
                // Check whether there is effect info to be read
                if (numEffects != 0) {
                    var effectInfo = new byte[numEffects];

                    for (var j = 0; j < numEffects; j++) {
                        effectInfo[j] = packet.ReadByte();
                    }

                    // Save the effect info in the animation info instance
                    animationInfo.EffectInfo = effectInfo;
                }

                AnimationInfos.Add(animationInfo);
            }
        }

        if (UpdateTypes.Contains(PlayerUpdateType.ClipState)) {
            ClipChanges = packet.ReadUShort();
            StateClipId = packet.ReadUShort();
            StateClipTime = packet.ReadUShort();

            var numEffects = packet.ReadByte();
            if (numEffects != 0) {
                var effectInfo = new byte[numEffects];
                for (var i = 0; i < numEffects; i++) {
                    effectInfo[i] = packet.ReadByte();
                }

                StateEffectInfo = effectInfo;
            }
        }
    }
}

/// <summary>
/// Enumeration of player update types.
/// </summary>
internal enum PlayerUpdateType {
    Position = 0,
    Scale,
    MapPosition,
    Animation,

    /// <summary>
    /// What the player's character plays now, said with every update of theirs whether it changed or not. A clip
    /// change is said once, in an update that is not sent again if it is lost on the way, which left the character
    /// of another player on the clip before it - running on the spot, or frozen on the end of a slash - until their
    /// next change (USER 10-16 "直接把动作放在包里传输不可以嘛"). The next update that arrives puts it right.
    /// </summary>
    ClipState,
}

/// <summary>
/// Data class for animation related info.
/// </summary>
internal class AnimationInfo {
    /// <summary>
    /// The ID of the animation clip.
    /// </summary>
    public ushort ClipId { get; init; }

    /// <summary>
    /// The frame of the animation to start at.
    /// </summary>
    public byte Frame { get; init; }

    /// <summary>
    /// Byte array containing additional effect info.
    /// </summary>
    public byte[]? EffectInfo { get; set; }
}
