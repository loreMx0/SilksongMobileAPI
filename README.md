# IRIS Layer — Silksong Mobile Modding API & IL2CPP Compatibility Bridge

[![Dashboard](https://img.shields.io/badge/Changelog%20%26%20Dashboard-bearblog.dev%2Firislayer-ff3e00.svg)](https://bearblog.dev/irislayer/dashboard/)
[![Platform](https://img.shields.io/badge/Platform-Android%20%28ARM64%29-green.svg)](#)
[![Unity](https://img.shields.io/badge/Unity-6000.0.x%20%28IL2CPP%20%2B%20HybridCLR%29-blue.svg)](#)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.20%20%2F%205.4.23-orange.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)](#)

> **Official Changelog & Project Dashboard**: [https://bearblog.dev/irislayer/dashboard/](https://bearblog.dev/irislayer/dashboard/)  
> **Target Environment**: Hollow Knight: Silksong (`com.game.silksong`), Android ARM64, Unity 6000.0.x IL2CPP + HybridCLR runtime.

---

## 1. What the API Does

**IRIS** (Silksong Mobile PC Mod Compatibility Layer) is an on-device dynamic translation engine and runtime compatibility framework that enables desktop (PC) BepInEx, Harmony, and MonoMod mods to run directly on the Android release of *Hollow Knight: Silksong* **without requiring mod authors to rewrite, recompile, or maintain separate mobile forks**.

### The Problem It Solves:
* **The Desktop Environment**: PC Silksong mods target .NET Framework 4.8 / .NET Standard 2.0/2.1 running on desktop Mono. They rely heavily on Just-In-Time (JIT) compilation, dynamic bytecode emission (`System.Reflection.Emit.DynamicMethod`), native x86/x64 assembly detours, desktop DirectX/OpenGL shaders, Windows file paths, and physical keyboard/mouse input APIs.
* **The Mobile Environment**: The mobile Android build runs Ahead-Of-Time (AOT) compiled IL2CPP code paired with the **HybridCLR** interpreted runtime on 64-bit ARM (`arm64-v8a`). Under Android's security model and IL2CPP architecture:
  1. Runtime JIT dynamic method emission is strictly forbidden and causes instant native crashes (`SIGSEGV` / `SIGABRT` in `libil2cpp.so`).
  2. Unused Base Class Library (BCL) metadata, types, and generic instantiations are stripped during build time.
  3. Desktop shaders render as completely invisible or magenta/pink or crash the mobile GPU driver.
  4. Desktop keyboard hotkeys and IMGUI menus cannot be interacted with on touch screens.
* **The IRIS Solution**: IRIS acts as a transparent, high-performance middleware. It intercepts PC mod assemblies on boot, transforms their IL bytecode on-device, bridges Harmony patches into AOT-safe HybridCLR hook chains, repairs shaders and oversized textures, polyfills stripped .NET APIs, and injects a native mobile touch UI with on-screen button mapping.

---

## 2. How the API Does It (Technical Architecture)

IRIS operates through six tightly synchronized subsystems that work together across the mod lifecycle:

```
+-----------------------------------------------------------------------------------+
|                            PC Mod Assembly (.dll)                                 |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| 1. Mono.Cecil Runtime Rewriter (CecilCore / CecilEngine.cs)                       |
|    - FNV-1a Hash Cache Check (.cecil_cache_v*)                                    |
|    - Redirect [netstandard] / stripped BCL types -> mscorlib                      |
|    - Bridge diverging method overloads (FsmUtil, BossKit, AssetHelper)            |
|    - Strip desktop debug / God Mode Harmony patches                               |
|    - Repair invalid coroutine signatures (IEnumerator vs AssetBundle)             |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| 2. HybridCLR Dynamic Hook Engine (RuntimeHybridCLRAdapterBuilder.cs)             |
|    - Converts dynamic Emit / Harmony patches -> precompiled IL2CPP Hook.Patch    |
|    - Enforces exact signature safety: Prefix (bool/void), Postfix (T result)      |
|    - Boxed result unboxing with fallback (prevents Il2CppExceptionWrapper aborts) |
|    - Mod hook lifecycle tracking & clean in-game detachment                       |
+-----------------------------------------------------------------------------------+
        |                                   |                                   |
        v                                   v                                   v
+-----------------------+   +-----------------------+   +-----------------------+
| 3. Harmony & BCL      |   | 4. Asset & Shader     |   | 5. PlayMaker FSM      |
|    Reflection Bridge  |   |    Bridge             |   |    Bridge             |
|    (HarmonyBridge.cs) |   |    (AssetHelperBridge)|   |    (PlayMakerBridge)  |
| - AccessTools shims   |   | - Shader repair to    |   | - Intercepts state    |
| - Traverse shims      |   |   mobile fallbacks    |   |   deserialization     |
| - Polyfills ValueTuple|   | - 4K/8K texture scale |   | - FsmState action lock|
|   (arities 0-8)       |   | - Async scene template|   | - Missing method      |
| - Sequential tasks    |   |   extraction & cleanup|   |   overload dispatch   |
+-----------------------+   +-----------------------+   +-----------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| 6. In-Game Touch UI & Mod Console (InGameModMenu.cs & IrisProfileManager)         |
|    - Draggable circular floating bubble widget (CircularFloatingWindow)           |
|    - Touch-to-keyboard hotkey binding & custom button registration                |
|    - In-game mod manager (enable, disable, configure, safe runtime unloading)     |
|    - Iris Profile System (iris_profile.jsonc) with 3-second hold auto-downloader  |
+-----------------------------------------------------------------------------------+
```

### A. Mono.Cecil Runtime Bytecode Rewriting (`CecilCore / CecilEngine.cs`)
* **In-Memory IL Transformation**: Embedded compressed payloads of Mono.Cecil inspect and transform mod assemblies before they are passed to the runtime loader.
* **Type & Assembly Redirection**: Detects references to stripped assemblies (such as `[netstandard]System.ValueTuple`) and redirects them to the system's `mscorlib.dll` with strong-name tokens (`b77a5c561934e089`) and version `4.0.0.0`.
* **Method Overload Bridging**: Transparently redirects incompatible method calls to IRIS bridge methods. For example:
  * Redirects 4-parameter `Silksong.FsmUtil.FsmUtil::InsertMethod(FsmState, int, Action, bool)` to mobile 3-parameter equivalents.
  * Replaces background Addressables coroutine yields (`Borrow.LoadAll`, `Borrow.ByAddress`) with safe instant enumerators (`EmptyCoroutine`) to prevent Unity native coroutine runner crashes.
* **Signature Sanitization**: Fixes coroutine return types that differ between PC and mobile (e.g. converting `AssetBundle` returns to `IEnumerator` in `BossKit.World.Borrow.Open`), preventing native `__cxa_get_exception_ptr` $\rightarrow$ `abort()` failures in Android `libc.so`.
* **Lightning Disk Caching**: Transformed assemblies are cached in `.cecil_cache_v*` using 64-bit FNV-1a checksums. Subsequent game boots bypass IL rewriting entirely, achieving sub-second mod initialization.

### B. HybridCLR Dynamic Hook Engine (`RuntimeHybridCLRAdapterBuilder.cs`)
* **JIT-Free Patching**: Desktop Harmony relies on runtime IL emission (`DynamicMethod`), which crashes immediately on mobile IL2CPP. IRIS maps Harmony patch annotations (`[HarmonyPatch]`, `[HarmonyPrefix]`, `[HarmonyPostfix]`) into mobile BepInEx delegate chains (`Hook.Patch`).
* **Strict Return Type Matching**: Mobile BepInEx enforces strict type signatures:
  * **Prefix**: Returns `bool` (returning `false` skips original execution) or `void`.
  * **Postfix on Non-Void Method**: Method return type must be `T`, and the final parameter must be by-value `T result` (not `ref T __result`). IRIS generates safe unboxing IL with `isinst` guards: if an unboxed value is invalid or null, it falls back to the original return value, eliminating `Il2CppExceptionWrapper` crashes.
* **Safe Unloading**: Implements `DeactivateLoadedModRuntime` and `UnregisterAllHooksForMod`. When a mod is toggled off in the in-game menu, its prefixes, postfixes, and MonoMod detours are unlinked immediately without requiring a game reboot.

### C. Mobile Harmony & BCL Reflection Bridge (`HarmonyBridge.cs` & `BepInExShims.cs`)
* **Stripped Reflection Polyfills**: Re-implements `AccessTools.Method`, `AccessTools.Field`, `Traverse.GetValueType()`, `Traverse.Fields()`, and `Traverse.Methods()` so PC mods can inspect private game state under IL2CPP.
* **ValueTuple Generic Resolution**: Provides native fallbacks and eliminates unhandled `EqualityComparer<ValueTuple<...>>` calls in interpreted HybridCLR code, replacing composite tuple dictionaries with optimized string-keyed hash lookups.
* **Concurrency Stabilization**: Replaces calls to `System.Threading.Tasks.Parallel.Invoke` (stripped in mobile Unity BCL) with sequential cache builders to avoid bootstrap `MissingMethodException`s.

### D. Mobile Asset, Shader & Texture Bridge (`AssetHelperBridge.cs`)
* **Automated Shader Repair**: PC mods bundle shaders compiled for DirectX 11/12 or desktop OpenGL Core. IRIS intercepts asset bundle loading and maps unsupported materials to mobile-native shaders (`Sprites/Default`, `Mobile/Particles/Additive`, `Particles/Standard Unlit`).
* **VRAM & OOM Protection**: Downscales oversized textures and manages GPU texture memory to prevent the Android Low Memory Killer (LMK) from terminating the game process.
* **Async Scene Preloading & Cleanup**: Safely manages scene extraction templates (e.g., `Memory_Coral_Tower`, `Hang_17b`, `Cradle_03`, `Arborium_11`), ensuring additive sub-scenes are cleanly isolated and unloaded.

### E. PlayMaker FSM IL2CPP Bridge (`PlayMakerHybridCLRBridge.cs`)
* **State Action Guard**: Prevents native PlayMaker deserialization (`FsmState.LoadActions()`) from resetting modded actions.
* **FSM Overload Dispatch**: Bridges custom PlayMaker actions and methods that take `everyFrame` flags or custom event targets, ensuring boss state machines transition smoothly across combat phases.

### F. Touch UI, Floating Bubble & Profile Manager (`InGameModMenu.cs`)
* **Circular Floating Window (`CircularFloatingWindow`)**: A draggable on-screen floating bubble that provides quick access to mod toggles, touch-to-keyboard hotkeys, and the debug console.
* **In-Game Mod Manager**: Lets players browse installed mods, view authors/descriptions, toggle mods on/off, and configure in-game mod settings.
* **Iris Profile System (`iris_profile.jsonc`)**: Allows players to export and import mod configurations. Features a **3-Second Hold to Download** button that automatically fetches missing mods and dependencies directly from Thunderstore.
* **Diagnostics & Hook Profiler**: Real-time HUD showing frame timings, memory usage, and per-hook CPU execution overhead to quickly identify laggy mods.

---

## 3. Currently Tested & Working Mobile Mods

All mods listed below have been thoroughly tested on Android (`com.game.silksong`), verified against native crash dumps (`tombstones`) and engine logs (`LogOutput.log`, `ModLog.log`), and confirmed fully playable under IRIS.

### Boss Encounters & Combat Expansions
| Mod Name | Package / Author | Tested Version | Status | Technical Details & Fixes Applied |
| :--- | :--- | :---: | :---: | :--- |
| **Aeternal Everwatcher** | `AstrumNova-Aeternal_Everwatcher` | `0.1.0` | **Working (100%)** | Custom boss in `Coral_39`. Parry guard attached cleanly, arena entry black screen lifted, and last-priority loading enforced. |
| **Pale Automaton** | `AstrumNova-Pale_Automaton` | `1.0.0` | **Working (100%)** | Custom boss in `Arborium_11`. Fixed underground teleport softlock, preserved Merchant hierarchy, and enabled nail clash hooks. |
| **Awakened Grand Mother Silk** | `Launfer-AwakenedGrandMotherSilk` | `1.0.5` | **Working (100%)** | High-difficulty boss in `Cradle_03`. Survived 22+ minutes in continuous combat. Fixed `SendRandomEventV4` bounds, resolved Nuke state action wiping, and patched bundle fallbacks. |
| **Bonemaster & Karmelita** | `Kiahah-Bonemaster` | `1.0.6` | **Working (100%)** | Two-phase boss fight in Hunter's March. Fixed startup coroutine signature mismatch in `Borrow.Open` and replaced composite `ValueTuple` dictionary lookups. |
| **Final Judge** | `TheWeirdo931-Final_Judge` | `1.0.1` | **Working (100%)** | Overhauls Last Judge with Deep Docks lava hazards, faster charges, and custom attack patterns. |
| **Rebellious Second Sentinel** | `Danny752786-RebelliousSecondSentinel` | `0.1.2` | **Working (100%)** | Upgrades Second Sentinel aggression and combos. Death hook bridging ensures boss defeat triggers scene completion. |
| **Faded Phantom** | `spaet1s-Faded_Phantom` | `0.1.4` | **Working (100%)** | Reworks Phantom encounter in Exhaust Organ with rapid combos and new attacks. |
| **Boss Rush / Pantheon Mod** | `mochasippadev-Boss_Rush_for_Silksong` | `1.1.1` | **Working (100%)** | Full Boss Rush mode with custom title screen menus, pantheon tiers, and difficulty options. |
| **Pantheon of Pharloom** | `PantheonOfPharloom` | `1.0.0` | **Working (100%)** | Expanded pantheon gauntlets and custom boss flow. |

### Major Gameplay & Area Overhauls
| Mod Name | Package / Author | Tested Version | Status | Technical Details & Fixes Applied |
| :--- | :--- | :---: | :---: | :--- |
| **Pharloom's Glory** | `Norrie_Boy-Pharlooms_Glory` | `0.1.4` | **Working (100%)** | Restores the cut Red Coral Gorge area, waterfalls, and custom soundtrack. Full additive scene repacker and camera region handling verified. |
| **Double Enemies Mod** | `adwamogus-DoubleEnemiesMod` | `0.7.4` | **Working (100%)** | Duplicates enemies during exploration and combat. `HealthManager::Die` prefix/postfix chains bridged to prevent immortal enemies. |

### Mechanics, Abilities & Combat Tweaks
| Mod Name | Package / Author | Tested Version | Status | Technical Details & Fixes Applied |
| :--- | :--- | :---: | :---: | :--- |
| **Stylemeter** | `GlowyStar-Stylemeter` | `0.4.1` | **Working (100%)** | ULTRAKILL-style combat ranking HUD tracking combos, parries, aerial hits, and boss kills in real time. |
| **Shaman Dive Bind** | `tetriscat66-ShamanDiveBind` | `1.1.1` | **Working (100%)** | Restores cut Shaman Crest dive bind animation, spell effects, and mechanics. |
| **Auric Aegis** | `AuricAegis` | `1.0.0` | **Working (100%)** | Parry shield mechanic. Clean runtime detachment ensures invulnerability does not persist when uninstalled. |
| **Rebalanced Crests** | `RebalancedCrests` | `1.0.0` | **Working (100%)** | Crest balance adjustments. Supported via `Silksong.ModMenu` shims and `Traverse.Fields` reflection. |
| **Architect Pogo QoL** | `ArchitectPogoQOL` | `1.0.0` | **Working (100%)** | Fluid pogo responsiveness and aerial mobility improvements. |
| **Triple Projectiles Mobile** | `TripleProjectiles_Mobile` | `1.0.0` | **Working (100%)** | Enhanced ranged tool combat with multi-projectile spreads. |

### Economy, QoL & Utility Mods
| Mod Name | Package / Author | Tested Version | Status | Technical Details & Fixes Applied |
| :--- | :--- | :---: | :---: | :--- |
| **Stonks** | `SaigoNoTanuki-Stonks` | `1.0.0` | **Working (100%)** | Configurable multiplier for dropped Rosaries and Shell Shards. |
| **Tribe Tower Performance** | `TribeTowerPerformance` | `1.0.0` | **Working (100%)** | Fixes blood hook pool allocations and eliminates frame drops in crowded arenas. |
| **HKS1in10000** | `HKS1in10000` | `1.0.0` | **Working (100%)** | Texture overlay Easter egg optimized with native byte texture loading. |
| **Heartless Bellhome** | `HeartlessBellhome` | `1.0.0` | **Working (100%)** | Environmental mod hooked cleanly via generic `OrigTracker` MonoMod detours. |
| **Withered Foxy Jumpscare**| `WitheredFoxyJumpscare` | `1.0.0` | **Working (100%)** | Audio/visual event mod confirmed crash-free under IL2CPP. |

### Core Frameworks & Dependencies Bridged
| Framework / Library | Role & Integration |
| :--- | :--- |
| **Silksong.AssetHelper** (`1.1.0`) | Bridged for runtime AssetBundle loading, fallback resolution, and scene extraction. |
| **Silksong.FsmUtil** (`0.3.13`) | Bridged for PlayMaker action injection and method overload dispatch. |
| **Silksong.ModMenu** | Shims provided for `ConfigEntryFactory`, `PaginatedMenuScreenBuilder`, and UI elements. |
| **Kiahah-BossKit** (`1.0.2`) | Bridged for custom boss creation (`Words`, `Machines`, `Borrow` coroutines). |
| **silksong_modding-I18N** (`1.0.4`)| Bridged for in-game localization and dialogue translation. |
| **WavLib** | Bridged for custom WAV audio stream playback. |
| **UnityHelper / SFCore / Vasi / Satchel** | Bridged utility libraries for custom GameObjects, animations, and sound effects. |

---

## 4. Installation & Usage on Android

### Prerequisites
* Android device running 64-bit ARM (`arm64-v8a`).
* Hollow Knight: Silksong Android port (`com.game.silksong`).
* Mobile BepInEx pre-installed.

### Step-by-Step Setup
1. **Deploy IRIS**:
   Copy the `00_IRIS` directory into your device's BepInEx plugins folder:
   ```text
   /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/plugins/00_IRIS/
   ├── IRIS.dll
   └── plugin.json
   ```
2. **Install PC Mods**:
   Place any supported desktop mod `.dll` and its asset files directly into:
   ```text
   /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/plugins/<ModName>/
   ```
3. **Launch the Game**:
   * On first boot, IRIS automatically analyzes mod assemblies, performs IL rewriting, and creates optimized caches in `.cecil_cache_v*`.
   * The **IRIS Floating Bubble** will appear on-screen. Tap it to open the in-game Mod Manager, configure mod settings, or assign touch buttons to keyboard hotkeys.

### Using the Profile Manager (`iris_profile.jsonc`)
IRIS includes an automatic profile sync system. If you share or download an `iris_profile.jsonc` file with missing mods:
1. Open the In-Game Mod Menu.
2. The UI will display: `HOLD FOR 3 SECONDS TO DOWNLOAD MISSING MOD(S) + DEPENDENCIES`.
3. Press and hold the button for 3 seconds. IRIS will automatically download, unpack, and verify all required mods and dependencies from Thunderstore in the background.

---

## 5. Developer Guide: Integrating with IRIS

If you are developing a mod or porting a PC mod to mobile, you can leverage IRIS's built-in touch and UI hooks directly:

### 1. Adding Buttons to the Floating Mod Bubble
Register custom touch triggers on the draggable floating bubble without building custom UI canvases:
```csharp
private void Awake()
{
    Invoke(nameof(SetupTouchButtons), 1f);
}

void SetupTouchButtons()
{
    var floatingWindow = FindFirstObjectByType<CircularFloatingWindow>();
    if (!floatingWindow) return;

    floatingWindow.RegisterButton(new CircularButtonItem(
        icon: null,
        text: "Special\nAbility",
        buttonColor: new Color(0f, 0.54f, 0.15f, 0.9f),
        onClickCallback: () => {
            Debug.Log("[MyMod] Special ability triggered via touch button!");
        }
    ));
}
```

### 2. Linking In-Game Settings to the Mod Manager
Hook your mod's configuration panel directly into the **Configure** button in the in-game Mod List:
```csharp
private void Awake()
{
    // The identifier must match your mod's DLL filename (without .dll)
    PluginLoader.RegisterEditor("MyModPlugin", OnToggleSettings);
}

private void OnToggleSettings(bool visible)
{
    mySettingsCanvas.SetActive(visible);
}

public void CloseMenu()
{
    PluginLoader.SetEditorVisible("MyModPlugin", false);
}
```

### 3. Safe Global Logging Setup
Avoid static null references during initialization by establishing an early static instance:
```csharp
public class MyModPlugin : BaseUnityPlugin
{
    public static MyModPlugin Instance;
    internal static ManualLogSource Log => Instance.Logger;

    private void Awake()
    {
        Instance = this;
        Log.LogInfo("MyMod initialized successfully under IRIS.");
    }
}
```

---

## 6. Building from Source

### Prerequisites
* Linux environment (Arch / Ubuntu / Debian / Fedora)
* [.NET SDK 8.0 or later](https://dotnet.microsoft.com/)
* Stock mobile reference libraries placed in `~/Projects/lib/`

### Build Script
The API is built using the master build script, which manages embedded payload regeneration, CecilCore compilation, and main API assembly generation:
```bash
# Build the complete IRIS API binary
bash /home/btw/Projects/build_silksong_mobile_api.bash
```
The output artifacts will be deployed to `~/Projects/SilksongMobileAPI/00_IRIS/`.

---

## 7. Diagnostics & Crash Reporting

If an issue occurs during gameplay:
1. Pull the newest logs from your device:
   ```bash
   adb pull /storage/emulated/0/Android/data/com.game.silksong/files/BepInEx/LogOutput.log .
   adb pull /storage/emulated/0/Android/data/com.game.silksong/files/ModLog.log .
   ```
2. If the game crashed, check for Android native tombstones in `/data/tombstones/`:
   ```bash
   adb shell su -c "ls -la /data/tombstones/"
   ```
3. Report issues or check progress on the [IRIS Project Dashboard](https://bearblog.dev/irislayer/dashboard/).

---

## 8. Legal & Compliance Notice

This repository contains solely original compatibility bridge code, runtime shims, and touch UI management logic licensed under the [MIT License](file:///home/btw/Projects/SilksongMobileAPI/LICENSE). **No game binaries, proprietary game assemblies, decompiled assets, copyrighted artwork, or game libraries are contained within this repository.**

*Hollow Knight: Silksong* is the intellectual property of Team Cherry. All rights to third-party mods belong to their respective authors. Mod authors who do not wish for their PC mods to be adapted for mobile can specify restriction flags in their manifest or contact the project maintainers.
