using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
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
    /// MonoMod hook for HealthManager.Die.
    /// </summary>
    private Hook? _healthManagerDieHook;

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
    /// When the copy's damage was last sent.
    /// </summary>
    private float _ownDamageSentAt;

    /// <summary>
    /// How many times the copy's damage was sent again since this game's player last hit it.
    /// </summary>
    private int _ownDamageResends;

    /// <summary>
    /// In the game that runs the creature: the damage of each other player's copy that is in its health, by player -
    /// the number of the copy, and all of its damage taken in.
    /// </summary>
    private readonly Dictionary<ushort, (uint copyKey, int damage)> _damageTaken = new();

    /// <summary>
    /// In the game that runs the creature: the number of its last telling of its health in the current epoch.
    /// </summary>
    private uint _tellNumber;

    /// <summary>
    /// Whether the creature has had any health. Some are set up with none of their own and are alive at none, and a
    /// copy that is told it has none dies.
    /// </summary>
    private bool _hadHealth;

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

        _healthManagerDieHook = new Hook(dieMethod, HealthManagerOnDie);
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
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
                // Said once: a copy with no health left asks to die every frame for as long as the room lasts
                if (!_toldRefusedDeath) {
                    _toldRefusedDeath = true;
                    Logger.Info($"HealthManager Die was called on client entity '{self.name}'");
                }
            } else {
                Logger.Info($"HealthManager Die was called on client entity '{self.name}', but it is allowed death");

                InvokeOrig();

                _allowDeath = false;
                _toldRefusedDeath = false;
            }

            return;
        }

        Logger.Info($"HealthManager Die was called on host entity '{self.name}'");

        // Whether the creature's own FSM plays its deaths, as it is right now. Some rooms switch that off on the
        // creature just before they kill it (SetSpecialDeath), only ever in the game that runs them
        var hasSpecialDeath = self.hasSpecialDeath;

        _hostCorpse = null;
        InvokeOrig();

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
        var observedHealthManager = IsControlled ? _healthManager.Client : _healthManager.Host;
        if (observedHealthManager == null) {
            return;
        }

        var newHp = observedHealthManager.hp;
        if (newHp > 0) {
            _hadHealth = true;
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
            var previousHp = _lastHp;
            _lastHp = newHp;

            // The copy that is not running is kept at the same health. It is the one that carries on when the room
            // changes hands, and left at the health from before this game's own hits it gave all of them back: the
            // game that took the room over put both copies at its health, and since nothing had changed as far as it
            // could tell, never said so to the player who came back in, who kept the health that had been sent
            var otherHealthManager = IsControlled ? _healthManager.Host : _healthManager.Client;
            if (otherHealthManager != null) {
                otherHealthManager.hp = newHp;
            }

            if (IsControlled) {
                // A blow of this game's player on the copy. All of the damage so far is what is sent, so that what
                // went missing on the way comes with the next
                _ownDamage += previousHp - newHp;
                _ownDamageResends = 0;
                SendOwnDamage();
            } else {
                SendHealthState();
            }
        } else if (IsControlled && _ownDamage > _ownDamageTaken &&
                   Time.unscaledTime - _ownDamageSentAt >=
                   (_ownDamageResends < OwnDamageQuickResends ? OwnDamageResendTime : OwnDamageSlowResendTime)) {
            // The game that runs the creature has not told health with all of it in: sent again, as it is all of it
            _ownDamageResends++;
            SendOwnDamage();
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
        ResetHealthOrderingForEpoch(sceneHostEpoch);

        // The health the copies were made with, which the copies in the other games were made with too
        var copiedHp = _lastHp;
        var currentHp = GetCurrentHp();
        ApplyHp(currentHp, triggerHostDeath: false);

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

    /// <inheritdoc />
    public override void InitializeClient(uint sceneHostEpoch) {
        _wasRunElsewhere = true;
        ResetHealthOrderingForEpoch(sceneHostEpoch);
        var currentHp = GetCurrentHp();
        ApplyHp(currentHp, triggerHostDeath: false);
    }

    /// <summary>
    /// Tells the other games the health of the creature as it is here, in the game that runs it, and how much of the
    /// damage of each of their copies is in it already. A copy takes it as it is, less the damage of its own player
    /// that is not in it yet (UpdateHealth). Changes used to be sent as such, from one value to another, and each one
    /// lost on the way left the copies off by that much for as long as the creature lived, more with every stretch in
    /// which nothing arrived; health told as it is is made right by whichever telling comes next.
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
    /// current epoch (see <see cref="TakeInOwnDamage"/>).
    /// </summary>
    private void SendOwnDamage() {
        var hpData = ObjectPool<EntityNetworkData>.Get();
        hpData.Type = EntityComponentType.Health;
        hpData.Packet.Write(HealthMessage.OwnDamage);
        hpData.Packet.Write(_currentHealthEpoch);
        hpData.Packet.Write(_copyKey);
        hpData.Packet.Write(_ownDamage);
        SendData(hpData);

        _ownDamageSentAt = Time.unscaledTime;
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
            _damageTaken.Clear();
            _tellNumber = 0;
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

                // A creature whose FSM plays its own death threw out its body in the replay of that, which came just
                // before this from the same death in the scene host's game (EntityFsmActions, SimulateDeath), and the
                // copy's own death throws out none: the body whose place the scene host sent is that one. Only one
                // thrown out this frame, as a body of an earlier death may since have been taken for another
                var replayedCorpse = _clientCorpseFrame == Time.frameCount ? _clientCorpse : null;

                // Set a boolean to indicate that the client health manager is allowed to execute the Die method
                _allowDeath = true;
                _clientCorpse = null;
                // What the short overload passes, apart from whether the special death was passed over
                _healthManager.Client.Die(
                    attackDirection, attackType, NailElements.None, null, ignoreEvasion, 1f, overrideSpecialDeath, false
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
        // set over, it would be lost
        var client = _healthManager.Client;
        if (client != null && client.hp < _lastHp) {
            _ownDamage += _lastHp - client.hp;
            _lastHp = client.hp;
            _ownDamageResends = 0;
            SendOwnDamage();
        }

        if (epoch > _currentHealthEpoch) {
            ResetHealthOrderingForEpoch(epoch);
        }

        if (epoch == _currentHealthEpoch && number > _toldNumber) {
            _toldHp = hp;
            _toldNumber = number;
            _toldDead = dead;
            _ownDamageTaken = ownDamageTaken;

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

        // As told, less the damage of this game's player that is not in it yet: on its way, or lost and sent again
        ApplyHp(_toldHp - System.Math.Max(0, _ownDamage - _ownDamageTaken), triggerHostDeath: false);

        // Walking into a room that the other player already cleared sends the health of everything in it, but never
        // the deaths themselves - the server keeps health and whether an object is active, and drops death data. The
        // same for the state of the room told again after a stretch in which nothing arrived, which can be all there
        // is of a death that went missing. Health alone leaves a copy that is alive with nothing left, which then asks
        // to die every frame and is turned down every frame, for as long as the room lasts. Its death is played out
        // once here instead, the same way one that arrives over the network is - for a creature told dead, not one
        // told it has no health, which some are alive at. A player who is scene host needs none of this: their own
        // object dies by itself and tells the others.
        if (alreadyInSceneUpdate && _toldDead && client != null && !client.GetIsDead()) {
            _allowDeath = true;
            _clientCorpse = null;
            client.Die(null, AttackTypes.Generic, true);
        }
    }

    /// <summary>
    /// Takes in the damage of a copy in another game on the creature that this game runs: what of it is not in the
    /// creature's health yet. All of the copy's damage so far comes each time, so none is taken in twice and none is
    /// lost with a message that went missing. Answered by telling the health as it is then, which is how the copy
    /// knows its damage was taken in.
    /// </summary>
    private void TakeInOwnDamage(EntityNetworkData data) {
        var epoch = data.Packet.ReadUInt();
        var copyKey = data.Packet.ReadUInt();
        var damage = data.Packet.ReadInt();

        if (IsControlled) {
            return;
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

        if (damage > taken.damage) {
            ApplyHp(GetCurrentHp() - (damage - taken.damage), triggerHostDeath: true);
            taken.damage = damage;
        }

        _damageTaken[data.SenderId] = taken;
        SendHealthState();
    }

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
    private void ApplyHp(int newHp, bool triggerHostDeath) {
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
            _healthManager.Host!.Die(null, AttackTypes.Generic, true);
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

        _healthManagerDieHook?.Dispose();
        _healthManagerDieHook = null;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
