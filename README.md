# IRIS — Silksong Mobile Modding API & IL2CPP Runtime Compatibility Layer

**IRIS** (Silksong Mobile API) is an advanced runtime compatibility layer and modding framework designed to run desktop BepInEx, Harmony, and MonoMod PC mods on Unity IL2CPP / HybridCLR mobile environments (Android).

Desktop mods typically rely on desktop Mono JIT compilation, dynamic IL generation (`DynamicMethod` / `Emit`), native x86/x64 Detours, desktop input APIs, and DirectX/desktop OpenGL shaders. Mobile builds run in an Ahead-Of-Time (AOT) stripped environment where JIT and native x86 detours fail. IRIS acts as the translation layer bridging desktop mods into the mobile runtime seamlessly.

---

## Key Architecture & Components

### 1. Mono.Cecil Runtime Rewriter (`CecilCore / CecilEngine.cs`)
- **Bytecode Adaptation**: Inspects and transforms PC mod assemblies at runtime using an embedded, stripped build of Mono.Cecil.
- **Reference Redirection**: Redirects unsupported desktop assembly references and calls to mobile-safe shims and the `IRIS` engine.
- **Disk Caching**: Generates fast binary caches indexed by 64-bit FNV-1a assembly hashes, reducing cold launch and mod loading times to milliseconds on subsequent runs.
- **AppDomain Isolation**: Detects pre-loaded assemblies and renames runtime types to avoid namespace collisions.

### 2. HybridCLR Dynamic Hook Engine (`RuntimeHybridCLRAdapterBuilder.cs`)
- **JIT-Free Hooking**: Replaces MonoMod/Harmony dynamic IL emission with pre-compiled delegate chains and reflection-based invokers safe for IL2CPP.
- **Hook Lifecycle Management**: Supports prefixes, postfixes, and finalizers with fast parameter binding (`__instance`, `__result`, `__state`, `__runOriginal`).
- **Live Hook Profiler**: Integrated CPU profiler that tracks execution overhead per mod and per method to pinpoint runtime bottlenecks.

### 3. Mobile Harmony & Reflection Bridge (`HarmonyBridge.cs` & `BepInExShims.cs`)
- **Harmony Emulation**: Intercepts `Harmony.PatchAll()` and manual patch annotations, redirecting them into the HybridCLR adapter.
- **Reflection Shims**: Polyfills commonly stripped or desktop-specific .NET BCL APIs (`AccessTools`, `Traverse`, `System.HashCode`, `InControl` touch bindings, and TMPro extensions).
- **Localization Overrides**: Manages dynamic in-game text and UI overrides across custom game modes and language packs.

### 4. Mobile Asset & Shader Bridge (`AssetHelperBridge.cs`)
- **Shader Repair**: Automatically inspects loaded materials and maps unsupported PC shaders to mobile-compatible fallbacks (`Sprites/Default`, `Mobile/Particles/Additive`, `Particles/Standard Unlit`).
- **Texture Management**: Downscales oversized textures (e.g., 4K/8K PC textures) to prevent Android out-of-memory (OOM) GPU crashes.
- **Asynchronous Preloading**: Background loading and caching of heavy character, ability, and combat assets during loading screens to eliminate in-game stutter.

### 5. PlayMaker FSM IL2CPP Bridge (`PlayMakerHybridCLRBridge.cs`)
- **PlayMaker State Action Guard**: Provides zero-overhead interception of `FsmState.LoadActions()`.
- **Reflection Sanitation**: Fixes missing type metadata and private field access when custom mods inject actions into PlayMaker state machines under IL2CPP.

### 6. In-Game Mobile Mod Console & Touch UI (`InGameModMenu.cs`)
- **Installed Mods Manager**: In-game touch menu to inspect, enable, disable, and safely unload mods with automatic hook detaching.
- **Online Mod Catalog**: Direct browser for discovering, downloading, and installing curated mobile-compatible community mods.
- **Touch Button & Hotkey Mapper**: Floating circular bubble widget (`CircularFloatingWindow`) allowing mobile players to bind desktop keyboard hotkeys to customizable on-screen touch buttons.
- **Live Diagnostics**: Tabbed debug console featuring hook CPU profilers, inventory state inspectors, and crash loggers.

---

## Project Structure

```text
SilksongMobileAPI/
├── CecilCore/
│   ├── CecilEngine.cs                      # Mono.Cecil IL rewriter & cache engine
│   └── SilksongMobileAPI.CecilCore.csproj  # Stage 1 project file
├── Source/
│   ├── AssetHelperBridge.cs               # Mobile shader repair, textures & asset loader
│   ├── BepInExShims.cs                    # BepInEx, Harmony & BCL compatibility shims
│   ├── CecilRuntimeRewriter.cs            # In-memory assembly loader & Cecil hook
│   ├── EmbeddedPayloads.cs                # Embedded GZip byte arrays of Mono.Cecil
│   ├── HarmonyBridge.cs                   # Harmony patching bridge & AccessTools shims
│   ├── InGameModMenu.cs                   # Touch UI, Mod Store, keybind mapper & profiler
│   ├── PlayMakerHybridCLRBridge.cs        # PlayMaker FSM IL2CPP action bridge
│   ├── RuntimeHybridCLRAdapterBuilder.cs  # IL2CPP dynamic hook chain adapter
│   └── SilksongMobileAPIPlugin.cs         # Main BepInPlugin entrypoint & touch UI
├── curated_mods.json                      # Curated mod catalog definitions
├── SilksongMobileAPI.csproj               # Main Stage 2 project file
├── CHANGELOG.md                           # Version history and bugfix notes
├── LICENSE                                # MIT License
└── README.md
```

---

## Building the API

### Prerequisites
- [.NET SDK 8.0 or later](https://dotnet.microsoft.com/)
- Python 3 (for byte array payload compression)

### Build Commands

```bash
# Set path to stripped game dependencies (not included in this repository)
export GameLibsDir="/path/to/game/libs"

# 1. Build Stage 1 (CecilCore)
dotnet build CecilCore/SilksongMobileAPI.CecilCore.csproj -c Release /p:GameLibsDir="$GameLibsDir"

# 2. Build Stage 2 (IRIS Main Assembly)
dotnet build SilksongMobileAPI.csproj -c Release /p:GameLibsDir="$GameLibsDir"
```

The resulting compiled plugin will be placed in `00_IRIS/IRIS.dll`.

---

## Deployment to Android

1. Copy the generated `00_IRIS` directory to your mobile device's BepInEx directory:
   ```text
   /storage/emulated/0/Android/data/<package_name>/files/BepInEx/plugins/00_IRIS/
   ├── IRIS.dll
   └── plugin.json
   ```
2. Place PC mods in `BepInEx/plugins/` as normal.
3. Launch the game. IRIS will automatically rewrite assemblies on first launch and cache them for instant subsequent loads.

---

## Legal & Compliance Notice

This repository contains solely original compatibility bridge code, shims, and UI management logic licensed under the MIT License. **No game binaries, proprietary game assemblies, decompiled assets, copyrighted artwork, or game libraries are contained within this repository.**



Most Modders don't like to thier mods to be associated with mobile port if so your video using boss/mods can be taken down.
