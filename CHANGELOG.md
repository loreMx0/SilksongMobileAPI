# Silksong Mobile PC Mod Compatibility API - Changelog

## [v2.8.0] - 2026-11-16

### Fixed & Corrected
- **Runtime Mod Hook Deactivation & Unload (`InGameModMenu` & `SilksongMobileAPIPlugin`)**:
  - Implemented `DeactivateLoadedModRuntime` and `UnregisterAllHooksForMod`. When a mod is disabled or uninstalled in-game, its registered prefixes, postfixes, finalizers, and MonoMod detours are detached from runtime chains, preventing leaked invulnerability hooks (such as `AuricAegis` parry gating) from persisting across boss encounters.
  - Automatically destroys child `MonoBehaviour` and `BaseUnityPlugin` instances associated with unloaded mod assemblies.
  - Neutralizes persistent God Mode if `CheatManager.Invincibility == 3`.

- **Enemy Death Hook Bridging (`RuntimeHybridCLRAdapterBuilder` & `HarmonyBridge`)**:
  - Added `InvokeBridgedHealthManagerDieChains(hm, lastHit)` to dispatch `HealthManager::Die` prefix and postfix calls from fatal hits, fixing dropped enemy death hooks in `DoubleEnemiesMod`.

- **Harmony Reflection Shims (`BepInExShims`)**:
  - Implemented `Traverse.GetValueType()`, `Traverse.Fields()`, `Traverse.Properties()`, and `Traverse.Methods()`, resolving `MissingMethodException: HarmonyLib.Traverse::Fields` in `RebalancedCrests`.

- **Debug God Mode Neutralization (`CecilEngine`)**:
  - Strips all `HarmonyPatch` attributes from `Awakened_Grand_Mother_Silk.Source.Patches.DebugPatches` at rewrite time, preventing player God Mode.

- **AssetBundle Platform Path Resolution (`AssetHelperBridge`)**:
  - Added `ResolveAndroidBundlePath` and `LoadAssetBundleFromFileSafe` to resolve missing asset bundles from `aa/Android/` and memory, preventing frame-1 NRE crashes in `AwakenedGrandMotherSilk`.

- **Boss Encounter Flow & Parries (`AssetHelperBridge`)**:
  - Added `PostPrimedSentinelOnFsmEnabled` (clamps scaled Song Knight HP back to 1800 so Phase 2 is reachable) and `PrePrimedSentinelDeathReset` (unsets invincibility and fires `ZERO HP` event upon defeat).

- **Generic MonoMod Detour Hooking (`HarmonyBridge` & `RuntimeHybridCLRAdapterBuilder`)**:
  - Added `RegisterMonoModDetourChain` and generic `OrigTracker` delegates, supporting non-SSMP MonoMod `new Hook(...)` calls for mods like `HeartlessBellhome`.

- **Performance & Assembly Resolution (`CecilEngine` & `HarmonyBridge`)**:
  - Rewrites parameterless `Harmony.PatchAll()` to pass the calling mod assembly name, enabling proper patch discovery under HybridCLR.
  - Stubs `TribeTowerPerformance.BloodHook.Install` to prevent `ObjectPool.Spawn` parameter mismatch errors.
  - Replaces `HKS1in10000` per-pixel bitmap loop with fast `ImageConversion.LoadImage`.

## [v2.7.9] - 2026-11-16

### Fixed & Corrected
- **BepInExShims / ReflectionHelper**:
  - Replaced `System.Threading.Tasks.Parallel.Invoke` inside `PreloadCommonTypes()` with sequential calls to `CacheFields<T>()`. This eliminates runtime `MissingMethodException: MethodNotFind System.Threading.Tasks.Parallel::Invoke` caused by Unity IL2CPP BCL stripping on Android during HybridCLR bootstrap.

## [v2.7.6] - 2026-11-16

### Fixed & Corrected
- **BepInExShims / MobileVirtualInput**:
  - Implemented `ParseKeyName(string)` helper mapping `UnityEngine.InputSystem` button/key names and aliases (`enter`, `ctrl`, `shift`, `alt`) to Unity `KeyCode`.
  - Added `RegisterUnityInputAction(object, KeyCode)` stub to resolve unresolved compiler reference.

## [v2.7.2] - 2026-11-15

### Fixed & Corrected
- **Build Infrastructure & Tooling**:
  - Corrected `apply_v272_fixes.py` heredoc regex delimiter (`EOF_[A-Z_]*`) so all 9 C# source files correctly embed into `build_silksong_mobile_api.bash`.
  - Replaced illegal compile-time reference `typeof(BepInEx.Boot)` in `SilksongMobileAPIPlugin.cs` with safe runtime reflection and fallback to `Instance.gameObject`.
  - Added headless/sandbox fallback for `XDG_RUNTIME_DIR` in `build_silksong_mobile_api.bash` to prevent NuGet mutex `lstat` ENOENT crashes.
  - Bumped version across all components to `v2.7.2` (including `plugin.json` and `.cecil_cache_v272`).

- **AssetHelperBridge**:
  - Keep extracted scene templates `activeSelf=true` inside inactive container GameObject.
  - Ensure bound templates maintain `activeSelf=true` in `TryBindManagedAssetFromTemplateCache`.
  - Exclude additive Coral sub-scenes (`_boss`, `_base`, `_secret`, `_top`, `_mid`, `_left`, `_join`, `_arrow`) from `IsCurrentSceneInCoral`.
  - Filter additive Coral sub-scenes early in `ShouldSkipPharloomsGlorySceneLoaded`.
  - Added `PharloomsGloryGetActiveScene`, `PharloomsGloryBeforeSceneModify`, and `PharloomsGloryAfterSceneModify` with cleanup for `currentlyAddedGameObjects` and `WindCameraRegion`.
  - Added direct IL redirect target `AndroidSceneAssetRepacker_StartExtract_Redirect`.
  - Automatically unload lingering background extraction scenes (`Hang_17b`, `Memory_Coral_Tower`, `Hang_02`, `Hang_03`, `Coral_26`, `Cradle_03`, `Song_24`, `Ant_04`).

- **RuntimeHybridCLRAdapterBuilder**:
  - Extended `ChainCallFrame` to preserve ByRef arguments and `HeroController.willThrowTool`.
  - Fixed `Marshal.SizeOf` on enum underlying types in `IsMultiSlotValueType`.
  - Installed real `nativeHarmony.Patch` on native `AssetHelper.dll` and `Silksong.FsmUtil.dll` methods.
  - Preserved and restored ByRef arguments and `HeroController.willThrowTool` through prefix and postfix call chains.

- **PlayMakerHybridCLRBridge**:
  - Only lock `FsmState` in `GetStateActionsSafe` when custom/mod `FsmStateActions` are present, eliminating lock contention on native states.

- **HarmonyBridge**:
  - Ensured `SceneManager.GetActiveScene()` matches `GameManager.sceneName` prior to `BeginSceneTransition`.
  - Guarded `CrossStitchMadness` Wait/TakeSilk patch on `HeroController.silkSpecialFSM` until initialized and in Idle state.
  - Added and registered `ToolItemManager_GetAttackToolBinding_Postfix` and 3 MemberRef bridges.

- **CecilEngine**:
  - Added IL redirects for `StartExtract`, `PharloomsGloryGetActiveScene`, and MemberRef bridges.
  - Removed `CanThrowTool` from WeaverCrest `IsWeaverEquipped()` override.
  - Weaved `PharloomsGloryAfterSceneModify` at the end of `SceneModifier.Modify(Scene)`.
  - Added `Nullable<T>` return unboxing support in `BuildSingleHookMethod`.

- **SilksongMobileAPIPlugin**:
  - Restored `plugin.json.disabled_architect` and top-level DLLs in `_knownNativeMobileFolders`.
  - Added `EnsureMissedNativeMobilePluginsLoaded` to recover native plugins missed by BepInEx bootstrap.
  - Removed `PantheonOfPharloom` from `_excludedArchitectOrPortingNames`.

- **InGameModMenu**:
  - Guarded against `MenuButtonList.SelectDelayed(null)` NullReferenceExceptions when selectable entries are empty.

---

## [v2.7.1] - 2026-11-15
- Initial diagnostic release for PC mod compatibility testing.
