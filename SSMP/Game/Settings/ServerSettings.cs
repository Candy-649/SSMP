using System;
using SSMP.Api.Server;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable StringLiteralTypo

namespace SSMP.Game.Settings;

/// <inheritdoc cref="IServerSettings" />
public class ServerSettings : IServerSettings, IEquatable<ServerSettings> {
    /// <inheritdoc />
    public event Action<string>? ChangeEvent;

    /// <inheritdoc />
    [SettingAlias("pvp")]
    public bool IsPvpEnabled {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(IsPvpEnabled));
        }
    }

    /// <inheritdoc />
    [SettingAlias("globalmapicons")]
    public bool AlwaysShowMapIcons {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(AlwaysShowMapIcons));
        }
    }

    /// <inheritdoc />
    [SettingAlias("compassicon", "compassicons")]
    public bool OnlyBroadcastMapIconWithCompass {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(OnlyBroadcastMapIconWithCompass));
        }
    } = true;

    /// <inheritdoc />
    [SettingAlias("names")]
    public bool DisplayNames {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(DisplayNames));
        }
    } = true;

    /// <inheritdoc />
    [SettingAlias("teams")]
    public bool TeamsEnabled {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(TeamsEnabled));
        }
    }

    /// <inheritdoc />
    [SettingAlias("skins")]
    public bool AllowSkins {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(AllowSkins));
        }
    } = true;

    /// <inheritdoc />
    [SettingAlias("needledmg")]
    public byte NeedleDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(NeedleDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("needlestrikedmg", "strikedmg", "artdmg")]
    public byte NeedleStrikeDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(NeedleStrikeDamage));
        }
    } = 2;

    /// <inheritdoc />
    [SettingAlias("voltfilamentdmg", "voltdmg", "filamentdmg", "voltmodifier", "voltmod")]
    public byte VoltFilamentDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(VoltFilamentDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("shamandmg", "shamanmodifier", "shamanmod")]
    public byte ShamanDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(ShamanDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("crossstitchdmg", "stitchdmg", "parrydmg")]
    public byte CrossStitchDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(CrossStitchDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("palenailsdmg", "palenaildmg", "paledmg")]
    public byte PaleNailsDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(PaleNailsDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("runeragedmg")]
    public byte RuneRageDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(RuneRageDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("sharpdartdmg", "dartdmg")]
    public byte SharpDartDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(SharpDartDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("silkspeardmg", "speardmg")]
    public byte SilkSpearDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(SilkSpearDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("threadstormdmg", "stormdmg")]
    public byte ThreadStormDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(ThreadStormDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("wardingbelldmg", "bindbelldmg", "belldmg")]
    public byte WardingBellDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(WardingBellDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("clawmirrordmg", "mirrordmg", "mirror1dmg")]
    public byte ClawMirrorDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(ClawMirrorDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("clawmirrorupgradeddmg", "mirror2dmg")]
    public byte ClawMirrorUpgradedDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(ClawMirrorUpgradedDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("sawtoothcircletdmg", "circletdmg", "umbrelladmg")]
    public byte SawtoothCircletDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(SawtoothCircletDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("poisonbrewdmg", "brewdmg", "fleabrewdmg")]
    public byte PoisonBrewDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(PoisonBrewDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("straightpindmg", "pindmg", "pindamage")]
    public byte StraightPinDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(StraightPinDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("threefoldpindmg", "threepindmg", "threepindamage")]
    public byte ThreefoldPinDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(ThreefoldPinDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("longpindmg")]
    public byte LongpinDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(LongpinDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("tackdmg", "tacksdmg", "tackdamage")]
    public byte TacksDamage {
        get;
        init {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(TacksDamage));
        }
    } = 1;

    /// <inheritdoc />
    [SettingAlias("enemyhp", "enemyhpmode")]
    public byte EnemyHealthMode {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(EnemyHealthMode));
        }
    }

    /// <inheritdoc />
    [SettingAlias("enemyhppower", "enemyhpexp")]
    public byte EnemyHealthExponentialFactor {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(EnemyHealthExponentialFactor));
        }
    } = 15;

    /// <inheritdoc />
    [SettingAlias("enemyhplinear")]
    public byte EnemyHealthLinearFactor {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(EnemyHealthLinearFactor));
        }
    } = 5;

    /// <inheritdoc />
    [SettingAlias("enemyhpfixed")]
    public byte EnemyHealthFixedFactor {
        get;
        set {
            if (field == value) return;
            field = value;
            ChangeEvent?.Invoke(nameof(EnemyHealthFixedFactor));
        }
    } = 15;

    /// <summary>
    /// Set all properties in this <see cref="ServerSettings"/> instance to the values from the given
    /// <see cref="ServerSettings"/> instance.
    /// </summary>
    /// <param name="serverSettings">The instance to copy from.</param>
    public void SetAllProperties(ServerSettings serverSettings) {
        // Use reflection to copy over all properties into this object
        foreach (var prop in GetType().GetProperties()) {
            if (!prop.CanRead || !prop.CanWrite) {
                continue;
            }

            prop.SetValue(this, prop.GetValue(serverSettings, null), null);
        }
    }

    /// <summary>
    /// Get a copy of this instance of the server settings.
    /// </summary>
    /// <returns>A new instance of the server settings with the same values as this instance.</returns>
    public ServerSettings GetCopy() {
        var serverSettings = new ServerSettings();
        serverSettings.SetAllProperties(this);

        return serverSettings;
    }

    /// <inheritdoc />
    public bool Equals(ServerSettings other) {
        if (ReferenceEquals(null, other)) {
            return false;
        }
    
        if (ReferenceEquals(this, other)) {
            return true;
        }
    
        foreach (var prop in GetType().GetProperties()) {
            if (!prop.CanRead) {
                continue;
            }
    
            if (prop.GetValue(this) != prop.GetValue(other)) {
                return false;
            }
        }
    
        return true;
    }
    
    /// <inheritdoc />
    public override bool Equals(object? obj) {
        if (ReferenceEquals(null, obj)) {
            return false;
        }
    
        if (ReferenceEquals(this, obj)) {
            return true;
        }
    
        if (obj.GetType() != GetType()) {
            return false;
        }
    
        return Equals((ServerSettings) obj);
    }
    
    /// <inheritdoc />
    public override int GetHashCode() {
        unchecked {
            var hashCode = 0;
            var first = true;
            foreach (var prop in GetType().GetProperties()) {
                if (!prop.CanRead) {
                    continue;
                }
                
                var propHashCode = prop.GetValue(this).GetHashCode();
    
                if (first) {
                    hashCode = propHashCode;
                    first = false;
                    continue;
                }
    
                hashCode = (hashCode * 397) ^ propHashCode;
            }
    
            return hashCode;
        }
    }
    
    /// <summary>
    /// Indicates whether one <see cref="ServerSettings"/> is equal to another <see cref="ServerSettings"/>.
    /// </summary>
    /// <param name="left">The first <see cref="ServerSettings"/> to compare.</param>
    /// <param name="right">The second <see cref="ServerSettings"/> to compare.</param>
    /// <returns>true if <paramref name="left"/> is equal to <paramref name="right"/>; false otherwise.</returns>
    public static bool operator ==(ServerSettings? left, ServerSettings? right) {
        return Equals(left, right);
    }
    
    /// <summary>
    /// Indicates whether one <see cref="ServerSettings"/> is not equal to another <see cref="ServerSettings"/>.
    /// </summary>
    /// <param name="left">The first <see cref="ServerSettings"/> to compare.</param>
    /// <param name="right">The second <see cref="ServerSettings"/> to compare.</param>
    /// <returns>true if <paramref name="left"/> is not equal to <paramref name="right"/>; false otherwise.</returns>
    public static bool operator !=(ServerSettings? left, ServerSettings? right) {
        return !Equals(left, right);
    }
}
