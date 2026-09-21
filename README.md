# Broken Arrow — Game Saves Mod (`BASaveGame`)

A [MelonLoader](https://github.com/LavaGang/MelonLoader) mod that adds the ability to **save an in-progress skirmish / PvE session and resume it later** in Broken Arrow — functionality the base game lacks. Campaign saves are a stretch goal.

> ⚠️ **Offline only.** This mod runs only with EasyAntiCheat **disabled** (the "Anti-Cheat Disabled" / Modded launch option). Never use mods in online/ranked play — you will be banned. The mod hard-refuses to load when EAC is detected.

## Status

Recon + prototyping phase. See the plan at `~/.claude/plans/let-s-figure-out-a-toasty-heron.md`.

The final save-fidelity tier is being decided from decompiled evidence:
- **Tier 1** — quick-relaunch (save the setup: map/deck/difficulty/options). Guaranteed; early deliverable.
- **Tier 2** — deterministic command-replay resume (record config + command stream, load = fast-forward re-sim).
- **Tier 3** — true state snapshot (reuse the game's `GhostService` entity serialization).

## Layout

```
src/          mod source (BASaveGame.cs, BASaveGame.csproj)
recon/         decompile outputs + feasibility notes (git-ignored dumps)
docs/          MODDING.md build notes, feasibility memo
tools/         third-party tools (MelonLoader, Cpp2IL) — git-ignored
```

## Build

Requires .NET SDK 6.0+ and MelonLoader installed into the game so the `Il2CppBrokenArrow.*` interop assemblies exist.

```bash
dotnet build -c Release src/BASaveGame.csproj -p:GameDir="G:\Games\Broken Arrow"
```

Output DLL is copied to `<GameDir>\Mods\`.

## Credits / references

Load-path techniques studied from BovineOverlord's `broken-arrow-local-skirmish` and `broken-arrow-balance-mod` (MelonLoader, offline).
