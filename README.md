# Broken Arrow — Game Saves (`BASaveGame`)

A [MelonLoader](https://github.com/LavaGang/MelonLoader) mod that lets you **save a PvE battle in progress and pick it up later** in Broken Arrow. The base game has no mid-battle saves; with this mod you can quicksave, keep six manual save slots, and load any of them from the pause menu or the main menu.

> **Offline only.** The mod is built and tested on an offline install with EasyAntiCheat off. Never use mods in online or ranked play.

## What gets saved

- Every living unit: type, position, facing, health, ammunition, options/loadout, skin, and passengers or garrisons inside vehicles and buildings.
- Mission progress: the mission script's state (objectives, triggers, timers), captured zones and the active sector, objective and marker visibility.
- Economy: money and income for each player.
- Your deck: which cards you've already used and any pending refunds.
- The in-battle clock.

When you load, the mod relaunches the same mission with the same deck, holds back the mission's own starting units, and then puts your saved army and the mission state back in place. A load takes a few seconds longer than starting the mission normally.

## Install

1. Install **MelonLoader 0.7.3** into the game folder (the folder that contains `BrokenArrow.exe`). Start the game once so MelonLoader can finish setting up, then quit.
2. Download `BASaveGame-vX.Y.Z.zip` from the [Releases](https://github.com/NickTheGrizzly/broken-arrow-save-mod/releases) page and extract it into the game folder. That puts `BASaveGame.dll` into `Mods\`.
3. Start the game by running **`BrokenArrow.exe` directly** (not the EasyAntiCheat launcher).

The MelonLoader console should show a line from BA Save Game starting with `Ready. F5 = quicksave, F10 = quickload`.

## Use

| Where | What |
|---|---|
| In battle, **F5** | Quicksave (overwrites the previous quicksave). |
| Anywhere, **F10** | Load the quicksave. Works from the main menu and from inside any battle. |
| Pause menu → **Save game** | Pick one of 6 slots. Overwriting asks for confirmation. |
| Pause menu → **Load game** | Quicksave first, then your manual saves. Asks for confirmation. |
| Main menu → **Saved games** card | Shows your newest save; click to see the list and load one. |
| Bin icon on any save | Deletes that save, after a confirmation. |

A short message at the top of the screen confirms each save and load and explains anything that went wrong (for example, "the mission isn't installed" or "no quicksave yet").

Save files are stored in:

```
%USERPROFILE%\AppData\LocalLow\SteelBalalaikaStudio\BrokenArrow\Saves\
    quicksave.basave
    Slots\slot1.basave … slot6.basave
```

They're plain JSON, so you can back them up or copy them between machines.

## Known limits

- **Game version 1.2.0 only.** Game updates can change what the mod relies on.
- **PvE missions.** Tested on Parnu Invasion and River Defense. Skirmish game-mode state (score and timers) is saved, but that path couldn't be tested on the offline build.
- **No campaign saves.** Campaign progress between missions isn't handled.
- **Not supported: Welcome to Kadaga and Assault on Daugavpils.** Their in-mission unit selection and scripted waypoints aren't captured by a save, so saving is disabled there (the pause menu's Save game is dimmed) and older saves of them can't be loaded.
- **Current save format only.** Saves made by development builds (format version below 5) show up as "made by an older version of the mod" and can't be loaded.
- Units that were in the middle of an action (moving, firing, loading) restart from their saved position and state rather than resuming the exact action.
- Loading while you're in a *different* mission goes back to the main menu first, then starts the saved one. This is intentional: it's the only way the game keeps your deck.

## Troubleshooting

- **Nothing happens on F5/F10:** check the MelonLoader console for lines from BA Save Game, and make sure you launched `BrokenArrow.exe` directly.
- **A save shows "(unreadable save)":** the file is damaged or was edited by hand. Load another slot or overwrite it.
- **A load stops with a message:** the message says why. The full log is `MelonLoader\Latest.log`. For more detail, turn on developer mode (below) and try again.

## For developers

### Build

Requires the .NET 6+ SDK and a game folder where MelonLoader has already generated `MelonLoader\Il2CppAssemblies\`.

```bash
dotnet build -c Release src/BASaveGame.csproj -p:GameDir="G:\Games\Broken Arrow"
```

The build copies `BASaveGame.dll` into `<GameDir>\Mods\`.

### Developer mode

Set `DeveloperMode = true` under `[BASaveGame]` in `<GameDir>\UserData\MelonPreferences.cfg` (the entry appears after the first run), then restart the game. It turns on:

- verbose step-by-step save and load logging in the console and in `Saves\live_load.txt`;
- diagnostic hotkeys: **F3** dump menu UI, **F4** dump the running mission script, **F6** load dry-run, **F7** world summary, **F8** component census, **F9** unit value dump, **F11** unit records, **F12** spawn every saved unit into the current battle.

### How it works

The game is Unity 2022.3 (IL2CPP) with a DefaultEcs world (`GameController.Instance.GameContext`). The engine has no serializer that works under IL2CPP and no runtime save of mission-script state, so the mod saves the game state by hand and rebuilds it with the game's own systems.

- **Unit-centric save.** A battle holds about 2,800 entities, but only a few dozen are units; the rest is map scenery that the mission rebuilds itself. The mod saves only the units (`Inspector.WriteSave`). Components are read through DefaultEcs pool mappings, and positions come from Unity transforms, because large ECS structs don't marshal reliably.
- **Relaunch, then restore** (`LoadFlow`). The saved mission is started the way the menu starts it (`ISceneTransition.ChangeScene` with the saved deck in `PreloadSharedPlayerDeck`). Inside a battle of the same mission, the mod uses the game's "Restart mission" route instead. When the world is ready (`GameController.OnScenarioControllerLoaded`), the stages run in order: game clock and zone owners; stop the mission-script nodes that the fresh start left running; respawn the saved army through `SpawnService.SpawnUnit` under the units' original mission ids; replay the effect journal; restore the script node state, money, deck usage and game mode; resume the nodes that were running. The mission's own starting spawns are suppressed throughout.
- **Mission script state** (`ScriptState`). The ScriptEngine node graph keeps its progress in node status, port signals and logic fields. The mod captures those and writes them back after the respawn. Units are re-registered under their saved mission ids, so the script's unit references still point to the right units. The design is in [`recon/SCRIPT_STATE.md`](recon/SCRIPT_STATE.md).
- **Mission effects** (`MissionState`). Sector activation, objective and marker visibility and task updates go through the game's event bus. The mod appends its own listeners and keeps a journal that it replays on load.
- **UI** (`NativeUi`). The Save/Load buttons, slot lists, confirmation dialog and main-menu card are cloned from the game's own widgets, so they match the game's look.

Background notes: [`recon/FEASIBILITY.md`](recon/FEASIBILITY.md) (architecture), [`recon/LOAD_PATH.md`](recon/LOAD_PATH.md) (how battles are launched), [`CLAUDE.md`](CLAUDE.md) (IL2CPP interop rules learned the hard way).

## Credits

Load-path techniques studied from BovineOverlord's `broken-arrow-local-skirmish` and `broken-arrow-balance-mod` (MelonLoader, offline).

## License

[MIT](LICENSE). Broken Arrow belongs to Steel Balalaika; this is an unofficial fan mod and is not affiliated with or endorsed by the developers.
