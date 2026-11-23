# IRIS — Silksong Mobile Modding API

[![Version](https://img.shields.io/badge/IRIS-v2.9.8.32-blueviolet.svg)](CHANGELOG.md)
[![Dashboard](https://img.shields.io/badge/Dashboard-irislayer.bearblog.dev-ff3e00.svg)](https://irislayer.bearblog.dev/)
[![Platform](https://img.shields.io/badge/Android-ARM64-green.svg)](#)
[![Unity](https://img.shields.io/badge/Unity-6000.0.x%20IL2CPP%20%2B%20HybridCLR-blue.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)](LICENSE)

On-device compatibility layer for running PC BepInEx, Harmony, and MonoMod mods on *Hollow Knight: Silksong* Android (`com.game.silksong`) without separate mobile forks.

## Core Features
- **Runtime Bytecode Rewriter (`CecilCore`)**: Rewrites PC mod DLLs on-device (BCL redirects, overload/struct field bridges, IL2CPP guards) with disk caching (`.cecil_cache_v*`).
- **AOT-Safe Harmony & MonoMod Bridge**: Maps desktop patches to JIT-free HybridCLR hook chains.
- **Asset, Shader & Memory Guard**: Repairs PC shaders, bridges `AssetHelper`/`FsmUtil`/`BossKit`/`I18N`, and unloads finished HK boss scenes/bundles to prevent Android OOM crashes.
- **Touch UI & Profiles**: Floating mod bubble, touch hotkeys, Pantheon boss skip, and `iris_profile.jsonc` sync.

## Highlighted Tested Mods
- **Bosses & Expansions**: Godhome (`HkCore` + `HkBossLoader`), *Third Acolyte*, *Bonemaster*, *Lost & Chained*, *Pale Automaton*, *Aeternal Everwatcher*, *Cousins of Void*, *Final Judge*, *Boss Rush*.
- **Mechanics & QoL**: *Needleforge*, *Crest Wheel*, *Rebalanced Crests*, *Architect Pogo QoL*, *Tribe Tower Performance*.

## Build (Linux)
Requires [.NET SDK 8.0+](https://dotnet.microsoft.com/) and mobile reference DLLs in `~/Projects/lib/`:
```bash
bash ~/Projects/build_silksong_mobile_api.bash
```
Outputs to `~/Projects/SilksongMobileAPI/00_IRIS/`.

## Logs
```bash
adb pull /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/LogOutput.log .
adb pull /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/ModLog.log .
```

## Pending API Bugs
- [ ] P2 softlock at Mantis Lords
- [ ] Mantis Lords & Sisters of Battle not ending properly
- [x] Pantheon boss skip button (`BossFixes.EnablePantheonBossSkip`)

## License
MIT — original compatibility bridge & UI code only (no game binaries or third-party mod assets).
