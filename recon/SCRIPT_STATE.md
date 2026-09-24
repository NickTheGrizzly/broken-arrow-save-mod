# Mission script state: save/restore design (the "native" route)

Recon source: F4 dumps of Parnu Invasion at t=73 s, 961 s, 1313 s (2026-09-24).

## Where the mission's progress lives
`GameController.Instance.GetScenarioController.GetNodeController` (`NodeController`):
- `_nodeList` — 2347 `NodeCore` for Parnu (subgraph instances included; key = `ID`, `SubID` = parent subgraph).
- `CustomVars` — null for Parnu. `NodeController.MissionStorage` (static) — only constant links
  (zone/spawn/team ids), no progress.
- **Progress is stored inside the nodes** as plain values:
  - `NodeCore.Status` (Idle / PreActivated / InProcess).
  - `PortCore._SignalWasReceived_k__BackingField` per input port.
  - Logic fields, e.g. `NodeOnObjectiveZoneCaptured._isCaptured/_wasActivatedOnce`,
    `NodeAND.Input_A/Input_B/Result`, `NodeOnUnitDead._eventsCount/UnitsCount`,
    `NodeSpawn*._spawnedCount`, `BaseWaypoint.Completed`, `NodeCountUnits.UnitCount`,
    `NodeGetDifficultyBranch.Difficulty`, `NodeDelay.Time/_lastTime`, `NodePulse._time`.
  - Unit references are `UnitObject` = list of **mission uids** (`_unitList`), not entities.
- The game has no runtime save of this (`GraphSaveData`/`NodeSaveData` = mission definition).

Parnu progression, from the activation timeline: zone A captured (#2 `NodeOnObjectiveZoneCaptured`)
→ AND #8 input A; zone B (#3) → AND #8 input B → AddMoney, UpdateTask×3, `NodeActivatePZ` #11
(sector 2). Sector-2 zone (#9177) → AND #26 input B, ...

## Relevant engine hooks
- `SpawnData.UID`, `.Groups` (string), `.IsMEUnit`, `.MEUnitData` — respawn a unit as a native
  mission unit with its old uid ⇒ `MissionEntitiesStorageSystem` registers it and every
  `UnitObject` in the script still points at the right unit. No remapping.
- `EcsEventBus.NodeSystem.SetScriptEngineActiveState(bool)` — switch the script engine off while we
  restore (semantics to verify).
- `EcsEventBus.UI.UpdateTask(int taskUid, TaskStatus)` — objectives panel (Active/Success/Fail/Hidden).
  Recorded by appending our own delegate; replayed on load.
- `EcsEventBus.Gameplay.ActivatePlayableZone(uid, snap)`, `ChangeObjectiveOwner`, `Set/GetMoney`,
  `Set/GetIncome`.
- `NodeController.OnActivateNode/OnDeactivateNode` — activation recorder (`ScriptDump`).

## Save (v5)
Per node: `id`, `st` (Status), signaled input ports, state-ish logic fields (bool/int/float/enum/
string/Vector3, `UnitObject` as uid list), plus activation times from the recorder. Skip delegates,
Flow, `DataLinkerItem`s (config), cancellation/disposable handles, `_subscribed`/`_initialized`.
World effects: task statuses, active playable zone, zone owners, money/income. Units save their
mission uid + groups (already in v4).

## Load
1. Relaunch the scenario; **suppress all non-mod spawns** until the restore is done (the script's
   start-up spawns would otherwise duplicate the saved units).
2. World ready → let the start-up chain run ~1 s → script engine off.
3. Cancel every node the fresh run left InProcess (cancel `CancellationTokenSource`, dispose
   `IDisposable` fields), status → Idle.
4. Spawn the saved army with `UID`/`Groups`/`IsMEUnit` (native registration).
5. Write node state: status, port flags, logic fields, `UnitObject`s.
6. World effects: zone owners, playable zone, tasks, money/income, game time.
7. Resume nodes that were InProcess at save: `NodeLogic.Run(false)` (timers get their remaining
   time where computable: `Time - (saveTime - lastActivation)`).
8. Script engine on.

## Open questions (answer by testing)
- Does `SetScriptEngineActiveState(false)` pause flow without tearing down subscriptions?
- Does writing `_isCaptured` etc. suffice, or do event nodes cache state elsewhere?
- Resuming blocking nodes (moves, delays) mid-flight: does `Run(false)` re-issue cleanly?
- `NodeRandomData.OutData` (randomly chosen reference) — restore by index of the matching DataN.
