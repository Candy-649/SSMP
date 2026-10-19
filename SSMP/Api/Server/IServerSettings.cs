using System;

namespace SSMP.Api.Server;

/// <summary>
/// Settings related to gameplay that is shared between server and clients.
/// </summary>
public interface IServerSettings {
    /// <summary>
    /// Event triggered whenever any of the server settings are changed.
    /// </summary>
    event Action<string>? ChangeEvent;

    /// <summary>
    /// Whether player vs. player damage is enabled.
    /// </summary>
    bool IsPvpEnabled { get; }

    /// <summary>
    /// Whether to always show map icons.
    /// </summary>
    bool AlwaysShowMapIcons { get; }

    /// <summary>
    /// Whether to only broadcast the map icon of a player if they have wayward compass equipped.
    /// </summary>
    bool OnlyBroadcastMapIconWithCompass { get; }

    /// <summary>
    /// Whether to display player names above the player objects.
    /// </summary>
    bool DisplayNames { get; }

    /// <summary>
    /// Whether teams are enabled.
    /// </summary>
    bool TeamsEnabled { get; }

    /// <summary>
    /// Whether skins are allowed.
    /// </summary>
    bool AllowSkins { get; }

    /// <summary>
    /// The number of masks of damage that a player's needle swing deals.
    /// </summary>
    byte NeedleDamage { get; }

    /// <summary>
    /// The number of masks of damage that Needle Strikes deal.
    /// </summary>
    byte NeedleStrikeDamage { get; }

    /// <summary>
    /// The number of extra half-masks of damage that silk skills with volt filament should deal.
    /// </summary>
    byte VoltFilamentDamage { get; }

    /// <summary>
    /// The number of extra half-masks of damage that silk skills on shaman crest should deal.
    /// </summary>
    byte ShamanDamage { get; }

    /// <summary>
    /// The number of masks of damage that Cross Stitch deals.
    /// </summary>
    byte CrossStitchDamage { get; }

    /// <summary>
    /// The number of masks of damage that Pale Nails deals.
    /// </summary>
    byte PaleNailsDamage { get; }

    /// <summary>
    /// The number of masks of damage that Rune Rage deals.
    /// </summary>
    byte RuneRageDamage { get; }

    /// <summary>
    /// The number of masks of damage that Sharpdart deals.
    /// </summary>
    byte SharpDartDamage { get; }

    /// <summary>
    /// The number of masks of damage that Silk Spear deals.
    /// </summary>
    byte SilkSpearDamage { get; }

    /// <summary>
    /// The number of masks of damage that Thread Storm deals.
    /// </summary>
    byte ThreadStormDamage { get; }

    /// <summary>
    /// The number of masks of damage that the Warding Bell deals.
    /// </summary>
    byte WardingBellDamage { get; }

    /// <summary>
    /// The number of masks of damage that the base Claw Mirror deals.
    /// </summary>
    byte ClawMirrorDamage { get; }

    /// <summary>
    /// The number of masks of damage that the upgraded Claw Mirror deals.
    /// </summary>
    byte ClawMirrorUpgradedDamage { get; }

    /// <summary>
    /// The number of masks of damage that Sawtooth Circlet deals.
    /// </summary>
    byte SawtoothCircletDamage { get; }

    /// <summary>
    /// The number of masks of damage that a cloud from the poisoned Flea Brew deals.
    /// </summary>
    byte PoisonBrewDamage { get; }

    /// <summary>
    /// The number of masks of damage that a Straight Pin deals.
    /// </summary>
    byte StraightPinDamage { get; }

    /// <summary>
    /// The number of masks of damage that a Threefold Pin deals.
    /// </summary>
    byte ThreefoldPinDamage { get; }

    /// <summary>
    /// The number of masks of damage that a Longpin deals.
    /// </summary>
    byte LongpinDamage { get; }
    
    /// <summary>
    /// The number of masks of damage that Tacks deal.
    /// </summary>
    byte TacksDamage { get; }

    /// <summary>
    /// How the health of enemies grows with the number of players: 0 the default, each extra player adding half of
    /// the health of one player alone; 1 each extra player multiplying it by n; 2 each extra player adding n times
    /// it; 3 n times it, however many play. The n of each is its own setting below, in tenths.
    /// </summary>
    byte EnemyHealthMode { get; }

    /// <summary>
    /// The n of <see cref="EnemyHealthMode"/> 1, in tenths: what each extra player multiplies enemy health by.
    /// </summary>
    byte EnemyHealthExponentialFactor { get; }

    /// <summary>
    /// The n of <see cref="EnemyHealthMode"/> 2, in tenths: how many times the health of one player alone each extra
    /// player adds.
    /// </summary>
    byte EnemyHealthLinearFactor { get; }

    /// <summary>
    /// The n of <see cref="EnemyHealthMode"/> 3, in tenths: what enemy health is multiplied by, however many play.
    /// </summary>
    byte EnemyHealthFixedFactor { get; }
}
