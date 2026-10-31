using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using SSMP.Animation.Effects;
using SSMP.Animation.Effects.Movement;
using SSMP.Animation.Effects.SilkSkills;
using SSMP.Animation.Effects.Tools;
using SSMP.Collection;
using SSMP.Fsm;
using SSMP.Game;
using SSMP.Game.Client;
using SSMP.Game.Client.Save;
using SSMP.Game.Client.Skin;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Internals;
using SSMP.Networking.Client;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation;

/// <summary>
/// Class that manages all forms of animation from clients.
/// </summary>
internal class AnimationManager {
    /// <summary>
    /// Whether to log debug messages about animations. For debugging purposes this can be enabled so that all
    /// animation events are logged.
    /// </summary>
    private static bool _debugLogAnimations = false;
    
    /// <summary>
    /// The distance threshold for playing certain effects.
    /// </summary>
    public const float EffectDistanceThreshold = 25f;

    #region Clip Tables

    /// <summary>
    /// Animations that are allowed to loop, because they need to transmit the effect.
    /// </summary>
    private static readonly string[] AllowedLoopAnimations = [
        "Airborne", 
        "Wall Slide"
    ];

    /// <summary>
    /// Clip names of animations that are handled by the animation controller.
    /// </summary>
    private static readonly string[] AnimationControllerClipNames = [
        // "Airborne"
    ];

    /// <summary>
    /// Bi-directional lookup table for linking animation clip names with their respective animation clip enum
    /// values.
    /// </summary>
    private static readonly BiLookup<string, AnimationClip> ClipEnumNames = new() {
        { "Wake To Sit", AnimationClip.WakeToSit },
        { "Sit Idle", AnimationClip.SitIdle },
        { "SitLook 1", AnimationClip.SitLook1 },
        { "SitLook 2", AnimationClip.SitLook2 },
        { "SitLook 3", AnimationClip.SitLook3 },
        { "SitLook 4", AnimationClip.SitLook4 },
        { "Sit Lean", AnimationClip.SitLean },
        { "Sit Look Left", AnimationClip.SitLookLeft },
        { "Sitting Asleep", AnimationClip.SittingAsleep },
        { "Get Off", AnimationClip.GetOff },
        { "Land", AnimationClip.Land },
        { "HardLand", AnimationClip.HardLand },
        { "HardLand Greymoor", AnimationClip.HardLandGreymoor },
        { "HardLand Quick", AnimationClip.HardLandQuick },
        { "Super Hard Land", AnimationClip.SuperHardLand },
        { "Hop", AnimationClip.Hop },
        { "Hop Land", AnimationClip.HopLand },
        { "Hop To Somersault", AnimationClip.HopToSomersault },
        { "Run", AnimationClip.Run },
        { "RunQ", AnimationClip.RunQ },
        { "Run To Idle", AnimationClip.RunToIdle },
        { "Run To Idle C", AnimationClip.RunToIdleC },
        { "Idle", AnimationClip.Idle },
        { "Idle Backward", AnimationClip.IdleBackward},
        { "Idle BG", AnimationClip.IdleBG},
        { "Idle Hurt", AnimationClip.IdleHurt},
        { "Idle Hurt Listen", AnimationClip.IdleHurtListen},
        { "Idle Hurt Listen Backward", AnimationClip.IdleHurtListenBackward},
        { "Idle Hurt Listen Windy", AnimationClip.IdleHurtListenWindy},
        { "Idle Hurt NoNeedle", AnimationClip.IdleHurtNoNeedle},
        { "Idle Hurt Talk", AnimationClip.IdleHurtTalk},
        { "Idle Hurt Talk Backward", AnimationClip.IdleHurtTalkBackward},
        { "Idle Hurt Talk Turn Backward", AnimationClip.IdleHurtTalkTurnBackward},
        { "Idle Hurt Talk Turn Forward", AnimationClip.IdleHurtTalkTurnForward},
        { "Idle Hurt Talk Windy", AnimationClip.IdleHurtTalkWindy},
        { "Idle Hurt Windy", AnimationClip.IdleHurtWindy},
        { "Idle Slap", AnimationClip.IdleSlap},
        { "Idle To Run Short", AnimationClip.IdleToRunShort},
        { "Idle To Run Weak", AnimationClip.IdleToRunWeak},
        { "Idle To Run", AnimationClip.IdleToRun },
        { "Idle Windy", AnimationClip.IdleWindy },
        { "Walk To Idle", AnimationClip.WalkToIdle },
        { "Run To Walk", AnimationClip.RunToWalk },
        { "Run To Walk Weak", AnimationClip.RunToWalkWeak },
        { "Land To Walk", AnimationClip.LandToWalk },
        { "Land to Sprint", AnimationClip.LandToSprint },
        { "Turn", AnimationClip.Turn },
        { "Dash", AnimationClip.Dash },
        { "Dash Down", AnimationClip.DashDown },
        { "Dash Down Land", AnimationClip.DashDownLand },
        { "Dash Down End", AnimationClip.DashDownEnd },
        { "Sprint Lv2", AnimationClip.SprintLv2 },
        { "Sprint Lv3", AnimationClip.SprintLv3 },
        { "Sprint Recoil", AnimationClip.SprintRecoil },
        { "Sprint Skid To Run", AnimationClip.SprintSkidToRun },
        { "Sprint To Run", AnimationClip.SprintToRun },
        { "Mantle Cling", AnimationClip.MantleCling },
        { "Mantle Land", AnimationClip.MantleLand },
        { "Mantle Land To Idle", AnimationClip.MantleLandToIdle },
        { "Mantle Vault", AnimationClip.MantleVault },
        { "Sprint Air Short", AnimationClip.SprintAirShort },
        { "Sprint Turn", AnimationClip.SprintTurn },
        { "Shuttlecock Antic", AnimationClip.ShuttlecockAntic },
        { "Shuttlecock", AnimationClip.Shuttlecock },
        { "Sprint Air", AnimationClip.SprintAir },
        { "Sprint Air Loop", AnimationClip.SprintAirLoop },
        { "Double Jump", AnimationClip.DoubleJump },
        { "Double Jump Wings 2", AnimationClip.DoubleJumpWings2 },
        { "Land To Run", AnimationClip.LandToRun },
        { "Dash Attack Antic", AnimationClip.DashAttackAntic },
        { "Dash Attack", AnimationClip.DashAttack },
        { "Dash Attack Recover", AnimationClip.DashAttackRecover },
        { "Dash Attack Antic Long", AnimationClip.DashAttackAnticLong },
        { "Dash Attack Antic Short", AnimationClip.DashAttackAnticShort },
        { "Skid End 1", AnimationClip.SkidEnd1 },
        { "Skid End 2", AnimationClip.SkidEnd2 },
        { "Dash To Idle", AnimationClip.DashToIdle },
        { "Dash To Run", AnimationClip.DashToRun },
        { "Slash", AnimationClip.Slash },
        { "SlashAlt", AnimationClip.SlashAlt },
        { "Slash To Run", AnimationClip.SlashToRun },
        { "Air Dash", AnimationClip.AirDash },
        { "Slash Land Run Alt", AnimationClip.SlashLandRunAlt },
        { "DownSpike Antic", AnimationClip.DownSpikeAntic },
        { "DownSpike", AnimationClip.DownSpike },
        { "DownSpike Burst", AnimationClip.DownSpikeBurst },
        { "Downspike Recovery", AnimationClip.DownSpikeRecovery },
        { "Downspike Recovery Land", AnimationClip.DownSpikeRecoveryLand },
        { "Downspike Followup", AnimationClip.DownSpikeFollowup },
        { "UpSlash", AnimationClip.UpSlash },
        { "Slash Land", AnimationClip.SlashLand },
        { "Slash_Charged", AnimationClip.SlashCharged },
        { "Slash_Charged_Loop", AnimationClip.SlashChargedLoop },
        { "Umbrella Inflate Antic", AnimationClip.UmbrellaInflateAntic },
        { "Walljump", AnimationClip.WallJump },
        { "Walljump Antic", AnimationClip.WallJumpAntic },
        { "Walljump Puff", AnimationClip.WallJumpPuff },
        { "Walljump Somersault", AnimationClip.WallJumpSomersault },
        { "Wallrun", AnimationClip.Wallrun },
        { "Wallrun Antic", AnimationClip.WallrunAntic },
        { "Wall Slide", AnimationClip.WallSlide },
        { "Wall Cling", AnimationClip.WallCling },
        { "Slide Brake", AnimationClip.SlideBrake },
        { "Slide Brake End", AnimationClip.SlideBrakeEnd },
        { "Slide Fast", AnimationClip.SlideFast },
        { "Slide Fast End", AnimationClip.SlideFastEnd },
        { "Slide Normal", AnimationClip.SlideNormal },
        { "Slide Start", AnimationClip.SlideStart },
        { "Sit Fall Asleep", AnimationClip.SitFallAsleep },
        { "Umbrella Inflate", AnimationClip.UmbrellaInflate },
        { "Umbrella Float", AnimationClip.UmbrellaFloat },
        { "Umbrella Deflate", AnimationClip.UmbrellaDeflate },
        { "Umbrella Turn", AnimationClip.UmbrellaTurn },
        { "Idle Rest", AnimationClip.IdleRest },
        { "Mantle Land To Run", AnimationClip.MantleLandToRun },
        { "Sit", AnimationClip.Sit },
        { "Sprint", AnimationClip.Sprint },
        { "Sprint Backflip", AnimationClip.SprintBackflip },
        { "AirSphere", AnimationClip.AirSphere },
        { "AirSphere Antic", AnimationClip.AirSphereAntic },
        { "AirSphere Dissipate", AnimationClip.AirSphereDissipate },
        { "AirSphere RepeatAntic", AnimationClip.AirSphereRepeatAntic },
        { "Silk Boss Needle Cast", AnimationClip.SilkBossNeedleCast },
        { "Silk Boss Needle Fire", AnimationClip.SilkBossNeedleFire },
        { "Taunt", AnimationClip.Taunt },
        { "Taunt Back", AnimationClip.TauntBack },
        { "Taunt Back End", AnimationClip.TauntBackEnd },
        { "Taunt Back Up", AnimationClip.TauntBackUp },
        { "Taunt End to Idle", AnimationClip.TauntEndtoIdle },
        { "Taunt Idle", AnimationClip.TauntIdle },
        { "Taunt Rings", AnimationClip.TauntRings },
        { "Taunt Rings Flash", AnimationClip.TauntRingsFlash },
        { "Taunt Straight Back", AnimationClip.TauntStraightBack },
        { "Taunt Straight Back Q", AnimationClip.TauntStraightBackQ },
        { "Taunt Thread", AnimationClip.TauntThread },
        { "Challenge Strong", AnimationClip.ChallengeStrong },
        { "Challenge Talk", AnimationClip.ChallengeTalk },
        { "ToChallengeTalk", AnimationClip.ToChallengeTalk },
        { "Challenge Talk End", AnimationClip.ChallengeTalkEnd },
        { "Challenge Talk End ToIdle", AnimationClip.ChallengeTalkEndToIdle },
        { "Challenge Talk End ToTalk", AnimationClip.ChallengeTalkEndToTalk },
        { "Challenge Talk Idle", AnimationClip.ChallengeTalkIdle },
        { "Challenge Talk Idle Start", AnimationClip.ChallengeTalkIdleStart },
        { "Challenge Talk Start", AnimationClip.ChallengeTalkStart },
        { "ChallengeStrongToIdle", AnimationClip.ChallengeStrongToIdle },

        { "Needolin Start", AnimationClip.NeedolinStart },
        { "Needolin StartCancelable", AnimationClip.NeedolinStartCancelable },
        { "Needolin End", AnimationClip.NeedolinEnd },
        { "Needolin Play", AnimationClip.NeedolinPlay },
        { "NeedolinSit Start", AnimationClip.NeedolinSitStart },
        { "NeedolinSit Play", AnimationClip.NeedolinSitPlay },
        { "Needolin Play Low", AnimationClip.NeedolinPlayLow },
        { "Needolin Play Low Transition", AnimationClip.NeedolinPlayLowTransition },
        { "Needolin Play High", AnimationClip.NeedolinPlayHigh },
        { "Needolin Play High Transition", AnimationClip.NeedolinPlayHighTransition },
        { "NeedolinSit End", AnimationClip.NeedolinSitEnd },
        { "NeedolinSit Turn", AnimationClip.NeedolinSitTurn },
        { "Needolin Deep End", AnimationClip.NeedolinDeepEnd },
        { "Needolin Prompted Idle", AnimationClip.NeedolinPromptedIdle },
        { "Needolin Prompted Play End", AnimationClip.NeedolinPromptedPlayEnd },
        { "Needolin Prompted Play Start", AnimationClip.NeedolinPromptedPlayStart },
        { "Needolin Prompted Start", AnimationClip.NeedolinPromptedStart },
        { "Needolin Thread", AnimationClip.NeedolinThread },
        { "Needolin Turn", AnimationClip.NeedolinTurn },
        { "Harpoon Antic", AnimationClip.HarpoonAntic },
        { "Harpoon Throw", AnimationClip.HarpoonThrow },
        { "Harpoon Catch", AnimationClip.HarpoonCatch },
        { "Harpoon Catch Back", AnimationClip.HarpoonCatchBack },
        { "Harpoon Dash", AnimationClip.HarpoonDash },
        { "Harpoon Needle", AnimationClip.HarpoonNeedle },
        { "Harpoon Needle Return", AnimationClip.HarpoonNeedleReturn },
        { "Harpoon Needle Wall Hit", AnimationClip.HarpoonNeedleWallHit },
        { "Harpoon Thread", AnimationClip.HarpoonThread },

        { "Silk Charge End", AnimationClip.SilkChargeEnd },
        { "Map Open", AnimationClip.MapOpen },
        { "Map Idle", AnimationClip.MapIdle },
        { "Map Away", AnimationClip.MapAway },
        { "Map Turn", AnimationClip.MapTurn },
        { "Map Update", AnimationClip.MapUpdate },
        { "Map Walk", AnimationClip.MapWalk },
        { "Sit Map Open", AnimationClip.SitMapOpen },
        { "Sit Map Close", AnimationClip.SitMapClose },
        { "BindCharge Ground", AnimationClip.BindChargeGround },
        { "BindCharge Air", AnimationClip.BindChargeAir },
        { "BindBurst Air", AnimationClip.BindBurstAir },
        { "BindBurst Ground", AnimationClip.BindBurstGround },
        { "Reserve BindBurst Air", AnimationClip.ReserveBindBurstAir },
        { "Reserve BindBurst Ground", AnimationClip.ReserveBindBurstGround },
        { "Bind Cursed End", AnimationClip.BindCursedEnd },
        { "Bind Cursed Start", AnimationClip.BindCursedStart },
        { "Bind Cursed Mid", AnimationClip.BindCursedMid },
        { "Bind First End", AnimationClip.BindFirstEnd },
        { "Bind First Stand", AnimationClip.BindFirstStand },
        { "Bind Flash", AnimationClip.BindFlash },
        { "Bind Silk", AnimationClip.BindSilk },
        { "Bind Silk FirstWeaver", AnimationClip.BindSilkFirstWeaver },
        { "Bind Silk Loop", AnimationClip.BindSilkLoop },
        { "Bind Silk Quick", AnimationClip.BindSilkQuick },
        { "BindCancel Air", AnimationClip.BindCancelAir },
        { "BindCancel Ground", AnimationClip.BindCancelGround },
        { "BindCharge GrabNeedle", AnimationClip.BindChargeGrabNeedle },
        { "Reserve BindCharge Air", AnimationClip.ReserveBindChargeAir },
        { "Reserve BindCharge Ground", AnimationClip.ReserveBindChargeGround },
        { "LookDown", AnimationClip.LookDown },
        { "LookDownEnd", AnimationClip.LookDownEnd },
        
        { "Super Jump Antic", AnimationClip.SuperJumpAntic },
        { "Super Jump Throw", AnimationClip.SuperJumpThrow },
        { "Super Jump Throw Wait", AnimationClip.SuperJumpThrowWait },
        { "Super Jump Jump Antic", AnimationClip.SuperJumpJumpAntic },
        { "Super Jump Loop", AnimationClip.SuperJumpLoop },
        { "Super Jump Hit Roof", AnimationClip.SuperJumpHitRoof },
        { "Super Jump Fall", AnimationClip.SuperJumpFall },
        { "Super Jump Antic Cancel", AnimationClip.SuperJumpAnticCancel },
        { "Super Jump Antic Effect", AnimationClip.SuperJumpAnticEffect },
        { "Super Jump Antic Effect End", AnimationClip.SuperJumpAnticEffectEnd },
        { "Super Jump Catch Cancel", AnimationClip.SuperJumpCatchCancel },
        { "Super Jump Hit Roof Q", AnimationClip.SuperJumpHitRoofQ },
        { "Super Jump Loop Cancel", AnimationClip.SuperJumpLoopCancel },
        { "Super Jump Thread", AnimationClip.SuperJumpThread },
        { "Super Jump Thread Loop", AnimationClip.SuperJumpThreadLoop },

        { "Fall", AnimationClip.Fall },
        { "FallToProstrate", AnimationClip.FallToProstrate },

        { "LookUp", AnimationClip.LookUp },
        { "LookUpEnd", AnimationClip.LookUpEnd },
        { "Surface In", AnimationClip.SurfaceIn },
        { "Surface InToIdle", AnimationClip.SurfaceInToIdle },
        { "Surface Idle", AnimationClip.SurfaceIdle },
        { "Surface IdleToSwim", AnimationClip.SurfaceIdleToSwim },
        { "Surface TurnToSwim", AnimationClip.SurfaceTurnToSwim },
        { "Surface Current In Recover", AnimationClip.SurfaceCurrentInRecover },
        { "Surface Current In Tumble", AnimationClip.SurfaceCurrentInTumble },
        { "Swim Dash", AnimationClip.SwimDash },
        { "Swim Dash Turn", AnimationClip.SwimDashTurn },
        { "Swim Dash Bonk", AnimationClip.SwimDashBonk },
        { "Mantle Cancel To Jump", AnimationClip.MantleCancelToJump },
        { "Mantle Cancel To Jump Backwards", AnimationClip.MantleCancelToJumpBackwards },
        { "Wall Scramble Antic", AnimationClip.WallScrambleAntic },
        { "Wall Scramble", AnimationClip.WallScramble },
        { "Wall Scramble End", AnimationClip.WallScrambleEnd },
        { "Wall Scramble Mantle", AnimationClip.WallScrambleMantle },
        { "Wall Scramble Quickened", AnimationClip.WallScrambleQuickened },
        { "Wall Scramble Repeat", AnimationClip.WallScrambleRepeat },
        { "Wall Slash", AnimationClip.WallSlash },
        { "Somersault Pin Drop", AnimationClip.SomersaultPinDrop },
        { "Airborne", AnimationClip.Airborne },
        { "Bonked", AnimationClip.Bonked },
        { "Sprint Bonk", AnimationClip.SprintBonk },
        { "Bonked Fast", AnimationClip.BonkedFast },
        { "Bonked Land", AnimationClip.BonkedLand },
        { "Slash Land Run", AnimationClip.SlashLandRun },
        { "v3 Down Slash Antic", AnimationClip.V3DownSlashAntic },
        { "v3 Down Slash", AnimationClip.V3DownSlash },
        { "Dash Upper Antic", AnimationClip.DashUpperAntic },
        { "Dash Upper", AnimationClip.DashUpper },
        { "Dash Upper Recovery", AnimationClip.DashUpperRecovery },
        { "Wanderer Dash Attack", AnimationClip.WandererDashAttack },
        { "Wanderer Dash Attack Alt", AnimationClip.WandererDashAttackAlt },
        { "DownSlash", AnimationClip.DownSlash },
        { "DownSlashAlt", AnimationClip.DownSlashAlt },
        { "SpinBall Antic", AnimationClip.SpinBallAntic },
        { "SpinBall Launch", AnimationClip.SpinBallLaunch },
        { "SpinBall", AnimationClip.SpinBall },
        { "NeedleArt Dash", AnimationClip.NeedleArtDash },
        
        { "Rage Bind", AnimationClip.RageBind },
        { "Rage Bind Quick", AnimationClip.RageBindQuick },
        { "Rage Bind Long", AnimationClip.RageBindLong },
        { "Rage Bind Grounded", AnimationClip.RageBindGrounded },
        { "Rage Bind Quick Grounded", AnimationClip.RageBindQuickGrounded },
        { "Rage Bind Long Grounded", AnimationClip.RageBindLongGrounded },
        { "Rage Bind Burst", AnimationClip.RageBindBurst },
        { "Rage Bind Burst Quick", AnimationClip.RageBindBurstQuick },
        { "Rage Bind Burst Long", AnimationClip.RageBindBurstLong },
        { "Land Q", AnimationClip.LandQ },
        { "Rage Idle", AnimationClip.RageIdle },
        { "Rage Idle End", AnimationClip.RageIdleEnd },
        { "Dash Attack Antic 1", AnimationClip.DashAttackAntic1 },
        { "Dash Attack 1", AnimationClip.DashAttack1 },
        { "Dash Attack Antic 2", AnimationClip.DashAttackAntic2 },
        { "Dash Attack 2", AnimationClip.DashAttack2 },
        
        { "BindCharge Witch", AnimationClip.BindChargeWitch },
        { "BindCharge Witch Quick", AnimationClip.BindChargeWitchQuick },
        { "BindCharge Witch Long", AnimationClip.BindChargeWitchLong },
        
        { "DownSpike Charge", AnimationClip.DownSpikeCharge },
        { "DownSpike Charged", AnimationClip.DownSpikeCharged },
        { "Dash Attack Charge", AnimationClip.DashAttackCharge },
        { "Quick Craft Ground", AnimationClip.QuickCraftGround },
        { "Quick Craft Air", AnimationClip.QuickCraftAir },
        { "Dash Attack Leap", AnimationClip.DashAttackLeap },
        { "Dash Attack Slash", AnimationClip.DashAttackSlash },
        { "BindCharge Ground Land", AnimationClip.BindChargeGroundLand },
        { "BindCharge Heal Burst", AnimationClip.BindChargeHealBurst },
        { "BindCharge End", AnimationClip.BindChargeEnd },
        
        { "BindCharge Twirl Air", AnimationClip.BindChargeTwirlAir },
        { "BindCharge Twirl Ground", AnimationClip.BindChargeTwirlGround },
        
        { "NeedleThrow AnticG", AnimationClip.NeedleThrowAnticG },
        { "NeedleThrow AnticA", AnimationClip.NeedleThrowAnticA },
        { "NeedleThrow Throwing", AnimationClip.NeedleThrowThrowing },
        { "NeedleThrow Catch", AnimationClip.NeedleThrowCatch },
        { "NeedleThrow Burst", AnimationClip.NeedleThrowBurst },
        { "NeedleThrow Out", AnimationClip.NeedleThrowOut },
        { "NeedleThrow Return", AnimationClip.NeedleThrowReturn },
        { "NeedleThrow Return Short", AnimationClip.NeedleThrowReturnShort },
        { "NeedleThrow Thread", AnimationClip.NeedleThrowThread },
        { "NeedleThrow Thunk", AnimationClip.NeedleThrowThunk },

        { "Parry Stance", AnimationClip.ParryStance },
        { "Parry Stance Ground", AnimationClip.ParryStanceGround },
        { "Parry Recover", AnimationClip.ParryRecover },
        { "Parry Recover Ground", AnimationClip.ParryRecoverGround },
        { "Parry Clash", AnimationClip.ParryClash },
        { "Parry Clash Effect", AnimationClip.ParryClashEffect },
        { "Parry Dash", AnimationClip.ParryDash },
        { "Parry DashBurst", AnimationClip.ParryDashBurst },
        { "Parry Ready", AnimationClip.ParryReady },
        { "Parry Recovery Skid", AnimationClip.ParryRecoverySkid },
        { "Parry Stance Flash", AnimationClip.ParryStanceFlash },
        { "Parry Stance Flash Q", AnimationClip.ParryStanceFlashQ },
        { "Parry Thread", AnimationClip.ParryThread },
        { "Get Parry Dash", AnimationClip.GetParryDash },
        { "Get Parry End", AnimationClip.GetParryEnd },
        { "Get Parry Prepare", AnimationClip.GetParryPrepare },

        { "AirSphere Attack", AnimationClip.AirSphereAttack },
        { "AirSphere End", AnimationClip.AirSphereEnd },
        { "AirSphere Refresh", AnimationClip.AirSphereRefresh },
        
        { "Silk Charge Antic", AnimationClip.SilkChargeAntic },
        { "Silk Charge", AnimationClip.SilkCharge },
        { "Silk Charge Recover", AnimationClip.SilkChargeRecover },
        { "Silk Charge Zap", AnimationClip.SilkChargeZap },
        { "Silk Charge Recover Zap", AnimationClip.SilkChargeRecoverZap },
        { "Silk Charge Antic Zap", AnimationClip.SilkChargeAnticZap },


        { "Silk Bomb Antic", AnimationClip.SilkBombAntic },
        { "Silk Bomb Antic Q", AnimationClip.SilkBombAnticQ },
        { "Silk Bomb Loop", AnimationClip.SilkBombLoop },
        { "Silk Bomb Recover", AnimationClip.SilkBombRecover },
        { "Silk Bomb Locations", AnimationClip.SilkBombLocations },
        
        { "Sit Craft", AnimationClip.SitCraft },
        { "Sit Craft Silk", AnimationClip.SitCraftSilk },
        { "ToolThrow Up", AnimationClip.ToolThrowUp },
        { "ToolThrow Q", AnimationClip.ToolThrowQ },
        { "ToolThrowAlt Q", AnimationClip.ToolThrowAltQ },
        { "ToolThrow M", AnimationClip.ToolThrowM },
        { "ToolThrow Wall", AnimationClip.ToolThrowWall },

        { "Recoil Twirl", AnimationClip.RecoilTwirl },
        { "DownSpikeBounce 1", AnimationClip.DownSpikeBounce1 },
        { "DownSpikeBounce 2", AnimationClip.DownSpikeBounce2 },
        
        { "Charge Up", AnimationClip.ChargeUp },
        { "Charge Up Air", AnimationClip.ChargeUpAir },
        { "Charge Up Bench", AnimationClip.ChargeUpBench },
        { "Charge Up Bench Silk", AnimationClip.ChargeUpBenchSilk },
        { "Charge Up Burst", AnimationClip.ChargeUpBurst },

        { "TurnWalk", AnimationClip.TurnWalk },
        { "Turn Back Three Quarter", AnimationClip.TurnBackThreeQuarter },
        { "Turn Back Three Quarter EndToIdle", AnimationClip.TurnBackThreeQuarterEndToIdle },
        { "Turn Head Backward", AnimationClip.TurnHeadBackward },
        { "Turn Head Forward", AnimationClip.TurnHeadForward },
        { "Turn Quick", AnimationClip.TurnQuick },
        { "TurnFromBG", AnimationClip.TurnFromBG },
        { "TurnFromBG Loop", AnimationClip.TurnFromBGLoop },
        { "TurnToBG", AnimationClip.TurnToBG },
        { "TurnToBG Loop", AnimationClip.TurnToBGLoop },
        { "TurnToChallengeIdle", AnimationClip.TurnToChallengeIdle },
        { "TurnToChallengeStrong", AnimationClip.TurnToChallengeStrong },
        { "TurnToChallengeTalk", AnimationClip.TurnToChallengeTalk },
        { "TurnToFG", AnimationClip.TurnToFG },
        { "TurnToIdle", AnimationClip.TurnToIdle },
        
        { "Walk", AnimationClip.Walk },
        { "Walk Q", AnimationClip.WalkQ },
        { "Look Up Half", AnimationClip.LookUpHalf },
        { "Look Up Half End", AnimationClip.LookUpHalfEnd },
        { "LookDown Slight", AnimationClip.LookDownSlight },
        { "LookDown Slight End", AnimationClip.LookDownSlightEnd },
        { "Look Down Talk", AnimationClip.LookDownTalk },
        { "Look Up Half Flinch", AnimationClip.LookUpHalfFlinch },
        { "Look Up Half Talk", AnimationClip.LookUpHalfTalk },
        { "Look Up Talk", AnimationClip.LookUpTalk },
        { "LookDown Updraft", AnimationClip.LookDownUpdraft },
        { "LookDown Windy", AnimationClip.LookDownWindy },
        { "LookDownEnd Updraft", AnimationClip.LookDownEndUpdraft },
        { "LookDownEnd Windy", AnimationClip.LookDownEndWindy },
        { "LookDownToIdle", AnimationClip.LookDownToIdle },
        { "LookingUp", AnimationClip.LookingUp },
        { "LookUp Updraft", AnimationClip.LookUpUpdraft },
        { "LookUp Windy", AnimationClip.LookUpWindy },
        { "LookUpEnd Updraft", AnimationClip.LookUpEndUpdraft },
        { "LookUpEnd Windy", AnimationClip.LookUpEndWindy },
        { "LookUpToIdle", AnimationClip.LookUpToIdle },

        { "Scuttle Start", AnimationClip.ScuttleStart },
        { "Scuttle Loop", AnimationClip.ScuttleLoop },
        { "Scuttle TurnToLoop", AnimationClip.ScuttleTurnToLoop },
        { "Scuttle End", AnimationClip.ScuttleEnd },
        { "Scuttle Fall", AnimationClip.ScuttleFall },
        { "Scuttle Vault", AnimationClip.ScuttleVault },
        { "Scuttle Jump", AnimationClip.ScuttleJump },
        { "Scuttle Climb", AnimationClip.ScuttleClimb },
        { "Scuttle Climb End", AnimationClip.ScuttleClimbEnd },
        
        { "Stun", AnimationClip.Stun },
        { "Recoil", AnimationClip.Recoil },
        
        { "Grab Escape", AnimationClip.GrabEscape },
        
        { "Idle Updraft", AnimationClip.IdleUpdraft },
        { "Updraft Antic", AnimationClip.UpdraftAntic },
        { "Updraft Antic DJ", AnimationClip.UpdraftAnticDJ },
        { "Updraft Rise", AnimationClip.UpdraftRise },
        { "Updraft Rise Turn", AnimationClip.UpdraftRiseTurn },
        { "Updraft End", AnimationClip.UpdraftEnd },
        { "Updraft Idle", AnimationClip.UpdraftIdle },
        { "Updraft Shoot", AnimationClip.UpdraftShoot },

        { "Prostrate Rise NoNeedle", AnimationClip.ProstrateRiseNoNeedle },
        { "Bind First", AnimationClip.BindFirst },
        { "BindBurst First", AnimationClip.BindBurstFirst },
        { "Thwip To Idle", AnimationClip.ThwipToIdle },
        { "GetUpToIdle", AnimationClip.GetUpToIdle },

        { "Weakened Stun", AnimationClip.WeakenedStun },
        { "Weak Rise To Idle", AnimationClip.WeakRiseToIdle },
        { "Weak Walk", AnimationClip.WeakWalk },
        { "Weak TryJumpAntic", AnimationClip.WeakTryJumpAntic },
        { "Weak TryAttack", AnimationClip.WeakTryAttack },
        { "Weakened StunEnd", AnimationClip.WeakenedStunEnd },
        { "Weak Fall", AnimationClip.WeakFall },
        { "Weak Flinch To Idle", AnimationClip.WeakFlinchToIdle },
        { "Weak Idle Look Up", AnimationClip.WeakIdleLookUp },
        { "Weak Walk Faster", AnimationClip.WeakWalkFaster },
        { "Weak Walk To Idle", AnimationClip.WeakWalkToIdle },


        { "Taunt Collapse1", AnimationClip.TauntCollapse1 },
        { "Taunt Collapse2", AnimationClip.TauntCollapse2 },
        { "Taunt CollapseHit", AnimationClip.TauntCollapseHit },
        { "Prostrate", AnimationClip.Prostrate },
        { "Prostrate Rise", AnimationClip.ProstrateRise },
        { "Prostrate NoNeedle", AnimationClip.ProstrateNoNeedle },
        { "Prostrate Rise Slow", AnimationClip.ProstrateRiseSlow },
        { "ProstrateRiseToKneel", AnimationClip.ProstrateRiseToKneel },
        { "ProstrateRiseToKneel NoLoop", AnimationClip.ProstrateRiseToKneelNoLoop },
        { "ProstrateRiseToWound", AnimationClip.ProstrateRiseToWound },

        { "Talking Standard", AnimationClip.TalkingStandard },
        { "Talking Backward", AnimationClip.TalkingBackward },

        { "Abyss Kneel", AnimationClip.AbyssKneel },
        { "Abyss Kneel Back Idle", AnimationClip.AbyssKneelBackIdle },
        { "Abyss Kneel Back Talk", AnimationClip.AbyssKneelBackTalk },
        { "Abyss Kneel Idle", AnimationClip.AbyssKneelIdle },
        { "Abyss Kneel to Stand", AnimationClip.AbyssKneeltoStand },
        { "Abyss Kneel Turn Back", AnimationClip.AbyssKneelTurnBack },
        { "Kneel To Prostrate", AnimationClip.KneelToProstrate },
        { "Kneeling", AnimationClip.Kneeling },

        { "Acid Death", AnimationClip.AcidDeath },
        { "Death", AnimationClip.Death },
        { "Death Final", AnimationClip.DeathFinal },
        { "Spike Death", AnimationClip.SpikeDeath },
        { "Spike Death Antic", AnimationClip.SpikeDeathAntic },

        { "Beastling Call Fail", AnimationClip.BeastlingCallFail },
        { "Beastling Call Fail Windy", AnimationClip.BeastlingCallFailWindy },
        { "Bellway Call", AnimationClip.BellwayCall },
        { "Fast Travel Call", AnimationClip.FastTravelCall },
        { "Fast Travel Child Arrive", AnimationClip.FastTravelChildArrive },
        { "Fast Travel Fail", AnimationClip.FastTravelFail },
        { "Fast Travel Leap", AnimationClip.FastTravelLeap },

        { "Collect Heart Piece", AnimationClip.CollectHeartPiece },
        { "Collect Heart Piece End", AnimationClip.CollectHeartPieceEnd },
        { "Collect Memory Orb", AnimationClip.CollectMemoryOrb },
        { "Collect Normal 1", AnimationClip.CollectNormal1 },
        { "Collect Normal 1 Q", AnimationClip.CollectNormal1Q },
        { "Collect Normal 2", AnimationClip.CollectNormal2 },
        { "Collect Normal 3", AnimationClip.CollectNormal3 },
        { "Collect Normal 3 Q", AnimationClip.CollectNormal3Q },
        { "Collect Silk Heart", AnimationClip.CollectSilkHeart },
        { "Collect Stand 1", AnimationClip.CollectStand1 },
        { "Collect Stand 2", AnimationClip.CollectStand2 },
        { "Collect Stand 3", AnimationClip.CollectStand3 },
        { "CollectToWound", AnimationClip.CollectToWound },
        { "DropToWounded", AnimationClip.DropToWounded },
        { "Blue Health Over Burst", AnimationClip.BlueHealthOverBurst },
        { "Crest Shrine Powerup Loop", AnimationClip.CrestShrinePowerupLoop },
        { "Hazard Respawn", AnimationClip.HazardRespawn },
        { "Respawn Wake", AnimationClip.RespawnWake },
        { "Pod Bounce", AnimationClip.PodBounce },
        { "Quick Charged", AnimationClip.QuickCharged },
        { "Quick Craft Silk", AnimationClip.QuickCraftSilk },
        { "Sprintmaster Low", AnimationClip.SprintmasterLow },
        { "Sprintmaster Start", AnimationClip.SprintmasterStart },

        { "Dress Flourish", AnimationClip.DressFlourish },
        { "Give Dress", AnimationClip.GiveDress },
        { "Give Dress Idle", AnimationClip.GiveDressIdle },
        { "Enter", AnimationClip.Enter },
        { "Exit", AnimationClip.Exit },
        { "Exit Door To Idle", AnimationClip.ExitDoorToIdle },
        { "Exit To Idle", AnimationClip.ExitToIdle },
        { "Wake", AnimationClip.Wake },
        { "Wake Up Ground", AnimationClip.WakeUpGround },

        { "Hurt Listen Down", AnimationClip.HurtListenDown },
        { "Hurt Listen Up", AnimationClip.HurtListenUp },
        { "Hurt Look Down", AnimationClip.HurtLookDown },
        { "Hurt Look Down Windy", AnimationClip.HurtLookDownWindy },
        { "Hurt Look Down Windy End", AnimationClip.HurtLookDownWindyEnd },
        { "Hurt Look Up", AnimationClip.HurtLookUp },
        { "Hurt Look Up End", AnimationClip.HurtLookUpEnd },
        { "Hurt Look Up Windy", AnimationClip.HurtLookUpWindy },
        { "Hurt Look Up Windy End", AnimationClip.HurtLookUpWindyEnd },
        { "Hurt Talk Down", AnimationClip.HurtTalkDown },
        { "Hurt Talk Up", AnimationClip.HurtTalkUp },
        { "Hurt Talk Up Windy", AnimationClip.HurtTalkUpWindy },
        { "Hurt To Idle", AnimationClip.HurtToIdle },

        { "Needle Fall", AnimationClip.NeedleFall },
        { "Needle Land", AnimationClip.NeedleLand },

        { "Ring Drop Impact", AnimationClip.RingDropImpact },
        { "Ring Eject", AnimationClip.RingEject },
        { "Ring Grab Hornet", AnimationClip.RingGrabHornet },
        { "Ring Grab Rail", AnimationClip.RingGrabRail },
        { "Ring Harpoon Connect", AnimationClip.RingHarpoonConnect },
        { "Ring Look Down", AnimationClip.RingLookDown },
        { "Ring Look Down End", AnimationClip.RingLookDownEnd },
        { "Ring Look Up", AnimationClip.RingLookUp },
        { "Ring Look Up End", AnimationClip.RingLookUpEnd },
        { "Ring Turn", AnimationClip.RingTurn },

        { "Roar Lock", AnimationClip.RoarLock },
        { "Roar To LookUp", AnimationClip.RoarToLookUp },

        { "Spa Surface Idle", AnimationClip.SpaSurfaceIdle },
        { "Spa Surface IdleToSwim", AnimationClip.SpaSurfaceIdleToSwim },
        { "Spa Surface In", AnimationClip.SpaSurfaceIn },
        { "Spa Surface InToIdle", AnimationClip.SpaSurfaceInToIdle },
        { "Spa Surface TurnToSwim", AnimationClip.SpaSurfaceTurnToSwim },

        { "Weaver Pray", AnimationClip.WeaverPray },
        { "Weaver Pray End", AnimationClip.WeaverPrayEnd },
        { "Weaver Pray Prepare", AnimationClip.WeaverPrayPrepare },
        { "Weaver Pray Prepare Front", AnimationClip.WeaverPrayPrepareFront },

        { "Wound", AnimationClip.Wound },
        { "Wound Double Strike", AnimationClip.WoundDoubleStrike },
        { "Wound Zap", AnimationClip.WoundZap },

        { "Bench", AnimationClip.Bench },

        { "Witch Tentacles!", AnimationClip.WitchTentacles },
        { "Shaman Cancel", AnimationClip.ShamanCancel },
        { "Bind Fail Burst", AnimationClip.BindInterrupt },
        { "Tool Magnetite Dice", AnimationClip.ToolMagnetiteDice },
        { "Tool Flea Brew", AnimationClip.ToolFleaBrew },
        { "Tool Fractured Mask", AnimationClip.ToolFracturedMask },
        { "Tool Magma Bell", AnimationClip.ToolMagmaBell },
        { "Tool Straight Pin ", AnimationClip.ToolStraightPin  },
        { "Tool Threefold Pin", AnimationClip.ToolThreefoldPin },
        { "Tool Long Pin", AnimationClip.ToolLongpin },
        { "Tool Tacks", AnimationClip.ToolTacks }
    };

    /// <summary>
    /// Dictionary mapping animation clip enum values to IAnimationEffect instantiations.
    /// </summary>
    private static readonly Dictionary<AnimationClip, IAnimationEffect> AnimationEffects = new() {
        { AnimationClip.Slash, new Slash(SlashBase.SlashType.Normal) },
        { AnimationClip.SlashAlt, new Slash(SlashBase.SlashType.NormalAlt) },
        { AnimationClip.UpSlash, new Slash(SlashBase.SlashType.Up) },
        { AnimationClip.WallSlash, new Slash(SlashBase.SlashType.Wall) },
        { AnimationClip.DownSpike, new DownSpike() }, // Hunter Crest down slash
        { AnimationClip.DownSpikeCharged, new DownSpike() }, // Architect Crest charged down slash
        { AnimationClip.V3DownSlash, new Slash(SlashBase.SlashType.Down) },
        { AnimationClip.DownSlash, new Slash(SlashBase.SlashType.Down) },
        { AnimationClip.DownSlashAlt, new Slash(SlashBase.SlashType.DownAlt) },
        { AnimationClip.SpinBall, new Slash(SlashBase.SlashType.Down) },
        { AnimationClip.DashAttackAntic, DashSlashAntic.Instance },
        { AnimationClip.DashAttackAntic1, DashSlashAntic.Instance },
        { AnimationClip.DashAttackAntic2, DashSlashAntic.Instance },
        { AnimationClip.DashAttack, new DashSlash(DashSlash.DashSlashType.Shared) },
        { AnimationClip.DashAttack1, new DashSlash(DashSlash.DashSlashType.Witch1) },
        { AnimationClip.DashAttack2, new DashSlash(DashSlash.DashSlashType.Witch2) },
        { AnimationClip.DashUpperAntic, DashSlashAntic.Instance },
        { AnimationClip.DashUpper, new DashSlashReaper() },
        { AnimationClip.WandererDashAttack, new Slash(SlashBase.SlashType.Dash) },
        { AnimationClip.WandererDashAttackAlt, new Slash(SlashBase.SlashType.DashAlt) },
        { AnimationClip.DashAttackCharge, DashSlashAntic.Instance },
        { AnimationClip.DashAttackSlash, new Slash(SlashBase.SlashType.Dash) },
        { AnimationClip.SlashCharged, new NeedleStrike(false) },
        { AnimationClip.SlashChargedLoop, new NeedleStrike(true) },
        { AnimationClip.NeedleArtDash, new NeedleStrike(false) },
        { AnimationClip.BindChargeGround, new Bind() },
        { AnimationClip.BindChargeGroundLand, new Bind { BindState = Bind.State.ShamanDoneFalling } },
        { AnimationClip.BindBurstGround, BindBurst.Instance },
        { AnimationClip.BindChargeHealBurst, BindBurst.Instance },
        { AnimationClip.BindBurstAir, BindBurst.Instance },
        { AnimationClip.RageBindBurst, BindBurst.Instance },
        { AnimationClip.Death, new Death() },
        { AnimationClip.DoubleJump, new FaydownCloak() },
        { AnimationClip.UmbrellaInflate, new DriftersCloak() },

        // Silk Skills
        { AnimationClip.NeedleThrowThrowing, new SilkSpear() },
        { AnimationClip.AirSphereAttack, new ThreadStorm() },
        { AnimationClip.SilkCharge, new SharpDart() },
        { AnimationClip.SilkChargeZap, new SharpDart { Volt = true } },
        { AnimationClip.ParryStance, CrossStitch.StartingInstance },
        { AnimationClip.ParryStanceGround, CrossStitch.StartingInstance },
        { AnimationClip.ParryClash, new CrossStitch() },
        { AnimationClip.SilkBombAntic, new RuneRage { IsAntic = true } },
        { AnimationClip.SilkBossNeedleCast, new PaleNails { IsAntic = true } },

        // Needolin play clips, which send whether the Musician Charm is equipped (see NeedolinCoop)
        { AnimationClip.NeedolinPlay, NeedolinEffect.Instance },
        { AnimationClip.NeedolinSitPlay, NeedolinEffect.Instance },
        { AnimationClip.NeedolinTurn, NeedolinEffect.Instance },
        { AnimationClip.NeedolinSitTurn, NeedolinEffect.Instance },
        { AnimationClip.NeedolinPlayHigh, NeedolinEffect.Instance },
        { AnimationClip.NeedolinPlayHighTransition, NeedolinEffect.Instance },
        { AnimationClip.NeedolinPlayLow, NeedolinEffect.Instance },
        { AnimationClip.NeedolinPlayLowTransition, NeedolinEffect.Instance },
    };

    private static readonly Dictionary<AnimationClip, IAnimationEffect> SubAnimationEffects = new() {
        { AnimationClip.WitchTentacles, BindBurst.Instance },
        { AnimationClip.ShamanCancel, new Bind { BindState = Bind.State.ShamanCancel } },
        { AnimationClip.BindInterrupt, BindInterrupt.Instance },
        { AnimationClip.Bench, new Bench() },

        // Silk Skills
        { AnimationClip.AirSphereRefresh, new ThreadStorm() },
        { AnimationClip.SilkBombLocations, new RuneRage() },
        { AnimationClip.SilkBossNeedleFire, new PaleNails() },

        // Tools
        { AnimationClip.ToolMagnetiteDice, new MagnetiteDice() },
        { AnimationClip.ToolFleaBrew, FleaBrew.Instance },
        { AnimationClip.ToolFracturedMask, new FracturedMask() },
        { AnimationClip.ToolMagmaBell, new MagmaBell() },
        { AnimationClip.ToolStraightPin, new StraightPin() },
        { AnimationClip.ToolThreefoldPin, new ThreefoldPin() },
        { AnimationClip.ToolLongpin, new Longpin() },
        { AnimationClip.ToolTacks, new Tacks() },
        { AnimationClip.ToolThrown, new ThrownTool() },

        // Parts of the hero that the game switches on and off, what they play and start, and what the hero says as it
        // taunts
        { AnimationClip.HeroChildEffect, HeroChildEffects.Instance },
        { AnimationClip.TauntVoice, new TauntVoice() },
        { AnimationClip.HeroPartClip, HeroPartClips.Instance },
        { AnimationClip.HeroChildParticle, HeroChildParticles.Instance },

        // The game hiding the hero, and a figure of the hero that the room shows in its place
        { AnimationClip.HeroHidden, HeroHidden.Instance },
        { AnimationClip.HeroRoomFigure, HeroRoomFigure.Instance },

        // The dust that the game raises anew under the hero
        { AnimationClip.HeroDust, HeroDust.Instance },

        // What the round of a game that each player plays on their own shows, for the players who watch it
        { AnimationClip.MinigameView, MinigameView.Instance },

        // The needle that the harpoon throws, the threads it draws and the spark of a needle striking a wall
        { AnimationClip.HeroHarpoon, HeroHarpoon.Instance },
        { AnimationClip.HeroHit, HeroHit.Instance }
    };

    #endregion

    /// <summary>
    /// The net client for sending animation updates.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The player manager to get player objects.
    /// </summary>
    private readonly PlayerManager _playerManager;

    /// <summary>
    /// Copied instance of the player data to get positions for targeting of Silk Skills.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// Copied instance of the server settings for checking whether PvP, Teams, etc. are enabled.
    /// </summary>
    private ServerSettings _serverSettings;

    /// <summary>
    /// The last animation clip sent.
    /// </summary>
    private string? _lastAnimationClip;

    /// <summary>
    /// How often the local player says again what their character plays (see <see cref="SendClipState"/>), in
    /// seconds: as often as their position.
    /// </summary>
    private const float ClipStateEvery = 1f / 60f;

    /// <summary>
    /// How far a clip that plays once may be ahead on the figure of another player of where that player's character is
    /// in it, in seconds, before it is taken to have been started again in their game. More than a figure falls behind
    /// a character by its clip changes taking a different time over the network from one to the next.
    /// </summary>
    private const float ClipRestartedAfter = 0.15f;

    /// <summary>
    /// How far behind the newest word of another player a word that says how many clip changes they sent may be before
    /// it is taken for a count started over rather than a late word: their game started anew. No word comes that late.
    /// </summary>
    private const short ClipChangesStartedOver = -1000;

    /// <summary>
    /// How often the clip changes of other players that were put right or left out are written down, in seconds.
    /// </summary>
    private const float ClipLogEvery = 60f;

    /// <summary>
    /// How many clip changes of its character the local player has sent, counted round (see
    /// <see cref="SendClipState"/>).
    /// </summary>
    private ushort _clipChanges;

    /// <summary>
    /// The clip of the last clip change that the local player sent, or null before they sent one on this connection.
    /// </summary>
    private AnimationClip? _stateClip;

    /// <summary>
    /// When the local player sent it, in game time.
    /// </summary>
    private float _stateClipSentAt;

    /// <summary>
    /// Its effect info when what its effect sets holds while it plays (see
    /// <see cref="AnimationEffect.HoldsWhileClipPlays"/>), said with it; otherwise null.
    /// </summary>
    private byte[]? _stateEffectInfo;

    /// <summary>
    /// When the local player next says what their character plays, in unscaled time.
    /// </summary>
    private float _nextClipStateAt;

    /// <summary>
    /// How many clip changes each other player had sent by the newest word heard of what their character plays, by ID.
    /// </summary>
    private readonly Dictionary<ushort, ushort> _clipChangesHeard = new();

    /// <summary>
    /// How many clip changes of each other player never arrived and were put right since they were last written down,
    /// and how many came after newer ones and were left out, by ID.
    /// </summary>
    private readonly Dictionary<ushort, (int PutRight, int LeftOut)> _clipFixes = new();

    /// <summary>
    /// When the clip changes put right or left out are next written down, in unscaled time.
    /// </summary>
    private float _nextClipLogAt;

    /// <summary>
    /// Whether we should stop sending animations until the scene has changed.
    /// </summary>
    private bool _stopSendingAnimationUntilSceneChange;

    /// <summary>
    /// Whether the current dash has ended and we can start a new one.
    /// </summary>
    private bool _dashHasEnded = true;

    public AnimationManager(
        NetClient netClient,
        PlayerManager playerManager,
        ServerSettings serverSettings,
        Dictionary<ushort, ClientPlayerData> playerData
    ) {
        _netClient = netClient;
        _playerManager = playerManager;
        _playerData = playerData;
        _serverSettings = serverSettings;

        HeroRoomFigure.DressCopy = playerManager.DressStandIn;
        HeldLook.SheetOf = playerManager.FigureSheet;
        HeroCopies.Dress = playerManager.DressHeroCopy;
        HeroCopies.CharacterOf = playerManager.CharacterOf;
    }

    /// <summary>
    /// Initialize the animation manager by registering packet handlers and initializing animation effects.
    /// </summary>
    public void Initialize(ServerSettings serverSettings) {
        _serverSettings = serverSettings;

        // Set the server settings for all animation effects
        foreach (var effect in AnimationEffects.Values) {
            effect.SetServerSettings(serverSettings);
        }

        foreach (var effect in SubAnimationEffects.Values) {
            effect.SetServerSettings(serverSettings);
        }
    }

    #region Core Animation Hooks

    /// <summary>
    /// Register the game hooks for the animation manager.
    /// </summary>
    public void RegisterHooks() {
        StartClipStatesOver();

        // Register scene change, which is where we update the animation event handler
        SceneManager.activeSceneChanged += OnSceneChange;

        // Register callbacks for tracking the start of playing animation clips
        EventHooks.SpriteAnimatorWarpClipToLocalTime += Tk2dSpriteAnimatorOnWarpClipToLocalTime;
        EventHooks.SpriteAnimatorProcessEvents += Tk2dSpriteAnimatorOnProcessEvents;

        EventHooks.HeroControllerDie += OnDeath;

        // Register FSM hooks for certain bind actions
        HeroController.OnHeroInstanceSet += CreateHeroHooks;
        if (HeroController.SilentInstance != null) {
            CreateHeroHooks(HeroController.instance);
        }

        EventHooks.UseLavaBell += OnMagmaBell;

        ToolCopies.Install();
        ToolCopies.MessageReady += OnToolMessage;
        MonoBehaviourUtil.Instance.OnUpdateEvent += ToolCopies.Tick;
    }

    /// <summary>
    /// Deregister the game hooks for the animation manager.
    /// </summary>
    public void DeregisterHooks() {
        SceneManager.activeSceneChanged -= OnSceneChange;

        HeroController.OnHeroInstanceSet -= CreateHeroHooks;
        FsmStateActionInjector.UninjectAll();

        MagnetiteDice.Unhook();

        EventHooks.HeroControllerDie -= OnDeath;
        EventHooks.UseLavaBell -= OnMagmaBell;

        ToolItemManager.BoundAttackToolUsed -= AttackToolUsed;
        ToolCopies.MessageReady -= OnToolMessage;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= ToolCopies.Tick;

        // Remove listener for benching
        var eventRegister = HeroController.SilentInstance?.gameObject.GetComponents<EventRegister>().FirstOrDefault(r => r.SubscribedEvent == "BENCHREST START");
        if (eventRegister) {
            eventRegister.ReceivedEvent -= OnBench;
        }
    }

    /// <summary>
    /// Callback method when a player animation update is received. Will update the player object with the new
    /// animation.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="clipId">The ID of the animation clip.</param>
    /// <param name="frame">The frame that the animation should play from.</param>
    /// <param name="effectInfo">A byte array containing effect info for the animation.</param>
    /// <param name="showClip">Whether the clip is played too, rather than only its effect: not for a clip change that
    /// came after a newer one (see <see cref="ShowsClipChanges"/>).</param>
    public void OnPlayerAnimationUpdate(ushort id, int clipId, int frame, byte[]? effectInfo, bool showClip = true) {
        var crestType = _playerManager.GetPlayerCrestType(id);

        if (showClip) {
            UpdatePlayerAnimation(id, clipId, frame, crestType);
        } else if (ClipEnumNames.ContainsSecond((AnimationClip) clipId)) {
            NoteClipFix(id, putRight: false);
        }

        var animationClip = (AnimationClip) clipId;

        if (!AnimationEffects.TryGetValue(animationClip, out var animationEffect)) {
            SubAnimationEffects.TryGetValue(animationClip, out animationEffect);
        }

        if (animationEffect != null) {
            var playerObject = _playerManager.GetPlayerObject(id);
            if (!playerObject) {
                return;
            }

            // Check if the animation effect is a DamageAnimationEffect and if so,
            // set whether it should deal damage based on player teams
            if (animationEffect is DamageAnimationEffect damageAnimationEffect) {
                var localPlayerTeam = _playerManager.LocalPlayerTeam;
                var otherPlayerTeam = _playerManager.GetPlayerTeam(id);

                damageAnimationEffect.SetShouldDoDamage(
                    otherPlayerTeam != localPlayerTeam
                    || otherPlayerTeam.Equals(Team.None)
                    || localPlayerTeam.Equals(Team.None)
                );
            }
            
            if (_debugLogAnimations) Logger.Info($"Playing animation effect for animation clip: {animationClip}, {animationEffect.GetType()}");

            animationEffect.Play(
                playerObject,
                crestType,
                effectInfo
            );
        }
    }

    /// <summary>
    /// Update the animation of the player sprite animator.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="clipId">The ID of the animation clip.</param>
    /// <param name="frame">The frame that the animation should play from.</param>
    /// <param name="crestType">The type of crest the player is using.</param>
    /// <param name="startTime">How far into the clip to start it, in seconds, or 0 to start it at the frame.</param>
    /// <returns>Whether a clip was started on the player's figure.</returns>
    public bool UpdatePlayerAnimation(ushort id, int clipId, int frame, CrestType crestType, float startTime = 0f) {
        var playerObject = _playerManager.GetPlayerObject(id);
        if (!playerObject) {
            return false;
        }

        var animationClip = (AnimationClip) clipId;

        if (_debugLogAnimations) Logger.Info($"Received PlayerAnimationUpdate: {animationClip}");

        if (SubAnimationEffects.ContainsKey(animationClip)) {
            if (_debugLogAnimations) Logger.Info($"PlayerAnimationUpdate was sub-effect: {animationClip}");
            return false;
        }

        // Track whether the player is playing the needolin, so needolin range checks count them
        NeedolinCoop.OnRemoteAnimation(playerObject!, animationClip);

        if (!ClipEnumNames.ContainsSecond(animationClip)) {
            // This happens when we send custom clips, that can't be played by the sprite animator, so for now we
            // don't log it. This warning might be useful if we seem to be missing animations from the player's
            // sprite animator.

            if (_debugLogAnimations) Logger.Warn($"Tried to update animation, but there was no entry for clip ID: {clipId}, enum: {animationClip}");
            return false;
        }

        var clipName = ClipEnumNames[animationClip];
        if (clipName == null) {
            Logger.Warn($"Clip name was null after lookup in {nameof(ClipEnumNames)}");
            return false;
        }

        if (_debugLogAnimations) Logger.Info($"  clipName: {clipName}");

        // The body of a player lying in their cocoon is kept off the screen, which playing a clip on it would undo
        if (CoopSave.HoldsBackAnimation(id, clipName)) {
            return false;
        }

        // Get the sprite animator and check whether this clip can be played before playing it
        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();

        var clip = spriteAnimator.GetClipByName(clipName);

        // Get the config groups for the used crest type, so we can find animation clips that should override the
        // normal animation library
        if (!AnimationUtil.GetConfigsFromCrestType(
            crestType,
            out var configGroup,
            out var overrideGroup
        )) {
            Logger.Warn($"Could not get configs from crest type: {crestType}");
            return false;
        }

        var clipInOverrideGroupLib = false;

        if (overrideGroup != null) {
            if (AnimationUtil.TryFindClipInOverrideGroup(overrideGroup, clipName, out var overrideGroupClip)) {
                // The character's own copy, which draws from sheets of its own, in what its player wears
                clip = _playerManager.FigureClip(
                    playerObject!,
                    overrideGroup.Config.heroAnimOverrideLib,
                    overrideGroupClip!
                );
                clipInOverrideGroupLib = true;
                
                if (_debugLogAnimations) Logger.Info("Found clip in override group's override animation library");
            }
        }

        if (configGroup != null && !clipInOverrideGroupLib) {
            if (AnimationUtil.TryFindClipInOverrideGroup(configGroup, clipName, out var configGroupClip)) {
                clip = _playerManager.FigureClip(
                    playerObject!,
                    configGroup.Config.heroAnimOverrideLib,
                    configGroupClip!
                );
                
                if (_debugLogAnimations) Logger.Info("Found clip in config group's override animation library");
            }
        }

        if (clip == null) {
            Logger.Warn("Could not find clip in normal library, config group's override library, or override group's override library");
            return false;
        }

        if (_debugLogAnimations) Logger.Info($"  playing clip: {clipName}");
        if (startTime <= 0f) {
            spriteAnimator.PlayFromFrame(clip, frame);
            return true;
        }

        // A clip that plays once stays on its last frame after its end, where a time past the end would show its first
        // frame until the next update
        if (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once && clip.fps > 0f && clip.frames.Length > 0) {
            startTime = Mathf.Min(startTime, (clip.frames.Length - 0.01f) / clip.fps);
        }

        spriteAnimator.Play(clip, startTime, 0f);
        return true;
    }

    /// <summary>
    /// Says again in the packet that is being put together what the local player's character plays now: the clip of
    /// the last clip change they sent, how many they sent, and how long ago that one started. A clip change is said
    /// once, in a packet that is not sent again if it is lost on the way; said with every update, what the character
    /// plays reaches the other players with the next one that arrives (see <see cref="OnPlayerClipState"/>).
    /// </summary>
    public void SendClipState() {
        if (_stateClip is not { } clip || Time.unscaledTime < _nextClipStateAt) {
            return;
        }

        _nextClipStateAt = Time.unscaledTime + ClipStateEvery;
        var playedFor = Mathf.Clamp((Time.time - _stateClipSentAt) * 1000f, 0f, ushort.MaxValue);
        _netClient.UpdateManager.UpdatePlayerClipState(_clipChanges, clip, (ushort) playedFor, _stateEffectInfo);
    }

    /// <summary>
    /// Sends a clip change of the local player's character, counted, and from then on says with every update that the
    /// character plays its clip (see <see cref="SendClipState"/>).
    /// </summary>
    /// <param name="animationClip">The clip.</param>
    /// <param name="effectInfo">The effect info of the clip, if it has an effect.</param>
    private void SendClipChange(AnimationClip animationClip, byte[]? effectInfo) {
        _clipChanges = unchecked((ushort) (_clipChanges + 1));
        _stateClip = animationClip;
        _stateClipSentAt = Time.time;
        _stateEffectInfo = AnimationEffects.TryGetValue(animationClip, out var effect) &&
                           effect is AnimationEffect { HoldsWhileClipPlays: true }
            ? effectInfo
            : null;
        _netClient.UpdateManager.UpdatePlayerClipChange(animationClip, effectInfo, _clipChanges, _stateEffectInfo);
    }

    /// <summary>
    /// Starts over what the local player says and hears of the clips that characters play, for a new connection.
    /// <list type="bullet">
    /// <item>The counts of a player whose game started anew begin again: kept, they made the clip changes of that
    /// player look late, and these were left out.</item>
    /// <item>What the local character played before it is not what it plays now: said again, it put a new figure of
    /// theirs on a clip they had left while not connected.</item>
    /// <item>The last clip sent is forgotten, so that the next one is sent even when it is that clip.</item>
    /// </list>
    /// </summary>
    private void StartClipStatesOver() {
        _stateClip = null;
        _stateEffectInfo = null;
        _lastAnimationClip = null;
        _clipChangesHeard.Clear();
    }

    /// <summary>
    /// Whether the clip changes in an update of another player are to be played, by how many clip changes the player
    /// said they had sent in it: not when a newer word of what their character plays came already, as then these came
    /// late, and their clips are over in the player's game.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="clipChanges">How many clip changes they said they had sent in the update.</param>
    public bool ShowsClipChanges(ushort id, ushort clipChanges) {
        if (!_clipChangesHeard.TryGetValue(id, out var heard)) {
            return true;
        }

        var behind = (short) (clipChanges - heard);
        return behind >= 0 || behind < ClipChangesStartedOver;
    }

    /// <summary>
    /// Takes what another player said their character plays now, which puts right a clip change of theirs that never
    /// arrived: when they sent a change since the last word of it, and their figure plays another clip than the one
    /// they said, that clip is started on it from where their character is in it. Its effect, such as the flash of a
    /// slash, stays lost, unless what the effect sets holds while the clip plays (see
    /// <see cref="AnimationEffect.HoldsWhileClipPlays"/>). A clip that plays once and that they started again counts
    /// as another clip.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="clipChanges">How many clip changes they had sent.</param>
    /// <param name="clipId">The clip of the last of them, which their character plays.</param>
    /// <param name="clipTime">How long ago their character started it, in milliseconds.</param>
    /// <param name="effectInfo">The effect info of that clip, when what its effect sets holds while it plays.</param>
    public void OnPlayerClipState(ushort id, ushort clipChanges, ushort clipId, ushort clipTime, byte[]? effectInfo) {
        WriteDownClipFixes();

        if (_clipChangesHeard.TryGetValue(id, out var heard)) {
            var ahead = (short) (clipChanges - heard);
            if (ahead == 0 || ahead < 0 && ahead >= ClipChangesStartedOver) {
                return;
            }
        }

        // Heard only by a figure that is there. An update that came before it, such as one that overtook the word of
        // its player coming in, was played on nothing; taken as heard, it kept the first word after it from putting
        // the new figure right
        var playerObject = _playerManager.GetPlayerObject(id);
        if (!playerObject) {
            return;
        }

        _clipChangesHeard[id] = clipChanges;

        var animationClip = (AnimationClip) clipId;
        if (!ClipEnumNames.ContainsSecond(animationClip)) {
            return;
        }

        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();
        var shown = spriteAnimator.CurrentClip;
        var startTime = clipTime / 1000f;
        if (shown != null && shown.name == ClipEnumNames[animationClip] &&
            (shown.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once ||
             spriteAnimator.ClipTimeSeconds - startTime < ClipRestartedAfter)) {
            return;
        }

        // Counted only when a clip was started: not one held back on a body in its cocoon, or one missing for a crest
        var crestType = _playerManager.GetPlayerCrestType(id);
        if (UpdatePlayerAnimation(id, clipId, 0, crestType, startTime)) {
            NoteClipFix(id, putRight: true);
        }

        if (effectInfo != null && AnimationEffects.TryGetValue(animationClip, out var effect) &&
            effect is AnimationEffect { HoldsWhileClipPlays: true }) {
            effect.Play(playerObject, crestType, effectInfo);
        }
    }

    /// <summary>
    /// Counts a clip change of another player that was put right or left out, for the log.
    /// </summary>
    private void NoteClipFix(ushort id, bool putRight) {
        _clipFixes.TryGetValue(id, out var fixes);
        _clipFixes[id] = putRight ? (fixes.PutRight + 1, fixes.LeftOut) : (fixes.PutRight, fixes.LeftOut + 1);
    }

    /// <summary>
    /// Writes down how many clip changes of other players were put right and left out, once a minute when any were.
    /// </summary>
    private void WriteDownClipFixes() {
        if (Time.unscaledTime < _nextClipLogAt) {
            return;
        }

        _nextClipLogAt = Time.unscaledTime + ClipLogEvery;
        if (_clipFixes.Count == 0) {
            return;
        }

        Logger.Info(
            $"[Clips] {DateTime.Now:HH:mm:ss} In the last minute, of the clip changes of other players, " +
            string.Join(
                "; ",
                _clipFixes.Select(pair =>
                    $"player {pair.Key}: {pair.Value.PutRight} that never arrived were put right from what they " +
                    $"play, {pair.Value.LeftOut} that came after newer ones were left out"
                )
            )
        );
        _clipFixes.Clear();
    }

    /// <summary>
    /// Callback method when the scene changes.
    /// </summary>
    /// <param name="oldScene">The old scene instance.</param>
    /// <param name="newScene">The name scene instance.</param>
    private void OnSceneChange(Scene oldScene, Scene newScene) {
        // A scene change occurs, so we can send again
        _stopSendingAnimationUntilSceneChange = false;

        // The figures of other players are made anew, and take what their players play from the next word of it
        _clipChangesHeard.Clear();
    }

    /// <summary>
    /// Callback method when a player enters the scene. Their new figure takes what they play from the next word of it,
    /// not from a count heard before it was there, or from a word that was on its way when they left.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    public void OnPlayerEnterScene(ushort id) {
        _clipChangesHeard.Remove(id);
    }

    /// <summary>
    /// Callback method when a player leaves the scene. Used to destroy leftover animation effect objects.
    /// </summary>
    /// <param name="player">The player that left the scene.</param>
    public void OnPlayerLeaveScene(ClientPlayerData player) {
        Tacks.DestroyPlayerTacks(player.PlayerObject);

        // A figure that comes back takes what its player plays from the next word of it
        _clipChangesHeard.Remove(player.Id);
    }

    /// <summary>
    /// Callback method when an animation fires in the sprite animator.
    /// </summary>
    /// <param name="clip">The sprite animation clip.</param>
    private void OnAnimationEvent(tk2dSpriteAnimationClip clip) {
        if (_debugLogAnimations) Logger.Info($"Animation event with name: {clip.name}");

        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        // If we need to stop sending until a scene change occurs, we skip
        if (_stopSendingAnimationUntilSceneChange) {
            return;
        }

        // If this is a clip that should be handled by the animation controller hook, we return
        if (AnimationControllerClipNames.Contains(clip.name)) {
            // Update the last clip name
            _lastAnimationClip = clip.name;

            return;
        }

        if (_debugLogAnimations) Logger.Info($"  conditions 1: {clip.name.Equals(_lastAnimationClip)}, {clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once}, {!AllowedLoopAnimations.Contains(clip.name)}");

        // Skip event handling when we already handled this clip, unless it is a clip with wrap mode once
        if (clip.name.Equals(_lastAnimationClip)
            && clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once
            && !AllowedLoopAnimations.Contains(clip.name)) {
            return;
        }

        if (_debugLogAnimations) Logger.Info($"  conditions 2: {clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Loop}, {clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.LoopSection}, {clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once}");

        // Skip clips that do not have the wrap mode loop, loop-section, once or single. A clip of a single frame is a
        // pose that the hero holds - lying on the ground after a dream sent them out, kneeling, asleep on a bench, the
        // pull of the harpoon - and it was skipped too: the other players saw the player stand where they lay, until
        // the getting up began (USER 10-31 "她还没有动作的时候站着，但是实际上出梦境是躺着的"). Of the clips that go
        // over the network these five are the only ones of a single frame, and none ping-pongs or picks frames at random
        if (clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Loop &&
            clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.LoopSection &&
            clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once &&
            clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Single) {
            return;
        }

        if (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Single) {
            Logger.Info($"[Clips] The character holds the pose '{clip.name}', which the other players are told of");
        }

        // Make sure that when we enter a building, we don't transmit any more animation events
        // TODO: the same issue applied to exiting a building, but that is less trivial to solve
        if (clip.name.Equals("Enter")) {
            _stopSendingAnimationUntilSceneChange = true;
        }

        // Check special case of downwards dashes that trigger the animation event twice
        // We only send it once if the current dash has ended
        if (clip.name.Equals("Dash Down Test1")
            || clip.name.Equals("Shadow Dash Down Test1")
            || clip.name.Equals("Shadow Dash Down Sharp Test1")) {
            if (!_dashHasEnded) {
                return;
            }

            _dashHasEnded = false;
        }

        if (!ClipEnumNames.ContainsFirst(clip.name)) {
            Logger.Warn($"Player sprite animator played unknown clip, name: {clip.name}");
            return;
        }

        var animationClip = ClipEnumNames[clip.name];

        // A stand-in for a part of a bind that has no clip of its own is only an effect, and the figure stays on the
        // clip of the last change. It is sent uncounted, and what the character plays stays that clip: said instead, a
        // clip change lost just before it could not be put right
        if (SubAnimationEffects.TryGetValue(animationClip, out var subEffect)) {
            _netClient.UpdateManager.UpdatePlayerAnimation(animationClip, 0, subEffect.GetEffectInfo());
        } else {
            SendClipChange(
                animationClip,
                AnimationEffects.TryGetValue(animationClip, out var effect) ? effect.GetEffectInfo() : null
            );
        }

        if (_debugLogAnimations) Logger.Info($"  Sending animation: {animationClip}");

        // Update the last clip name, since it changed
        _lastAnimationClip = clip.name;
    }

    /// <summary>
    /// Creates hooks for the Witch Tentacles and Shaman Cancel states in
    /// the Bind fsm once the HeroController is ready.
    /// </summary>
    private void CreateHeroHooks(HeroController hc) {
        // Initialize warding bell FSM if it isn't already.
        // This fills it in with the template
        var bellFsm = HeroController.instance.bellBindFSM;
        if (!bellFsm.fsm.initialized) {
            bellFsm.Init();
        }

        CreateSkillHooks();
        CreateToolHooks();

        HeroChildEffects.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroChildEffect, effectInfo));
        HeroPartClips.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroPartClip, effectInfo));
        HeroChildParticles.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroChildParticle, effectInfo));
        HeroHidden.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroHidden, effectInfo));
        HeroRoomFigure.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroRoomFigure, effectInfo));
        HeroDust.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroDust, effectInfo));
        HeroHarpoon.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroHarpoon, effectInfo));
        HeroHit.Watch(hc, effectInfo => SendSubAnimation(AnimationClip.HeroHit, effectInfo));
        BeamShot.Watch(hc, OnToolMessage);
        if (hc.silkSpecialFSM != null) {
            TauntVoice.Hook(hc.silkSpecialFSM, effectInfo => SendSubAnimation(AnimationClip.TauntVoice, effectInfo));
        } else {
            Logger.Warn("Unable to find Silk Skill FSM to hook the taunt.");
        }

        // Find bind FSM
        var heroFsms = hc.GetComponents<PlayMakerFSM>();

        var bindFsm = heroFsms.FirstOrDefault(fsm => fsm.FsmName == "Bind");
        if (bindFsm != null) {
            // Find FSM states to inject
            var tentacles = bindFsm.GetState("Witch Tentancles!"); // no that's not a typo... at least on my end
            FsmStateActionInjector.Inject(tentacles, OnWitchTentacles, 4);
        
            var shamanCancel = bindFsm.GetState("Shaman Air Cancel");
            FsmStateActionInjector.Inject(shamanCancel, OnShamanCancel);

            var bindInterrupt = bindFsm.GetState("Remove Silk?");
            FsmStateActionInjector.Inject(bindInterrupt, OnBindInterrupt, 2);
        } else {
            Logger.Warn("Unable to find Bind FSM to hook.");
        }

        // Add listener for benching
        var eventRegister = hc.gameObject.GetComponents<EventRegister>().FirstOrDefault(r => r.SubscribedEvent == "BENCHREST START");
        if (eventRegister) {
            eventRegister.ReceivedEvent += OnBench;
        }
    }

    #endregion

    #region Bind Hooks

    /// <summary>
    /// Animation subanimation hook for the Witch Tentacles FSM state.
    /// </summary>
    private void OnWitchTentacles(PlayMakerFSM fsm) {
        var dummyClip = new tk2dSpriteAnimationClip {
            name = "Witch Tentacles!",
            wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
        };
        OnAnimationEvent(dummyClip);
    }

    /// <summary>
    /// Animation subanimation hook for the Shaman Air Cancel FSM state.
    /// </summary>
    
    private void OnShamanCancel(PlayMakerFSM fsm) {
        var dummyClip = new tk2dSpriteAnimationClip {
            name = "Shaman Cancel",
            wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
        };
        OnAnimationEvent(dummyClip);
    }
    
    /// <summary>
    /// Animation subanimation hook for interrupted binds.
    /// </summary>
    private void OnBindInterrupt(PlayMakerFSM fsm) {
        var dummyClip = new tk2dSpriteAnimationClip {
            name = "Bind Fail Burst",
            wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
        };
        OnAnimationEvent(dummyClip);
    }

    #endregion

    /// <summary>
    /// Sends a sub-animation of the local hero, whose effect info says all there is to it, to the other players.
    /// </summary>
    /// <param name="clip">The sub-animation.</param>
    /// <param name="effectInfo">Its effect info.</param>
    private void SendSubAnimation(AnimationClip clip, byte[] effectInfo) {
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(clip, 0, effectInfo);
    }

    #region Silk Skill Hooks

    /// <summary>
    /// Creates hooks for silk skills.
    /// </summary>
    private void CreateSkillHooks() {
        // Silk skill injections
        var silkSkillFsm = HeroController.instance.silkSpecialFSM;
        if (silkSkillFsm == null) {
            Logger.Warn("Unable to find Silk Skill FSM to hook.");
            return;
        }

        // Thread Storm
        var threadStormExtend = silkSkillFsm.GetState("Extend");
        FsmStateActionInjector.Inject(threadStormExtend, OnThreadStormExtend);

        // Rune Rage
        var sonarBuildArray = silkSkillFsm.GetState("Build Enemy Array");
        FsmStateActionInjector.Inject(sonarBuildArray, OnBuildRuneRageArray);

        var blastFinished = silkSkillFsm.GetState("Silk Bomb Recover");
        FsmStateActionInjector.Inject(blastFinished, OnRuneBlastFinished);

        // Pale Nails
        var nailObject = silkSkillFsm.GetAction<SpawnObjectFromGlobalPool>("BossNeedle Cast", 5)?.gameObject.Value;
        var nailFsm = nailObject?.LocateMyFSM("Control");
        if (nailObject == null || nailFsm == null) {
            Logger.Warn("Unable to find Pale Nail FSM to hook.");
            return;
        }

        // Find all existing pale nails
        List<GameObject> nails = [
            nailObject
        ];

        if (ObjectPool.instance.pooledObjects.TryGetValue(nailObject, out var globalPoolNails)) {
            nails.AddRange(globalPoolNails);
        }

        // Add injector components to all current instances of nails
        foreach (var nail in nails) {
            var injector = nail.AddComponent<FsmActionInjectorComponent>();
            List<FsmActionInjectorComponent.Injection> injections = [
                new() {
                    fsmName = nailFsm.FsmName,
                    fsmStateName = "Follow HeroFacingLeft",
                    actionIndex = 12,
                    Hook = OnPaleNailAttackCheck,
                    hookName = "Nail Target"
                },

                new() {
                    fsmName = nailFsm.FsmName,
                    fsmStateName = "Follow HeroFacingRight",
                    actionIndex = 12,
                    Hook = OnPaleNailAttackCheck,
                    hookName = "Nail Target"
                },

                new() {
                    fsmName = nailFsm.FsmName,
                    fsmStateName = "Fire Antic",
                    actionIndex = 0,
                    Hook = OnPaleNailFire,
                    hookName = "Nail Fire"
                }
            ];

            injector.SetInjections(injections);
        }
    }

    /// <summary>
    /// Animation subanimation hook for extending a thread storm.
    /// </summary>
    private void OnThreadStormExtend(PlayMakerFSM fsm) {
        var effectInfo = BaseSilkSkill.GetEffectFlags();
        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.AirSphereRefresh, 0, effectInfo);
    }

    /// <summary>
    /// Influences Rune Rage to be able to target players that can be attacked.
    /// </summary>
    private void OnBuildRuneRageArray(PlayMakerFSM fsm) {
        // The clusters that the cast puts down from here are numbered by it (see RuneRolls)
        RuneRolls.BeginCast();

        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        // Find tracker for Rune Rage
        var sonarObject = HeroController.instance.gameObject
            .FindGameObjectInChildren("Special Attacks")?
            .FindGameObjectInChildren("Sonar Enemy Tracker");

        if (sonarObject == null) return;

        // Get needed components
        var sonar = sonarObject.GetComponent<TrackTriggerObjects>();
        if (sonar == null) return;

        var sonarCollider = sonarObject.GetComponent<CircleCollider2D>();
        if (sonarCollider == null) return;

        // Remove any old player objects
        sonar.Refresh();

        // No need to target if pvp is off
        if (!_serverSettings.IsPvpEnabled) {
            return;
        }

        var radius = sonarCollider.radius * sonar.transform.GetScaleX();

        var inSonar = sonar.insideObjectsList;

        // Find all players within sonar
        foreach (var player in _playerData.Values) {
            if (!player.IsInLocalScene || !player.PlayerObject) {
                continue;
            }

            // Don't bother to target players on same team
            if (_serverSettings.TeamsEnabled && player.Team == _playerManager.LocalPlayerTeam && player.Team != Team.None) {
                inSonar.Remove(player.PlayerObject);
                continue;
            }

            var collider = player.PlayerObject.GetComponent<BoxCollider2D>();
            if (!collider) continue;

            // Determine if the player is within the sonar circle
            var closestPlayerPoint = collider.ClosestPoint(sonarCollider.transform.position);
            var distanceFromCenter = Vector2.Distance(closestPlayerPoint, sonarCollider.transform.position);

            if (distanceFromCenter <= radius) {
                inSonar.AddIfNotPresent(player.PlayerObject);
            } else {
                inSonar.Remove(player.PlayerObject);
            }
        }
    }

    /// <summary>
    /// Animation hook to send the number of the Rune Rage cast and where its clusters are, read from the clusters once
    /// all are down: a cluster put on an enemy is at the enemy, moved a little at random, and one put down at random
    /// is placed after it comes out of the pool.
    /// </summary>
    private void OnRuneBlastFinished(PlayMakerFSM fsm) {
        var effectInfo = RuneRage.EncodeClusters(
            BaseSilkSkill.GetEffectFlags(),
            RuneRolls.Cast,
            RuneRolls.TakeCastClusters()
        );
        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.SilkBombLocations, 0, effectInfo);
    }

    /// <summary>
    /// Influences Pail Nails to be able to target players that can be attacked.
    /// </summary>
    private void OnPaleNailAttackCheck(PlayMakerFSM fsm) {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        var sonarObject = fsm.gameObject;

        // Get needed components
        var sonar = sonarObject.GetComponentInChildren<TrackTriggerObjectsLineOfSight>();
        if (sonar == null) return;

        var sonarCollider = sonarObject.GetComponentInChildren<CircleCollider2D>();
        if (sonarCollider == null) return;

        // Remove any old player objects
        sonar.Refresh();

        // Don't bother targeting if PvP is off
        if (!_serverSettings.IsPvpEnabled) {
            return;
        }

        var radius = sonarCollider.radius * sonar.transform.GetScaleX();
        var inSonar = sonar.insideObjectsList;

        // Find all players within sonar
        foreach (var player in _playerData.Values) {
            if (!player.IsInLocalScene || !player.PlayerObject) {
                continue;
            }

            // Don't bother to target players on same team
            if (_serverSettings.TeamsEnabled && player.Team == _playerManager.LocalPlayerTeam && player.Team != Team.None) {
                continue;
            }

            var collider = player.PlayerObject.GetComponent<BoxCollider2D>();
            if (!collider) continue;

            // Determine if the player is within the sonar circle and is not obstructed
            var closestPlayerPoint = collider.ClosestPoint(sonarCollider.transform.position);
            var distanceFromCenter = Vector2.Distance(closestPlayerPoint, sonarCollider.transform.position);

            if (distanceFromCenter <= radius && sonar.IsCounted(collider.gameObject)) {
                inSonar.AddIfNotPresent(player.PlayerObject);
            }
        }
    }

    /// <summary>
    /// Sends an update to fire a set of nails at a specific target.
    /// </summary>
    private void OnPaleNailFire(PlayMakerFSM fsm) {
        // Only get info from one needle position (the first one)
        var needleOffset = fsm.FsmVariables.GetFsmVector3("Offset");
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (needleOffset.Value.x != 3) return;

        // Don't send if no target, it'll go off on its own.
        var target = fsm.FsmVariables.FindFsmGameObject("Target").Value;
        if (target == null) {
            return;
        }

        // Send nail target info
        var effectInfo = PaleNails.EncodeTargetInfo(target);
        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.SilkBossNeedleFire, 0, effectInfo);
    }

    #endregion

    #region Tool Hooks

    /// <summary>
    /// Creates hooks for tools.
    /// </summary>
    private void CreateToolHooks() {
        MagnetiteDice.Hook(OnDiceEnable);
        MagmaBell.HookRecharge(OnMagmaBellRecharge);

        var toolFsm = HeroController.instance.toolsFSM;
        var brewBurst = toolFsm.GetState("Flea Brew Burst");
        FsmStateActionInjector.Inject(brewBurst, OnFleaBrew, 0, "Flea Brew");

        var maskFsm = HeroController.instance.gameObject
            .FindGameObjectInChildren("Charm Effects")?
            .FindGameObjectInChildren("Fractured Mask Break")?
            .LocateMyFSM("Spawn Effect");

        if (maskFsm) {
            var maskEffect = maskFsm.GetState("Instantiate Effect");
            FsmStateActionInjector.Inject(maskEffect, OnFracturedMaskBreak, 0, "Fractured Mask Break");
        }

        ToolItemManager.BoundAttackToolUsed += AttackToolUsed;
    }

    /// <summary>
    /// Sends a message about a thing that a tool of the local player threw or spawned, for the other players to see.
    /// </summary>
    /// <param name="message">The message, as the effect info of the animation.</param>
    private void OnToolMessage(byte[] message) {
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolThrown, 0, message);
    }

    /// <summary>
    /// Callback method for when the local player uses a tool.
    /// </summary>
    /// <param name="binding">The AttackToolBinding enum for the used tool.</param>
    private void AttackToolUsed(AttackToolBinding binding) {
        var tool = ToolItemManager.GetBoundAttackTool(binding, ToolEquippedReadSource.Active);
        if (tool == null) {
            return;
        }

        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        // Get proper clip from tool name
        if (!BaseAttackTool.ToolNameMap.TryGetValue(tool.name, out var toolClip)) {
            return;
        }

        // Where the things it threw went, which the game rolled, goes with it (see ToolPlacements)
        var effectInfo = ToolPlacements.Append(BaseAttackTool.GetToolInfo());
        _netClient.UpdateManager.UpdatePlayerAnimation(toolClip, 0, effectInfo);
    }

    /// <summary>
    /// Hook for the magnetite dice being triggered.
    /// </summary>
    private void OnDiceEnable() {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolMagnetiteDice);
    }

    /// <summary>
    /// Hook for the flea brew being triggered.
    /// </summary>
    private void OnFleaBrew(PlayMakerFSM fsm) {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        var effectInfo = FleaBrew.Instance.GetEffectInfo();
        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolFleaBrew, 0, effectInfo);
    }

    /// <summary>
    /// Hook for the fractured mask being triggered.
    /// </summary>
    private void OnFracturedMaskBreak(PlayMakerFSM fsm) {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolFracturedMask);
    }

    /// <summary>
    /// Hook for the magma bell being triggered.
    /// </summary>
    private void OnMagmaBell() {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolMagmaBell, 0, [0]);
    }

    /// <summary>
    /// Hook for the magma bell being triggered.
    /// </summary>
    private void OnMagmaBellRecharge() {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.ToolMagmaBell, 0, [1]);
    }

    /// <summary>
    /// Hook for resting on a bench.
    /// </summary>
    private void OnBench() {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.UpdatePlayerAnimation(AnimationClip.Bench);
    }

    #endregion

    /// <summary>
    /// The animator of the player character that <see cref="_heroAnimatorOf"/> is, kept for
    /// <see cref="HeroAnimatorOf"/>.
    /// </summary>
    private tk2dSpriteAnimator? _heroAnimator;

    /// <summary>
    /// The player character whose animator <see cref="_heroAnimator"/> is.
    /// </summary>
    private HeroController? _heroAnimatorOf;

    /// <summary>
    /// The animator of the given player character, looked up again only for another one: the handlers below ask for it
    /// for every animator of the game, every frame.
    /// </summary>
    private tk2dSpriteAnimator HeroAnimatorOf(HeroController hero) {
        if (!ReferenceEquals(hero, _heroAnimatorOf) || _heroAnimator == null) {
            _heroAnimatorOf = hero;
            _heroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
        }

        return _heroAnimator;
    }

    /// <summary>
    /// Callback method on the tk2dSpriteAnimator#WarpClipToLocalTime method. This method executes
    /// the animation event for clips, and we want to know when those clips start playing.
    /// </summary>
    /// <param name="self">The tk2dSpriteAnimator instance.</param>
    /// <param name="clip">The tk2dSpriteAnimationClip instance.</param>
    /// <param name="time">The time to warp to.</param>
    private void Tk2dSpriteAnimatorOnWarpClipToLocalTime(
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float time
    ) {
        var localPlayer = HeroController.instance;
        if (!localPlayer) {
            return;
        }
    
        var spriteAnimator = HeroAnimatorOf(localPlayer);
        if (self != spriteAnimator) {
            return;
        }

        var clipTime = self.clipTime;
        var index = (int) clipTime % clip.frames.Length;
        var frame = clip.frames[index];
    
        if (index == 0 || frame.triggerEvent || AllowedLoopAnimations.Contains(clip.name)) {
            if (_debugLogAnimations) Logger.Info($"OnAnimationEvent from tk2dSpriteAnimatorOnWarpClipToLocalTime: {clip.name}, conditions: {index == 0}, {frame.triggerEvent}, {AllowedLoopAnimations.Contains(clip.name)}");
            OnAnimationEvent(clip);
        }
    }

    /// <summary>
    /// Callback method on the tk2dSpriteAnimator#OnProcessEvents method. This method executes
    /// the animation event for clips, and we want to know when those clips start playing.
    /// </summary>
    /// <param name="self">The tk2dSpriteAnimator instance.</param>
    /// <param name="start">The start of frames to process.</param>
    /// <param name="last">The last frame to process.</param>
    /// <param name="direction">The direction in which to process.</param>
    private void Tk2dSpriteAnimatorOnProcessEvents(
        tk2dSpriteAnimator self,
        int start,
        int last,
        int direction
    ) {
        var localPlayer = HeroController.instance;
        if (!localPlayer) {
            return;
        }

        var spriteAnimator = HeroAnimatorOf(localPlayer);
        if (self != spriteAnimator) {
            return;
        }

        if (start == last) {
            return;
        }

        var num = last + direction;
        var frames = self.CurrentClip.frames;

        var allowedClipNames = new[] { "" };

        for (var i = start + direction; i != num; i += direction) {
            if (i >= frames.Length || i < 0) {
                Logger.Warn("tk2dSpriteAnimator ProcessEvents index out of bounds!");
                continue;
            }

            if (i != 0 && !frames[i].triggerEvent) {
                continue;
            }

            if (_debugLogAnimations) Logger.Info($"OnAnimationEvent from tk2dSpriteAnimatorOnProcessEvents: {self.CurrentClip.name}, conditions: {i}, {frames[i].triggerEvent}");
        }
    }

    /// <summary>
    /// Callback method for when the local player dies.
    /// </summary>
    private void OnDeath(bool nonLethal, bool frostDeath) {
        // If we are not connected, there is nothing to send to
        if (!_netClient.IsConnected) {
            return;
        }

        // A player lying in a cocoon waiting to be pulled back up is not dead as far as the game knows, so a hit that
        // deals its damage directly still asks for a death of theirs whenever their health is at nothing
        // (HeroController.CheckDeathCatch). Nothing is played out for it, and the other player has already been shown
        // the one death there is.
        if (HeroController.instance is { } hero && PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            return;
        }

        Logger.Debug("Client has died, sending PlayerDeath data");

        // Let the server know that we have died
        // Whether it was lethal goes with it. This is sent for every death the game has, and a death that is not
        // lethal is the player being knocked down and getting up again - no cocoon is left, so the other player
        // should not be shown one being spun.
        byte[] effectInfo = [
            (byte)(frostDeath ? 1 : 0),
            (byte)(nonLethal ? 1 : 0)
        ];
        // Counted like the clip changes of the character. Uncounted, a word of what the character plays that came with
        // it or after it put the clip from before the death back on the figure, over the death
        SendClipChange(AnimationClip.Death, effectInfo);
    }

    /// <summary>
    /// Get the AnimationClip enum value for the currently playing animation of the local player.
    /// </summary>
    /// <returns>An AnimationClip enum value.</returns>
    public static AnimationClip GetCurrentAnimationClip() {
        var currentClipName = HeroController.instance.GetComponent<tk2dSpriteAnimator>().CurrentClip.name;

        if (ClipEnumNames.ContainsFirst(currentClipName)) {
            return ClipEnumNames[currentClipName];
        }

        return 0;
    }
}
