# Broken Arrow — Save Mod Feasibility Memo (recon phase)

Game **v1.2.0**, Unity **2022.3.62f3**, IL2CPP. MelonLoader 0.7.3 confirmed working; interop assemblies generated. All findings below come from decompiled signatures in `recon/dump/` (AsmResolver over `Il2CppBrokenArrow.dll`, 4,757 types).

## Architecture (confirmed)

- **Custom ECS on `DefaultEcs`** (`Il2CppDefaultEcs.dll`), which ships full serialization (`BinarySerializer`/`TextSerializer`, `EntityWriter`, `ComponentTypeWriter`). The game itself implements `DefaultEcs.Serialization.IComponentReader.OnRead` and has `GetSerializableFields`/`IsSerializableField` helpers — a real component-serialization path exists.
- **`GameController` (singleton, `_instance`) is the battle controller.** It exposes:
  - `GameContext : DefaultEcs.World` — the **authoritative simulation world** (the save target).
  - `UiContext : DefaultEcs.World` — separate UI world.
  - `GameTime : float`, `TimeScale : float`, `GameSession : GameSessionContext`, `EcsEventBus`, `ScenarioController`, `BattleMemoryContext`.
- **Fixed timestep:** `GameController.SIMULATION_TARGET_FPS` / `SIMULATION_TARGET_DELTATIME` constants ⇒ deterministic stepping.
- **`EcsLoader`** owns the ordered system pipeline: init, input, **commands**, move, battle, plane, selection, ai, network, render, ui, postUpdate, cleanup.
- **Commands** = clean `BaseCommand` hierarchy (`MoveToPositionCommand`, `AttackCommand`, `AirstrikeCommand`, `FireMissionCommand`, `ResupplyCommand`, …) → `Commands.Systems.*` (`CommandsStateSystem : AEntitySetSystem<float>`).
- Networking = GOG **Galaxy** (`Il2CppGalaxyCoreLib`, `GalaxyNetworkController`, `GalaxyConnection`); message-driven.
- **`GhostService` is NOT netcode** — it's the unit-deployment placement preview (`Client.Ecs.Render`). No network state-snapshot serializer to borrow.

## The key discovery: a built-in replay system

`Il2CppBrokenArrow.Client.Ecs.GNetwork.Services.Replay.ReplayService`:
- Replays are **ZIP archives** in `ReplayService.GetReplayDirectory()`, each holding **metadata** + a **`RoomState`** (`ReadRoomState`, `ReadReplayInfo`, `ReadJson(ZipArchive,…)`).
- `ReplayMetadata`: fightId, lobbyType, mapId, mapName, mapSceneName, scenarioId/Guid/Hash, scenarioWorkshopId, **matchDurationSeconds**, savedUtc.
- Playback: `Play(ReplayInfo)` → `StartReplayWhenReady(controller, connection)` via a **virtual Galaxy instance** (`ReplayVirtualInstance`), with **`SetTimeSpeed`/`TimeSpeed`** (fast-forward) and `AddReplayViewer(RoomState)`.

⇒ The replay = the game's own **session serialization** (initial `RoomState` + recorded message/command stream). Deterministic sim + fast-forward playback is exactly the machinery a "load = resimulate to save point" needs.

## Feasibility questions — answered

1. **Deterministic sim?** Yes (fixed timestep + full replay reconstruction from recorded messages).
2. **Reusable serialization?** Yes — the replay format, and separately DefaultEcs component serialization over `GameContext`.
3. **Command pipeline?** Yes — `BaseCommand` + message-driven `Commands.Systems`.

## Open risks — must verify empirically (need the game running)

- **R1 (make-or-break): is a replay actually *recorded* for an offline / modded skirmish?** `ReplayService` only *plays/reads* — no client-side *recorder* method is exposed by name. Recording may be **server-side** (replays downloaded for online matches), and the offline local-skirmish path bypasses the network lobby, so it may bypass recording too. **Test:** play a normal (online) skirmish to completion, then an offline one; check whether a zip appears in the replay directory.
- **R2: hand-back to live control.** Replays are spectator/view-only. A true *resume-and-keep-playing* needs converting the replay's virtual instance into an interactive local host at the target tick — novel, unproven. Fallback if impossible: "save → resume as replay, then branch into a fresh local skirmish seeded from the reconstructed `GameContext`" (Tier-3-style snapshot of the reconstructed world).
- **R3: v1.2.0 API drift.** `NetworkLobbyService` no longer exposes `CreateLobby` (the BovineOverlord local-skirmish hook). The offline-launch and load hooks must be re-derived for 1.2.0.
- **R4: IL2CPP + DefaultEcs generic serialization.** The built-in `BinarySerializer` relies on runtime generics that AOT may have stripped; a direct `World.Serialize` may not work for all component types without a custom per-component reader.

## Recommendation (candidate tiers, decide after R1 test)

- **If replays ARE recorded offline (R1 ✓):** build on the replay system.
  - *Tier 2a (safe first win):* "Save" copies the in-progress replay; "Load" uses `ReplayService.Play` + `SetTimeSpeed` to fast-forward to `matchDurationSeconds`. Ships as **resume-as-replay** even before hand-back works.
  - *Tier 2b (true resume):* add hand-back to live control at target tick (R2).
- **If replays are NOT recorded offline (R1 ✗):** pivot to **Tier 3 snapshot** of `GameContext` World via a custom component reader (DefaultEcs `IComponentReader`), restored into a fresh local skirmish. Harder, patch-fragile.
- **Tier 1 quick-relaunch** (save map/deck/difficulty/options) remains a guaranteed early deliverable regardless.

## Immediate next step
Empirically resolve **R1**: play one online skirmish + one offline skirmish to completion (or a few minutes then surrender), then locate the replay directory and confirm whether/what got written. That single result picks the architecture.
