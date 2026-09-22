# Broken Arrow v1.2.0 — LOAD side plan (spawn recipe)

Goal: recreate saved units in a battle. Reads solved (see save side). This is the write/spawn path.

## Ingredients (all confirmed in `recon/dump`)
- **Service locator:** `Il2CppBrokenArrow.Shared.Ecs.Services.Session` (static): `TryGetService<T>(out svc, bool)`, `GetService<T>()`, **`GetService(Il2CppSystem.Type)`** (non-generic, reflection-friendly; get the type via `Il2CppInterop.Runtime.Il2CppType.Of<T>()`), `ServicesCollection` (ConcurrentDictionary<Type,IService>).
- **Unit blueprint by id:** `Il2CppBrokenArrow.Shared.Ecs.DataBaseService.GetUnitById(int unitId, bool throwIfNull)` → `DataBase.Models.Units` (instance method). Also `DataBase.LoadUnits.GetNewUnit(int, bool forceNewLoad)` / `GetInitById(int, bool)`.
- **Player by owner uid:** `GameController.Instance.GameSession.GetPlayer(int id)` → `Client.Ecs.Utils.PlayerInfo` (also `TryGetPlayer`, `GetPlayers()`; `IPlayerInfo.UID`/`TeamSide`).
- **Spawn input:** `Il2CppBrokenArrow.Client.Ecs.Spawn.SpawnData` (new()): `UnitToSpawn` (Units), `UnitIDToSpawn` (int), `OwnerInfo` (PlayerInfo), `SpawnerPosition` (Vector3), `RotationY` (float), `RequestPosition` (Nullable<Vector3>), `RequestRotate` (Nullable<Quaternion>), `Cargo`/`OptionIds`, `PostInit`.
- **Spawn call:** `NetworkSpawnService.UnitSpawner` (`NetworkUnitSpawner`) → **`SendSpawnUnit(SpawnData data, IEnumerable<Units> cargo)`** (UniTask). Get `NetworkSpawnService` from `Session.GetService`.
  - `NetworkUnitSpawner` also has `SendSpawnContainer`, `SendSpawnCargo` for transports/cargo (later).
- **Injection timing:** Harmony postfix `GameController.OnScenarioControllerLoaded()` (full flow). First PoC: spawn into the CURRENT running battle via a hotkey.

## Steps
1. **Dry-run (F6, safe):** parse `quicksave.basave`; resolve `NetworkSpawnService` + `DataBaseService` via `Session.GetService(Il2CppType.Of<T>())`; for unit #1 resolve `Units = db.GetUnitById(typeId,false)` and `PlayerInfo = GameSession.GetPlayer(owner)`; confirm `NetworkSpawnService.UnitSpawner != null`. Log all to `Saves\live_load.txt`. No spawn yet.
2. **Spawn one unit:** build `SpawnData{ UnitToSpawn, UnitIDToSpawn, OwnerInfo, SpawnerPosition=pos, RotationY=yaw }` (start with non-nullable pos/yaw; try `RequestPosition`/`RequestRotate` nullables if placement is wrong) and `await UnitSpawner.SendSpawnUnit(data, emptyUnitsList)`. Verify a unit appears at the saved position/owner.
3. **Spawn all**, then set HP (open interop question — writing value-type components; probe a health setter / damage-apply / `SpawnData.PostInit`; v1 may accept full HP).
4. **Full flow:** save scenario/deck identity → relaunch via `ISceneTransition.ChangeScene` → postfix `OnScenarioControllerLoaded` → clear default units → spawn saved army.
5. Cargo/transport, orders, game-mode timers as follow-ups.

## Interop notes
- Build Il2Cpp objects with `new Il2Cpp...()`; UnityEngine.Vector3/Quaternion construct normally. `Il2CppSystem.Nullable<T>` construction is fiddly — prefer `SpawnerPosition`+`RotationY` first.
- `SendSpawnUnit` returns `UniTask` — fire it (it schedules on the game loop); don't block. Pass an empty `Il2CppSystem.Collections.Generic.List<Units>` for cargo.
- Cast `Session.GetService` result with `.Cast<T>()`.
