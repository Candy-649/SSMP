# Co-op coordination flows: protocol survey (SSMP, branch sync-fixes @ 743df53)

The models in this folder were built from this survey. Suspects fixed since it was written: S2.1 (7ad4205), S4.1
(ad2aa9a), S7a.1 (d5add9e), S7b.1 (7311920), S7f.1 (e283ff9), and the frozen partner of a scene already left
(e55e328), which the scene host model found. Line numbers have moved since.

All paths are relative to `D:\programming\silksong_mod\SSMP\SSMP\`. `C/` = `Game/Client/`, `S/` = `Game/Client/Save/`.
Line numbers are for the working tree at 743df53 (read-only survey, nothing built or run).

Written for two players. Since 0.4.90 a save holds any number of members: "the partner" below is each other member,
and `_checkedWith`/`CheckedPartnerId`, `GetCheckedPartner` and `FindPartner` are gone in favour of `_checkedMembers`,
`CheckedMemberIds` and `FindMembers`. The flea game score's `Part` carries bit 1 for playing and bit 2 for running
the room.

---------------------------------------------------------------------------------------------------------------------
## 0. Cross-cutting facts every model needs

### 0.1 Routing (server, host's game)
- `BossRoomUpdate` (reliable, `Networking/Packet/Data/BossRoomUpdate.cs:8`): server `Game/Server/ServerManager.cs:1427-1450`.
  Kind `Waiting` goes to **every other player in any scene** (1435-1443). Every other kind goes through
  `SendDataInSameScene(id, update.SceneName, …)` (1445-1449, impl 1889-1905): delivered only to players whose
  **server-side** `CurrentScene == update.SceneName` at the moment the server handles it. No queueing, no resend.
- `BattleSceneUpdate` (reliable, `BattleSceneUpdate.cs:8`): same scene filter (ServerManager.cs:1408-1419).
- `CoopSaveUpdate` (reliable, `CoopSaveUpdate.cs:12`): targeted by `TargetId`, **no scene filter**
  (ServerManager.cs:1457-1469); dropped if target not connected or target == sender.
- Server `CurrentScene` is set on `PlayerEnterScene` (ServerManager.cs:521) which the client sends from
  `AfterEnterSceneHeroTransformed` (C/ClientManager.cs:441, 1552-1586) — i.e. after the new scene is active on the
  client. It is cleared by `HandlePlayerLeaveScene` (ServerManager.cs:1180-1230), which also **transfers scene host**
  to another player in the scene and sends them `SceneHostTransfer` (~1214-1227).
- Server-side handling order inside one client packet (ServerUpdatePacketId): PlayerEnterScene 8, PlayerLeaveScene 9
  **before** BattleSceneUpdate 16, BossRoomUpdate 17, CoopSaveUpdate 18. So a room update sent in the same packet as
  a leave is still routed by `update.SceneName` (sender's scene no longer matters), but an update sent in the same
  packet as the *receiver's* leave is dropped.
- Client-side order (given): PlayerConnect 2 … PlayerEnterScene 5, AlreadyInScene 6, PlayerLeaveScene 7,
  SceneHostTransfer 13, BattleSceneUpdate 20, BossRoomUpdate 21, CoopSaveUpdate 22.
  Client handlers: `OnPlayerEnterScene` C/ClientManager.cs:1104-1134 (sets `IsInLocalScene=true` at 1115, then calls
  `_arenaCoop.OnPlayerEnterScene()` 1124 and `_bossRoomCoop.OnPlayerEnterScene()` 1125 = the only resend hooks);
  `OnPlayerLeaveScene` 1140-1175 (ignored if `data.SceneName` ≠ active scene, else `IsInLocalScene=false` 1161);
  `OnSceneHostTransfer` 1316-1337 (ignored if scene ≠ active; on promote calls `_arenaCoop.OnBecomeSceneHost()` 1335);
  local scene change `OnSceneChange` 1463-1488 sets every `IsInLocalScene=false` and sends LeaveScene.
- Local disconnect `InternalDisconnect` C/ClientManager.cs:704-735: `_coopSave.OnLocalDisconnect()` (710),
  `_playerData.Clear()`, then `DeregisterHooks()` (735) which calls `ArenaCoop.DeregisterHooks` (clears `_arenas`),
  `BossRoomCoop.DeregisterHooks` → `ClearScene()` (C/BossRoomCoop.cs:332), `FleaGameCoop.DeregisterHooks`
  (clears `_partnerOutroReady`, `_sentOutroReady`). Hooks are registered again on connect (ClientManager.cs:832).
- Remote disconnect `OnPlayerDisconnect` C/ClientManager.cs:1009-1048: removes from `_playerData` then
  `_coopSave.OnPlayerDisconnect(id)` (1040). There is **no** BossRoomCoop/ArenaCoop/FleaGameCoop disconnect callback;
  they notice absence only through `_playerData` / `IsInLocalScene` / `_isPartnerMissing()`.

### 0.2 CoopSave dispatch and per-frame driver
- `CoopSave.OnCoopSaveUpdate` S/CoopSave.cs:770-902: drops the message if the sender is not in `_playerData` (771-773),
  otherwise switches on `Kind`. No scene check at this level.
- All CoopSave per-frame work hangs off `HeroController` updates: `OnHeroControllerUpdate` S/CoopSave.cs:391 →
  `UpdateSession` 416-489. Order: `UpdateCheck` (only while `_checkedWith != partner.Id`, 437-447), `UpdateCheckpoint`
  (450-457), `UpdateWishTalk` (→ `UpdateWishConfirm`), `UpdateDeliverySummon`, `UpdateRescue`, `UpdateLavaChase`,
  `UpdateChaseStandUp`, `UpdateRaces`, `UpdatePrisonCapture`, `UpdateClothesGrab` (459-466); only while checked:
  `UpdateInteractions`, `UpdateWorldChanges`, `UpdateWishes`, `UpdateStoryFlags`, `UpdateLifts`, `UpdateTrapdoors`,
  `ReleaseHold` (468-478); else `Hold` (481-483). "partner" passed to most updates is
  `partner != null && _checkedWith == partner.Id ? partner : null`.
- The FSM `SwitchState` hook that drives cage/clothes/race (`OnInteractionSwitchState` S/CoopSave.Interactions.cs:418)
  is a no-op until `_everChecked` (419-422).
- Session resets:
  - `ResetSession(notifyPartner)` S/CoopSave.cs:579-612: sends `Left{Key=_highestCheckKey}` to `_checkedWith ??
    _checkPartnerId` if still connected (581-583), then resets every sub-flow (ResetWishConfirm, ResetCheck,
    ResetCheckpointSession, ResetWishTalk, ResetLifts, ResetDeliveries, ResetRescue, ResetRaces, ResetPrisonCapture,
    ResetClothesGrab …). Called on slot change (UpdateSession 425-428) and on return to menu (617-620).
  - `PartnerLeft(id, how)` S/CoopSave.cs:697-735 (remote disconnect via `OnPlayerDisconnect` 683-690, `Left` via
    `OnLeft` S/CoopSave.Check.cs:410-416, key-mismatch hello 298): ResetWishConfirm, `_checkedWith=null`, ResetCheck,
    ResetRescue, OnClothesGrabPartnerLeft, sets `_interruptPending` if in a boss fight. **Does not** reset races, lifts,
    deliveries, trapdoors-open-list, prison, flea; those notice the partner being gone per frame.
  - `OnLocalDisconnect` S/CoopSave.cs:737-767: ReleaseHold, clears pairing requests, `_checkedWith=null`, ResetCheck,
    ResetRescue, OnClothesGrabPartnerLeft. Nothing is sent (the connection is gone).
  - `OnActiveSceneChanged` S/CoopSave.cs:626-641 (local scene change): OnCheckpointSceneChanged, ResetInteractions,
    OnWishTalkSceneChanged (→ ResetWishConfirm, which **does** send answers/Gone), OnLiftSceneChanged,
    OnDeliverySceneChanged, OnRescueSceneChanged, OnLavaChaseSceneChanged, OnCageSceneChanged,
    OnClothesGrabSceneChanged, ResetRaces.
- `CoopSave.Send` S/CoopSave.cs:1951-1965: stamps `Sequence` for WorldChange/WishChange/Interaction/WishTurnIn and
  DeliveryBreak reports; sends only if connected. No retransmission layer above the reliable channel anywhere.

### 0.3 Who is "the partner"
- `_checkedWith` (S/CoopSave.cs:312 `CheckedPartnerId`) is set only by `FinishCheck` (S/CoopSave.Check.cs:422) and
  cleared by PartnerLeft / OnLocalDisconnect / ResetSession / a newer Hello (Check.cs:353-355).
- `GetCheckedPartner()` S/CoopSave.WishBoard.cs:410-412 = `_checkedWith` if still in `_playerData`.
- `IsPartnerMissing()` S/CoopSave.Checkpoint.cs:104-112 = a paired marker is loaded and `FindPartner` finds nobody on
  the server. BossRoomCoop treats that as "a player every room waits for" (C/BossRoomCoop.cs:599-601, Events 377-379,
  Dialogue 898-900).

---------------------------------------------------------------------------------------------------------------------
## 1. Waiting room ready (menu, before any save is loaded)

Purpose: after joining, neither player opens the save menu until both pressed "ready".

Messages
- `CoopSaveUpdate{Kind=WaitingRoomReady, Part=1|0}` reliable. Sent by `CoopSave.SendWaitingRoomReady(ready)`
  S/CoopSave.cs:1934-1947 to **every** player in `_playerData` at that moment (1939). Triggered by
  `ConnectInterface.WaitingRoomReadyToggled` → `UiManager.ReadyToggledEvent` (Ui/UiManager.cs:631) →
  ClientManager.cs:395. Fired by:
  - Ready button toggle `Ui/ConnectInterface.cs:750-754` (flips `_waitingRoomLocalReady`, sends, refreshes).
  - Host backing out of save selection `OnHostSaveSelectionClosed` ConnectInterface.cs:1445-1463 (sends `false` at
    1456-1459 if it was ready).

State (Ui/ConnectInterface.cs)
- `_waitingRoomActive` (false), `_waitingRoomHosting`, `_waitingRoomLocalReady` (529, false),
  `_waitingRoomReadyNames` (534, empty; partner readiness **by username**), `_waitingRoomProceeded` (538, false),
  `_waitingRoomPlayers` (member list; index 0 = local).
- ClientManager `_openSaveAfterReady` (65; set at 850 for a joiner, consumed by `OpenSaveAfterReady` 999-1003).

Handlers
- Receive: S/CoopSave.cs:797-801 → `UiManager.OnPartnerReadyChanged` → `ConnectInterface.OnPartnerReadyChanged`
  1405-1417: **drop if `!_waitingRoomActive`** (1406); add/remove username; `RefreshWaitingRoom()`.
- `RefreshWaitingRoom` 1361-1398: members = `_waitingRoomPlayers` with ready = local flag / name in ReadyNames;
  return if `_waitingRoomProceeded || members.Count < 2` (1378); return if any member not ready; else
  `_waitingRoomProceeded=true`; host → `SuspendWaitingRoom()` + `HostSaveSelectionRequested`; joiner →
  `HideWaitingRoom()` + `JoinSaveSelectionRequested` → `BothReadyEvent` → `OpenSaveAfterReady` (ClientManager 396).
- Room lifecycle: host `ShowWaitingRoom` 1320-1323 (alone); joiner `ShowJoinWaitingRoom` 1331-1335, called from
  ClientManager.cs:888 after ServerInfo; `OpenWaitingRoom` 1340-1356 resets local ready and ReadyNames.
  `OnPlayerJoined` 1478-1485 (ClientManager.cs:982): adds name, refresh. `OnPlayerLeft` 1491-1500
  (ClientManager.cs:1039): removes name and its readiness. `HideWaitingRoom` 1422-1431 clears everything.
  `OnHostSaveSelectionClosed` 1445-1463 re-shows the room, clears `_waitingRoomProceeded`, un-readies local but
  **keeps `_waitingRoomReadyNames`**.

Wait condition: each side proceeds when its own `_waitingRoomLocalReady` and every other listed member's name is in its
own `_waitingRoomReadyNames`. Ends: proceeding, `HideWaitingRoom` (leave/disconnect/save loaded), partner leaves.

Suspects
- **S1.1 (one-sided, no resend; high plausibility)**: ready state is never re-sent when a player joins. Order: host
  opens room alone → presses Ready (the button is not disabled when alone, `WaitingRoomPanel.SetRoom`
  Ui/Component/WaitingRoomPanel.cs:428-470 only changes text) → `SendWaitingRoomReady(true)` goes to an empty
  `_playerData` → joiner connects, its room starts with nobody ready → joiner presses Ready → host now sees both
  ready and proceeds; joiner still shows host "not ready" and never proceeds (host would have to toggle twice).
- S1.2: messages arriving while `_waitingRoomActive==false` are dropped (1406). Joiner's room is opened only after
  ServerInfo processing (ClientManager 888); a Ready that raced ahead of `ShowJoinWaitingRoom` is lost. (Order within
  the join is probably safe because the host only learns of the joiner via PlayerConnect first; not traced further.)
- S1.3: after a host backs out (`OnHostSaveSelectionClosed`), the joiner has already hidden its room (proceeded), so
  the host's `false` is dropped there; host keeps the joiner's stale "ready" (ReadyNames not cleared) and will
  proceed alone on its next press. Benign unless the joiner also backed out (it disconnects then: ClientManager 860).

Size: ~190 lines (ConnectInterface ~150, CoopSave 20, ClientManager ~20).

---------------------------------------------------------------------------------------------------------------------
## 2. Boss rooms (BossRoomCoop*.cs)

Purpose: a room/boss only starts once every player has arrived; gates are per player; the scene host's boss dialogue
is shown to followers; a fight after dialogue starts once everyone got there.

Common send path: `Send(kind, fsm,…)` / `Send(kind, path, fsmName, from, to, event, variable)`
C/BossRoomCoop.cs:1192-1226 — no-op when not connected; `SceneName = active scene`. Dialogue updates use
`_netClient.UpdateManager.SetBossRoomUpdate` directly (Dialogue.cs:333).
Common receive: `OnBossRoomUpdate` C/BossRoomCoop.cs:381-433. Guards: `!_isFullSynchronisation()` → drop (382);
`Waiting` handled in any scene (387-390); `DialogueDone` handled in any scene (395-398); every other kind dropped if
`active scene != update.SceneName` (400-402).
Gating predicates: `IsHoldActive()` 1232-1234 = (other players connected OR partner missing) && connected && full
sync; `IsCoopActive()` 1239-1241 = connected && full sync && someone `IsInLocalScene`; `IsFollower()` 1246-1248 =
IsCoopActive && role determined && not scene host.
Per-frame: `OnHeroControllerUpdate` 848-868: if IsHoldActive → `ScanScene` (5 s interval), `UpdateArrivals`; always →
`ReleaseHeldStarts`, `UpdateDialogues`, `UpdateEventStarts`, `UpdateRoomWaits`, `CheckStartedRooms`,
`ClosePendingGates`.
Reset: `OnActiveSceneChanged` 1654-1656 → `ClearScene` 1661-1676 (also from DeregisterHooks on local disconnect).
ClearScene **drops `_heldStarts` without firing them** (1667) but `ClearEventStarts` (Events.cs:742-763) and
`ClearDialogues` (Dialogue.cs:1118-1164) **fire** held event-starts / held fights / held dialogue ends if the FSM is
still in the held state. Nothing is told to the partner by any reset.

### 2a. Trigger-started rooms: Arrived / Waiting (hold the start event)
State: `_arrivals: Dictionary<Fsm, RoomArrivals{Local=false, Remote={}, NextNoticeTime=0}>` (169, class 1768-1783);
`_heldStarts: Dictionary<Fsm, HeldStart{StateName, EventName}>` (174); `_startFsms` (164); `_remotePositions` (194).
Sends
- `Arrived(path,fsm)`: `MarkLocalArrival` 552-560 (once per FSM per scene visit, when `Local` flips true); callers
  `TryHoldStart` 525-528 (collider trigger fired, or hero inside alert-range regions) and `UpdateArrivals` 930-932
  (hero's path this frame crossed the regions). **Resend**: `OnPlayerEnterScene` 439-447 re-sends Arrived for every
  FSM with `Local==true` whenever another player enters the local scene.
- `Waiting`: `NotifyWaiting(RoomArrivals)` 575-583, only if `Local` and ≥30 s since last (`NoticeInterval` 78);
  called from MarkLocalArrival 559 and TryHoldStart 539. Pure notice.
Handlers
- `OnArrived` 781-791: `FindFsm(path,fsmName)` null → drop; `Remote.Add(sender)`; register in `_startFsms`.
- `OnTeammateWaiting` 796-803: chat line only.
Local logic
- `TryHoldStart` 504-547 (from OnProcessEvent 465-468, only when IsHoldActive): trigger lookup; not held if the
  boss room `HasBossStarts` and FSM isn't an entity (512-514) or no regions (517-520). Marks local arrival; if
  `HaveAllArrived` → remove hold, `OnBossFightStarting`, let event through (530-534); else store/refresh HeldStart,
  `NotifyWaiting`, `BeginRoomWait(room,…,freeHero=true)` (536-544) and swallow the event.
- `HaveAllArrived` 589-621: needs `Local`; true if !IsHoldActive; **false while partner missing**; for every
  `_playerData` entry: must be `IsInLocalScene`, and either in `Remote` or its avatar currently inside the regions
  (then added to Remote, 612-617).
- `UpdateArrivals` 918-961: removes Remote for players not IsInLocalScene (935-937); adds Remote when an avatar's
  path crosses the regions (945-949).
- `ReleaseHeldStarts` 966-989: drop hold if FSM gone or left the held state; if HaveAllArrived → remove,
  `OnBossFightStarting`, `EndRoomWaitFor`, `fsm.Event(held.EventName)`.
Wait condition: `Local && !partnerMissing && ∀ other players: IsInLocalScene && (Remote∋id || avatar in regions)`.
Ends: all arrived; FSM leaves state; scene change (hold dropped, event NOT fired); /giveup only opens gates
(Wait.cs:386-413), the start stays held.

### 2b. Gates, Started / RoomDone / GateOpened
State: `_closedGates: Dictionary<Fsm gate, Fsm? closer>` (179), `_pendingCloses` (184), `_startChecks:
List<StartCheck{Update, ReceivedTime}>` (189).
Sends
- `Started(path,fsm,from,to,event)`: `OnSwitchState` 640-660, only when `IsFollower()` and the transition is a start
  trigger of a controller FSM (653-657).
- `RoomDone(path,fsm)`: `CheckStartedRooms` 1017-1046 on the scene host, ≥15 s (`StartCheckTime` 53) after a Started
  arrived, when the host's FSM is inactive / not in `FromState` / not running a fight (1031-1034); if the host's FSM
  is still in `FromState` and the host hero isn't in the boss room, the check is **re-queued for another 15 s**
  (1037-1041, unbounded).
- `GateOpened(path,fsm,event)`: `OnGateOpening` 626-635 when a gate that was closed/pending opens, only if local is
  scene host and IsCoopActive.
Handlers
- `Started` → `_startChecks.Add` (408-410) (no role check at receipt; role checked at expiry 1025).
- `OnRoomDone` 808-825: drop if receiver is (determined) scene host; FindFsm; drop pending closes by that controller;
  send `BG OPEN` to gates closed by it.
- `OnGateOpened` 830-842: drop if receiver is scene host; drop if gate neither closed nor pending here; else fire the
  event.
Local: close events from another FSM while the local hero is outside that FSM's room become `_pendingCloses`
(486-497), closed when the hero walks in (`ClosePendingGates` 1051-1076).

### 2c. Event-started boss rooms: RoomEvent, held event starts, room waits
State: `_bossRooms`, `BossRoom{Started, FightBegan, ForwardPending, Wait, HasBossStarts, …}` (Events.cs:768-856),
`_heldEventStarts: Dictionary<Fsm, HeldEventStart{StateName, EventName, Shape, NextNoticeTime}>` (90),
`_nextForwardTimes` (106, 0.5 s throttle `ForwardInterval` 47).
Sends
- `RoomEvent(path=room root, event)`: `OnFsmEvent` Events.cs:136-181 on a **follower** whose non-entity object sends a
  fight event of the room before `FightBegan` (throttled 0.5 s per room+event, 157-161). Then, unless `room.Started`
  or everyone already in the room shape, sets `room.ForwardPending=true` and `BeginRoomWait` (166-173).
- `Waiting`: `NotifyWaitingInRoom` Events.cs:353-366 (only if local hero in the shape; else chat "teammate waiting").
Handlers
- `OnRoomEvent` Events.cs:186-220: **drop unless receiver role determined && scene host** (187-189); root not found →
  drop; `room.FightBegan` → drop; fires the event on every enabled entity FSM of the room whose active state has
  that event as a start, with `_forwardedAnchor` = sender avatar position.
Local logic
- `TryHoldEventStart` Events.cs:226-287: only holds if room not Started, event is a start, FSM is entity only on host,
  not a trigger start, not a room object of a `HasBossStarts` room, and (if self-sent) a detection event. If
  `AreAllInRoom(shape)` → `room.Started=true`, `OnBossFightStarting`, pass; else create HeldEventStart,
  `BeginRoomWait`, notify, swallow.
- `AreAllInRoom` Events.cs:371-394: true if !IsHoldActive; **false while partner missing**; local hero and every
  player (must be IsInLocalScene) inside the shape. Position-only; no message involved.
- `UpdateEventStarts` 292-318: releases when AreAllInRoom → `MarkRoomStarted`, `OnBossFightStarting`, restore hero,
  `EndRoomWaitFor`, `fsm.Event`.
- Follower side: `UpdateRoomWaits` Wait.cs:267-309: `ForwardPending` is cleared (and `room.Started=true`,
  `OnBossFightStarting`) when `room.FightBegan || AreAllInRoom(wait.Shape)` (280-285) — i.e. **by the follower's own
  position check, not by any acknowledgement from the host**.
- `MarkFightBegan` 324-338 (from OnSwitchState 646): an FSM entering a base fight state sets FightBegan/Started.
- Room wait (Wait.cs): `BeginRoomWait` 168-195 (makes host boss invincible 185-194), `EndRoomWait` 205-231,
  `LockGates` 345-356 (closes gates for the local hero inside), `KeepHeroFree` 315-339 (gives control back after 2 s),
  `OpenUnclaimedGates` 362-380 (8 s), `GiveUpWaiting` 386-413 (/giveup opens gates; room still waits).

### 2d. Shared boss dialogue: DialogueStarted / DialogueDone (and the dead end-hold)
State (host/sharer): `_sharedDialogues: Dictionary<Fsm, SharedDialogue{StateName, Update, StartTime, Waiting={},
Done={}, EndHeld=false, HoldsEnd}>` (93, class 1169-1210).
State (reader): `_dialogueQueue` (98), `_shownDialogue` (133, null), `_shownDialogueId` (138), `_shownDialogueTime`,
`_tookHeroControl`.
Sends
- `DialogueStarted`: `OnStartDialogue` 206-259 → `ShareDialogue` 323-335 (only to players IsInLocalScene not yet in
  `Done`; only if someone new was added to `Waiting`). Scene host + boss entity only, unless key talk
  (`_isSharedTalk`, which is only true for deliveries now: S/CoopSave.WishTalk.cs:842-844). Resent to new entrants
  via `ShareDialoguesWithNewPlayers` 341-345 (from OnPlayerEnterScene).
- `DialogueDone`: reader `SendDialogueDone` 569-571, from `OnSharedDialogueEnded` 554-564, from OnDialogueStarted when
  the reader is in its own conversation (431-437), from ShowNextDialogue when text empty or scene differs (460-469).
Handlers
- `OnDialogueStarted` 404-442: drop if not shared talk and receiver is scene host (411-414); drop duplicates by key
  (417-421); auto-DialogueDone if own conversation running (431-437); else enqueue + `ShowNextDialogue`.
- `OnDialogueDone` 390-399 (any scene): drop if FSM not found or no SharedDialogue or state differs; move sender from
  Waiting to Done.
Hold: `TryHoldDialogueEnd` 357-376 — **never holds today**: `HoldsEnd = isSharedTalk && _holdsSharedTalkEnd(fsm)`
(255) and `HoldsSharedTalkEnd` returns false (S/CoopSave.WishTalk.cs:850-852). So DialogueDone only affects the dead
`UpdateDialogues` end-hold path (1011-1047) and the reader's own queue.
Reader timers (UpdateDialogues 1058-1067): box closed without callback after 1 s → ended; open > 12 s
(`SharedDialogueReadTime` 48) → force-ended.

### 2e. Fight after room dialogue: Ready (the handshake that still waits)
State: `_fightReadiness: Dictionary<Fsm, RoomArrivals{Local=false, Remote={}}>` (103), `_heldFights:
Dictionary<Fsm, HeldFight{StateName, EventName}>` (108), `_startedFights` (113), `_dialogueFsms` (123).
Sends
- `Ready(path,fsm)`: `TryHoldFightGate` 699-703 **only if `!readiness.Local && !StillReadingWhatTheFightWaitsFor()`**
  (`_shownDialogue != null || _dialogueQueue.Count > 0`, 954-956); `PassUnreachableFight` 930-945 (local room cannot
  reach its fight any more, checked on every Ready received and every 1 s while `Remote.Count>0`, 1102-1111);
  resend on partner entering the scene for FSMs with `Local==true` (`ShareDialoguesWithNewPlayers` 346-350).
Handlers
- `OnReady` 914-923: FindFsm else drop; `Remote.Add(sender)`; `PassUnreachableFight`.
Local
- `TryHoldFightGate` 675-733 (from OnProcessEvent, only if IsHoldActive): only for a fight transition of a
  non-entity gate-closing FSM that ran dialogue, or a start event sent by another dialogue FSM of the boss room. If not
  after dialogue or `HaveAllReached` → started, pass. Else create HeldFight, `FreeHero`, `BeginRoomWait`, swallow.
- `HaveAllReached` 888-909: needs `Local`; true if !IsHoldActive; false while partner missing; every other player
  IsInLocalScene and in `Remote`.
- `UpdateDialogues` 1075-1100: prunes `Remote` of players not in the scene (1075-1079); releases held fights when
  HaveAllReached → `_startedFights`, `OnBossFightStarting`, restore hero, `EndRoomWaitFor`, `fsm.Event`.
Wait condition: `readiness.Local && !partnerMissing && ∀ other: IsInLocalScene && Remote∋id`. No timeout.

### 2f. Suspects (boss rooms)
- **S2.1 (deadlock until scene change; traced)**: `readiness.Local` is only ever set in `TryHoldFightGate` (701) or
  `PassUnreachableFight` (942). If the fight transition event arrives while the local player is still reading a line
  the scene host shared (`_shownDialogue != null` or queue non-empty), `Local` stays false (700), the event is held
  (720-729), and nothing sets `Local` later: the held FSM will not re-send the event, `HaveAllReached` needs `Local`
  (889), and `PassUnreachableFight` returns early because the held state can still reach its fight (937-939).
  Order: room FSM finishes its own dialogue → host's boss shares a line → reader shows it → room FSM's own
  FINISHED/next event hits the fight transition → held forever. The partner, who sent Ready, also waits forever for
  this Ready. Escape: scene change / disconnect (ClearDialogues fires the held event), or /giveup (gates only).
- **S2.2 (one-sided start; lost RoomEvent)**: `OnRoomEvent` drops the event if the host's role is not yet determined
  (187-189), the root isn't found, or `FightBegan`; nothing is resent except by the follower's object re-sending the
  event (0.5 s throttle). The follower's `ForwardPending` is cleared by its own `AreAllInRoom` (Wait.cs:280-285), which
  marks `room.Started` and raises `BossFightStartedEvent` → `CoopSave.OnBossFightStarted` → checkpoint + `BossFight`
  message, while the host's boss never received the start event.
- **S2.3 (held start dropped, not fired)**: `ClearScene` drops `_heldStarts` (1667) without firing, unlike event starts
  and held fights. On local disconnect (DeregisterHooks → ClearScene) a player standing in a room whose trigger start
  was held keeps the room un-started; the trigger will only fire again if the FSM re-evaluates. Model as: held start
  → disconnect → no start until re-entry.
- **S2.4 (host transfer vs. in-flight updates; packet order)**: `SceneHostTransfer` (13) is handled before
  `BossRoomUpdate` (21). A `GateOpened`, `RoomDone` or non-shared `DialogueStarted` sent by the old host just before
  it left arrives after the receiver was promoted and is dropped by the "receiver is scene host" guards (809-811,
  831-833, 411-414). A follower whose gates the old host was about to open keeps them closed (only UnclaimedGate 8 s or
  /giveup help).
- **S2.5 (Arrived before load / after flicker)**: Arrived/Ready are dropped by the scene check (400) or FindFsm; the only
  resend is `OnPlayerEnterScene` (partner entering the sender's scene). `UpdateArrivals`/`UpdateDialogues` delete a
  player's Remote entry whenever `IsInLocalScene` is false for a frame (935-937, 1076-1078). For Arrived the avatar
  position check recovers it; for **Ready there is no position fallback**, so a LeaveScene/EnterScene flicker of the
  partner inside the room loses their Ready until the partner's game gets a PlayerEnterScene for *this* player (it
  resends on the partner's side only then). Order: partner Ready → local prunes it during a flicker → partner never
  re-enters → held fight never releases (same symptom as S2.1).
- **S2.6 (Started check never concludes)**: a `StartCheck` whose host FSM is still in `FromState` while the host hero
  is outside the boss room is re-queued every 15 s without bound (1037-1041); the follower's gates stay closed for as
  long as the host stays out of the room.
- S2.7 (partner missing): every wait in 2a/2c/2e is false while `IsPartnerMissing()` — two-player save partner off the
  server keeps rooms held indefinitely (intended; only /giveup opens gates).

Size: BossRoomCoop.cs 1850 + Wait 565 + Events 1244 + Dialogue 1252 = 4911 lines; protocol-relevant ≈1300.

---------------------------------------------------------------------------------------------------------------------
## 3. Arena BattleScene (C/ArenaCoop.cs)

Purpose: scene host runs the arena; followers close only their own gates and follow waves/win; a follower who walks in
first asks the host to start.

State per arena `ArenaState` 1302-1363: `Started=false`, `LockedIn=false`, `Wave=-1`, `Ended=false`,
`SentEnemies=-1`, `HostUpdate=null`, `HostUpdatePending=false`, `RequestTime=null`. `_arenas` (191) cleared on scene
change (1291-1297) and DeregisterHooks (283-294).
Send `Send(path,status,wave,enemies)` 1147-1161: requires connected, full sync, **someone IsInLocalScene**.
`IsFollower()` 1167-1170 = connected && full sync && role determined && not host && someone in scene.
Messages
- `StartRequest`: follower's `OnStartBattle` 426-465 → `LockIn` + send once while `HostUpdate==null &&
  RequestTime==null` (460-464), sets `RequestTime`.
- `Running(wave, enemies)`: `SendRunning` 1139-1142 from host `OnStartBattle` 446-448, `OnStartWave` 520-522,
  `OnStartRequest` 418, `OnUpdate` when enemy count changed (723-725), `OnPlayerEnterScene` 360-362,
  `OnBecomeSceneHost` 382-384.
- `Won`: host `OnEndBattle` 531-549 (once, `Ended`).
- `AlreadyWon`: host `OnStartRequest` 408-410, `OnPlayerEnterScene` 358-359.
- `Unavailable`: host `OnStartRequest` 401-404 (arena missing/inactive).
Handlers
- `OnBattleSceneUpdate` 317-345: drop if not full sync or scene ≠ active (318-320). StartRequest → `OnStartRequest`
  396-419 (**drop unless receiver `IsSceneHost`** 397-399; no role-determined check). Other statuses: drop if arena
  not found or receiver is (determined) host (329-331); store `HostUpdate`, `HostUpdatePending=true`,
  `RequestTime=null`; apply now if IsFollower.
- `ApplyHostUpdate` 731-786: with no HostUpdate: after 5 s (`StartRequestTimeout` 38) since the request → `Release`
  gates (733-742). Waits until arena active and loop counter 0. Running → start battle for other player / start wave /
  set enemy count; Won → EndBattle remotely; AlreadyWon/Unavailable → release gates if locked in.
- `OnUpdate` 702-726: if nobody in scene → return (715-717) (pending host updates are not applied); follower applies
  pending; host sends enemy count changes.
- `OnBecomeSceneHost` 369-391 (ClientManager 1335): clears `HostUpdate`, `HostUpdatePending`, `RequestTime`;
  continues a started battle (SendRunning) or starts a locked-in one.
- `GiveUpBattle` 976-996 (/giveup) releases locally.
Wait: follower locked in waits for Running→…→Won; request wait bounded by 5 s timeout.

Suspects
- **S3.1 (Won dropped by host transfer; traced by packet order)**: host wins (`Won` sent) and leaves the scene in the
  same server-side batch (die, walk out through opened gates quickly, menu/disconnect). Server handles
  LeaveScene (9) before BattleSceneUpdate (16) and sends the follower `PlayerLeaveScene` + `SceneHostTransfer`; the
  follower handles 7 and 13 before 20, so `OnBattleSceneUpdate` sees `IsSceneHost==true` and drops `Won` (329-331).
  `OnBecomeSceneHost` has already cleared HostUpdate; the new host now runs its own copy of a battle whose enemies were
  killed in the other game (outcome depends on its local enemy count; not traced). Even without promotion,
  `IsInLocalScene=false` makes `OnUpdate` skip applying (715-717).
- S3.2: `OnStartRequest` answers nothing when the receiver is not host (397-399) — e.g. both players' role not yet
  settled — so the requester is released only by its 5 s timeout (no retry; a second request is only sent after
  `RequestTime` was cleared and the follower re-triggers `StartBattle`).
- S3.3: before the scene role is determined, `IsFollower()` is false, so a player who enters and trips the arena
  runs it locally as if host (430-450) and sends `Running` that the real host drops (329-331) — two independent
  battles for that window.

Size: ArenaCoop.cs 1364 lines; protocol ≈400.

---------------------------------------------------------------------------------------------------------------------
## 4. Both-agree prompts: WishConfirm (S/CoopSave.WishConfirm.cs), plus the WishTalk/board lock

Purpose: a yes/no prompt that spends shared things (accept/turn in wish, story item, race start) only goes through
when both players said yes. Wishes and races: each player answers their own box ("meeting"). Items: the partner is
shown a box of ours.

Constants: timeout 20 s (41). PartCount codes: Ask 0 (52), Yes 1 (57), No 2 (62), Gone 3 (67). Kind strings in
`Records[0]`: "wish", "wishdone", "item", "race" (72-88). `Key` = random 64-bit per ask (`CreateConfirm` 588-598).
State: `_openPrompt` (171), `_wishConfirm: HeldConfirm{PartnerId, Key, Action, Box, Callback, Proceed, WishKey,
Started}` (176, 1260-1323), `_partnerConfirm: PartnerConfirm{PartnerId, Key, IsWish, Started}` (181),
`_pendingConfirmAsk: PendingAsk` (186), `_partnerWishWait: PartnerWishWait{PartnerId, Key, Kind, Wish, Started}`
(195), `_heroHeldForConfirm` (201). All null/false initially.

Sends
- Ask: `OnPromptSelectYes` 266-353 (hook on `YesNoBox.SelectYes`), only if `_wishConfirm==null &&
  _partnerConfirm==null && _everChecked && _checkedWith` set (292) and the prompt needs both (GetConfirmAsk 455-481).
  If a matching `_partnerWishWait` exists → no hold: send Yes to the partner's key and press for real (311-324).
  Else clear the box's remembered side (329), store `_wishConfirm`, `Send(ask)` (336).
- Yes/No: `SendConfirmAnswer` 1128-1135, from: OnWishConfirm guard failure (610), `TakeWishWait` meeting (853),
  displacing an older wait (870), QueuePartnerConfirm refusals (800 auto-yes if save already has the wish; 820, 828,
  1012, 1040, 1065 no), `ShowPartnerConfirm.Answer` (1027), `OnPromptSelectNo` for a waited wish (367-374), timeouts in
  `UpdateWishConfirm` (1167, 1174, 1189), `ResetWishConfirm` (1233, 1240, 1250).
- Gone: `CancelHeldConfirm` 729-766 (741-746) from OnExit of the held prompt (246-248, Silence), local No on the held
  box (361-363, LeaveAlone), 20 s timeout (1154-1161, PressNo), `ResetWishConfirm` (1254).
Handler `OnWishConfirm` 603-652: if no marker / not partner / `_checkedWith != sender` → answer No to an Ask, drop
everything else (607-614). Ask → `QueuePartnerConfirm` 792-840 (already has wish → Yes; wish/race kind →
`TakeWishWait` 846-889; else one-at-a-time: refuse if `_partnerConfirm`, `_pendingConfirmAsk` or `_wishConfirm`
non-null; queue if box busy (`CanShowConfirmNow` 923-932); else `ShowPartnerConfirm` 1007-1083). Yes/No →
`AnswerHeldConfirm` 657-692 (drop unless `_wishConfirm.Key == key`; on Yes: if the box no longer holds the same
callback → nothing taken (666-673); greyed yes → press No; else `Proceed()` = real button). Gone → clears
`_pendingConfirmAsk`/`_partnerConfirm` with that key and closes our box (636-647). **Gone does not touch
`_partnerWishWait`.**
`TakeWishWait` 846-889: if local `_wishConfirm.WishKey` matches → send Yes to partner key and AnswerHeldConfirm(local,
yes) (both proceed); else answer No to an older `_partnerWishWait`, store new one.
Timers `UpdateWishConfirm` 1152-1209 (every frame via UpdateWishTalk): held 20 s → Gone + press No; shown box 20 s →
No; partner wish wait 20 s → No; pending ask 20 s → No; pending ask ends a shared dialogue to make room (1195-1201).
Resets: `ResetWishConfirm` 1215-1255 on local scene change (WishTalk.cs:924), PartnerLeft, ResetSession: answers No to
waits/pending/shown, cancels own hold (Gone).
Wait conditions: holder waits for Yes/No with its key or 20 s; partner-wish-wait waits for local yes/no or 20 s.

Suspects
- **S4.1 (one-sided accept; traced)**: `Gone` leaves the receiver's `_partnerWishWait` in place (636-647). Order: A
  says yes (Ask kA) → B stores wait (TakeWishWait) → A takes it back with No (CancelHeldConfirm LeaveAlone, sends Gone
  kA) or A's dialogue ends (Silence, Gone) or A's 20 s expires (PressNo, Gone) → B presses yes within B's own 20 s
  window (which started later) → meeting branch (311-324) sends Yes(kA) (ignored at A, 658) and **presses B's real
  button**: B accepts/turns in/starts the race alone, A declined. (Whether WishTurnIn sync later copies it to A was not
  traced.)
- **S4.2 (two-generals crossing; traced)**: A's `Gone`/timeout crosses B's `Yes`. B commits on sending Yes (meeting
  path presses immediately; TakeWishWait path calls AnswerHeldConfirm immediately), A drops the Yes because
  `_wishConfirm` is already null or the key differs. There is no commit/ack phase; window = one-way latency.
- S4.3 (livelock, ends by refusal): two simultaneous *item* asks: each has `_wishConfirm` set when the other's Ask
  arrives → both refuse (819-822) → both "didn't agree". Repeats as long as they press together.
- S4.4 (dropped when check state differs): Yes/No/Gone from a sender that is not `_checkedWith` are silently dropped
  (607-614); a holder whose partner's check just reset waits out 20 s.

WishTalk lock (S/CoopSave.WishTalk.cs, boards in WishBoard.cs)
- `WishTalk{PartCount: Ended 0, Started 1, Refused 2}` (79-90). Started sent by `TryStartWishTalk` 1021-1022, boards
  WishBoard.cs:181, 377/380, deliveries Deliveries.cs:508; Ended by `EndWishTalk` 1334-1346 (only for key/delivery
  talk, and only if `FindPartner` non-null); Refused 1012, WishBoard.cs:168, 354.
- `OnWishTalk` 1438-1472: Started → `_partnerTalk = new PartnerTalk(npc)`; Ended → null; Refused → chat.
- The lock only refuses a **board** (and donations) the partner is using (`OnNpcInteract` 968-974,
  WishBoard.cs:144-151, 340-343). Released by Ended, partner leaving the scene, or 300 s (`PartnerTalkTimeout` 54,
  UpdateWishTalk 874-877).
- **S4.5 (mutual exclusion race)**: advisory lock with no arbitration. Both start using the same board in the same RTT:
  each has `_partnerTalk==null` locally, both send Started, both proceed (double turn-in/donation at one board).

Size: WishConfirm.cs 1431 (protocol ≈700), WishTalk lock ≈150, board lock ≈100.

---------------------------------------------------------------------------------------------------------------------
## 5. Lifts (S/CoopSave.Lifts.cs, LiftReplay.cs, FsmLift.cs, CageLift.cs, Carriage.cs)

Purpose: in a room both players are in, one game ("decider" = larger save key) plays rides and serves calls; the other
forwards calls and replays rides; a room entrant takes the lift state from whoever has been in the room longer.

Constants (Lifts.cs): wait at stop 2.5 s (26), call lifetime 30 s (31), call resend 1 s (36), state request delay
0.5 s (41), move retry 2 s (51), state wait 2 s (57), room-time tie 0.25 s (68).
State: `_lifts: Dictionary<object, SyncedLift>` (83) with per lift `WasMoving`, `ToldPartnerStop=-1`, `ArrivedAt=-∞`,
`PartnerRide=0`, `Calls` (list of `LiftCall{Stop, ByPartner, Inside, Time}`), `SentCallStop=-1`,
`SentCallTime=-∞`, avatar ride fields; `_liftRideCount` (93, monotonic, never reset), `_liftRoomStart` (98),
`_liftStateRequested` (103), `_liftStateReceived` (108). Reset on scene change (`OnLiftSceneChanged` 464-467 →
`ResetLifts` 472-478, `_liftRoomStart=now`) and ResetSession.
Roles: `GetLiftPartner` 395-399 (checked and IsInLocalScene). `DecidesLifts(partner)` 404-410 = partner null OR
(`!IsWaitingForLiftState()` && `!PartnerKeyWins()`); `IsWaitingForLiftState` 416-418 = state not received and
< 2.5 s since room start. `PartnerKeyWins` S/CoopSave.WorldChanges.cs:192-194 = ordinal(marker.PartnerKey) >
LocalKey.
Messages
- `LiftStateRequest{Scene}`: `UpdateLifts` 486-497, once 0.5 s after room entry if the room has lifts (else marks
  received).
- `LiftState{Scene, ObjectPath, FsmName, Part=stop, PartCount=moving, Key=_liftRideCount, PlayTime=room time or
  +∞ for a correction, Values=[state value]}`: `SendLiftState` LiftReplay.cs:210-223, from `OnLiftStateRequest`
  195-197 (every room lift) and `OnLiftCall` 108-113 (correction when the lift already stands at the called stop).
- `LiftCall{…, Part=stop, PartCount=inside}`: non-decider `RequestLiftRide` Lifts.cs:579-602 (resend at most every 1 s
  for the same stop); `OnLiftState` LiftReplay.cs:251-262 (local hero inside a lift that the partner's state would
  move away).
- `LiftMove{…, Part=stop, Key=++_liftRideCount, Values=[fromY]}`: `SendLiftMove` Lifts.cs:660-672 from
  `StartLiftRide` 634-655 and `UpdateFsmLiftRide` FsmLift.cs:383-413 (any FSM-lift ride not yet told, **in either
  game**).
- `LiftDrive{…, FsmName=ManualLift kind, PartCount=driving, Key=++_carriageDriveCount, Amounts=[claim],
  Values=[part, velocity, direction]}`: `SendCarriageDrive` Carriage.cs:445-465 (on button change, then every 1/15 s
  while driving or until stopped).
Local triggers: cage lift `OnLiftMoveToStop` CageLift.cs:220-245 → RequestLiftRide; FSM lift `HoldFsmLiftRide`
FsmLift.cs:354-377 (TOUCHED/CANCEL only); per frame `UpdateLift` Lifts.cs:528-568 (arrival stamps, decider serves
queued calls after the 2.5 s hold for the other player, non-decider clears its queue once the state wait is over).
Handlers (LiftReplay.cs; all drop unless `_checkedWith == sender` and the scene is loaded)
- `OnLiftMove` 26-80: drop if lift not found or `Key <= PartnerRide`; set PartnerRide; drop unknown stop; if already at
  stop → return; else unlock, snap height if idle and hero not inside, `Move(stop, inside, rtt/2)`; on refusal retry
  every frame for 2 s (`RetryLiftMove` 136-176), then give up (log only).
- `OnLiftCall` 85-125: drop if lift missing/stop unknown/locked; non-decider queues only during the state wait; decider:
  at stop → send correction state; else start ride or queue.
- `OnLiftStateRequest` 178-201: drop if scene ≠ active; reset PartnerRide and carriage partner fields; send state of
  every room lift.
- `OnLiftState` 229-300: set `_liftStateReceived=true`; **take the state only if the partner has been in the room
  longer** (PlayTime > local+0.25) or tie and partner key wins; hero inside → send a LiftCall back instead; else
  JoinRide/PlaceAt.
- `OnLiftDrive` Carriage.cs:471-515: drop if `Key <= PartnerDriveKey`; claim arbitration (higher claim wins, tie →
  key); updates follow fields. Local override after 10 s hold (`CarriageMaxHold` 31, OnCarriageUpdate 365-370).
Waits: calls wait for the decider (≤30 s lifetime); a lift waits 2.5 s at a stop for the other player; the entrant
does not decide for ≤2.5 s.

Suspects
- **S5.1 (divergence, no repair)**: `RetryLiftMove` gives up after 2 s (172-175); the two games then keep the lift at
  different stops until someone leaves the room. Only a `LiftCall` for the stop the decider's lift already stands at
  triggers a correction (108-113).
- **S5.2 (crossing FSM-lift rides; traced)**: `UpdateFsmLiftRide` sends `LiftMove` for rides the FSM started by itself
  in *both* games (406-412, no decider check). If both FSMs start rides to different stops in the same RTT, each
  replays the other's (Key > PartnerRide) and turns around; afterwards `ToldPartnerStop` equals the replayed stop so
  neither re-sends → the lifts swap ends. (Player-initiated TOUCHED/CANCEL are held on the non-decider, so this needs
  scripted rides.)
- S5.3 (key disagreement): if both sides compute `PartnerKeyWins()` the same way (stale/mismatched `marker.PartnerKey`)
  nobody decides (calls are forwarded and never served; `OnLiftCall` 98-105) or both decide.
- S5.4: `LiftStateRequest` dropped when the partner isn't in the scene yet (179); the entrant simply decides after
  2.5 s — if the partner arrives a moment later, both entrants compare room times; fine unless clocks tie within 0.25 s.

Size: Lifts 741 + LiftReplay 411 + FsmLift 534 + CageLift 329 + Carriage 587 = 2602 lines; protocol ≈1100.

---------------------------------------------------------------------------------------------------------------------
## 6. Rescue from a cocoon (S/CoopSave.Rescue.cs; chase variant in LavaChase.cs)

Purpose: a dying player waits (≤45 s) for the partner to hit their cocoon 5 times; two deaths = both go to bench.

Constants: 5 hits (27), wait 45 s (33), leave-key dead time 0.5 s (40).
State (victim): `_rescue: PendingRescue{Scene, Position, StartTime, Hits, Outcome=Waiting|Rescued|Ended, Key,
PartnerDown, Chase, StandAt}` (314, class 131-201), `_lastRescueKey` (320, ++ per death), `_deathCanWait`,
`_heldDeathAnnouncement`, `_deathPassesNoTime`.
State (rescuer): `_partnerWaitingRescue: ushort?` (342), `_partnerCocoonScene=""` (349), `_partnerCocoonPosition`,
`_partnerRescueKey` (359), `_rescueTarget: RescueTarget{PlayerId, Scene, Cocoon, Hits, Key}` (325).
Messages
- `RescueOffer{Scene, Values=[x,y] or [x,y,1 for chase], Key}`: `StartRescueWait` 893-940 (send 918-924), called from
  `HoldDeath` 776 after the first step of the death coroutine, only if `_deathCanWait` (`CanWaitForRescue` 733-748:
  connected, in game, marker, checked partner, and `_partnerWaitingRescue != partner`).
- `RescueHit{Part=hits, PartCount=5, Key=target.Key, [Values=[x,y] stand-at for chase]}`: `OnRescueCocoonHit`
  2023-2042 (each hit on the shown cocoon; on 5th removes the target locally but keeps `_partnerWaitingRescue`);
  chase stand-up `StandUpChasePartner` LavaChase.cs:1120-1142 (sets `_partnerWaitingRescue=null` first).
- `RescueEnd`: `EndRescueWait` 979-998, every end of a wait (timeout, leave key, rescued, lost, hero alive again),
  only if a checked partner exists.
- `RescueLost`: `TellPartnerNobodyIsComing` 653-681 when the local player dies (`WrapDeath` 603-607 with
  `!_deathCanWait`) while `_partnerWaitingRescue == checked partner`; also `OnChaseDeath` LavaChase.cs:1151-1154.
  Clears local `_partnerWaitingRescue` and the target. **No Key.**
Handlers
- `OnRescueOffer` 1836-1884: drop unless marker/partner/`_checkedWith==sender` (1837-1839); remove old target; if local
  `_rescue` is Waiting → treat as double death `OnRescueLost(sender)` (1850-1854); else set
  `_partnerWaitingRescue`, scene/pos/key; chase flag → `OnChaseDeath`; else chat + `ShowPartnerCocoon` 2049-2067
  (spawns only if in that scene; also on every scene change `OnRescueSceneChanged` 2129-2139).
- `OnRescueHit` 1892-1917: drop unless local Waiting and sender is checked partner; drop if `Key != rescue.Key`;
  record hits; if hits ≥ count → `Outcome=Rescued` (+ StandAt).
- `OnRescueEnd` 1924-1934: clear `_partnerWaitingRescue` if sender; forget chase death; remove target.
- `OnRescueLost` 688-703: drop unless local Waiting and sender is checked partner; `Outcome=Ended`, `PartnerDown=true`,
  announce the held death.
Local events: `HoldDeath` loop 804-848 (45 s timeout, leave key); after the loop `EndRescueWait` + `TryRevive` or bench
(857-886). `UpdateRescue` 1792-1827: hero alive while Waiting → EndRescueWait; checked partner null → Ended;
rescuer side removes target if partner changes or scene differs. `ResetRescue` 2146-2161 (PartnerLeft, local
disconnect, ResetSession) — sends nothing.
Wait condition (victim): until `Outcome != Waiting` via final RescueHit, RescueLost, crossing Offer, 45 s, leave key,
partner unchecked, hero alive again, reset.

Suspects
- S6.1 (one-sided wait, bounded 45 s): `RescueOffer` dropped when the receiver's `_checkedWith` isn't the sender yet
  (1837-1839) — e.g. the two `FinishCheck`s complete at different times (Check.cs:236-243). Victim waits with no cocoon
  anywhere; no resend.
- S6.2 (cosmetic divergence): victim times out (45 s) and sends RescueEnd while rescuer's 5th hit is in flight;
  rescuer prints "You broke X out", victim goes to bench (OnRescueHit drops because Outcome != Waiting).
- S6.3 (stale RescueLost, no key): RescueLost carries no death key (670-673, 689). If a RescueLost is delayed past the
  end of the wait it was about (victim timed out, benched, died again and started a new wait), it ends the new wait.
  Needs a long network stall (seen: 9 s relay blackout) to be reachable.
- S6.4: the rescuer keeps `_partnerWaitingRescue` after the 5th hit until `RescueEnd` arrives (2038-2041 removes only
  the target). If the rescuer dies in that window, `CanWaitForRescue` is false → rescuer goes straight to bench and
  sends RescueLost, which the (already Rescued) victim ignores. Consistent, but the rescuer loses their own rescue.

Size: Rescue.cs 2193 lines (protocol ≈500) + chase stand-up ≈150 in LavaChase.cs.

---------------------------------------------------------------------------------------------------------------------
## 7. Other flows

### 7a. Races (S/CoopSave.Races.cs)
Purpose: both say yes (WishConfirm kind "race"), both wait at the start line, and either player's win wins for both.
Codes: Ready 0, Won 1, Out 2 (36-46); start timeout 60 s (31).
State: `_raceFsm` (112), `_raceStarted` (124), `_raceHold: RaceHold{State, EventName, IsStart, Since}` (129),
`_raceWonHere` (139), `_raceResultSent` (144), `_partnerRacing` (149), `_partnerRaceWon` (154), `_partnerRaceOut` (159),
`_partnerRaceReadyAt=-∞` (165). `StartRaceRound` 275-293 resets all but `_partnerRaceReadyAt`; `EndRaceRound`
298-307; `ResetRaces` 312-323 (scene change, ResetSession).
Sends (`SendRace` 672-679, Scene = current scene): Ready from `HoldRaceStart` 379 (partner was first) and 389 (hold),
and `OnPartnerRaceReady` 649 (reply when already started). Won/Out once per round via `SendRaceResult` 661-670 from
`OnLocalRaceWon` 576, Reset state 263, `HoldRaceLoss` 409.
Handler `OnRace` 588-629: drop unless checked sender and same scene (590-592). Ready → `OnPartnerRaceReady` 635-656
(release own start hold; or reply Ready if already started; else remember `_partnerRaceReadyAt`). Won → set
`_partnerRaceWon` if racing and not won (599-603). Out → `_partnerRaceOut` if racing.
Holds: FSM events via `CoopSave.OnProcessEvent` S/CoopSave.cs:560-567 → `HoldsRaceEvent` 329-354: start line FINISHED
(`HoldRaceStart` 360-396), loss HERO END / DISQUALIFIED (`HoldRaceLoss` 402-421, only if partner racing, not out,
in room). `UpdateRaces` 427-488: start hold released by partner not in room or 60 s; loss hold released by partner
won (→ `WinRaceWithPartner`), partner not in room, partner out; any hold dropped if the racer left the held state.
**Loss hold has no timeout.**
Suspects
- **S7a.1 (Ready ping-pong; traced)**: `OnPartnerRaceReady` replies Ready whenever `_raceFsm != null && _raceStarted`
  and no start hold (647-651). If both players are "started without a hold" when a Ready is in flight, they reply to
  each other forever (one message per RTT) until one side's round ends/restarts. Reachable order: B holds at line,
  sends Ready at t0 → B's hold times out at t0+60 and B runs alone (`_raceStarted=true`) → A reaches the line at
  t0+59.9, sees `_partnerRaceReadyAt` fresh, starts at once and sends Ready (379) → B replies (649) → A replies → …
  When one side restarts a round, the in-flight Ready lands in `_partnerRaceReadyAt` and the next try starts
  "together" without the partner at the line.
- S7a.2: loss hold waits indefinitely for the partner's Won/Out or the partner leaving the room; `_raceResultSent`
  makes the partner send its result only once per round, and Won/Out are dropped if the local `_raceFsm` is null at
  arrival (599-601, 611-613). No concrete deadlock order was found, but there is no timeout to bound one.

### 7b. Clothes grab (S/CoopSave.ClothesGrab.cs)
Purpose: while both lack their clothes, whoever reaches first crouches and waits; both grab once both reached.
Codes (PartCount): NotReady 0, Ready 1, Started 2, Ended 3 (103-118).
State: `_clothesGrabHeld: Fsm?` (123), `_clothesGrabGoHeld` (128), `_partnerReadyToGrab` (133, **kept across scene
changes**), `_partnerStandIn` (138).
Sends (`SendClothesGrab` 233-245): Ready on local reach (Idle→Crouch) if the checked partner's `CrestType` is Cloakless
(`OnLocalClothesGrab` 177-197); Started on entering Slam Down (155-158, also clears `_partnerReadyToGrab`); Ended on
Hero Flip (159-161); NotReady on jump-to-cancel (`UpdateClothesGrab` 318-320).
Handler `OnClothesGrab` 252-291: drop unless marker/partner/checked. Ready → `_partnerReadyToGrab=true`, release own
hold if held. NotReady → false. Started → false, release own hold, start stand-in. Ended → end stand-in.
Hold: NEXT event in Crouch swallowed (`HoldsClothesGrab` 204-212, via S/CoopSave.cs:570-572). Released by partner
Ready/Started, jump (UpdateClothesGrab 297-327), scene change (OnClothesGrabSceneChanged 422-426, **no NotReady sent**),
partner left (442-445). No timeout.
Suspects
- **S7b.1 (stale readiness; traced)**: a waiting player who leaves the room some other way than jump (death, scene
  change) sends nothing; the partner keeps `_partnerReadyToGrab=true` and, on reaching, grabs immediately and alone
  (183-186). The returning player then gets the partner's Ready and also grabs alone.
- S7b.2: whether to wait at all is decided from the partner's `CrestType` as last received by a different message
  (PlayerSetting); a stale "Cloakless" makes the local player wait with no timeout for a partner who will never reach
  (only jump frees them).

### 7c. Prison capture (S/CoopSave.PrisonCapture.cs)
Purpose: if one player is captured, the other is taken to the prison too.
Send `PrisonCapture` in `OnApplyCaptured` 64-73 after the game's own capture, unless replaying (`_capturingForPartner`)
or unchecked. Handler `OnPrisonCapture` 79-90: drop unless marker/partner/checked, drop if already in prison clothes;
set `_pendingCaptureBy`. `UpdatePrisonCapture` 95-137 moves the hero when movable (no timeout; cleared by
ResetPrisonCapture 142-144 on ResetSession only).
Suspects: S7c.1 drop when the receiver is not checked with the sender at that moment (80) — no resend → one player
captured, the other free. S7c.2 both captured at once → each sends; each receiver sees prison clothes already → drop
(fine).

### 7d. Cage trap (S/CoopSave.CageTrap.cs)
Purpose: the bait cage only springs when the other player stands in the cage; then it springs in both games.
Send `CageSprung{Scene, ObjectPath}`: `RedirectCageBait` 96-123 → `SendCageSprung` 148-161 when the local player takes
the bait and the partner's **avatar** is inside the cage's capture range (`IsPartnerInCage` 135-143).
Handler `OnCageSprung` 168-197: drop unless marker/partner/checked, bait object loaded and active; set
`_partnerSprangCage`; if bait FSM is Idle or Stand → force `Take Control` → (Kneel redirected to Send Trap Event);
otherwise (e.g. kneeling) it goes off when the local player takes it. Reset on scene change 202-205.
Suspects
- **S7d.1 (one-sided capture)**: the sender decides from the partner's avatar position (latency-delayed). If the
  receiver has already stepped out of the cage in its own game, its cage still springs (forced state) but without its
  hero inside → sender captured, receiver not.
- S7d.2: if the receiver's bait FSM is in a state other than Idle/Stand/kneeling-path (e.g. Tracking/Cancel), nothing
  happens until the receiver takes the bait themself.

### 7e. Rising lava chase + set down (S/CoopSave.LavaChase.cs)
Purpose: one lava per room, run by the scene host; the scene client follows; a burn puts a player back beside the
partner; a chase death stands up beside the partner.
Messages
- `LavaChase{Scene, Sequence, Values=[y, vy, chasing, hasSafe, sx, sy, safeAge]}` every 0.2 s while chasing and the
  partner is in the room (`SendLavaSample` 780-804, from `UpdateLavaChase` 702-740). Handler `OnLavaChase` 844-867:
  drop unless checked sender, lava set up, same scene; drop older sequence only while fresh (≤1 s). Used by
  `FollowTheRoomsLava` 603-628 on the scene client (≤1 s old samples).
- `LavaChaseSetDown{Scene, Values=[y, holdSeconds]}` from the **scene client** when its lava enters the "Wait" state
  (`ReportLavaSetDown` 811-837, once per set-down). Handler `OnLavaChaseSetDown` 875-893: drop unless checked, lava,
  same scene, **receiver is scene host**, lava chasing and new y lower; hold host lava for the given time.
- Chase deaths ride on the rescue messages: victim's `RescueOffer` with Values[2]=1 → `OnChaseDeath` 1148-1165
  (schedules `_chaseStandUp` 3 s later, hides body); stand-up is a `RescueHit(5/5, Key, [x,y])` sent by
  `UpdateChaseStandUp` 1053-1084 / `StandUpOnLeaving` 1099-1114 (on scene change via ForgetLavaChase 395-397).
Suspects
- S7e.1: set-down dropped when roles are not settled on the host side (877) → the client's lava is pulled back up over
  its player by the next samples (repeated burns). Bounded by the chase.
- S7e.2: role flip during the chase (host transfer) → both games may follow each other's samples or neither; not
  traced.
- S7e.3: chase stand-up waits for the survivor to have a safe spot (1070-1073) with no bound other than the victim's
  45 s rescue timeout.

### 7f. Flea games (C/FleaGameCoop.cs)
Purpose: show both scores; the host feeds the partner's points to the difficulty director; the outro waits until both
won every game; the host doesn't put fleas away while the partner still plays.
Messages (`Send` 419-432, only if a checked partner exists)
- `FleaGameScore{Key=running total, Part=playing?}`: on each local scoring tink (358-359) and on `ForgetScores`
  394-404 (reset events).
- `FleaGamesOutroReady{Part=1|0}`: `OnHeroControllerUpdate` 493-512, **only when the local value changes**
  (`_sentOutroReady` 173, reset only in DeregisterHooks 245-246).
Handlers: `OnPartnerScore` 257-269 (max total; `_partnerPlaying = Part!=0`; host feeds points and may release a held
reset); `OnPartnerOutroReady` 463-469 (`_partnerOutroReady = Part!=0`). No scene check on either.
Waits: `OnFleaGamesOutroReady` 479-486 returns `_partnerOutroReady` in place of the game's own answer while a partner
exists. Host swallows `RESET FLEA GAMES` while `_partnerPlaying` (378-382), released by `ReleaseHeldReset` 275-287
only from `OnPartnerScore`.
Suspects
- **S7f.1 (one-sided outro wait; traced)**: after a disconnect/reconnect of one player, the one who stayed keeps
  `_sentOutroReady=true` and never resends; the reconnecting game starts with `_partnerOutroReady=false`
  (DeregisterHooks on its local disconnect, ClientManager.cs:735) → its outro gate stays closed forever (until the
  stayer's value changes, which it never will once all three are won).
- S7f.2 (stale gate the other way): the stayer keeps `_partnerOutroReady` from before the disconnect (never reset on
  remote disconnect or new check).
- S7f.3 (held reset never released): `_partnerPlaying` is only updated by FleaGameScore; if the partner disconnects or
  leaves while playing, or their last score message carried Part=1 (their `IsLocalPlaying()` still non-idle when their
  reset ran), the host's held reset is never released (fleas never put away). Last case not verified.

### 7g. Deliveries (S/CoopSave.Deliveries.cs)
Purpose: a delivery breaking for one player only fails if the partner doesn't carry theirs either; the first to turn
it in fades the partner in.
- `DeliveryBreak{PartCount=0 report, WishNames=[w], WishValues=[state], Sequence}` from `NoticeDeliveryBreak` 324-341
  (records `_brokenDeliveries[w] = {Value, Report=Sequence}`). Answer `{PartCount=1 still carried | 2 failed,
  Key=report.Sequence}` from `OnPartnerDeliveryBroke` 394-448 → `SendDeliveryAnswer` 453-468. Handler
  `OnDeliveryBreak` 347-386: reports go to OnPartnerDeliveryBroke (ignores reports older than the last change, and
  reports about a wish the local player already turned in — **no answer sent** in that case, 406-409 and 412-414);
  answers must match `broken.Report` (362).
- `DeliverySummon{Scene, ObjectPath, Records=[gate], Values=[x,y]}` from `SendDelivery` 520-544 →
  `SendDeliverySummon` 549-565. Handler `OnDeliverySummon` 700-713: drop unless checked and no summon beyond Waiting.
  `UpdateDeliverySummon` 719-765 (5 s wait for a movable moment, 20 s arrival, 0.3 s fades; skips when in a fight,
  arena, waiting boss room, bench, dialogue, race). Fire-and-forget; nothing waits on the partner.
Suspects: S7g.1 a break report crossing the partner's turn-in gets no answer (406-409); the breaker keeps
`_brokenDeliveries[w]` without `StillCarried` (message only). S7g.2 a second summon while one is past Waiting is
dropped (702). Neither blocks anyone.

### 7h. Boss checkpoint BossFight (S/CoopSave.Checkpoint.cs)
Purpose: when a boss fight starts with both players, both save and remember the door; a partner dropping out ends the
fight for the other.
- Send `BossFight{Records=[scene]}`: `OnBossFightStarted` 119-128 (from `BossRoomCoop.BossFightStartedEvent`,
  ClientManager.cs:337) and `UpdateCheckpoint` 231-234 when the partner enters the scene of an ongoing checkpointed
  fight (edge `_partnerWasInScene` false→true).
- Handler `OnBossFight` 134-143: drop unless `Records[0] == current scene` and `GetFightPartner` 150-161 (not already
  started here, in game, hero alive, checked partner in scene).
- `PartnerLeft` sets `_interruptPending` if in a fight (S/CoopSave.cs:~722-725) → `UpdateCheckpoint` leaves through the
  door (244-260, retry 0.5 s).
Suspects: S7h.1 BossFight dropped when the receiver's hero is dead or the scene differs at arrival; resend only on the
partner's next scene entry. S7h.2 inherits S2.2 (a follower-side "start" that the host never saw still creates
checkpoints on both).

### 7i. Save check: Hello / WorldState / Left (S/CoopSave.Check.cs) — the "wait for the partner at the bench" gate
Purpose: both saves exchange world progress before either player may move; the player is held until checked.
State: `_checkPartnerId` (85), `_checkKey` (90, 0), `_highestCheckKey` (95), `_partnerHello` (101), `_stateSent` (106),
`_stateReceived` (111), `_stateAdded` (117), `_stateParts` (122), `_lastHelloUtc` (167); `_checkedWith`,
`_everChecked`, `_held` in CoopSave.cs. `ResetCheck` 172-199.
Sends: Hello{Key, PartnerKey, PartCount=0 start|1 answer} `SendHello` 259-269 from `UpdateCheck` 205-244 (start, and
resend every 2 s while `!_partnerHello || !_stateReceived`, 223-228) and `OnHello` (338, 346, 361). WorldState parts
{Key, Part, PartCount, …} `SendWorldState` 513-569 once when `_partnerHello` (230-234) and again from
`ResendWorldState` 579-593 on every matching Hello. Left{Key=_highestCheckKey} from ResetSession (S/CoopSave.cs:581-583).
Handlers: `OnHello` 274-362 (key mismatch → PartnerLeft once; answer for same key; older key → re-hello if not
checked; newer key → drop `_checkedWith`, adopt key, answer). `OnWorldState` 368-405 (drop if already complete, wrong
key/partner). `OnLeft` 410-416 (ignored if older than the current check).
Wait: `UpdateSession` holds (`Hold` S/CoopSave.cs:491-524; bench get-up events swallowed 553-556) while not checked
and (`_held || !_everChecked || atBench`); released when `FinishCheck` sets `_checkedWith` (Check.cs:422).
Note: `UpdateCheck` stops running as soon as the local side is checked (S/CoopSave.cs:437); the other side's hellos
keep it answering via `OnHello`. Known unproven race from memory ("check key race"): not re-traced here.

### 7j. Pairing (S/CoopSave.cs:1100-1340) — handshake, not a live wait
Request{Key=id, Records=defeats} → Accept{Key, Records} or Refused → Confirm{Key} → (Cancel{Key}). Requests open 120 s
(`PairRequestTime` 55). Simultaneous requests: larger id wins (1172-1178). Requester pairs on Accept (1253) *before*
sending Confirm; accepter pairs on Confirm (1284). Suspect S7j.1: requester paired, Confirm lost to a disconnect or
arrives after the accepter's 120 s window (→ Cancel, 1272-1276, which unpairs the requester 1308-1316); if the
disconnect happens first, no Cancel ever comes → one-sided pairing and a save that waits at the bench for a partner
whose save is not paired.

### 7k. Trapdoors (S/CoopSave.Trapdoors.cs) — lease, not a wait
`Trapdoor{Scene, ObjectPath, PartCount=1 kept open|0 released, Values=[sign], Names=[exitScene, exitGate]}` from
`NoticeTrapdoorOpen` 134-156 and `UpdateTrapdoors` 158-182 (edge-triggered on "hero inside"); handler `OnTrapdoor`
221-249 keeps `_partnerTrapdoors[key]` until a 0 arrives; `ResetTrapdoors` 380-385 on ResetCheck. A game never
re-announces what it holds for the partner (by design, avoids mutual hold). Low risk; a lost 0 is impossible on the
reliable channel, and PartnerLeft clears the lease.

---------------------------------------------------------------------------------------------------------------------
## 8. Not coordination waits (checked and excluded)
- `Interaction`, `WorldChange`, `WishChange`, `WishTurnIn`, `WishProgress`, `StoryItem`, `WorldTrigger`: one-way
  replays with sequence stamps; nothing waits for an answer.
- BossRoom `RecordSet` (BossRoomCoop.cs:698-730): one-way.
- `CoopHitUpdate`, `CoopCheckUpdate` (state check): not surveyed here.
