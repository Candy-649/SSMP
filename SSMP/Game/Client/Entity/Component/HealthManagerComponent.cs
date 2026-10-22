using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Fsm;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

#pragma warning disable CS0414 // Field is assigned but its value is never used

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the <see cref="HealthManager"/> component of the entity.
internal class HealthManagerComponent : EntityComponent {
    private const BindingFlags HookBindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const float ControlledHealCorrectionDelaySeconds = 0.4f;

    /// <summary>
    /// How long a copy waits for the game that runs the creature to take its damage in before sending it again, in
    /// seconds: at first, and after <see cref="OwnDamageQuickResends"/> times.
    /// </summary>
    private const float OwnDamageResendTime = 1f, OwnDamageSlowResendTime = 5f;

    /// <summary>
    /// How many times a copy sends its damage again a second apart, before it waits longer between them. It goes on
    /// until the damage is taken in: a stretch in which nothing arrives can outlast all of the quick ones, and damage
    /// that is not sent again after it is lost.
    /// </summary>
    private const int OwnDamageQuickResends = 10;

    /// <summary>
    /// The number of the next copy made in this game. Each copy has its own, by which the game that runs the creature
    /// tells the damage of one visit of a player to the room from the next, which counts from nothing again.
    /// </summary>
    private static uint _nextCopyKey = (uint) new System.Random().Next();

    /// <summary>
    /// Host-client pair of health manager components of the entity.
    /// </summary>
    private readonly HostClientPair<HealthManager> _healthManager;

    /// <summary>
    /// The type of the entity, which says which of its FSMs hear of the hits of the player of their own game only.
    /// </summary>
    private readonly EntityType _type;

    /// <summary>
    /// Host death effects used to capture the emitted corpse snapshot.
    /// </summary>
    private readonly EnemyDeathEffects? _hostDeathEffects;

    /// <summary>
    /// Client death effects used to enable local physics on emitted corpses.
    /// </summary>
    private readonly EnemyDeathEffects? _clientDeathEffects;

    /// <summary>
    /// The host corpse emitted by the current death call.
    /// </summary>
    private GameObject? _hostCorpse;

    /// <summary>
    /// The client corpse emitted by the current death call.
    /// </summary>
    private GameObject? _clientCorpse;

    /// <summary>
    /// The frame in which <see cref="_clientCorpse"/> was emitted.
    /// </summary>
    private int _clientCorpseFrame = -1;

    /// <summary>
    /// Boolean indicating whether the health manager of the client entity is allowed to die.
    /// </summary>
    private bool _allowDeath;

    /// <summary>
    /// Whether a death of the client entity that was turned down has already been told about. A copy that has no
    /// health left asks to die again every frame, so without this one room fills the log with thousands of lines.
    /// </summary>
    private bool _toldRefusedDeath;

    /// <summary>
    /// Whether a blow of this game's player on the copy is being taken, and whether the copy asked to die of it, which
    /// it does when the blow leaves it no health (see <see cref="AfterOwnBlow"/>).
    /// </summary>
    private bool _takingOwnBlowOnCopy, _ownBlowAskedToDie;

    /// <summary>
    /// For a copy kept from hurting this game's player (see <see cref="KeepFromHurting"/>): when the blow of theirs
    /// that left it no health landed, and how far the copy's damage had come with it; otherwise a negative time.
    /// </summary>
    private float _ownKillAt = -1f;

    /// <inheritdoc cref="_ownKillAt" />
    private int _ownKillDamage;

    /// <summary>
    /// How long a copy is kept from hurting this game's player without an answer from the game that runs the creature,
    /// in seconds (see <see cref="KeepFromHurting"/>).
    /// </summary>
    private const float OwnKillAnswerTime = 5f;

    /// <summary>
    /// How far a copy is put to where its creature died before that is said in the log (see
    /// <see cref="PutCopyWhereItDied"/>): about the width of a player, below which nobody sees it.
    /// </summary>
    private const float LoggedDeathPlaceDistance = 1f;

    /// <summary>
    /// Whether the copy is a part of another entity's copy, which carries it where it goes: such a copy is told no
    /// place of its own, and is not put anywhere when it dies (see <see cref="PutCopyWhereItDied"/>).
    /// </summary>
    public bool CarriedByParent { get; init; }

    /// <summary>
    /// Whether a copy plays something that a state machine of this game's player told it (CoopHits.TellCopy), like the
    /// harpoon's blow that some creatures die of at once: a copy that asks to die of it is kept from hurting the player
    /// (see <see cref="KeepFromHurting"/>).
    /// </summary>
    internal static bool OwnSignalPlaying;

    /// <summary>
    /// The components whose copies are kept from hurting this game's player now (see <see cref="KeepFromHurting"/>).
    /// </summary>
    private static readonly HashSet<HealthManagerComponent> KeptFromHurting = new();

    /// <summary>
    /// The hook on the player's hit box touching something that may hurt them (see
    /// <see cref="HeroBoxOnCheckForDamage"/>). Made once, for every creature.
    /// </summary>
    private static Hook? _heroBoxDamageHook;

    /// <summary>
    /// MonoMod hook for HealthManager.Die, one for all creatures (see <see cref="OnAnyDie"/>), as the hooks on blows
    /// are. Each component used to put one of its own on and take it off as it went (see Entity.EntitiesByAnimator).
    /// </summary>
    private static Hook? _healthManagerDieHook;

    /// <summary>
    /// The last value for the "invincible" variable of the health manager.
    /// </summary>
    private bool _lastInvincible;

    /// <summary>
    /// The last synced HP value of the health manager.
    /// </summary>
    private int _lastHp;

    /// <summary>
    /// The last value for the "invincibleFromDirection" variable of the health manager.
    /// </summary>
    private int _lastInvincibleFromDirection;

    /// <summary>
    /// The current scene host epoch from our perspective.
    /// </summary>
    private uint _currentHealthEpoch;

    /// <summary>
    /// Whether another game ran the creature at some point, so that this game only runs it by taking it over.
    /// </summary>
    private bool _wasRunElsewhere;

    /// <summary>
    /// Whether a controlled client-side HP increase should be rolled back to the last authoritative value.
    /// </summary>
    private bool _hasPendingControlledHealCorrection;

    /// <summary>
    /// Unscaled time at which the next controlled heal correction should be applied.
    /// </summary>
    private float _pendingControlledHealCorrectionAt;

    /// <summary>
    /// The number of this copy (see <see cref="_nextCopyKey"/>).
    /// </summary>
    private readonly uint _copyKey = _nextCopyKey++;

    /// <summary>
    /// The health that the game that runs the creature last told, and the number of that telling in the current
    /// epoch, 0 before any (see <see cref="SendHealthState"/>).
    /// </summary>
    private int _toldHp;

    /// <inheritdoc cref="_toldHp" />
    private uint _toldNumber;

    /// <summary>
    /// Whether the creature was dead when its health was last told.
    /// </summary>
    private bool _toldDead;

    /// <summary>
    /// All the damage that the copy took from this game's player in the current epoch.
    /// </summary>
    private int _ownDamage;

    /// <summary>
    /// How much of <see cref="_ownDamage"/> was in the health last told.
    /// </summary>
    private int _ownDamageTaken;

    /// <summary>
    /// When the copy's damage was last sent with every blow in it that was not taken in yet.
    /// </summary>
    private float _ownDamageSentAt;

    /// <summary>
    /// How many times the copy's damage was sent again since it last went with every blow as one landed, or the game
    /// that runs the creature last told its health.
    /// </summary>
    private int _ownDamageResends;

    /// <summary>
    /// In the game that runs the creature: the damage of each other player's copy that is in its health, by player -
    /// the number of the copy, and all of its damage taken in.
    /// </summary>
    private readonly Dictionary<ushort, (uint copyKey, int damage)> _damageTaken = new();

    /// <summary>
    /// In the game that runs the creature: the frame in which it died a death of its own that it lives on after (see
    /// <see cref="HealthManager.hasSpecialDeath"/>), while it has not been able to be hit since, or -1. A summoned
    /// creature that the room puts away at full health in the moment it dies, to send it out again later, is one. The
    /// damage that copies tell of meanwhile was dealt before they heard of the death, as nothing can hit the creature
    /// then: taken off its health, it was missing from it as it came out again (see <see cref="TakeInOwnDamage"/>).
    /// </summary>
    private int _diedToComeBackAt = -1;

    /// <summary>
    /// In the game that runs the creature: the number of its last telling of its health in the current epoch.
    /// </summary>
    private uint _tellNumber;

    /// <summary>
    /// Whether the creature has had any health. Some are set up with none of their own and are alive at none, and a
    /// copy that is told it has none dies.
    /// </summary>
    private bool _hadHealth;

    /// <summary>
    /// Raised as damage of a player lands on a creature, as this game learns of it: a blow of this game's player on the
    /// creature or its copy, however it came - an attack, a tool, the tick of a tag they put on it - the damage of
    /// another player's copy that this game takes in, and a drop in the health that the game which runs the creature
    /// tells, which is how this game hears of the blows of that game's player, with what else hurt the creature there.
    /// With the creature in this game, and what struck the blow, if this game knows. For the outline of the player's
    /// own hero in a fight (FightOutline).
    /// </summary>
    internal static event Action<HealthManager, GameObject?>? DamageLanded;

    /// <summary>
    /// The copy that a blow just put back at the health last told, with the health the blow had left it. The log of
    /// damage (EnemyHealthCoop) says what a blow took from that, as its hook on blows may run around this one, which
    /// puts the health back before it looks (see <see cref="ForgetPutBack"/>, <see cref="HealthBeforePutBack"/>).
    /// </summary>
    private static (HealthManager? Copy, int Hp) _putBack;

    /// <summary>
    /// Raised as the copy in this game dies or lives again: whether it is dead now, and whether its death was seen here
    /// rather than found as this player walked into the room (see Entity.RecordCopyDeath).
    /// </summary>
    public event System.Action<bool, bool>? CopyDeathChanged;

    /// <summary>
    /// Whether the copy was dead as this game last looked (see <see cref="CopyDeathChanged"/>).
    /// </summary>
    private bool _copyWasDead;

    /// <summary>
    /// Whether the copy was killed here as this player walked into the room, for a creature that died before.
    /// </summary>
    private bool _copyKilledOnWalkIn;

    /// <summary>
    /// The component of each creature and copy in this game, by its health manager, for the hooks on blows and on what
    /// a blow that kills drops (see <see cref="OnBlow"/>).
    /// </summary>
    private static readonly Dictionary<HealthManager, HealthManagerComponent> ComponentOf = new();

    /// <summary>
    /// The health managers of this component's creature and copy as they were put in <see cref="ComponentOf"/>, to
    /// take them out again even once they are gone.
    /// </summary>
    private readonly HealthManager? _creatureHealthManager, _copyHealthManager;

    /// <summary>
    /// Hooks on the two ways a blow takes health that can end in a death with what a blow that kills drops,
    /// HealthManager.TakeDamage and ApplyExtraDamage(HitInstance), and on the queueing of that drop, DieDropFling.
    /// Made once, for every creature.
    /// </summary>
    private static Hook? _takeDamageHook, _extraDamageHook, _dieDropFlingHook;

    /// <summary>
    /// The blows of this game's player on the copy in the current epoch, as far as the game that runs the creature has
    /// not told that it took them in: how far the copy's damage had come before each and after it (see
    /// <see cref="_ownDamage"/>), and what it kills with. They go with the damage, for that game to kill the creature
    /// with the blow that took the last of its health, as it kills it with a blow of its own player (see
    /// <see cref="TakeInOwnDamage"/>).
    /// </summary>
    private readonly List<OwnBlow> _ownBlows = new();

    /// <summary>
    /// In the game that runs the creature: the tool damage of the blow that is killing it, kept as the game queues what
    /// the blow drops (see <see cref="HealthManagerOnDieDropFling"/>), for its death to tell the other games (see
    /// <see cref="HealthManagerOnDie"/>); otherwise none.
    /// </summary>
    private ToolDamageFlags _killingToolDamage;

    /// <summary>
    /// Whether what the copy's death drops for the blow that killed the creature is being queued, which the hook on
    /// DieDropFling lets through (see <see cref="HealthManagerOnDieDropFling"/>).
    /// </summary>
    private bool _queueingDeathDrop;

    /// <summary>
    /// For the room's own creature taken over with blows of this game's player that killed it: the frame after which
    /// it dies of them, or -1 for none, and the blow among them that killed it, if one was kept (see
    /// <see cref="InitializeHost"/>).
    /// </summary>
    private int _killAfterFrame = -1;

    /// <inheritdoc cref="_killAfterFrame" />
    private BlowDeath? _killAfterFrameBlow;

    /// <summary>
    /// In the game that runs the creature: whether a blow of this game's player on it is being taken, which makes a
    /// death that it brings theirs (see <see cref="HealthManagerOnDie"/>).
    /// </summary>
    private bool _takingOwnPlayerBlow;

    /// <summary>
    /// In the game that runs the creature: who struck the blow that SSMP is killing it of, just before the death (see
    /// <see cref="DieOfTakenInBlow"/>), for the death to take.
    /// </summary>
    private Killer? _pendingKiller;

    /// <summary>
    /// In the game that runs the creature: who struck the blow of its last death, told with the death and with the one
    /// that its FSM plays after it (see <see cref="WriteDeathOf"/>).
    /// </summary>
    private Killer _killer;

    /// <summary>
    /// How many of the newest blows of this game's player that the game that runs the creature has not taken in yet go
    /// with the damage at most as a blow lands: while it answers, that is all of them, and the few that went missing
    /// with lost messages. Past that, all of them go only when the damage is sent again (see
    /// <see cref="OwnDamageResendTime"/>), and damage that kills the creature before the first of those that came
    /// waits for it (see <see cref="TakeInOwnDamage"/>): sent with every blow, each message was longer than the last
    /// for as long as nothing came back, and a long silence queued hundreds of kilobytes.
    /// </summary>
    private const int BlowsSentWithABlow = 8;

    /// <summary>
    /// Hooks on what puts tag damage on a creature - a thing that leaves it on what it touches (TagDamager), the
    /// imbuement of the nail (DamageEnemies.DoEnemyDamageNailImbuement) - on the stack that all of it goes on
    /// (TagDamageTaker.AddDamageTagToStack) and on a tick of it (DamageTag.OnHit). Made once, for every creature.
    /// </summary>
    private static Hook? _tagDamagerHook, _imbuementHook, _addTagHook, _tagTickHook;

    /// <summary>
    /// What puts tag damage on something now, for the stack to be known as this game's player's or not (see
    /// <see cref="TagDamageTakerOnAddTag"/>): a thing that leaves it on what it touches, or else whether the blow that
    /// brings it is theirs; neither for anything else.
    /// </summary>
    private static TagDamager? _taggingDamager;

    /// <inheritdoc cref="_taggingDamager" />
    private static bool? _taggingBlowIsOwn;

    /// <summary>
    /// What a tick of tag damage hits now, and its tag (see <see cref="DamageTagOnHit"/>).
    /// </summary>
    private static HealthManager? _tickedHealthManager;

    /// <inheritdoc cref="_tickedHealthManager" />
    private static DamageTag? _tickingTag;

    /// <summary>
    /// The tags on the creature and its copy in this game whose stacks this game's player put on them, and nothing else
    /// since, by the health manager (see <see cref="TagDamageTakerOnAddTag"/>).
    /// </summary>
    private HashSet<(HealthManager, DamageTag)>? _ownTags;

    public HealthManagerComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<HealthManager> healthManager,
        EntityType type
    ) : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;
        _type = type;

        _hostDeathEffects = gameObject.Host.GetComponent<EnemyDeathEffects>();
        if (_hostDeathEffects != null) {
            _hostDeathEffects.CorpseEmitted += OnHostCorpseEmitted;
        }

        _clientDeathEffects = gameObject.Client.GetComponent<EnemyDeathEffects>();
        if (_clientDeathEffects != null) {
            _clientDeathEffects.CorpseEmitted += OnClientCorpseEmitted;
        }

        _lastInvincible = healthManager.Host.IsInvincible;
        _lastHp = healthManager.Host.hp;
        _toldHp = _lastHp;
        _hadHealth = _lastHp > 0;
        _lastInvincibleFromDirection = healthManager.Host.InvincibleFromDirection;

        HookDeaths();
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;

        HookBlows();
        if (healthManager.Host != null) {
            _creatureHealthManager = healthManager.Host;
            ComponentOf[healthManager.Host] = this;
        }

        if (healthManager.Client != null) {
            _copyHealthManager = healthManager.Client;
            ComponentOf[healthManager.Client] = this;
        }
    }

    /// <summary>
    /// Makes the hook on deaths, once for all creatures.
    /// </summary>
    private static void HookDeaths() {
        if (_healthManagerDieHook != null) {
            return;
        }

        // Get the largest overload of Die from HealthManager, because that is the method that is getting called by
        // all other overloads regardless
        var dieMethod = typeof(HealthManager).GetMethod(
            nameof(HealthManager.Die),
            HookBindingFlags,
            Type.DefaultBinder,
            [
                typeof(float?), typeof(AttackTypes), typeof(NailElements), typeof(GameObject),
                typeof(bool), typeof(float), typeof(bool), typeof(bool)
            ],
            null
        );

        if (dieMethod == null) {
            throw new MissingMethodException(
                typeof(HealthManager).FullName,
                $"{nameof(HealthManager.Die)}(float?, {nameof(AttackTypes)}, bool)"
            );
        }

        _healthManagerDieHook = new Hook(dieMethod, OnAnyDie);
    }

    /// <summary>
    /// Makes the hooks on blows and on what a blow that kills drops, once for all creatures.
    /// </summary>
    private static void HookBlows() {
        if (_takeDamageHook != null) {
            return;
        }

        var takeDamage = typeof(HealthManager).GetMethod(
            "TakeDamage", HookBindingFlags, Type.DefaultBinder, [typeof(HitInstance)], null
        );
        var extraDamage = typeof(HealthManager).GetMethod(
            nameof(HealthManager.ApplyExtraDamage), HookBindingFlags, Type.DefaultBinder, [typeof(HitInstance)], null
        );
        var dieDropFling = typeof(HealthManager).GetMethod(
            "DieDropFling", HookBindingFlags, Type.DefaultBinder, [typeof(ToolDamageFlags), typeof(bool)], null
        );
        if (takeDamage == null || extraDamage == null || dieDropFling == null) {
            throw new MissingMethodException(
                typeof(HealthManager).FullName,
                $"TakeDamage({nameof(HitInstance)}), {nameof(HealthManager.ApplyExtraDamage)}({nameof(HitInstance)}) " +
                $"or DieDropFling({nameof(ToolDamageFlags)}, bool)"
            );
        }

        _takeDamageHook = new Hook(takeDamage, HealthManagerOnTakeDamage);
        _extraDamageHook = new Hook(extraDamage, HealthManagerOnExtraDamage);
        _dieDropFlingHook = new Hook(dieDropFling, HealthManagerOnDieDropFling);

        var checkForDamage = typeof(HeroBox).GetMethod(
            "CheckForDamage", HookBindingFlags, Type.DefaultBinder, [typeof(GameObject)], null
        );
        if (checkForDamage == null) {
            Logger.Error("Could not find HeroBox.CheckForDamage; a copy the player killed can hurt them until it dies");
        } else {
            _heroBoxDamageHook = new Hook(checkForDamage, HeroBoxOnCheckForDamage);
        }

        // Without these, a tick of tag damage is judged by its kind of tag, as the game marks it
        var tagDamagerEnter = typeof(TagDamager).GetMethod(
            "OnTriggerEnter2D", HookBindingFlags, Type.DefaultBinder, [typeof(Collider2D)], null
        );
        var imbuement = typeof(DamageEnemies).GetMethod(
            "DoEnemyDamageNailImbuement", HookBindingFlags, Type.DefaultBinder,
            [typeof(HealthManager), typeof(HitInstance)], null
        );
        var addTag = typeof(TagDamageTaker).GetMethod(
            nameof(TagDamageTaker.AddDamageTagToStack), HookBindingFlags, Type.DefaultBinder,
            [typeof(DamageTag), typeof(int)], null
        );
        var tagTick = typeof(DamageTag).GetMethod(
            nameof(DamageTag.OnHit), HookBindingFlags, Type.DefaultBinder, [typeof(ITagDamageTakerOwner)], null
        );
        if (tagDamagerEnter == null || imbuement == null || addTag == null || tagTick == null) {
            Logger.Error("Could not find the methods that put tag damage on creatures; its ticks go by their tag");
            return;
        }

        _tagDamagerHook = new Hook(tagDamagerEnter, TagDamagerOnTriggerEnter2D);
        _imbuementHook = new Hook(imbuement, DamageEnemiesOnNailImbuement);
        _addTagHook = new Hook(addTag, TagDamageTakerOnAddTag);
        _tagTickHook = new Hook(tagTick, DamageTagOnHit);
    }

    /// <summary>
    /// A thing that leaves tag damage on what it touches touching something (TagDamager.OnTriggerEnter2D), which puts
    /// its tag on it if it is a creature (see <see cref="TagDamageTakerOnAddTag"/>).
    /// </summary>
    private static void TagDamagerOnTriggerEnter2D(
        Action<TagDamager, Collider2D> orig,
        TagDamager self,
        Collider2D collision
    ) {
        var taggingDamager = _taggingDamager;
        var taggingBlowIsOwn = _taggingBlowIsOwn;
        _taggingDamager = self;
        _taggingBlowIsOwn = null;
        try {
            orig(self, collision);
        } finally {
            _taggingDamager = taggingDamager;
            _taggingBlowIsOwn = taggingBlowIsOwn;
        }
    }

    /// <summary>
    /// The imbuement of the nail putting its tag on a creature after a blow of it
    /// (DamageEnemies.DoEnemyDamageNailImbuement), which the game does once the blow itself is over: the tag is of the
    /// player whose blow it was.
    /// </summary>
    private static void DamageEnemiesOnNailImbuement(
        Action<DamageEnemies, HealthManager, HitInstance> orig,
        DamageEnemies self,
        HealthManager healthManager,
        HitInstance hitInstance
    ) {
        var taggingDamager = _taggingDamager;
        var taggingBlowIsOwn = _taggingBlowIsOwn;
        _taggingDamager = null;
        _taggingBlowIsOwn = IsOwnPlayerBlow(hitInstance);
        try {
            orig(self, healthManager, hitInstance);
        } finally {
            _taggingDamager = taggingDamager;
            _taggingBlowIsOwn = taggingBlowIsOwn;
        }
    }

    /// <summary>
    /// A tag of damage put on a creature or a copy, or on it again (TagDamageTaker.AddDamageTagToStack). The ticks of
    /// its stack are this game's player's if they put it on while it was off (see <see cref="IsOwnTagDamager"/> and
    /// <see cref="OnBlow"/>) and nothing else has put it on since; once something else has, they are not, until it
    /// wears off, however often the player puts it on again. A tag has one stack whoever puts it on, which ticks no
    /// more for two of them: the dust of the player's bomb on a creature in the dust of a room's barnacle ticks once,
    /// and the game that runs the creature takes those ticks from its own barnacle already. Counted as the player's
    /// from the bomb on, they were taken twice; the bomb's own ticks are lost instead while the barnacle keeps the dust
    /// on.
    /// </summary>
    private static void TagDamageTakerOnAddTag(
        Action<TagDamageTaker, DamageTag, int> orig,
        TagDamageTaker self,
        DamageTag damageTag,
        int hitAmountOverride
    ) {
        var wasOn = damageTag != null && self.taggedDamage.ContainsKey(damageTag);
        orig(self, damageTag!, hitAmountOverride);

        if (damageTag == null || self.owner is not HealthManager healthManager ||
            !ComponentOf.TryGetValue(healthManager, out var component)) {
            return;
        }

        var own = _taggingDamager != null ? IsOwnTagDamager(_taggingDamager) : _taggingBlowIsOwn == true;
        component._ownTags ??= [];
        if (!own) {
            component._ownTags.Remove((healthManager, damageTag));
        } else if (!wasOn) {
            component._ownTags.Add((healthManager, damageTag));
        }
    }

    /// <summary>
    /// A tick of tag damage on something (DamageTag.OnHit), which takes a creature's health through
    /// ApplyExtraDamage (see <see cref="OnBlow"/>).
    /// </summary>
    private static void DamageTagOnHit(
        Action<DamageTag, ITagDamageTakerOwner> orig,
        DamageTag self,
        ITagDamageTakerOwner owner
    ) {
        var tickedHealthManager = _tickedHealthManager;
        var tickingTag = _tickingTag;
        _tickedHealthManager = owner as HealthManager;
        _tickingTag = self;
        try {
            orig(self, owner);
        } finally {
            _tickedHealthManager = tickedHealthManager;
            _tickingTag = tickingTag;
        }
    }

    /// <summary>
    /// Whether a thing that leaves tag damage on what it touches is this game's player's: marked as one of their tools,
    /// on their hero, or spawned. The census of the game (10-11) has only the things that the player's tools and
    /// skills spawn - the dust of their bombs, the poison trail of their brew, the poison that bursts from their
    /// health - and the dust of the barnacles of a few rooms, which is the room's. Those of other players are copies,
    /// which leave none (see ToolCopies and FleaBrew).
    /// </summary>
    /// <param name="damager">The thing.</param>
    private static bool IsOwnTagDamager(TagDamager damager) {
        var gameObject = damager.gameObject;
        if (RemoteAttackComponent.IsRemoteAttack(gameObject)) {
            return false;
        }

        if (LocalToolComponent.IsLocalTool(gameObject) || gameObject.GetComponentInParent<HeroController>() != null) {
            return true;
        }

        for (var transform = gameObject.transform; transform != null; transform = transform.parent) {
            if (ObjectPool.IsSpawned(transform.gameObject)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A blow of HealthManager.TakeDamage (see <see cref="OnBlow"/>).
    /// </summary>
    private static void HealthManagerOnTakeDamage(
        Action<HealthManager, HitInstance> orig,
        HealthManager self,
        HitInstance hitInstance
    ) {
        OnBlow(orig, self, hitInstance, fromTakeDamage: true);
    }

    /// <summary>
    /// A blow of HealthManager.ApplyExtraDamage(HitInstance) (see <see cref="OnBlow"/>).
    /// </summary>
    private static void HealthManagerOnExtraDamage(
        Action<HealthManager, HitInstance> orig,
        HealthManager self,
        HitInstance hitInstance
    ) {
        OnBlow(orig, self, hitInstance, fromTakeDamage: false);
    }

    /// <summary>
    /// A blow on a creature. Whether a blow kills is decided only in the game that runs the creature, which kills it
    /// with the blow the way the game kills with a blow of its own: the body thrown the way the blow went, the death of
    /// the attack's kind, the item that a tool's damage drops for a wish in every game, each for its own player, and
    /// the moment held still only for the player who struck it (see <see cref="HealthManagerOnDie"/>).
    /// - A blow of this game's player on a copy is counted and sent with what it kills with (see
    ///   <see cref="BlowDeathOf"/>), and the copy put back at the health last told (see <see cref="AfterOwnBlow"/>).
    ///   Anything else that hits the copy, such as a hazard of the room or another creature's attack, hits the creature
    ///   in the game that runs it as well, and counted here it took the health twice: it shows here, and takes none.
    /// - A blow on the creature that this game runs runs as it is, and a death it brings is this game's player's if
    ///   the blow is theirs. One that kills it tells its death the tool damage (see
    ///   <see cref="HealthManagerOnDieDropFling"/>).
    /// A tick of tag damage - the zap or poison that a tool or the nail leaves on a creature, the dust of a bomb - is a
    /// blow of whoever put the tag on (see <see cref="TagDamageTakerOnAddTag"/>). The game marks it as the player's by
    /// its kind of tag alone: a zap as nobody's, so that the zap of this game's player on a copy was lost, and the dust
    /// of a room's barnacles as the player's, so that it was counted on a copy on top of the game that runs the
    /// creature. A tag that a blow puts on is the blow's.
    /// </summary>
    /// <param name="orig">The hooked method.</param>
    /// <param name="self">The health manager that the blow hit.</param>
    /// <param name="hitInstance">The blow.</param>
    /// <param name="fromTakeDamage">Whether the blow is one of TakeDamage, rather than of ApplyExtraDamage, which kill
    /// differently (see <see cref="BlowDeathOf"/>).</param>
    private static void OnBlow(
        Action<HealthManager, HitInstance> orig,
        HealthManager self,
        HitInstance hitInstance,
        bool fromTakeDamage
    ) {
        if (!ComponentOf.TryGetValue(self, out var component)) {
            orig(self, hitInstance);
            return;
        }

        var own = !fromTakeDamage && _tickedHealthManager == self && _tickingTag != null
            ? component._ownTags?.Contains((self, _tickingTag)) == true
            : IsOwnPlayerBlow(hitInstance);

        var taggingDamager = _taggingDamager;
        var taggingBlowIsOwn = _taggingBlowIsOwn;
        _taggingDamager = null;
        _taggingBlowIsOwn = own;
        try {
            component.TakeBlow(orig, self, hitInstance, fromTakeDamage, own);
        } finally {
            _taggingDamager = taggingDamager;
            _taggingBlowIsOwn = taggingBlowIsOwn;
        }

        if (own) {
            DamageLanded?.Invoke(self, hitInstance.Source);
        }
    }

    /// <summary>
    /// A blow on the creature or its copy in this game (see <see cref="OnBlow"/>).
    /// </summary>
    /// <param name="orig">The hooked method.</param>
    /// <param name="self">The health manager that the blow hit.</param>
    /// <param name="hitInstance">The blow.</param>
    /// <param name="fromTakeDamage">Whether the blow is one of TakeDamage.</param>
    /// <param name="own">Whether the blow is one of this game's player.</param>
    private void TakeBlow(
        Action<HealthManager, HitInstance> orig,
        HealthManager self,
        HitInstance hitInstance,
        bool fromTakeDamage,
        bool own
    ) {
        if (self != _healthManager.Client) {
            // The tool damage of a blow that killed the creature goes with the death the blow brings, and no further:
            // a blow whose death did not come is not left to a later one. A blow on one with no health left since
            // another blow took the last of it is not the one that killed it: a death that it brings, like one that
            // the creature's own death goes on to, is of whoever struck that one
            var takingOwnPlayerBlow = _takingOwnPlayerBlow;
            _takingOwnPlayerBlow = own && (self.hp > 0 || _killer.Kind == KillerKind.None);
            try {
                orig(self, hitInstance);
            } finally {
                _takingOwnPlayerBlow = takingOwnPlayerBlow;
                _killingToolDamage = ToolDamageFlags.None;
            }

            return;
        }

        if (!IsControlled) {
            orig(self, hitInstance);
            return;
        }

        // A change of the health before the blow that was not counted yet is not the blow's
        CountOwnBlows();
        if (!own) {
            try {
                orig(self, hitInstance);
            } finally {
                PutBackToldHealth();
            }

            return;
        }

        var death = BlowDeathOf(self, hitInstance, fromTakeDamage);

        // Only a blow that kills the creature in the game counts as leaving it no health: one that takes the last of
        // its health, or any blow on one that dies at none. A creature that lives on at none after a death of its own
        // (hasSpecialDeath) is only hit again by the next blow, and goes on as it was, hurting as it did
        var blowCanKill = self.hp > 0 || !self.hasSpecialDeath;
        var (takingOwnBlow, askedToDie) = (_takingOwnBlowOnCopy, _ownBlowAskedToDie);
        (_takingOwnBlowOnCopy, _ownBlowAskedToDie) = (true, false);
        try {
            orig(self, hitInstance);
        } finally {
            var leftNoHealth = _ownBlowAskedToDie && blowCanKill;
            (_takingOwnBlowOnCopy, _ownBlowAskedToDie) = (takingOwnBlow, askedToDie);
            AfterOwnBlow(death, leftNoHealth);
        }
    }

    /// <summary>
    /// Whether a blow is one of this game's player: of their attacks, tools and what their skills leave in the world,
    /// which the game marks as the player's (HitInstance.IsHeroDamage, as SSMP takes the player's hits), or of a thing
    /// that they threw or set. Never of a copy of another player's attack, which the game marks as the player's too.
    /// </summary>
    /// <param name="hitInstance">The blow.</param>
    private static bool IsOwnPlayerBlow(HitInstance hitInstance) {
        return (hitInstance.IsHeroDamage || LocalToolComponent.IsLocalTool(hitInstance.Source)) &&
               !RemoteAttackComponent.IsRemoteAttack(hitInstance.Source);
    }

    /// <summary>
    /// What a blow of this game's player on the copy kills the creature with, worked out the way the game does at a
    /// blow that kills (see <see cref="BlowDeath"/>), here, where the blow struck. A blow of TakeDamage throws the body
    /// the way the blow went, harder for a critical hit, and the player's downward strike throws it the way they face
    /// rather than into the ground; a blow of ApplyExtraDamage throws it nowhere, keeps no way it came from, breaks no
    /// items, and can always kill.
    /// </summary>
    /// <param name="copy">The copy that the blow struck.</param>
    /// <param name="hit">The blow.</param>
    /// <param name="fromTakeDamage">Whether the blow is one of TakeDamage, rather than of ApplyExtraDamage.</param>
    private static BlowDeath BlowDeathOf(HealthManager copy, HitInstance hit, bool fromTakeDamage) {
        if (!fromTakeDamage) {
            return new BlowDeath(
                null, 0f, -1, hit.AttackType, hit.NailElement, hit.IgnoreInvulnerable, hit.ToolDamageFlags, false,
                false
            );
        }

        // As TakeDamage has the blow by then
        if (hit.CriticalHit) {
            hit.MagnitudeMultiplier *= GlobalSettings.Gameplay.WandererCritMagnitudeMult;
        }

        var transform = copy.transform;
        var direction = hit.GetActualDirection(transform, HitInstance.TargetType.Corpse);
        var breaksItems = false;
        if (hit.Source != null) {
            var root = hit.Source.transform.root;
            if (root.CompareTag("Player") && !hit.UseCorpseDirection &&
                global::Extensions.IsWithinTolerance(direction, Mathf.Epsilon, 270f)) {
                direction = root.lossyScale.x < 0f ? 0f : 180f;
            }

            breaksItems = hit.Source.GetComponent<BreakItemsOnContact>() != null;
        }

        return new BlowDeath(
            direction,
            hit.GetMagnitudeMultForType(HitInstance.TargetType.Corpse),
            DirectionUtils.GetCardinalDirection(hit.GetActualDirection(transform, HitInstance.TargetType.Regular)),
            hit.AttackType,
            hit.NailElement,
            hit.IgnoreInvulnerable,
            hit.ToolDamageFlags,
            breaksItems,
            hit.NonLethal
        );
    }

    /// <summary>
    /// Just after a blow of this game's player on the copy: counted and sent with what it kills with, and the copy back
    /// at the health last told. One that left the copy no health keeps it from hurting the player until the game that
    /// runs the creature answers it (see <see cref="KeepFromHurting"/>).
    /// </summary>
    /// <param name="death">What the blow kills with.</param>
    /// <param name="leftNoHealth">Whether the copy asked to die of the blow.</param>
    private void AfterOwnBlow(BlowDeath death, bool leftNoHealth) {
        CountOwnBlows(death: death);
        PutBackToldHealth();
        if (leftNoHealth) {
            KeepFromHurting(_ownDamage);
        }
    }

    /// <summary>
    /// Keeps the copy from hurting the player of this game after a blow of theirs left it no health, until the game
    /// that runs the creature answers the blow. In the game, a creature dies of such a blow at once, and nothing of it
    /// touches the player after; the copy dies only once that game has taken the blow in and told the death, and
    /// touched in between, it hurt them. The player's harpoon puts them just above what it struck, where the hurt broke
    /// off the bounce that follows, and they fell (USER 10-14, "哪怕飞针到了也会有碰撞伤", "受击打断了飞针到怪之后的高度攀升").
    /// Only what belongs to the copy is kept off - its body and the parts under it (see
    /// <see cref="IsKeptFromHurting"/>); what it threw before, and what its death puts out, hurt as they do. It can
    /// hurt them again as soon as that game tells its health with the blow in it and the creature alive, as its death
    /// is played here, as the room changes hands, or after <see cref="OwnKillAnswerTime"/> without an answer. A copy
    /// that asks to die of something that a state machine of the player told it, like the harpoon's blow that some
    /// creatures die of at once, is kept from hurting them the same way (see <see cref="OwnSignalPlaying"/>).
    /// </summary>
    /// <param name="damage">How far the copy's damage has to come in the health told for the creature to have lived
    /// through it, or <see cref="int.MaxValue"/> for something that no damage goes with.</param>
    private void KeepFromHurting(int damage) {
        _ownKillAt = Time.unscaledTime;
        _ownKillDamage = damage;
        KeptFromHurting.Add(this);
    }

    /// <summary>
    /// Lets the copy hurt the player of this game again (see <see cref="KeepFromHurting"/>).
    /// </summary>
    private void LetHurt() {
        _ownKillAt = -1f;
        KeptFromHurting.Remove(this);
    }

    /// <summary>
    /// Whether a blow of this game's player left the copy no health, and the game that runs the creature has not
    /// answered it yet (see <see cref="KeepFromHurting"/>): the creature dies of it, and the scene host's game tells it
    /// of the blow along with its death (see <see cref="ApplyHp"/>).
    /// </summary>
    /// <param name="copy">The health manager of the copy.</param>
    internal static bool DiesOfOwnBlow(HealthManager copy) {
        return ComponentOf.TryGetValue(copy, out var component) && KeptFromHurting.Contains(component);
    }

    /// <summary>
    /// The player's hit box touching something that may hurt them, as it starts to and for as long as it does
    /// (HeroBox.CheckForDamage): nothing of a copy kept from hurting them hurts them (see
    /// <see cref="KeepFromHurting"/>).
    /// </summary>
    private static void HeroBoxOnCheckForDamage(Action<HeroBox, GameObject> orig, HeroBox self, GameObject other) {
        if (KeptFromHurting.Count == 0 || !IsKeptFromHurting(other)) {
            orig(self, other);
        }
    }

    /// <summary>
    /// Whether something that touches the player belongs to a copy kept from hurting them: the copy is the creature
    /// that the thing's DamageHero found above it as it woke up, or else the one above it now.
    /// </summary>
    /// <param name="source">What touches the player.</param>
    private static bool IsKeptFromHurting(GameObject source) {
        if (source == null) {
            return false;
        }

        var healthManager = source.TryGetComponent<DamageHero>(out var damageHero) ? damageHero.healthManager : null;
        if (healthManager == null) {
            healthManager = source.GetComponentInParent<HealthManager>();
        }

        return healthManager != null && ComponentOf.TryGetValue(healthManager, out var component) &&
               healthManager == component._healthManager.Client && KeptFromHurting.Contains(component) &&
               Time.unscaledTime - component._ownKillAt < OwnKillAnswerTime;
    }

    /// <summary>
    /// Puts the copy back at the health last told, unless it is dead.
    /// </summary>
    private void PutBackToldHealth() {
        if (_healthManager.Client != null && !_healthManager.Client.GetIsDead()) {
            _putBack = (_healthManager.Client, _healthManager.Client.hp);
            ApplyHp(_toldHp, triggerHostDeath: false);
        }
    }

    /// <summary>
    /// Forgets which copy a blow last put back at the health last told, before a blow that the log of damage looks at.
    /// </summary>
    internal static void ForgetPutBack() {
        _putBack = (null, 0);
    }

    /// <summary>
    /// The health that a blow left a creature: on a copy that the blow put back at the health last told since
    /// <see cref="ForgetPutBack"/>, the health before that; on anything else, its health.
    /// </summary>
    /// <param name="creature">The creature.</param>
    internal static int HealthBeforePutBack(HealthManager creature) {
        return _putBack.Copy == creature ? _putBack.Hp : creature.hp;
    }

    /// <summary>
    /// Counts what the copy's health dropped by since it was last looked at as blows of this game's player. All of the
    /// damage so far is what is sent, so that what went missing on the way comes with the next.
    /// </summary>
    /// <param name="send">Whether to send the damage so far to the game that runs the creature.</param>
    /// <param name="death">For a drop that is one blow: what the blow kills with (see <see cref="_ownBlows"/>).</param>
    /// <returns>Whether the health had dropped.</returns>
    private bool CountOwnBlows(bool send = true, BlowDeath? death = null) {
        var client = _healthManager.Client;
        if (client == null || client.hp >= _lastHp) {
            return false;
        }

        var damageBefore = _ownDamage;
        _ownDamage += _lastHp - client.hp;
        _lastHp = client.hp;
        if (death is { } blow) {
            _ownBlows.Add(new OwnBlow(damageBefore, _ownDamage, blow));
        }

        if (send) {
            SendOwnDamage(resend: false);
        }

        return true;
    }

    /// <summary>
    /// What a blow that kills drops being queued (DieDropFling), which the game does only for a blow that kills, just
    /// before the death.
    /// - On the creature that this game runs: the blow's tool damage is kept for the death to tell the other games
    ///   (see <see cref="HealthManagerOnDie"/>). A death that came of no blow, such as one that an FSM brings, tells
    ///   none, as it drops none here.
    /// - A copy queues nothing for a blow on it, as whether the blow kills is not decided in its game. It queues what
    ///   the blow drops that the game that runs the creature told killed it, just before it dies (see
    ///   <see cref="QueueDeathDrop"/>).
    /// </summary>
    private static void HealthManagerOnDieDropFling(
        Action<HealthManager, ToolDamageFlags, bool> orig,
        HealthManager self,
        ToolDamageFlags toolDamage,
        bool breaksItems
    ) {
        if (ComponentOf.TryGetValue(self, out var component)) {
            if (self != component._healthManager.Client) {
                component._killingToolDamage = breaksItems ? ToolDamageFlags.None : toolDamage;
            } else if (!component._queueingDeathDrop) {
                return;
            }
        }

        orig(self, toolDamage, breaksItems);
    }

    /// <summary>
    /// Queues on the copy, just before it dies, what the blow that killed the creature drops, the way the game queues
    /// it for a blow of its own that kills: here for the player of this game. The copy dies all the same if that fails.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="killingToolDamage">The tool damage of the blow that killed the creature.</param>
    private void QueueDeathDrop(HealthManager copy, ToolDamageFlags killingToolDamage) {
        if (killingToolDamage == ToolDamageFlags.None) {
            return;
        }

        _queueingDeathDrop = true;
        try {
            copy.DieDropFling(killingToolDamage, false);
        } catch (Exception e) {
            Logger.Error($"Could not queue what the blow that killed '{copy.name}' drops:\n{e}");
        } finally {
            _queueingDeathDrop = false;
        }
    }

    /// <summary>
    /// Kills the creature that this game runs of a blow that it took in as health, from a copy in another game or from
    /// the copy here before this game took the room over, the way the game kills it with a blow of its own
    /// (TakeDamage, ApplyExtraDamage): it keeps the way the blow came from and its kind, for a death that its FSM plays
    /// (see <see cref="KeepLastAttack"/>); what the blow drops is queued, which the death tells the other games (see
    /// <see cref="HealthManagerOnDieDropFling"/>); and it dies of the blow, its body thrown the way the blow went, by
    /// the death of the attack's kind, with the moment held still only for the player who struck it (see
    /// <see cref="HealthManagerOnDie"/>). It used to die of no particular attack, thrown nowhere, as it still does of a
    /// blow that was not kept. The creature dies all the same if the queueing fails.
    /// </summary>
    /// <param name="creature">The room's own creature.</param>
    /// <param name="blow">What the blow that kills it kills with, if it was kept.</param>
    /// <param name="killer">Who struck the blow.</param>
    private void DieOfTakenInBlow(HealthManager creature, BlowDeath? blow, Killer killer) {
        _pendingKiller = killer;
        try {
            if (blow is not { } death) {
                creature.Die(null, AttackTypes.Generic, true);
                return;
            }

            KeepLastAttack(creature, death);
            if (death.ToolDamage != ToolDamageFlags.None) {
                try {
                    creature.DieDropFling(death.ToolDamage, death.BreaksItems);
                } catch (Exception e) {
                    Logger.Error($"Could not queue what the blow that killed '{creature.name}' drops:\n{e}");
                }
            }

            creature.Die(
                death.Direction, death.AttackType, death.NailElement, null, death.IgnoreInvulnerable, death.Magnitude,
                false, death.BreaksItems
            );
        } finally {
            _pendingKiller = null;
        }
    }

    /// <summary>
    /// Has a creature keep the way a blow came from and its kind, as TakeDamage has it keep those of every blow it
    /// takes, for a death that its FSM plays (HealthManager.GetAttackDirection, which throws the body, and
    /// GetLastAttackType, by which one blown up shows it). A blow of ApplyExtraDamage keeps none.
    /// </summary>
    /// <param name="healthManager">The creature or its copy.</param>
    /// <param name="death">What the blow kills with.</param>
    private static void KeepLastAttack(HealthManager healthManager, BlowDeath death) {
        if (death.LastAttackDirection < 0) {
            return;
        }

        healthManager.directionOfLastAttack = death.LastAttackDirection;
        healthManager.lastAttackType = death.AttackType;
    }

    /// <summary>
    /// Whether the player of this game struck the blow of a death, or no player did: only then does the death hold the
    /// moment still here (see <see cref="HoldMoment"/>).
    /// </summary>
    /// <param name="killer">Who struck the blow.</param>
    /// <param name="runsTheCreature">Whether this game runs the creature, rather than having a copy of it.</param>
    private bool StruckHere(Killer killer, bool runsTheCreature) {
        return killer.Kind switch {
            KillerKind.Host => runsTheCreature,
            KillerKind.Copy => !runsTheCreature && killer.CopyKey == _copyKey,
            _ => true
        };
    }

    /// <summary>
    /// Has a death of the creature or its copy in this game hold the moment still, as a death does for the player who
    /// struck its blow, or not (EnemyDeathEffects.SkipKillFreeze, which the camera's shake at the death reads). The
    /// game holds it at every kill but those of a few kinds of attack, and with the death of one game played in all of
    /// them, every player's game held it at a kill of the player of the game that runs the creature, and none at one
    /// of another player. Now only the game of the player who struck the blow holds it, and every game at a death of
    /// no player's blow; the death itself still lets it go for those kinds of attack (HealthManager.Die). Set at every
    /// death, as nothing in the game sets it back.
    /// </summary>
    /// <param name="healthManager">The creature or its copy.</param>
    /// <param name="hold">Whether to hold the moment still.</param>
    private static void HoldMoment(HealthManager healthManager, bool hold) {
        var deathEffects = healthManager.enemyDeathEffects;
        if (deathEffects != null) {
            deathEffects.SkipKillFreeze = !hold;
        }
    }

    /// <summary>
    /// Writes what a death of a creature in the game that runs it tells the copies besides how it died: the way the
    /// last blow came from and its kind, which a death that the copy's FSM plays throws its body by (see
    /// <see cref="KeepLastAttack"/>), and who struck the blow of the death (see <see cref="StruckHere"/>).
    /// </summary>
    /// <param name="creature">The room's own creature.</param>
    /// <param name="killer">Who struck the blow of its death.</param>
    /// <param name="data">The data that it goes with.</param>
    private static void WriteDeath(HealthManager creature, Killer killer, EntityNetworkData data) {
        data.Packet.Write((byte) creature.directionOfLastAttack);
        data.Packet.Write((byte) creature.lastAttackType);
        data.Packet.Write((byte) killer.Kind);
        if (killer.Kind == KillerKind.Copy) {
            data.Packet.Write(killer.CopyKey);
        }
    }

    /// <summary>
    /// Writes, for the replay of a death that the FSM of a creature in this game plays, what the death tells the
    /// copies besides how it died (see <see cref="WriteDeath"/>). Such a death throws the body the way the last blow
    /// came from and holds the moment still (EnemyDeathEffectsRegular.SimulateDeath), which each game took from its own
    /// creature, so that the body of the copy of a creature killed by another player was thrown the way that this
    /// game's player last struck it, and every game held the moment. Its blow is that of the creature's last death, if
    /// it has no health left; otherwise no player's.
    /// </summary>
    /// <param name="creature">The creature whose death its FSM plays, or null if it has no health manager.</param>
    /// <param name="data">The data of the replay.</param>
    internal static void WriteDeathOf(HealthManager? creature, EntityNetworkData data) {
        // The same length whatever it is, as it is read whatever it turns out to be
        if (creature == null) {
            data.Packet.Write((byte) 0);
            data.Packet.Write((byte) 0);
            data.Packet.Write((byte) KillerKind.None);
            return;
        }

        var killer = creature.hp <= 0 && ComponentOf.TryGetValue(creature, out var component)
            ? component._killer
            : default;
        WriteDeath(creature, killer, data);
    }

    /// <summary>
    /// Takes, on a copy in this game, what a death of the creature told besides how it died (see
    /// <see cref="WriteDeath"/>), just before the copy plays the death: from the death itself, or from the replay of a
    /// death that the creature's FSM plays (see <see cref="WriteDeathOf"/>).
    /// </summary>
    /// <param name="copy">The copy, or null if it has no health manager.</param>
    /// <param name="data">The data of the death.</param>
    internal static void TakeDeathOf(HealthManager? copy, EntityNetworkData data) => TakeDeath(copy, data);

    /// <inheritdoc cref="TakeDeathOf"/>
    /// <returns>Who struck the blow of the death.</returns>
    private static Killer TakeDeath(HealthManager? copy, EntityNetworkData data) {
        var lastAttackDirection = data.Packet.ReadByte();
        var lastAttackType = (AttackTypes) data.Packet.ReadByte();
        var kind = (KillerKind) data.Packet.ReadByte();
        var killer = new Killer(kind, kind == KillerKind.Copy ? data.Packet.ReadUInt() : 0);
        if (copy == null) {
            return killer;
        }

        copy.directionOfLastAttack = lastAttackDirection;
        copy.lastAttackType = lastAttackType;
        HoldMoment(
            copy,
            ComponentOf.TryGetValue(copy, out var component)
                ? component.StruckHere(killer, runsTheCreature: false)
                : killer.Kind == KillerKind.None
        );

        return killer;
    }

    /// <summary>
    /// Puts the copy at once where the creature died in the scene host's game, for a death that came of nothing a
    /// player did (see <see cref="Update"/>).
    /// </summary>
    /// <param name="diedAt">Where the creature died.</param>
    private void PutCopyWhereItDied(Vector2 diedAt) {
        var copy = GameObject.Client;
        if (copy == null) {
            return;
        }

        var transform = copy.transform;
        var from = (Vector2) transform.position;
        transform.position = new Vector3(diedAt.x, diedAt.y, transform.position.z);
        if (copy.TryGetComponent<Rigidbody2D>(out var body)) {
            body.position = diedAt;
        }

        // Where the positions of the creature put the copy from here on, which they would otherwise pull it back from
        if (copy.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
            interpolation.Rebase(transform.position, Vector3.zero);
        }

        var distance = Vector2.Distance(from, diedAt);
        if (distance >= LoggedDeathPlaceDistance) {
            Logger.Info(
                $"'{copy.name}' died of nothing a player did {distance:0.00} away from where its copy was here, " +
                $"at ({diedAt.x:0.00}, {diedAt.y:0.00}), so the copy dies there"
            );
        }
    }

    /// <summary>
    /// Captures the corpse emitted by the host death lifecycle.
    /// </summary>
    private void OnHostCorpseEmitted(GameObject corpse) {
        _hostCorpse = corpse;
    }

    /// <summary>
    /// Gives a remote corpse and all of its body parts back the bodies the game made them with.
    /// </summary>
    private void OnClientCorpseEmitted(GameObject corpse) {
        if (corpse == null) {
            return;
        }

        _clientCorpse = corpse;
        _clientCorpseFrame = Time.frameCount;
        RestoreCorpsePhysics(corpse);
    }

    /// <summary>
    /// Gives a corpse and all of its body parts back the body types the game made them with. A corpse the game made
    /// ahead of time sits under the copy, whose bodies were all made kinematic when it was set up. Every body used to
    /// be made dynamic here instead, but some boss corpses are kinematic by design and are dropped by their FSM to a set
    /// height: made dynamic, one came to rest on the floor just above that height and its death never went on.
    /// </summary>
    /// <param name="corpse">The emitted corpse.</param>
    private static void RestoreCorpsePhysics(GameObject corpse) {
        if (corpse == null) {
            return;
        }

        foreach (var rigidbody in corpse.GetComponentsInChildren<Rigidbody2D>(true)) {
            if (rigidbody != null) {
                EntityInitializer.RestoreBodyType(rigidbody);
            }
        }
    }

    /// <summary>
    /// Applies the host corpse's initial transform and velocity to the local corpse.
    /// </summary>
    /// <param name="corpse">The local corpse receiving the snapshot.</param>
    /// <param name="position">The host corpse's world position.</param>
    /// <param name="rotation">The host corpse's world rotation around the z-axis.</param>
    /// <param name="velocity">The host corpse's initial linear velocity.</param>
    private static void ApplyCorpseSnapshot(
        GameObject corpse,
        Vector2 position,
        float rotation,
        Vector2 velocity
    ) {
        if (corpse == null) {
            return;
        }

        var transform = corpse.transform;
        transform.position = new Vector3(position.x, position.y, transform.position.z);
        transform.rotation = Quaternion.Euler(0f, 0f, rotation);

        var rigidbody = corpse.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return;
        }

        rigidbody.position = position;
        rigidbody.rotation = rotation;
        rigidbody.linearVelocity = velocity;
    }

    /// <summary>
    /// The hook on HealthManager.Die that all creatures share: the death of a creature or of its copy goes to its
    /// component (see <see cref="HealthManagerOnDie"/>), and any other comes as it would.
    /// </summary>
    private static void OnAnyDie(
        Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool> orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        NailElements nailElements,
        GameObject gameObject,
        bool ignoreEvasion,
        float corpseFlingMultiplier,
        bool overrideSpecialDeath,
        bool disallowDropFlying
    ) {
        if (ComponentOf.TryGetValue(self, out var component)) {
            component.HealthManagerOnDie(
                orig, self, attackDirection, attackType, nailElements, gameObject, ignoreEvasion,
                corpseFlingMultiplier, overrideSpecialDeath, disallowDropFlying
            );
        } else {
            orig(
                self, attackDirection, attackType, nailElements, gameObject, ignoreEvasion, corpseFlingMultiplier,
                overrideSpecialDeath, disallowDropFlying
            );
        }
    }

    /// <summary>
    /// Callback method for when the health manager dies.
    /// </summary>
    private void HealthManagerOnDie(
        Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool> orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        NailElements nailElements,
        GameObject gameObject,
        bool ignoreEvasion,
        float corpseFlingMultiplier,
        bool overrideSpecialDeath,
        bool disallowDropFlying
    ) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            InvokeOrig();
            return;
        }

        if (self == _healthManager.Client) {
            if (!_allowDeath) {
                if (_takingOwnBlowOnCopy) {
                    _ownBlowAskedToDie = true;
                } else if (OwnSignalPlaying) {
                    // No damage of this game's player goes with it: only the death, or no answer, lets it hurt again
                    KeepFromHurting(int.MaxValue);
                }

                // Said once: a copy with no health left asks to die every frame for as long as the room lasts
                if (!_toldRefusedDeath) {
                    _toldRefusedDeath = true;
                    Logger.Debug($"HealthManager Die was called on client entity '{self.name}'");
                }
            } else {
                Logger.Debug($"HealthManager Die was called on client entity '{self.name}', but it is allowed death");

                InvokeOrig();

                _allowDeath = false;
                _toldRefusedDeath = false;
                LetHurt();
            }

            return;
        }

        Logger.Info($"HealthManager Die was called on host entity '{self.name}'");

        // Who struck the blow of this death: the player of a copy whose blow SSMP kills the creature of, or this game's
        // player whose blow it is taking. A death that comes of no blow while the creature has no health left, such as
        // one that its FSM brings after that of the blow, is still of whoever struck the blow; any other is of nobody's
        _killer = _pendingKiller ??
                  (_takingOwnPlayerBlow ? new Killer(KillerKind.Host, 0) : self.hp <= 0 ? _killer : default);
        _pendingKiller = null;
        HoldMoment(self, StruckHere(_killer, runsTheCreature: true));

        // Whether the creature's own FSM plays its deaths, as it is right now. Some rooms switch that off on the
        // creature just before they kill it (SetSpecialDeath), only ever in the game that runs them
        var hasSpecialDeath = self.hasSpecialDeath;

        // Where it dies, before its death takes it anywhere
        var diedAt = (Vector2) self.transform.position;

        _hostCorpse = null;
        InvokeOrig();

        // A death of its own that left it alive, which its FSM played out in that call: what it went on to, it went on
        // to in the same call (see _diedToComeBackAt)
        if (hasSpecialDeath && !self.isDead) {
            _diedToComeBackAt = Time.frameCount;
        }

        var data = ObjectPool<EntityNetworkData>.Get();
        data.Type = EntityComponentType.Death;

        if (attackDirection.HasValue) {
            data.Packet.Write(true);
            data.Packet.Write(attackDirection.Value);
        } else {
            data.Packet.Write(false);
        }

        data.Packet.Write((byte) attackType);

        data.Packet.Write(ignoreEvasion);

        // The rest of what the creature died with: the attack's element, how hard its body was thrown, and whether
        // what it drops was kept from flying out, which for a source that breaks items is all of it
        data.Packet.Write((byte) nailElements);
        data.Packet.Write(corpseFlingMultiplier);
        data.Packet.Write(disallowDropFlying);

        data.Packet.Write(_hostCorpse != null);
        if (_hostCorpse != null) {
            var corpsePosition = _hostCorpse.transform.position;
            var corpseRigidbody = _hostCorpse.GetComponent<Rigidbody2D>();

            data.Packet.Write(corpsePosition.x);
            data.Packet.Write(corpsePosition.y);
            data.Packet.Write(_hostCorpse.transform.eulerAngles.z);
            data.Packet.Write(corpseRigidbody?.linearVelocity.x ?? 0f);
            data.Packet.Write(corpseRigidbody?.linearVelocity.y ?? 0f);
        }

        data.Packet.Write(hasSpecialDeath);
        data.Packet.Write(overrideSpecialDeath);

        // The tool damage of the blow that killed the creature, if a blow did: each game drops by it what it drops for
        // its own player. Told with this death only - one that comes of the same blow, such as a death of the
        // creature's own that its FSM plays out at once, has dropped it already
        data.Packet.Write((int) _killingToolDamage);
        _killingToolDamage = ToolDamageFlags.None;

        data.Packet.Write(diedAt.x);
        data.Packet.Write(diedAt.y);

        WriteDeath(self, _killer, data);

        _hostCorpse = null;

        SendData(data);
        return;

        // Utility method to invoke the original method with all the original arguments
        void InvokeOrig() {
            orig(
                self, attackDirection, attackType, nailElements, gameObject, ignoreEvasion, corpseFlingMultiplier,
                overrideSpecialDeath, disallowDropFlying
            );
        }
    }

    /// <inheritdoc />
    public override void SendAgain() {
        if (_healthManager.Host != null) {
            _lastInvincible = !_healthManager.Host.IsInvincible;
        }
    }

    /// <inheritdoc />
    public override void AddToRoomSnapshot(System.Collections.Generic.List<EntityNetworkData> data) {
        if (IsControlled || !HasHealthToTell()) {
            return;
        }

        // The health as it is, told anew: the copies take it as they take any (UpdateHealth)
        var hpData = new EntityNetworkData {
            Type = EntityComponentType.Health
        };
        WriteHealthState(hpData);
        data.Add(hpData);
    }

    /// <summary>
    /// Callback method for updates to check whether health or invincibility changes.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (_ownKillAt >= 0f && Time.unscaledTime - _ownKillAt >= OwnKillAnswerTime) {
            Logger.Info(
                $"No answer to this player's blow that left '{GameObject.Client?.name}' no health in " +
                $"{OwnKillAnswerTime} s: it can hurt them again"
            );
            LetHurt();
        }

        var observedHealthManager = IsControlled ? _healthManager.Client : _healthManager.Host;
        if (observedHealthManager == null) {
            return;
        }

        // Out again where it can be hit, past the frame after its death, in which its FSM may still have put it away
        if (!IsControlled && _diedToComeBackAt >= 0 && Time.frameCount >= _diedToComeBackAt + 2 &&
            CanBeHit(observedHealthManager)) {
            _diedToComeBackAt = -1;
        }

        // The room's own creature, taken over with blows of this game's player that killed it, dies of them once it is
        // switched on and set up (see InitializeHost)
        if (!IsControlled && _killAfterFrame >= 0 && Time.frameCount > _killAfterFrame) {
            if (observedHealthManager.GetIsDead() || observedHealthManager.hp > 0) {
                _killAfterFrame = -1;
            } else if (observedHealthManager.gameObject.activeInHierarchy) {
                _killAfterFrame = -1;
                Logger.Info($"'{observedHealthManager.name}', taken over, dies of the blows of this player");

                // The way the blow came from is the creature's before it hears of it, as for a blow of the game
                if (_killAfterFrameBlow is { } blow) {
                    KeepLastAttack(observedHealthManager, blow);
                }

                TellOfPartnerBlow(observedHealthManager.gameObject, "HIT");
                TellOfPartnerBlow(observedHealthManager.gameObject, "TOOK DAMAGE");
                DieOfTakenInBlow(observedHealthManager, _killAfterFrameBlow, new Killer(KillerKind.Host, 0));
            }
        }

        // Whether the copy is dead goes into this game's save the way the game that runs the creature keeps it: from
        // whatever set it, not from every death the copy is told of - one that has a death of its own takes one and
        // can live on, and it is dead only once that is played out
        if (IsControlled) {
            var copyDead = observedHealthManager.GetIsDead();
            if (copyDead != _copyWasDead) {
                _copyWasDead = copyDead;
                CopyDeathChanged?.Invoke(copyDead, !_copyKilledOnWalkIn);
                if (!copyDead) {
                    _copyKilledOnWalkIn = false;
                }
            }
        }

        var newHp = observedHealthManager.hp;
        if (newHp > 0) {
            _hadHealth = true;

            // Alive, and of no blow that killed it any more
            _killer = default;
        }

        if (newHp != _lastHp) {
            if (IsControlled && newHp > _lastHp) {
                if (_hasPendingControlledHealCorrection) {
                    if (Time.unscaledTime >= _pendingControlledHealCorrectionAt) {
                        _hasPendingControlledHealCorrection = false;
                        ApplyHp(_lastHp, triggerHostDeath: false);
                    }
                } else {
                    _hasPendingControlledHealCorrection = true;
                    _pendingControlledHealCorrectionAt = Time.unscaledTime + ControlledHealCorrectionDelaySeconds;
                }

                return;
            }

            _hasPendingControlledHealCorrection = false;

            if (IsControlled) {
                // A blow of this game's player on the copy that the hooks on blows did not see. Health changes only in
                // the game that runs the creature: the blow is counted and sent, and the copy goes straight back to
                // the health last told, which shows the blow once that game has taken it in. A blow that would have
                // left the copy no health is no different: the copy dies when the creature does. The hit itself - its
                // flash, sound and knockback - was played here at once all the same.
                CountOwnBlows();
                ApplyHp(_toldHp, triggerHostDeath: false);
            } else {
                _lastHp = newHp;

                // The copy that is not running is kept at the same health. It is the one that carries on when the
                // room changes hands, and left at the health from before this game's own hits it gave all of them
                // back: the game that took the room over put both copies at its health, and since nothing had changed
                // as far as it could tell, never said so to the player who came back in, who kept the health that had
                // been sent
                if (_healthManager.Client != null) {
                    _healthManager.Client.hp = newHp;
                }

                SendHealthState();
            }
        } else if (IsControlled && _ownDamage > _ownDamageTaken &&
                   Time.unscaledTime - _ownDamageSentAt >=
                   (_ownDamageResends < OwnDamageQuickResends ? OwnDamageResendTime : OwnDamageSlowResendTime)) {
            // The game that runs the creature has not told health with all of it in: sent again, as it is all of it,
            // with all of its blows
            _ownDamageResends++;
            SendOwnDamage(resend: true);
        }

        var newInvincible = _healthManager.Host.IsInvincible;
        var newInvincibleFromDir = _healthManager.Host.InvincibleFromDirection;

        // Only retrieve and populate invincibilityData from the pool if a value has actually changed,
        // preventing unnecessary allocations on 99.9% of frames.
        if (newInvincible != _lastInvincible || newInvincibleFromDir != _lastInvincibleFromDirection) {
            _lastInvincible = newInvincible;
            _lastInvincibleFromDirection = newInvincibleFromDir;

            var invincibilityData = ObjectPool<EntityNetworkData>.Get();
            invincibilityData.Type = EntityComponentType.Invincibility;

            invincibilityData.Packet.Write(newInvincible);
            invincibilityData.Packet.Write((byte) newInvincibleFromDir);

            SendData(invincibilityData);
        }
    }

    /// <inheritdoc />
    public override void InitializeHost(uint sceneHostEpoch) {
        // The creature is this game's now, and dies here of a blow that left it no health (see below)
        LetHurt();

        // The blows of this game's player on the copy that the game that ran the creature had not taken in yet, a blow
        // of this very frame among them. The copy only ever showed the health that game told, so they are in no health
        // here, and taking the room over would lose them: the creature goes on with them. Not for a creature that is
        // dead already, told so or dead here.
        var ownDamageNotTakenIn = 0;
        var healthAfter = 0;
        BlowDeath? killingBlow = null;
        if (_wasRunElsewhere) {
            CountOwnBlows(send: false);

            var copyDead = _healthManager.Client != null && _healthManager.Client.GetIsDead();
            var dead = _toldDead || copyDead;
            if (!dead && _ownDamage > _ownDamageTaken) {
                ownDamageNotTakenIn = _ownDamage - _ownDamageTaken;

                // The health they leave, and the blow among them that takes the last of it if they do, as the game
                // that ran the creature would have taken them in (see TakeInOwnDamage)
                healthAfter = HealthAfter(GetCurrentHp(), _ownDamageTaken, _ownDamage, _ownBlows, out killingBlow);
            }

            // Dead as the game that ran the creature last told, if it told anything: a copy stays dead after the
            // creature lives again in a game that brought it back while the copy was off
            if (_toldNumber > 0 ? _toldDead : dead) {
                TakeOverDead(copyDead);
            }
        }

        ResetHealthOrderingForEpoch(sceneHostEpoch);

        // The health the copies were made with, which the copies in the other games were made with too
        var copiedHp = _lastHp;
        var healthBefore = GetCurrentHp();
        var currentHp = ownDamageNotTakenIn > 0 ? healthAfter : healthBefore;
        if (ownDamageNotTakenIn > 0) {
            Logger.Info(
                $"Taking over '{GameObject.Host?.name}' with {ownDamageNotTakenIn} damage of this player that the " +
                "scene host had not taken in"
            );
        }

        ApplyHp(currentHp, triggerHostDeath: false);

        // Killed by them: not here, as the room's own creature was switched on only now, before it is set up, but in
        // the next frame, if it is on and still alive then (OnUpdate)
        if (ownDamageNotTakenIn > 0 && healthBefore > 0 && currentHp <= 0) {
            _killAfterFrame = Time.frameCount;
            _killAfterFrameBlow = killingBlow;
        }

        // A room can set the health of its creatures as it starts, before it is settled which game runs them: a fight
        // gives its creatures the health of its first part. Nothing watched the room's own creature until now, so the
        // other games, whose copies keep the health they were made with, are told. A creature taken over from another
        // game goes on at the health that its copy had here, which the copies in the other games are told as the start
        // of the new count of who runs the room.
        if (_wasRunElsewhere) {
            SendHealthState();
        } else if (currentHp != copiedHp) {
            Logger.Info(
                $"The room set the health of '{GameObject.Host?.name}' to {currentHp} before it ran, from {copiedHp}"
            );
            SendHealthState();
        }
    }

    /// <summary>
    /// Has the room's own creature dead, as this game takes it over dead: it died in the game that ran it, and here
    /// only the copy died, or was told so. The health this game tells from now on says whether the room's own is dead,
    /// and it was not: a creature that the partner killed while this player stayed in the room was told alive once the
    /// partner left and this game took the room over, so when the partner came back in, the creature was neither dead
    /// nor there for them - no body, and nothing that the replay of its death drops (USER 10-12, "她刷完之后呆在房间里，
    /// 我再出去进来就刷不了了，看不到尸体了"). Only for a creature that is off: one that is on may still be playing out
    /// its death and is left as it is, and the game switches a dead creature off a frame after it is switched on, which
    /// for one under a parent that is off is when the parent comes on, as in a game where it died. What the room's own
    /// keeps in the save is not saved by this game for such a creature (Entity.CopyIsGone).
    /// </summary>
    /// <param name="copyDead">Whether the copy died here, rather than only being told so.</param>
    private void TakeOverDead(bool copyDead) {
        var host = _healthManager.Host;
        if (host == null || host.GetIsDead() || host.gameObject.activeInHierarchy) {
            return;
        }

        host.SetIsDead(true);
        Logger.Info(
            $"'{host.name}' is taken over dead, as its copy {(copyDead ? "died here" : "was told it died")}"
        );
    }

    /// <summary>
    /// Whether the health last told of the creature said that it is dead, until this game tells its own.
    /// </summary>
    public bool ToldDead => _toldDead;

    /// <inheritdoc />
    public override void InitializeClient(uint sceneHostEpoch) {
        _wasRunElsewhere = true;
        ResetHealthOrderingForEpoch(sceneHostEpoch);
        var currentHp = GetCurrentHp();
        ApplyHp(currentHp, triggerHostDeath: false);
    }

    /// <summary>
    /// Tells the other games the health of the creature as it is here, in the game that runs it, and how much of the
    /// damage of each of their copies is in it already. A copy takes it as it is (UpdateHealth): the blows of its own
    /// player show in it once they are taken in here. Changes used to be sent as such, from one value to another, and
    /// each one lost on the way left the copies off by that much for as long as the creature lived, more with every
    /// stretch in which nothing arrived; health told as it is is made right by whichever telling comes next.
    /// </summary>
    private void SendHealthState() {
        if (!HasHealthToTell()) {
            return;
        }

        var hpData = ObjectPool<EntityNetworkData>.Get();
        hpData.Type = EntityComponentType.Health;
        WriteHealthState(hpData);
        SendData(hpData);
    }

    /// <summary>
    /// Writes the health of the creature as a new telling (see <see cref="SendHealthState"/>).
    /// </summary>
    private void WriteHealthState(EntityNetworkData data) {
        data.Packet.Write(HealthMessage.State);
        data.Packet.Write(_currentHealthEpoch);
        data.Packet.Write(++_tellNumber);
        data.Packet.Write(GetCurrentHp());
        data.Packet.Write(_healthManager.Host != null ? _healthManager.Host.GetIsDead() : _lastHp <= 0);

        var count = System.Math.Min(_damageTaken.Count, byte.MaxValue);
        data.Packet.Write((byte) count);
        foreach (var (copyKey, damage) in _damageTaken.Values) {
            if (count-- == 0) {
                break;
            }

            data.Packet.Write(copyKey);
            data.Packet.Write(damage);
        }
    }

    /// <summary>
    /// Whether the health of the creature means anything to the copies: not for one that is alive at no health because
    /// it never had any (see <see cref="_hadHealth"/>).
    /// </summary>
    private bool HasHealthToTell() {
        return _hadHealth || GetCurrentHp() > 0;
    }

    /// <summary>
    /// Tells the game that runs the creature all the damage that the copy took from this game's player so far in the
    /// current epoch, with blows in it that it has not told it took in (see <see cref="TakeInOwnDamage"/>): as a blow
    /// lands, the newest of them (see <see cref="BlowsSentWithABlow"/>), and when it is sent again, all of them. Only a
    /// message with every one of them puts off sending it again: one with the newest alone is no help if those before
    /// them went missing, and the damage went again only when the player stopped hitting for a while.
    /// </summary>
    /// <param name="resend">Whether the damage is sent again, rather than as a blow lands.</param>
    private void SendOwnDamage(bool resend) {
        var hpData = ObjectPool<EntityNetworkData>.Get();
        hpData.Type = EntityComponentType.Health;
        hpData.Packet.Write(HealthMessage.OwnDamage);
        hpData.Packet.Write(_currentHealthEpoch);
        hpData.Packet.Write(_copyKey);
        hpData.Packet.Write(_ownDamage);

        _ownBlows.RemoveAll(blow => blow.DamageAfter <= _ownDamageTaken);
        var count = System.Math.Min(
            resend ? _ownBlows.Count : System.Math.Min(_ownBlows.Count, BlowsSentWithABlow), byte.MaxValue
        );
        var first = resend ? 0 : _ownBlows.Count - count;

        // Whether the game that runs the creature has every blow it will get with this: all of those not taken in,
        // or as many as there is room for, the oldest, when the damage is sent again
        var whole = resend || count == _ownBlows.Count;
        hpData.Packet.Write(whole);
        hpData.Packet.Write((byte) count);
        for (var i = first; i < first + count; i++) {
            WriteOwnBlow(hpData, _ownBlows[i]);
        }

        SendData(hpData);

        if (whole) {
            _ownDamageSentAt = Time.unscaledTime;
            if (!resend) {
                _ownDamageResends = 0;
            }
        }
    }

    /// <summary>
    /// Writes a blow of this game's player on the copy (see <see cref="_ownBlows"/>).
    /// </summary>
    private static void WriteOwnBlow(EntityNetworkData data, OwnBlow blow) {
        var death = blow.Death;
        data.Packet.Write(blow.DamageBefore);
        data.Packet.Write(blow.DamageAfter);
        data.Packet.Write(death.Direction.HasValue);
        if (death.Direction.HasValue) {
            data.Packet.Write(death.Direction.Value);
        }

        data.Packet.Write(death.Magnitude);
        data.Packet.Write((sbyte) death.LastAttackDirection);
        data.Packet.Write((byte) death.AttackType);
        data.Packet.Write((byte) death.NailElement);
        data.Packet.Write(death.IgnoreInvulnerable);
        data.Packet.Write((int) death.ToolDamage);
        data.Packet.Write(death.BreaksItems);
        data.Packet.Write(death.NonLethal);
    }

    /// <summary>
    /// Reads a blow of the player of a copy in another game (see <see cref="WriteOwnBlow"/>).
    /// </summary>
    private static OwnBlow ReadOwnBlow(EntityNetworkData data) {
        var damageBefore = data.Packet.ReadInt();
        var damageAfter = data.Packet.ReadInt();
        float? direction = data.Packet.ReadBool() ? data.Packet.ReadFloat() : null;
        var magnitude = data.Packet.ReadFloat();
        var lastAttackDirection = data.Packet.ReadSByte();
        var attackType = (AttackTypes) data.Packet.ReadByte();
        var nailElement = (NailElements) data.Packet.ReadByte();
        var ignoreInvulnerable = data.Packet.ReadBool();
        var toolDamage = (ToolDamageFlags) data.Packet.ReadInt();
        var breaksItems = data.Packet.ReadBool();
        var nonLethal = data.Packet.ReadBool();
        return new OwnBlow(
            damageBefore,
            damageAfter,
            new BlowDeath(
                direction, magnitude, lastAttackDirection, attackType, nailElement, ignoreInvulnerable, toolDamage,
                breaksItems, nonLethal
            )
        );
    }

    /// <summary>
    /// The blow that took the last of a creature's health, among blows of one copy: the one in which the copy's damage
    /// passed the amount at which the health ran out. None if that was in damage that no blow was kept for.
    /// </summary>
    /// <param name="blows">The blows, as in <see cref="_ownBlows"/>.</param>
    /// <param name="killedAt">How far the copy's damage had to come for the health to run out.</param>
    private static OwnBlow? KillingBlow(List<OwnBlow> blows, int killedAt) {
        foreach (var blow in blows) {
            if (blow.DamageBefore < killedAt && killedAt <= blow.DamageAfter) {
                return blow;
            }
        }

        return null;
    }

    /// <summary>
    /// The health that a creature is left with by damage of a copy's player that it has not taken in yet, and the blow
    /// in it that kills the creature if it does, as the game would take the blows one by one: a blow that kills
    /// nothing (see <see cref="BlowDeath.NonLethal"/>) leaves it 1 health where it would have taken the last, and only
    /// a later blow can kill it. The copy counts such a blow up to the health it last showed less 1, every time, and
    /// taken in at once those blows killed a creature that the game keeps alive.
    /// </summary>
    /// <param name="health">Its health.</param>
    /// <param name="taken">How far the copy's damage had come in that health.</param>
    /// <param name="damage">How far the copy's damage has come.</param>
    /// <param name="blows">The blows of the copy's damage that were kept, or null for none.</param>
    /// <param name="killingBlow">What the blow that kills it kills with, if one does and was kept.</param>
    private static int HealthAfter(
        int health,
        int taken,
        int damage,
        List<OwnBlow>? blows,
        out BlowDeath? killingBlow
    ) {
        killingBlow = null;
        var newHp = health - (damage - taken);
        var killedAt = taken + health;
        while (newHp <= 0 && blows != null && KillingBlow(blows, killedAt) is { } blow) {
            if (!blow.Death.NonLethal) {
                killingBlow = blow.Death;
                break;
            }

            // Left 1 health, which the blows after it wear down
            newHp = 1 - (damage - blow.DamageAfter);
            killedAt = blow.DamageAfter + 1;
        }

        return newHp;
    }

    /// <summary>
    /// Sets the count of who runs the room for a creature that came after the room's other ones and was set up for no
    /// role (see Entity.SetSceneHostEpoch). Left at the 0 it was made with, its health and damage were told in another
    /// count than the other game's, and each game passed over what the other said.
    /// </summary>
    internal void SetSceneHostEpoch(uint sceneHostEpoch) {
        ResetHealthOrderingForEpoch(sceneHostEpoch);
    }

    /// <summary>
    /// Starts the count of damage and of tellings over for a new scene-host epoch: the game that runs the creature from
    /// then on tells its health from its own, and the damage of the copies is counted against that.
    /// </summary>
    /// <param name="sceneHostEpoch">The new scene-host epoch assigned by the server.</param>
    private void ResetHealthOrderingForEpoch(uint sceneHostEpoch) {
        if (sceneHostEpoch != _currentHealthEpoch) {
            _toldHp = GetCurrentHp();
            _toldNumber = 0;
            _toldDead = false;
            _ownDamage = 0;
            _ownDamageTaken = 0;
            _ownDamageResends = 0;
            _ownBlows.Clear();
            _damageTaken.Clear();
            _tellNumber = 0;
            _diedToComeBackAt = -1;

            // The damage that the copy was kept from hurting the player for is not counted any more
            LetHurt();
        }

        _currentHealthEpoch = sceneHostEpoch;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled && data.Type != EntityComponentType.Health) {
            Logger.Info("  Entity was not controlled");
            return;
        }

        switch (data.Type) {
            case EntityComponentType.Death: {
                var attackDirection = new float?();
                if (data.Packet.ReadBool()) {
                    attackDirection = data.Packet.ReadFloat();
                }

                var attackType = (AttackTypes) data.Packet.ReadByte();
                var ignoreEvasion = data.Packet.ReadBool();
                var nailElement = (NailElements) data.Packet.ReadByte();
                var corpseFlingMultiplier = data.Packet.ReadFloat();
                var disallowDropFlying = data.Packet.ReadBool();

                var hasCorpseSnapshot = data.Packet.ReadBool();
                var corpsePosition = Vector2.zero;
                var corpseRotation = 0f;
                var corpseVelocity = Vector2.zero;

                if (hasCorpseSnapshot) {
                    corpsePosition = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
                    corpseRotation = data.Packet.ReadFloat();
                    corpseVelocity = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
                }

                // The copy dies the way the creature did in the scene host's game. A room that switched the special
                // death off before its final blow did so only there, and a copy that kept it only told its own
                // switched-off FSM and was never seen to die
                _healthManager.Client.hasSpecialDeath = data.Packet.ReadBool();
                var overrideSpecialDeath = data.Packet.ReadBool();
                var killingToolDamage = (ToolDamageFlags) data.Packet.ReadInt();
                var diedAt = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());

                // The way the last blow came from, for a death that the copy's FSM plays, and the moment held still
                // only in the game of the player who struck it
                var killer = TakeDeath(_healthManager.Client, data);

                // A creature that died of nothing a player did - the room's lava, its spikes, a fall - dies here where
                // it died there. The copy can be somewhere else by then: one that the local player wounded went its
                // own way from where it stood here, while the creature went its own from where it stood there, into
                // the lava, and the copy burnt up on dry ground. One that a player's blow killed dies where this game
                // has it, which is where the player who struck it saw the blow land
                if (killer.Kind == KillerKind.None && !CarriedByParent) {
                    PutCopyWhereItDied(diedAt);
                }

                // A creature whose FSM plays its own death threw out its body in the replay of that, which came just
                // before this from the same death in the scene host's game (EntityFsmActions, SimulateDeath), and the
                // copy's own death throws out none: the body whose place the scene host sent is that one. Only one
                // thrown out this frame, as a body of an earlier death may since have been taken for another
                var replayedCorpse = _clientCorpseFrame == Time.frameCount ? _clientCorpse : null;

                // Set a boolean to indicate that the client health manager is allowed to execute the Die method
                _allowDeath = true;
                _clientCorpse = null;
                // What the blow that killed the creature drops, whoever struck it: every player gets their own drops.
                // Not for a copy that is dead already, which a death does not drop anything from again
                if (!_healthManager.Client.GetIsDead()) {
                    QueueDeathDrop(_healthManager.Client, killingToolDamage);
                }
                // As the creature died, but for the source of the blow, which is in the game of the player who struck
                // it alone: the zap that a tool shows on the body of a creature it killed shows nowhere
                _healthManager.Client.Die(
                    attackDirection, attackType, nailElement, null, ignoreEvasion, corpseFlingMultiplier,
                    overrideSpecialDeath, disallowDropFlying
                );

                var corpse = _clientCorpse != null ? _clientCorpse : replayedCorpse;
                if (hasCorpseSnapshot && corpse != null) {
                    ApplyCorpseSnapshot(corpse, corpsePosition, corpseRotation, corpseVelocity);
                }

                break;
            }
            case EntityComponentType.Health:
                UpdateHealth(data, alreadyInSceneUpdate);
                break;
            case EntityComponentType.Invincibility: {
                var newInvincible = data.Packet.ReadBool();
                var newInvincibleFromDir = data.Packet.ReadByte();

                if (_healthManager.Host != null) {
                    _healthManager.Host.IsInvincible = newInvincible;
                    _healthManager.Host.InvincibleFromDirection = newInvincibleFromDir;
                }

                if (_healthManager.Client == null) {
                    return;
                }

                _healthManager.Client.IsInvincible = newInvincible;
                _healthManager.Client.InvincibleFromDirection = newInvincibleFromDir;
                break;
            }
        }
    }

    /// <summary>
    /// Takes in a health message: the health of the creature told by the game that runs it, or the damage of a copy in
    /// another game (see <see cref="SendHealthState"/> and <see cref="SendOwnDamage"/>).
    /// </summary>
    private void UpdateHealth(EntityNetworkData data, bool alreadyInSceneUpdate) {
        _hasPendingControlledHealCorrection = false;

        if (data.Packet.ReadByte() == HealthMessage.OwnDamage) {
            TakeInOwnDamage(data);
            return;
        }

        var epoch = data.Packet.ReadUInt();
        var number = data.Packet.ReadUInt();
        var hp = data.Packet.ReadInt();
        var dead = data.Packet.ReadBool();
        var ownDamageTaken = 0;
        var count = data.Packet.ReadByte();
        for (var i = 0; i < count; i++) {
            var copyKey = data.Packet.ReadUInt();
            var damage = data.Packet.ReadInt();
            if (copyKey == _copyKey) {
                ownDamageTaken = damage;
            }
        }

        if (!IsControlled) {
            // The game that runs the creature takes its health from another only as it walks into the room after the
            // other ran it - told in an earlier count of who runs the room, before this game told any of its own - and
            // tells it on as its own. What the server keeps of a room this game ran all along came from it, and an
            // answer to walking in that came twice would set the creature back to it, undoing the hits since.
            if (alreadyInSceneUpdate && !Entity.FromRoomState && epoch < _currentHealthEpoch && _tellNumber == 0) {
                ApplyHp(hp, triggerHostDeath: false);
                SendHealthState();
            }

            return;
        }

        // A blow of this game's player that the copy took before this, and that was not counted yet, is counted first:
        // set over, it would be lost. The copy goes back to the health last told, whatever this telling turns out to
        // be.
        var client = _healthManager.Client;
        if (CountOwnBlows()) {
            ApplyHp(_toldHp, triggerHostDeath: false);
        }

        if (epoch > _currentHealthEpoch) {
            ResetHealthOrderingForEpoch(epoch);
        }

        if (epoch == _currentHealthEpoch && number > _toldNumber) {
            // Lower than told before, as the game moves on: damage landed there, as the room's state that comes with
            // walking in is not
            if (!alreadyInSceneUpdate && hp < _toldHp && client != null) {
                DamageLanded?.Invoke(client, null);
            }

            _toldHp = hp;
            _toldNumber = number;
            _toldDead = dead;
            _ownDamageTaken = ownDamageTaken;

            // The creature lived through the blow of this game's player that left the copy no health: the health it
            // was told with had more in it, or the blow killed nothing there
            if (_ownKillAt >= 0f && ownDamageTaken >= _ownKillDamage && !dead && (hp > 0 || !_hadHealth)) {
                Logger.Info($"'{client?.name}' lived through this player's blow in the scene host's game");
                LetHurt();
            }

            // The game that runs the creature is heard from: what of this game's damage is not in it yet goes again
            // soon, however long it went unheard before
            _ownDamageResends = 0;
        } else if (epoch < _currentHealthEpoch && alreadyInSceneUpdate && _toldNumber == 0) {
            // Told before the room last changed hands, and nothing since, as the copy walks in: nearer the health of
            // the creature than the health the copy was made with. The damage in it was counted against another.
            _toldHp = hp;
            _toldDead = dead;
        } else if (!alreadyInSceneUpdate) {
            return;
        }

        // As told: a blow of this game's player that is not in it yet shows once it is, not before
        ApplyHp(_toldHp, triggerHostDeath: false);

        // Walking into a room that the other player already cleared sends the health of everything in it, but never
        // the deaths themselves - the server keeps health and whether an object is active, and drops death data. The
        // same for the state of the room told again after a stretch in which nothing arrived, which can be all there
        // is of a death that went missing. Health alone leaves a copy that is alive with nothing left, which then asks
        // to die every frame and is turned down every frame, for as long as the room lasts. Its death is played out
        // once here instead, the same way one that arrives over the network is - for a creature told dead, not one
        // told it has no health, which some are alive at. A player who is scene host needs none of this: their own
        // object dies by itself and tells the others.
        if (alreadyInSceneUpdate && _toldDead && client != null && !client.GetIsDead()) {
            _copyKilledOnWalkIn = !Entity.FromRoomState;
            _allowDeath = true;
            _clientCorpse = null;
            client.Die(null, AttackTypes.Generic, true);
        }
    }

    /// <summary>
    /// Takes in the damage of a copy in another game on the creature that this game runs: what of it is not in the
    /// creature's health yet. All of the copy's damage so far comes each time, so none is taken in twice and none is
    /// lost with a message that went missing. Answered by telling the health as it is then, which is how the copy
    /// knows its damage was taken in. Damage that kills the creature kills it here, of the blow in it that took the
    /// last of its health. Damage that kills it before the first of the blows that came with it waits for the copy to
    /// send them all, as it does when nothing answers it for a while: the blow there may have killed nothing, and the
    /// creature should die of it if it did.
    /// </summary>
    private void TakeInOwnDamage(EntityNetworkData data) {
        var epoch = data.Packet.ReadUInt();
        var copyKey = data.Packet.ReadUInt();
        var damage = data.Packet.ReadInt();
        var whole = data.Packet.ReadBool();

        if (IsControlled) {
            return;
        }

        List<OwnBlow>? blows = null;
        var blowCount = data.Packet.ReadByte();
        for (var i = 0; i < blowCount; i++) {
            blows ??= new List<OwnBlow>(blowCount);
            blows.Add(ReadOwnBlow(data));
        }

        // Damage counted against health that another game told, which this game does not go on from. A copy that
        // missed the change of who runs the room is told the health as it is, and counts from there.
        if (epoch != _currentHealthEpoch) {
            if (epoch < _currentHealthEpoch) {
                SendHealthState();
            }

            return;
        }

        // A copy of a later visit of the player to the room counts from nothing again
        if (!_damageTaken.TryGetValue(data.SenderId, out var taken) || taken.copyKey != copyKey) {
            taken = (copyKey, 0);
        }

        // Dealt before the copy heard that the creature died a death it lives on after, as nothing can hit it until it
        // is out again: taken in, so that it is not sent again, and left out of the health it comes out with
        if (_diedToComeBackAt >= 0 && _healthManager.Host is { } away && !CanBeHit(away)) {
            if (damage > taken.damage) {
                Logger.Info(
                    $"'{away.name}' died a death it lives on after and can't be hit yet: {damage - taken.damage} " +
                    "damage of a copy, dealt before it heard, is left out"
                );
                taken.damage = damage;
            }

            _damageTaken[data.SenderId] = taken;
            SendHealthState();
            return;
        }

        var health = GetCurrentHp();
        if (!whole && health > 0 && health - (damage - taken.damage) <= 0 && blows is { Count: > 0 } &&
            taken.damage + health <= blows[0].DamageBefore) {
            // Answered with the health as it is, which has the copy send all of its blows soon
            _damageTaken[data.SenderId] = taken;
            SendHealthState();
            return;
        }

        if (damage > taken.damage) {
            // The creature keeps the way the last of the blows came from and its kind, as it does for the blows of
            // this game's player, for a death that its FSM plays
            if (blows != null && _healthManager.Host != null) {
                for (var i = blows.Count - 1; i >= 0 && blows[i].DamageAfter > taken.damage; i--) {
                    if (blows[i].Death.LastAttackDirection >= 0) {
                        KeepLastAttack(_healthManager.Host, blows[i].Death);
                        break;
                    }
                }
            }

            // The health runs out where the copy's damage passes what of it is in the health already and the health
            // left: the blow there kills the creature, unless it kills nothing
            var newHp = HealthAfter(health, taken.damage, damage, blows, out var killingBlow);
            ApplyHp(newHp, triggerHostDeath: true, killingBlow, new Killer(KillerKind.Copy, copyKey));
            taken.damage = damage;
            if (_healthManager.Host != null) {
                DamageLanded?.Invoke(_healthManager.Host, null);
            }
        }

        _damageTaken[data.SenderId] = taken;
        SendHealthState();
    }

    /// <summary>
    /// Whether a creature can be hit as it is: switched on, with a collider switched on that the player's attacks touch
    /// (by the game's matrix of layers) anywhere on the body that hits on it go to - the creature's own, or the one it
    /// is part of - or under it. A hit goes from the collider it lands on to the nearest body above (HitTaker), so a
    /// creature can be hit on colliders of its children, and a second one of its own; the ranges that see the player
    /// coming and the blocks that keep the player out are on layers that attacks don't touch.
    /// </summary>
    /// <param name="creature">The creature.</param>
    private static bool CanBeHit(HealthManager creature) {
        if (!creature.gameObject.activeInHierarchy) {
            return false;
        }

        var body = creature.GetComponentInParent<Rigidbody2D>();
        var root = body != null ? body.gameObject : creature.gameObject;
        foreach (var collider in root.GetComponentsInChildren<Collider2D>()) {
            if (collider.enabled && !Physics2D.GetIgnoreLayerCollision(AttackLayer, collider.gameObject.layer)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The layer of the player's attacks.
    /// </summary>
    private const int AttackLayer = (int) GlobalEnums.PhysLayers.HERO_ATTACK;

    /// <summary>
    /// Gets the health value from the locally active side of the entity.
    /// </summary>
    /// <returns>The current HP value.</returns>
    private int GetCurrentHp() {
        var healthManager = IsControlled ? _healthManager.Client : _healthManager.Host;
        return healthManager != null ? healthManager.hp : _lastHp;
    }

    /// <summary>
    /// Applies HP to both entity copies and optionally runs host death when a remote hit was lethal.
    /// </summary>
    /// <param name="newHp">The HP value to apply.</param>
    /// <param name="triggerHostDeath">Whether the scene host should run death when HP crosses zero.</param>
    /// <param name="killingBlow">For a death that it runs: what the blow that kills kills with, if it was kept.</param>
    /// <param name="killer">For a death that it runs: who struck the blow that kills.</param>
    private void ApplyHp(
        int newHp,
        bool triggerHostDeath,
        BlowDeath? killingBlow = null,
        Killer killer = default
    ) {
        var wasAlive = GetCurrentHp() > 0;
        var killsHost = triggerHostDeath && !IsControlled && wasAlive && newHp <= 0 && _healthManager.Host != null;

        // A creature is told that it was hit before a blow takes its health, lethal or not, and some only go on to
        // die from where that takes them: a spine floater with no health left switches its body off to hits at once,
        // but only explodes from the states it goes into when it is hit, and stays in the air untouchable until then.
        // The partner's blow arrives here as health alone, so the creature is told the same as for a blow of its own.
        if (killsHost) {
            var hostObject = _healthManager.Host!.gameObject;
            TellOfPartnerBlow(hostObject, "HIT");
            TellOfPartnerBlow(hostObject, "TOOK DAMAGE");
        }

        _lastHp = newHp;

        if (_healthManager.Host != null) {
            _healthManager.Host.hp = newHp;
        }

        if (_healthManager.Client != null) {
            _healthManager.Client.hp = newHp;
        }

        if (killsHost) {
            DieOfTakenInBlow(_healthManager.Host!, killingBlow, killer);
        }
    }

    /// <summary>
    /// Tells the FSMs of the creature an event of the partner's last blow, the way the game's own FSMUtility tells all of
    /// them, except those that each game runs for the hits of its own player only: one of those pays whoever struck, and
    /// this blow was the partner's (see <see cref="EntityRegistryEntry.HitterGameFsms"/>).
    /// </summary>
    /// <param name="hostObject">The room's own creature.</param>
    /// <param name="eventName">The name of the event.</param>
    private void TellOfPartnerBlow(GameObject hostObject, string eventName) {
        var fsmEvent = HutongGames.PlayMaker.FsmEvent.FindEvent(eventName);
        foreach (var fsm in hostObject.GetComponents<PlayMakerFSM>()) {
            if (!EntityRegistry.IsHitterGameFsm(_type, fsm.FsmName)) {
                fsm.Fsm.Event(fsmEvent);
            }
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
        if (_hostDeathEffects != null) {
            _hostDeathEffects.CorpseEmitted -= OnHostCorpseEmitted;
        }

        if (_clientDeathEffects != null) {
            _clientDeathEffects.CorpseEmitted -= OnClientCorpseEmitted;
        }

        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        LetHurt();

        foreach (var healthManager in new[] { _creatureHealthManager, _copyHealthManager }) {
            if (healthManager is not null && ComponentOf.TryGetValue(healthManager, out var component) &&
                ReferenceEquals(component, this)) {
                ComponentOf.Remove(healthManager);
            }
        }
    }

    /// <summary>
    /// A blow of this game's player on the copy (see <see cref="_ownBlows"/>).
    /// </summary>
    /// <param name="DamageBefore">How far the copy's damage had come before the blow.</param>
    /// <param name="DamageAfter">How far it had come after it.</param>
    /// <param name="Death">What the blow kills with.</param>
    private readonly record struct OwnBlow(int DamageBefore, int DamageAfter, BlowDeath Death);

    /// <summary>
    /// What a blow kills a creature with, as the game works it out at a blow that kills (HealthManager.TakeDamage and
    /// ApplyExtraDamage) for the death that follows (HealthManager.Die).
    /// </summary>
    /// <param name="Direction">Which way the body is thrown, or null for none.</param>
    /// <param name="Magnitude">How hard the body is thrown.</param>
    /// <param name="LastAttackDirection">Which way the blow came from, as the creature keeps it for a death that its
    /// FSM plays (see <see cref="KeepLastAttack"/>), or -1 for a blow that keeps none.</param>
    /// <param name="AttackType">The kind of attack, by which the creature dies: burnt, blown up, in the lava, drowned,
    /// without the moment held still, and so on.</param>
    /// <param name="NailElement">The element of the attack.</param>
    /// <param name="IgnoreInvulnerable">Whether the blow passes over the creature's evasion.</param>
    /// <param name="ToolDamage">The tool damage of the blow, by which it drops the item of a wish.</param>
    /// <param name="BreaksItems">Whether the source of the blow breaks items, which drops nothing at all.</param>
    /// <param name="NonLethal">Whether the blow kills nothing: the game leaves a creature 1 health where it would have
    /// taken the last (HitInstance.NonLethal, of the player's harpoon and the dash stab of one crest).</param>
    private readonly record struct BlowDeath(
        float? Direction,
        float Magnitude,
        int LastAttackDirection,
        AttackTypes AttackType,
        NailElements NailElement,
        bool IgnoreInvulnerable,
        ToolDamageFlags ToolDamage,
        bool BreaksItems,
        bool NonLethal
    );

    /// <summary>
    /// Who struck the blow of a creature's death: no player, the player of the game that runs the creature, or the
    /// player of the copy with the given number (see <see cref="_copyKey"/>).
    /// </summary>
    /// <param name="Kind">Which of them.</param>
    /// <param name="CopyKey">The number of the copy, for the player of a copy.</param>
    private readonly record struct Killer(KillerKind Kind, uint CopyKey);

    /// <inheritdoc cref="Killer" />
    private enum KillerKind : byte {
        None,
        Host,
        Copy
    }
}
