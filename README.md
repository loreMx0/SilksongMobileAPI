# IRIS Layer — Silksong Mobile Modding API

[![Dashboard](https://img.shields.io/badge/Changelog%20%26%20Dashboard-bearblog.dev%2Firislayer-ff3e00.svg)](https://irislayer.bearblog.dev/)
[![Platform](https://img.shields.io/badge/Platform-Android%20%28ARM64%29-green.svg)](#)
[![Unity](https://img.shields.io/badge/Unity-6000.0.x%20%28IL2CPP%20%2B%20HybridCLR%29-blue.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)](#)

**IRIS** is an on-device compatibility layer that runs desktop (PC) BepInEx, Harmony, and MonoMod mods on the Android port of *Hollow Knight: Silksong* (`com.game.silksong`) without needing separate mobile forks.

> **Changelog & Dashboard:** [https://irislayer.bearblog.dev/](https://irislayer.bearblog.dev/)

## What It Does
* **Runtime Bytecode Rewriting (`CecilCore`)**: Transforms PC mod `.dll`s on-device (BCL redirects, overload bridges, signature fixes) and caches results in `.cecil_cache_v*` for fast boots.
* **JIT-Free Harmony & MonoMod Bridge**: Maps desktop patches to AOT-safe HybridCLR hook chains with safe in-game unloading.
* **Mobile Asset, Shader & Memory Guard**: Repairs PC shaders for mobile GPUs, protects against Android OOM kills, and bridges `AssetHelper`, `FsmUtil`, `BossKit`, `ModMenu`, and `I18N`.
* **In-Game Touch UI & Profiles**: Adds a draggable floating bubble, touch-to-hotkey buttons, an in-game mod manager, and `iris_profile.jsonc` Thunderstore auto-sync.

## Tested Mods (Highlights)
* **Bosses & Expansions**: Godhome (`HkCore` + `HkBossLoader`), *Lost & Chained*, *Aeternal Everwatcher*, *Pale Automaton*, *Awakened Grand Mother Silk*, *Bonemaster*, *Final Judge*, *Rebellious Second Sentinel*, *Faded Phantom*, *Pharloom's Glory*, *Boss Rush*, *Pantheon of Pharloom*.
* **Mechanics & QoL**: *Stylemeter*, *Shaman Dive Bind*, *Auric Aegis*, *Rebalanced Crests*, *Architect Pogo QoL*, *Double Enemies Mod*, *Stonks*, *Tribe Tower Performance*.

## Build from Source (Linux)
Requires [.NET SDK 8.0+](https://dotnet.microsoft.com/) and your mobile reference libraries in `~/Projects/lib/`:
```bash
bash ~/Projects/build_silksong_mobile_api.bash
```
Outputs to `~/Projects/SilksongMobileAPI/00_IRIS/`.

## Logs & Bug Reports
Pull logs from your device and check status on the [IRIS Dashboard](https://irislayer.bearblog.dev/):
```bash
adb pull /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/LogOutput.log .
adb pull /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/ModLog.log .
```

## License & Notice
MIT License — contains only original compatibility bridge code and touch UI logic (no game binaries, proprietary assemblies, or copyrighted assets). *Hollow Knight: Silksong* belongs to Team Cherry; third-party mods belong to their respective authors.
