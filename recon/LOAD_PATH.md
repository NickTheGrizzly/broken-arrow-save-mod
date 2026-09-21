# Broken Arrow v1.2.0 — Offline launch + save/load hook map

Offline-only (this install has no online; mods can't go online anyway). We ignore all `NetworkLobbyService`/Galaxy/replay paths. The normal menus already start skirmish/PvE locally; the mod hooks that flow to add Save/Load and to inject saved state after a battle loads.

All types are `Il2CppBrokenArrow.*` (interop names). Access from the MelonLoader mod via Il2CppInterop.

## 1. Reaching the running battle
- **`Client.Ecs.Controllers.GameController`** (singleton):
  - `static GameController Instance { get; }`, `static bool IsInstanceAlive`, `static IReadOnlyReactiveProperty<bool> IsInitialized`.
  - `static event OnUpdateDelegate OnUpdate` — per-frame tick hook for the mod.
  - Implements `IGameController`, which exposes:
    - **`World GameContext`** — authoritative DefaultEcs simulation world (the save target).
    - `GameSessionContext GameSession`, `ScenarioController GetScenarioController`, `ScenarioDataHandler GetScenarioData`, `EcsEventBus GetEcsEventBus`.
    - **`EntitiesHelper GetEntitiesHelper`**, **`SpawnUtils SpawnUtils`**, `IUnitLoadScope UnitLoadScope`, `IMemoryContext BattleMemoryContext` — enumerate/spawn units (core for read on save, recreate on load).
    - `float GameTime`, `float TimeScale`, `MapMetaData`, `MapSettings`, `string CurrentMapName`, `IPrefabLoader.Status LoadStatus`.

## 2. Starting a battle programmatically (LOAD side)
Reproduce the menu's own start, then inject. Order:
1. Resolve the saved scenario: **`MissionEditor.MissionResolver.ScenariosService`** → `ScenarioSource` (wrapped by `ScenarioSourceHolder`); `MissionEditor.Storage.ScenarioDataManager` for scenario data.
2. Seed decks: **`Client.Ecs.Decks_v2.PreloadSharedPlayerDeck`** statics — `Alpha`, `Bravo`, `ScenarioStartDeck` (`IDeckDataModel`), `IsMissionRestart`.
3. Set options: **`Client.Ecs.Utils.ISceneLoadManager.Instance.ScenarioPublicOptions`** (`Dictionary<int,int>`).
4. Kick the load (either works):
   - **`Client.Ecs.UI.ISceneTransition.Instance.ChangeScene(ScenarioSource src, string sceneName, bool editorMode=false, Sprite, string title, SceneTransitionDecorConfig.Hint, ChangeSceneExtraOptions)`**, or
   - **`ISceneLoadManager.Instance.LoadSceneAsync(ScenarioSource src, string sceneName, bool editorMode=false)`** / `LoadScene(sceneName, editorMode, loadMissionFileName, sharedMode)`.

## 3. "World is ready" — state-injection point (LOAD side)
Pick one, prefer a Harmony postfix:
- **`GameController.OnScenarioControllerLoaded()`** (postfix) — fires after the scenario/world is built. Primary hook.
- or **`ScenarioController.OnLoaded`** event / `ScenarioController.PostLoad()` (UniTask) postfix.
- or subscribe **`GameController.IsInitialized`** reactive property.
At this point: `GameController.Instance.GameContext` exists and default scenario entities are spawned. Injection = reconcile that world to the saved snapshot (clear/replace entities, restore components), then restore `GameModeService` + timers + `GameTime`.

## 4. State to capture (SAVE side)
- **ECS world**: enumerate entities in `GameContext` and their components. Serialize via a custom DefaultEcs `IComponentReader` (the game already implements this interface) or per-component-type readers. `EntitiesHelper` should provide enumeration; components live under `Shared.Ecs.Components.*` (+ client component namespaces).
- **Game mode / victory / timers**: `Client.Ecs.GameMode.GameModeService` — `GameModeType`, `InfiniteTime`, `Timers` (`Dictionary<TimerType,float>`), current mode data (Conquest/Destruction/WarGoals).
- **Session**: `GameSessionContext` (teams, players, colors, income) via `GameController.GameSession`; `ScenarioController.LoadTeamsAndPlayers`.
- **Time**: `GameTime`.
- **Reconstruction metadata** (for restart): scenario id/guid, `CurrentMapName`/sceneName, decks (Alpha/Bravo), `ScenarioPublicOptions`.

## 5. Save/Load UI
- In-game pause menu = **`Client.Ecs.UI.Menu.Profile.EscapeMenu`** (`UIElement<EscapeMenuModel>`); also `GameMenu`/`GameMenuModel`. Add Save/Load entries here (Harmony patch on its open/build), or ship a MelonLoader IMGUI overlay + hotkeys as the low-risk first cut.

## 6. Anti-cheat note
`BrokenArrow.Core.Security` scans loaded modules (`CreateToolhelp32Snapshot`/`Module32*`). Offline with EAC off this is expected to be inert/telemetry-only, but the mod should still assume the process is observed — keep everything offline, and gate the mod on EAC-not-loaded.

## Open items before implementation
- Confirm `EntitiesHelper` gives whole-world entity enumeration (dump its members).
- Gauge the component set: how many `Shared.Ecs.Components.*` are pure blittable structs vs. holding Unity/managed refs (decides snapshot serializer complexity). This is the main Tier-3 risk (R4).
- Decide save granularity: full-world snapshot vs. unit-centric (positions/HP/ammo/orders/owner) — the latter is smaller and more patch-robust.
