using System;
using MonoMod.RuntimeDetour;
using SSMP.Internals;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Being caught and taken to the prison, in a checked two-player save: it happens to all players. The user chose this
/// on 2026-09-24, so that the escape and all that comes after it is something the two of them go through together, and
/// on 2026-10-14 that they go through the capture together as well ("不能一个人被抓就直接两个人都有被抓的过程吗").
///
/// Only one of them is caught. What catches a player is a creature, which acts in the game of the player who runs the
/// room; a player who came into the room later is caught by its copy in their own game, which plays the whole catch
/// for them (Entity.CheckCaughtByCopy, USER 10-14 "可以吧"). The game of the player who was caught tells the others how
/// far the catch got (CoopSaveUpdateKind.PrisonCatch): from the moment the creature has them in its cage - the game's
/// own mark of a player held so - nobody else can move or be hurt either; when it blacks their screen out, the screens
/// of the others go black too; and when it lets go of them without taking them to the prison, it lets go of the others
/// as well. The capture itself is one call of the game's own, made both where it happens and in the other games,
/// which then do to their own player what the capture did: they take away what the prison takes away, and send them
/// in through the same door. That is why the hook sits on that call and not on the creature; the game that follows the
/// partner's catch leaves that call to the partner's game (EntityFsmActions.IsThePlayersOwn).
///
/// A player who can't be held right away - dead, on a bench, being moved already, riding a lift, in the pause menu, or
/// held by something the game is doing to them - is held as soon as they can be, and one who can't be moved when the
/// capture comes is taken as soon as they can be. A player who is in the prison already is left where they are.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The room that a caught player is taken to.
    /// </summary>
    private const string PrisonSceneName = "Slab_03";

    /// <summary>
    /// The door that a caught player comes in through, which plays their waking up in the cell.
    /// </summary>
    private const string PrisonDoorName = "door_slabCaged";

    /// <summary>
    /// The mark that the game keeps of the player while a creature holds them in its cage to take them to the prison,
    /// set by the creature as it catches them, and read by what would grab the player, which leaves them be then.
    /// </summary>
    private const string HeroInCageName = "HeroInCage";

    /// <summary>
    /// How long a catch of the partner's holds the local player at the most without a word of it from their game, in
    /// seconds. The capture comes well within it.
    /// </summary>
    private const float PartnerCatchTimeout = 30f;

    /// <summary>
    /// How long a cage that has the local player is followed at the most before the others are told that it let go of
    /// them, in seconds. The capture comes well within it.
    /// </summary>
    private const float OwnCatchTimeout = 40f;

    /// <summary>
    /// How long the screen of the local player takes to go black along with the partner's, in seconds: about as long as
    /// it takes the partner's.
    /// </summary>
    private const float PrisonFadeTime = 0.3f;

    /// <summary>
    /// How long the screen of the local player takes to come back when the partner's catch lets go, in seconds.
    /// </summary>
    private const float PrisonUnfadeTime = 0.5f;

    /// <summary>
    /// What keeps the local player from moving and from harm while a catch of the partner's holds them.
    /// </summary>
    private static readonly object PrisonHold = new();

    /// <summary>
    /// Hook on the call that takes away what the prison takes from a caught player.
    /// </summary>
    private Hook? _captureHook;

    /// <summary>
    /// Hook on the game keeping a mark of the player, which sees the mark of a player held in a cage.
    /// </summary>
    private Hook? _cageHook;

    /// <summary>
    /// Hook on the screen being faded, which sees a cage black the screen out.
    /// </summary>
    private Hook? _prisonFadeHook;

    /// <summary>
    /// Whether the capture is being made here for the partner, in which case it is not sent back to them.
    /// </summary>
    private bool _capturingForPartner;

    /// <summary>
    /// The name of the partner who was caught, while the local player still has to be taken to the prison after them.
    /// </summary>
    private string? _pendingCaptureBy;

    /// <summary>
    /// The catch of a partner's that holds the local player, or null (see <see cref="UpdatePartnerCatch"/>).
    /// </summary>
    private PartnerCatch? _partnerCatch;

    /// <summary>
    /// The cage that has the local player now, which the others are told about, or null.
    /// </summary>
    private OwnCatch? _ownCatch;

    /// <summary>
    /// Watches for the local player being caught.
    /// </summary>
    private void RegisterPrisonCaptureHooks() {
        _captureHook = CreateHook(
            typeof(HeroSlabCapture).GetMethod(nameof(HeroSlabCapture.ApplyCaptured), Type.EmptyTypes),
            new Action<Action>(OnApplyCaptured)
        );
        _cageHook = CreateHook(
            typeof(StaticVariableList).GetMethod(
                nameof(StaticVariableList.SetValue), StaticFlags, null, [typeof(string), typeof(object), typeof(int)],
                null
            ),
            new Action<Action<string, object, int>, string, object, int>(OnMarkSet)
        );
        _prisonFadeHook = CreateHook(
            typeof(ScreenFaderUtils).GetMethod(
                nameof(ScreenFaderUtils.Fade), StaticFlags, null, [typeof(Color), typeof(Color), typeof(float)], null
            ),
            new Action<Action<Color, Color, float>, Color, Color, float>(OnPrisonScreenFade)
        );
        Entity.Entity.CatchGivenBack += OnOwnCatchGivenBack;
    }

    /// <summary>
    /// Tells the partner that the local player was caught and taken to the prison, so that their game takes them there
    /// as well.
    /// </summary>
    /// <param name="orig">The original method.</param>
    private void OnApplyCaptured(Action orig) {
        orig();

        if (_capturingForPartner) {
            return;
        }

        // Taken there themselves, a catch of a partner's has nothing more to hold them for
        _ownCatch = null;
        ForgetPartnerCatch(HeroController.SilentInstance);

        if (_checkedMembers.Count == 0) {
            return;
        }

        SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.PrisonCapture });
        Logger.Info($"Was caught and taken to the prison, so {GetCheckedNames()} are taken there too");
    }

    /// <summary>
    /// Hook for the game keeping a mark of the player: a creature that set the mark of a player held in its cage has
    /// the local player in it, which the others are told about.
    /// </summary>
    private void OnMarkSet(Action<string, object, int> orig, string name, object value, int sceneTransitionsLimit) {
        orig(name, value, sceneTransitionsLimit);

        if (name != HeroInCageName || value is not true || _ownCatch != null) {
            return;
        }

        // The game's own code goes on afterwards, whatever goes wrong here
        try {
            OnCaged();
        } catch (Exception e) {
            Logger.Error($"Could not tell the others that the local player was caught:\n{e}");
        }
    }

    /// <summary>
    /// The local player is in a cage that takes them to the prison: the others are held.
    /// </summary>
    private void OnCaged() {
        _ownCatch = new OwnCatch(Time.unscaledTime);

        // Their own cage comes first. A catch of a partner's that held them holds them again if it lets go of them.
        if (_partnerCatch is { Frozen: true } held && HeroController.SilentInstance is { } hero && hero != null) {
            held.Frozen = false;
            LetGoOfTheHold(hero);
        }

        if (_checkedMembers.Count == 0) {
            return;
        }

        SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.PrisonCatch, PartCount = 0 });
        Logger.Info($"Was caught in a cage, so {GetCheckedNames()} are held until the capture");
    }

    /// <summary>
    /// Hook for the screen being faded: a cage that has the local player blacking out their screen blacks out the
    /// screens of the others too.
    /// </summary>
    private void OnPrisonScreenFade(
        Action<Color, Color, float> orig,
        Color startColour,
        Color endColour,
        float duration
    ) {
        orig(startColour, endColour, duration);

        if (_ownCatch is not { Dark: false } own || endColour.a < 1f) {
            return;
        }

        own.Dark = true;
        if (_checkedMembers.Count == 0) {
            return;
        }

        try {
            SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.PrisonCatch, PartCount = 1 });
            Logger.Info($"The cage blacked out the screen, so the screens of {GetCheckedNames()} go black too");
        } catch (Exception e) {
            Logger.Error($"Could not tell the others that the screen went black in the cage:\n{e}");
        }
    }

    /// <summary>
    /// A catch that the local player's game led was cut short (Entity.CatchGivenBack): a cage that had them let go.
    /// </summary>
    private void OnOwnCatchGivenBack() {
        if (_ownCatch != null) {
            LetGoOfOwnCatch("their game cut the catch short");
        }
    }

    /// <summary>
    /// The cage that had the local player let go of them without taking them to the prison, so it lets go of the
    /// others too.
    /// </summary>
    /// <param name="why">Why, for the log.</param>
    private void LetGoOfOwnCatch(string why) {
        _ownCatch = null;
        if (_checkedMembers.Count == 0) {
            return;
        }

        SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.PrisonCatch, PartCount = 2 });
        Logger.Info($"The cage let go of the local player ({why}), so it no longer holds {GetCheckedNames()}");
    }

    /// <summary>
    /// A cage that has the local player as they change rooms let go of them, unless the capture took them: it did
    /// before they left then.
    /// </summary>
    private void OnPrisonCatchSceneChanged() {
        if (_ownCatch != null) {
            LetGoOfOwnCatch("they left the room without being taken to the prison");
        }
    }

    /// <summary>
    /// Follows a cage that has the local player: past the black screen, the cage gives them their control back only
    /// when it lets go of them, as the capture takes them to the prison without; and one that takes far longer than a
    /// capture let go of them some other way.
    /// </summary>
    private void UpdateOwnCatch(HeroController hero) {
        if (_ownCatch is not { } own) {
            return;
        }

        if (Time.unscaledTime - own.Since > OwnCatchTimeout) {
            LetGoOfOwnCatch($"it did not take them to the prison within {OwnCatchTimeout} s");
            return;
        }

        var gameManager = global::GameManager.instance;
        if (own.Dark && gameManager != null && !gameManager.IsInSceneTransition && !hero.cState.transitioning &&
            !hero.controlReqlinquished) {
            LetGoOfOwnCatch("they have their control back after their screen went black");
        }
    }

    /// <summary>
    /// The partner told how far a cage that has them got: the local player is held from the catch on, their screen
    /// goes black with the partner's, and they are let go if the cage lets go of the partner. A player in the prison
    /// already is not held, as they are not taken again, and one catch holds them at a time.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="stage">How far it got: 0 caught, 1 the screen went black, 2 let go.</param>
    private void OnPrisonCatch(ClientPlayerData player, ushort stage) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id)) {
            return;
        }

        switch (stage) {
            case 0:
                if (_partnerCatch != null || IsInPrisonClothes()) {
                    return;
                }

                _partnerCatch = new PartnerCatch(player.Id, player.Username);
                Logger.Info($"{player.Username} was caught in a cage, so the local player is held until the capture");

                // In a cage of their own, they are told once it lets go of them, if it does (UpdatePartnerCatch)
                if (_ownCatch == null) {
                    _partnerCatch.Told = true;
                    Chat(CaughtMessage(player.Username));
                }

                break;
            case 1:
                if (_partnerCatch is { } dark && dark.Id == player.Id) {
                    dark.Dark = true;
                    dark.Heard = Time.unscaledTime;
                }

                break;
            case 2:
                if (_partnerCatch is { } freed && freed.Id == player.Id) {
                    ReleasePartnerCatch(
                        HeroController.SilentInstance,
                        $"the cage let go of {player.Username}",
                        Lang.Pick(
                            $"{player.Username} was not taken away, and you can move again.",
                            $"{player.Username} 没被抓走，你也可以动了。"
                        )
                    );
                }

                break;
        }
    }

    /// <summary>
    /// What the local player is told when a partner is caught.
    /// </summary>
    private static string CaughtMessage(string name) {
        return Lang.Pick($"{name} was caught, and you will be caught too.", $"{name} 被抓了，你也要被抓了。");
    }

    /// <summary>
    /// The partner was caught, so the local player goes to the prison after them.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    private void OnPrisonCapture(ClientPlayerData player) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) || IsInPrisonClothes()) {
            return;
        }

        _pendingCaptureBy = player.Username;

        // Held since the catch, they were told then
        if (_partnerCatch is { Told: true } held && held.Id == player.Id) {
            return;
        }

        Chat(Lang.Pick(
            $"{player.Username} was caught, and you are taken along.",
            $"{player.Username} 被抓走了，你也会被一起关进去。"
        ));
    }

    /// <summary>
    /// A member left: a catch of theirs that held the local player has nobody left to say how it ends.
    /// </summary>
    /// <param name="id">The ID of the member.</param>
    private void OnPrisonCatchMemberLeft(ushort id) {
        if (_partnerCatch is { } held && held.Id == id) {
            ReleasePartnerCatch(HeroController.SilentInstance, $"{held.Name} left", null);
        }
    }

    /// <summary>
    /// Follows a cage that has the local player (see <see cref="UpdateOwnCatch"/>), holds them for a cage that has a
    /// partner (see <see cref="UpdatePartnerCatch"/>), and takes them to the prison once they can be moved, after the
    /// partner was caught.
    /// </summary>
    private void UpdatePrisonCapture(HeroController hero) {
        UpdateOwnCatch(hero);
        UpdatePartnerCatch(hero);

        if (_pendingCaptureBy is not { } username) {
            return;
        }

        try {
            // Caught in their own game meanwhile, which already did all of this
            if (IsInPrisonClothes()) {
                _pendingCaptureBy = null;
                ForgetPartnerCatch(hero);
                return;
            }

            // Not out of shared dialogue either, which the move of a delivery takes over and this one doesn't, nor out
            // of a cage of their own, which takes them there itself
            var gameManager = global::GameManager.instance;
            if (gameManager == null || PlayerData.instance.atBench || hero.cState.dead || hero.cState.hazardDeath ||
                hero.cState.hazardRespawning || !CanMoveHeroNow(gameManager, hero) ||
                IsReadingSharedDialogue?.Invoke() == true || _ownCatch != null) {
                return;
            }

            _pendingCaptureBy = null;
            _capturingForPartner = true;
            try {
                HeroSlabCapture.ApplyCaptured();
            } finally {
                _capturingForPartner = false;
            }

            // Let go of before the move, which clears the hold too: kept, it would hold them in the prison as well
            var held = _partnerCatch;
            ForgetPartnerCatch(hero);

            var moved = BeginMove(hero, new global::GameManager.SceneLoadInfo {
                SceneName = PrisonSceneName,
                EntryGateName = PrisonDoorName,
                WaitForSceneTransitionCameraFade = true
            });
            if (moved) {
                Logger.Info($"Taken to the prison after {username}");
                return;
            }

            Logger.Info($"The game didn't start the move to the prison after {username}");
            if (held is { Darkened: true }) {
                ScreenFaderUtils.Fade(ScreenFaderUtils.GetColour(), held.ScreenBefore, PrisonUnfadeTime);
            }
        } catch (Exception e) {
            _pendingCaptureBy = null;
            Logger.Error($"Could not take the local player to the prison after {username}:\n{e}");
        }
    }

    /// <summary>
    /// Holds the local player while a cage has a partner (see <see cref="OnPrisonCatch"/>), as soon as they can be
    /// held: they can't move, and nothing hurts them. Their screen goes black once the partner's does. A catch that
    /// nothing more is heard of lets go of them, unless the capture came already, which takes them along whenever they
    /// can be moved.
    /// </summary>
    private void UpdatePartnerCatch(HeroController hero) {
        if (_partnerCatch is not { } held) {
            return;
        }

        if (_pendingCaptureBy == null && Time.unscaledTime - held.Heard > PartnerCatchTimeout) {
            ReleasePartnerCatch(
                hero,
                $"nothing more was heard of it for {PartnerCatchTimeout} s",
                Lang.Pick(
                    $"Nothing more came from {held.Name}, so you can move again.",
                    $"{held.Name} 那边一直没有消息，你可以动了。"
                )
            );
            return;
        }

        if (!held.Frozen) {
            if (!CanHoldForPartner(hero)) {
                return;
            }

            held.Frozen = true;
            Logger.Info($"The local player is held while {held.Name} is caught");
            if (!held.Told) {
                held.Told = true;
                Chat(CaughtMessage(held.Name));
            }
        }

        // Every frame, as a change of rooms clears both, and the hold goes on in the next room
        hero.AddInputBlocker(PrisonHold);
        hero.AddInvulnerabilitySource(PrisonHold);

        if (held.Dark && !held.Darkened) {
            held.Darkened = true;
            held.ScreenBefore = ScreenFaderUtils.GetColour();
            ScreenFaderUtils.Fade(held.ScreenBefore, Color.black, PrisonFadeTime);
            Logger.Info($"The screen of the local player goes black along with the screen of {held.Name}");
        }
    }

    /// <summary>
    /// Whether the local player can be held for a partner's catch right now: nothing else holds them, like a bench, a
    /// death, a hazard, their cocoon, a scene change, a lift, the pause menu, dialogue or a cage of their own, and they
    /// are not in the prison already.
    /// </summary>
    private bool CanHoldForPartner(HeroController hero) {
        var gameManager = global::GameManager.instance;
        return gameManager != null && _ownCatch == null && !IsInPrisonClothes() && !PlayerData.instance.atBench &&
               !hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning &&
               !PlayerTargetRegistry.IsPlayerDown(hero.gameObject) && CanMoveHeroNow(gameManager, hero) &&
               IsReadingSharedDialogue?.Invoke() != true;
    }

    /// <summary>
    /// Lets the local player go from a partner's catch that held them, and gives them their screen back if it went
    /// black with the partner's.
    /// </summary>
    /// <param name="hero">The hero, or null if there is none.</param>
    /// <param name="why">Why, for the log.</param>
    /// <param name="message">What they are told, if they were told they were held, or null.</param>
    private void ReleasePartnerCatch(HeroController? hero, string why, string? message) {
        if (_partnerCatch is not { } held) {
            return;
        }

        ForgetPartnerCatch(hero);
        if (held.Darkened) {
            ScreenFaderUtils.Fade(ScreenFaderUtils.GetColour(), held.ScreenBefore, PrisonUnfadeTime);
        }

        Logger.Info($"The catch of {held.Name} no longer holds the local player: {why}");
        if (message != null && held.Told) {
            Chat(message);
        }
    }

    /// <summary>
    /// Forgets the partner's catch that held the local player, and lets go of them, leaving their screen as it is.
    /// </summary>
    /// <param name="hero">The hero, or null if there is none.</param>
    private void ForgetPartnerCatch(HeroController? hero) {
        _partnerCatch = null;
        if (hero != null) {
            LetGoOfTheHold(hero);
        }
    }

    /// <summary>
    /// Takes away what keeps the local player still and from harm for a partner's catch.
    /// </summary>
    private static void LetGoOfTheHold(HeroController hero) {
        hero.RemoveInputBlocker(PrisonHold);
        hero.RemoveInvulnerabilitySource(PrisonHold);
    }

    /// <summary>
    /// Forgets the capture and the catches that the local player was in or still to follow, for a new session, and lets
    /// go of them.
    /// </summary>
    private void ResetPrisonCapture() {
        _pendingCaptureBy = null;
        _ownCatch = null;
        ReleasePartnerCatch(HeroController.SilentInstance, "the session ended", null);
    }

    /// <summary>
    /// Whether the local player wears what the prison leaves them in, which they do from being caught until they take
    /// back what it took.
    /// </summary>
    private static bool IsInPrisonClothes() {
        return CrestTypeExt.FromInternal(PlayerData.instance.CurrentCrestID) == CrestType.Cloakless;
    }

    /// <summary>
    /// A catch of a partner's that holds the local player (see <see cref="UpdatePartnerCatch"/>).
    /// </summary>
    /// <param name="id">The ID of the partner.</param>
    /// <param name="name">Their name.</param>
    private sealed class PartnerCatch(ushort id, string name) {
        public ushort Id { get; } = id;
        public string Name { get; } = name;

        /// <summary>
        /// When the partner's game last said anything of it.
        /// </summary>
        public float Heard { get; set; } = Time.unscaledTime;

        /// <summary>
        /// Whether the local player was told that they are held.
        /// </summary>
        public bool Told { get; set; }

        /// <summary>
        /// Whether the local player is held now.
        /// </summary>
        public bool Frozen { get; set; }

        /// <summary>
        /// Whether the screen of the partner went black.
        /// </summary>
        public bool Dark { get; set; }

        /// <summary>
        /// Whether the screen of the local player went black with it.
        /// </summary>
        public bool Darkened { get; set; }

        /// <summary>
        /// The colour of the screen of the local player before it went black.
        /// </summary>
        public Color ScreenBefore { get; set; }
    }

    /// <summary>
    /// A cage that has the local player.
    /// </summary>
    /// <param name="since">When it caught them.</param>
    private sealed class OwnCatch(float since) {
        public float Since { get; } = since;

        /// <summary>
        /// Whether it blacked out their screen yet.
        /// </summary>
        public bool Dark { get; set; }
    }
}
