using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace SilksongMobileAPI
{
    public static class AssetHelperBridge
    {
        private static Shader _cachedAndroidSpriteDefaultShader;
        private static Shader _cachedAndroidParticleShader;
        public static int RepairedShaderCount;
        public static int MaxModTextureDimension = 2048;
        public static int DownscaledTextureCount;

        public static Shader GetFallbackAndroidShader(string originalShaderName = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(originalShaderName) &&
                    (originalShaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     originalShaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (_cachedAndroidParticleShader == null)
                        _cachedAndroidParticleShader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Mobile/Particles/Additive") ?? Shader.Find("Sprites/Default");
                    if (_cachedAndroidParticleShader != null)
                        return _cachedAndroidParticleShader;
                }
                if (_cachedAndroidSpriteDefaultShader == null)
                    _cachedAndroidSpriteDefaultShader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                return _cachedAndroidSpriteDefaultShader;
            }
            catch { return null; }
        }

        public static void RepairMaterialShaderIfUnsupported(Material mat)
        {
            if (mat == null) return;
            try
            {
                var sh = mat.shader;
                string shName = sh != null ? sh.name : string.Empty;
                if (sh == null || !sh.isSupported || shName == "Hidden/InternalErrorShader")
                {
                    Texture mainTex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                    Color col = mat.HasProperty("_Color") ? mat.color : Color.white;
                    var fallback = GetFallbackAndroidShader(shName);
                    if (fallback != null)
                    {
                        mat.shader = fallback;
                        if (mainTex != null && mat.HasProperty("_MainTex")) mat.mainTexture = mainTex;
                        if (mat.HasProperty("_Color")) mat.color = col;
                        RepairedShaderCount++;
                    }
                }
                if (mat.HasProperty("_MainTex") && mat.mainTexture is Texture2D t2d)
                {
                    EnforceMobileTextureVramGuard(t2d);
                }
            }
            catch { }
        }

        public static void EnforceMobileTextureVramGuard(Texture2D tex)
        {
            if (tex == null || MaxModTextureDimension <= 0) return;
            try
            {
                int w = tex.width;
                int h = tex.height;
                if (w <= MaxModTextureDimension && h <= MaxModTextureDimension) return;

                float scale = Mathf.Min((float)MaxModTextureDimension / w, (float)MaxModTextureDimension / h);
                int newW = Mathf.Max(1, Mathf.RoundToInt(w * scale));
                int newH = Mathf.Max(1, Mathf.RoundToInt(h * scale));

                RenderTexture rt = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                RenderTexture prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(tex, rt);
                    RenderTexture.active = rt;
                    tex.Reinitialize(newW, newH, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0f, 0f, newW, newH), 0, 0, false);
                    tex.Apply(false, false);
                    DownscaledTextureCount++;
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            catch { }
        }
        private static readonly Dictionary<string, PropertyInfo> _handlePropCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FieldInfo> _handleFieldCache = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        private static bool _repackerResolved;
        private static Type _repackerType;
        private static FieldInfo _registeredInAddressablesField;
        private static FieldInfo _extractedTemplatesField;
        private static FieldInfo _isExtractingField;
        private static FieldInfo _currentExtractingSceneField;
        private static FieldInfo _cachedSceneBundleKeysField;
        private static FieldInfo _cachedSceneAddressableKeysField;
        private static FieldInfo _pinnedSceneBundleHandlesField;
        private static MethodInfo _ensureAddressableKeysIndexedMethod;
        private static MethodInfo _ensureTemplateContainerMethod;
        private static MethodInfo _buildCacheKeyMethod;
        private static MethodInfo _findLoadedSceneMethod;
        private static MethodInfo _findGoInSceneMethod;

        private static Func<bool> _fastGetIsExtracting;
        private static readonly Dictionary<string, List<Action<GameObject>>> _coalescedCallbacksByCacheKey = new Dictionary<string, List<Action<GameObject>>>(StringComparer.OrdinalIgnoreCase);
        private static bool _coalescedBatchCoroutineActive;
        private static readonly List<FieldInfo> _allIsExtractingFields = new List<FieldInfo>();
        private static readonly List<PropertyInfo> _allIsExtractingProps = new List<PropertyInfo>();
        private static readonly List<FieldInfo> _allCurrentSceneFields = new List<FieldInfo>();
        private static readonly List<PropertyInfo> _allCurrentSceneProps = new List<PropertyInfo>();
        private static readonly List<FieldInfo> _allExtractedTemplatesFields = new List<FieldInfo>();
        private static readonly List<FieldInfo> _allPinnedHandlesFields = new List<FieldInfo>();
        private static MethodInfo _paleAutomatonPinMethod;

        public static volatile bool IsExtractingPharloomsGloryScene;
        public static volatile string CurrentExtractingPharloomsGloryScene = string.Empty;

        private static readonly Dictionary<string, List<string>> _pendingScenePathsByScene = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static object _pharloomsGloryAssetManager;
        private static Assembly _pharloomsGloryAssembly;
        private static bool _pharloomsGloryBatchStarted;
        private static bool _pharloomsGloryBatchRunning;
        private static bool _lightingHooksInstalled;

        private static void RegisterRepackerTypeMembers(Type rt)
        {
            if (rt == null) return;
            const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                foreach (var f in rt.GetFields(SFlags))
                {
                    string fn = f.Name ?? "";
                    if (f.FieldType == typeof(bool) && fn.IndexOf("Extracting", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!_allIsExtractingFields.Contains(f)) _allIsExtractingFields.Add(f);
                    }
                    else if (f.FieldType == typeof(string) &&
                             (fn.IndexOf("Extracting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              fn.IndexOf("CurrentScene", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        if (!_allCurrentSceneFields.Contains(f)) _allCurrentSceneFields.Add(f);
                    }
                    else if (typeof(IDictionary).IsAssignableFrom(f.FieldType))
                    {
                        if (fn.IndexOf("ExtractedTemplates", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!_allExtractedTemplatesFields.Contains(f)) _allExtractedTemplatesFields.Add(f);
                        }
                        else if (fn.IndexOf("PinnedSceneBundle", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!_allPinnedHandlesFields.Contains(f)) _allPinnedHandlesFields.Add(f);
                        }
                    }
                }
                foreach (var p in rt.GetProperties(SFlags))
                {
                    string pn = p.Name ?? "";
                    if (p.CanWrite && p.PropertyType == typeof(bool) && pn.IndexOf("Extracting", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!_allIsExtractingProps.Contains(p)) _allIsExtractingProps.Add(p);
                    }
                    else if (p.CanWrite && p.PropertyType == typeof(string) && pn.IndexOf("Extracting", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!_allCurrentSceneProps.Contains(p)) _allCurrentSceneProps.Add(p);
                    }
                }
                foreach (var m in rt.GetMethods(SFlags))
                {
                    string mn = m.Name ?? "";
                    var ps = m.GetParameters();
                    if (_paleAutomatonPinMethod == null && ps.Length == 1 && ps[0].ParameterType == typeof(string) &&
                        mn.IndexOf("Pin", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (mn.IndexOf("Bundle", StringComparison.OrdinalIgnoreCase) >= 0 || mn.IndexOf("Dep", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _paleAutomatonPinMethod = m;
                    }
                }
            }
            catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L110", __apiEx_1); }
        }

        private static bool _startExtractHookInstalled;

        public static void EnsureRepackerMembersResolved()
        {
            if (_repackerResolved && _repackerType != null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    string asmName = asm.GetName().Name ?? "";
                    if (_repackerType == null)
                    {
                        var ahType = asm.GetType("Silksong.AssetHelper.Android.AndroidSceneAssetRepacker", false);
                        if (ahType != null)
                        {
                            _repackerType = ahType;
                            _repackerResolved = true;
                            try
                            {
                                var hgField = _repackerType.GetField("_harmonyGuardsInstalled", SFlags);
                                hgField?.SetValue(null, true);
                            }
                            catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L137", __apiEx_2); }
                            _registeredInAddressablesField = _repackerType.GetField("_registeredInAddressables", SFlags);
                            _extractedTemplatesField = _repackerType.GetField("ExtractedTemplates", SFlags);
                            _isExtractingField = _repackerType.GetField("<IsExtractingAnyScene>k__BackingField", SFlags);
                            _currentExtractingSceneField = _repackerType.GetField("<CurrentExtractingSceneName>k__BackingField", SFlags);
                            _cachedSceneBundleKeysField = _repackerType.GetField("CachedSceneBundleKeys", SFlags);
                            _cachedSceneAddressableKeysField = _repackerType.GetField("CachedSceneAddressableKeys", SFlags);
                            _pinnedSceneBundleHandlesField = _repackerType.GetField("PinnedSceneBundleHandles", SFlags);
                            _ensureAddressableKeysIndexedMethod = _repackerType.GetMethod("EnsureAddressableKeysIndexed", SFlags);
                            _ensureTemplateContainerMethod = _repackerType.GetMethod("EnsureTemplateContainer", SFlags);
                            _buildCacheKeyMethod = _repackerType.GetMethod("BuildCacheKey", SFlags);
                            _findLoadedSceneMethod = _repackerType.GetMethod("FindLoadedScene", SFlags);
                            _findGoInSceneMethod = _repackerType.GetMethod("FindGameObjectInScene", SFlags);
                            var getIsExtMi = _repackerType.GetMethod("get_IsExtractingAnyScene", SFlags);
                            if (getIsExtMi != null)
                            {
                                try { _fastGetIsExtracting = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getIsExtMi); } catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L153", __apiEx_3); }
                            }
                            RegisterRepackerTypeMembers(_repackerType);
                            if (!_startExtractHookInstalled)
                            {
                                _startExtractHookInstalled = true;
                                // Note: AndroidSceneAssetRepacker.StartExtract is a managed HybridCLR method in AssetHelper.dll,
                                // not an IL2CPP native method, so we do NOT pass it to Dobby/BepInEx.Hook (avoids TargetIsNotIl2CppObject).
                                PrepopulateAssetHelperCatalogBundleDeps(asm);
                                EnsureFinalJudgePrefabsReady();
                            }
                        }
                    }

                    if (asmName.IndexOf("PaleAutomaton", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Type[] paTypes = null;
                        try { paTypes = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { paTypes = rtle.Types; } catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L184", __apiEx_5); }
                        if (paTypes != null)
                        {
                            for (int i = 0; i < paTypes.Length; i++)
                            {
                                if (paTypes[i] != null) RegisterRepackerTypeMembers(paTypes[i]);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Repacker resolve warning: {ex.Message}");
            }
            EnsureAudioProtectionHookInstalled();
        }

        private static bool _audioProtectionHookInstalled;

        public static void EnsureAudioProtectionHookInstalled()
        {
            if (_audioProtectionHookInstalled) return;
            _audioProtectionHookInstalled = true;
            try
            {
                var amType = typeof(AudioManager);
                var applyCueMi = amType.GetMethod("ApplyMusicCue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(MusicCue), typeof(float), typeof(float), typeof(bool) }, null);
                if (applyCueMi != null)
                {
                    var preMi = typeof(AssetHelperBridge).GetMethod(nameof(AudioManager_ApplyMusicCue_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (preMi != null)
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.AudioProtection", applyCueMi, preMi, null, null, HarmonyLib.Priority.VeryHigh);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Audio protection hook warning: {ex.Message}");
            }
        }

        public static bool AudioManager_ApplyMusicCue_Prefix(AudioManager __instance, MusicCue musicCue)
        {
            if (IsExtractingPharloomsGloryScene)
            {
                // Background additive scene extraction must NEVER touch AudioManager music cues!
                return false;
            }
            return true;
        }

        public static bool IsAnyRepackerBusy()
        {
            if (IsExtractingPharloomsGloryScene) return true;
            if (_fastGetIsExtracting != null)
            {
                try { return _fastGetIsExtracting(); } catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L240", __apiEx_6); }
            }
            return false;
        }

        private static void SetAllRepackersExtracting(bool isExtracting, string sceneName)
        {
            string sc = isExtracting ? (sceneName ?? string.Empty) : string.Empty;
            IsExtractingPharloomsGloryScene = isExtracting;
            CurrentExtractingPharloomsGloryScene = sc;
            try
            {
                EnsureRepackerMembersResolved();
                for (int i = 0; i < _allCurrentSceneFields.Count; i++)
                {
                    try { _allCurrentSceneFields[i].SetValue(null, sc); } catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L255", __apiEx_7); }
                }
                for (int i = 0; i < _allCurrentSceneProps.Count; i++)
                {
                    try { _allCurrentSceneProps[i].SetValue(null, sc, null); } catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L259", __apiEx_8); }
                }
                for (int i = 0; i < _allIsExtractingFields.Count; i++)
                {
                    try { _allIsExtractingFields[i].SetValue(null, isExtracting); } catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L263", __apiEx_9); }
                }
                for (int i = 0; i < _allIsExtractingProps.Count; i++)
                {
                    try { _allIsExtractingProps[i].SetValue(null, isExtracting, null); } catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L267", __apiEx_10); }
                }
            }
            catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L270", __apiEx_11); }
        }

        private static void StoreTemplateInAllRepackers(string cacheKey, GameObject cloned)
        {
            if (string.IsNullOrEmpty(cacheKey) || cloned == null) return;
            _bridgeFallbackTemplates[cacheKey] = cloned;
            try
            {
                for (int i = 0; i < _allExtractedTemplatesFields.Count; i++)
                {
                    if (_allExtractedTemplatesFields[i].GetValue(null) is IDictionary dict)
                        dict[cacheKey] = cloned;
                }
            }
            catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L285", __apiEx_12); }
            List<Action<GameObject>> callbacks = null;
            lock (_coalescedCallbacksByCacheKey)
            {
                if (_coalescedCallbacksByCacheKey.TryGetValue(cacheKey, out callbacks))
                    _coalescedCallbacksByCacheKey.Remove(cacheKey);
            }
            if (callbacks != null)
            {
                for (int i = 0; i < callbacks.Count; i++)
                {
                    try { callbacks[i]?.Invoke(cloned); } catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L296", __apiEx_13); }
                }
            }
        }

        public static void AndroidSceneAssetRepacker_StartExtract_Redirect(string sceneName, string objPath, Action<GameObject> onComplete)
        {
            AndroidSceneAssetRepacker_StartExtract_Prefix(sceneName, objPath, onComplete);
        }

        public static bool AndroidSceneAssetRepacker_StartExtract_Prefix(string __0, string __1, Action<GameObject> __2)
        {
            string sceneName = __0;
            string objPath = __1;
            Action<GameObject> onComplete = __2;
            if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(objPath))
            {
                try { onComplete?.Invoke(null); } catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L313", __apiEx_14); }
                return false;
            }

            string ck = BuildSceneCacheKeySafe(sceneName, objPath);
            EnsureRepackerMembersResolved();
            var templatesDict = (_extractedTemplatesField?.GetValue(null) as IDictionary) ?? _bridgeFallbackTemplates;
            if (!string.IsNullOrEmpty(ck))
            {
                if (templatesDict != null && templatesDict.Contains(ck) && templatesDict[ck] is GameObject cachedGo && cachedGo != null)
                {
                    try { onComplete?.Invoke(cachedGo); } catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L324", __apiEx_15); }
                    return false;
                }
                if (_bridgeFallbackTemplates.TryGetValue(ck, out var fbGo) && fbGo != null)
                {
                    try { onComplete?.Invoke(fbGo); } catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L329", __apiEx_16); }
                    return false;
                }
            }

            if (onComplete != null && !string.IsNullOrEmpty(ck))
            {
                lock (_coalescedCallbacksByCacheKey)
                {
                    if (!_coalescedCallbacksByCacheKey.TryGetValue(ck, out var cbList))
                    {
                        cbList = new List<Action<GameObject>>(2);
                        _coalescedCallbacksByCacheKey[ck] = cbList;
                    }
                    cbList.Add(onComplete);
                }
            }

            lock (_pendingScenePathsByScene)
            {
                if (!_pendingScenePathsByScene.TryGetValue(sceneName, out var pathList) || pathList == null)
                {
                    pathList = new List<string>();
                    _pendingScenePathsByScene[sceneName] = pathList;
                }
                if (!pathList.Contains(objPath))
                    pathList.Add(objPath);
            }

            if (!_coalescedBatchCoroutineActive && SilksongMobileAPIPlugin.Instance != null)
            {
                _coalescedBatchCoroutineActive = true;
                SilksongMobileAPIPlugin.Instance.StartCoroutine(CoRunCoalescedSceneExtractions());
            }
            return false;
        }

        private static IEnumerator CoRunCoalescedSceneExtractions()
        {
            // Yield 2 frames so all FromSceneAsset requests during mod Awake/Start coalesce into 1 pass per scene
            yield return null;
            yield return null;
            yield return RunPreloaderPharloomsGloryBatchExtract();
            _coalescedBatchCoroutineActive = false;

            // Invoke any remaining callbacks (e.g. if an object path was not found in scene) with null so callers don't hang
            List<List<Action<GameObject>>> remaining = null;
            lock (_coalescedCallbacksByCacheKey)
            {
                if (_coalescedCallbacksByCacheKey.Count > 0)
                {
                    remaining = new List<List<Action<GameObject>>>(_coalescedCallbacksByCacheKey.Values);
                    _coalescedCallbacksByCacheKey.Clear();
                }
            }
            if (remaining != null)
            {
                for (int i = 0; i < remaining.Count; i++)
                {
                    var list = remaining[i];
                    for (int j = 0; j < list.Count; j++)
                    {
                        try { list[j]?.Invoke(null); } catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L391", __apiEx_17); }
                    }
                }
            }
        }

        private static readonly List<object> _permanentPinnedHandles = new List<object>();
        private static readonly List<UnityEngine.Object> _permanentPinnedObjects = new List<UnityEngine.Object>();

        private static void PinAsset(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try
            {
                obj.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                if (!(obj is GameObject) && !(obj is Component))
                {
                    UnityEngine.Object.DontDestroyOnLoad(obj);
                    lock (_permanentPinnedObjects)
                    {
                        if (!_permanentPinnedObjects.Contains(obj))
                            _permanentPinnedObjects.Add(obj);
                    }
                }
            }
            catch (System.Exception __apiEx_18) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L416", __apiEx_18); }
        }

        private static void PinAllAssetsOnHierarchy(GameObject root)
        {
            if (root == null) return;
            try
            {
                // Only pin GPU/RAM assets (Materials, Shaders, Textures, Meshes, Sprites).
                // NEVER call DontDestroyOnLoad on GameObjects or Components!
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats != null)
                    {
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat == null) continue;
                            PinAsset(mat);
                            if (mat.shader != null) PinAsset(mat.shader);
                            if (mat.HasProperty("_MainTex") && mat.mainTexture != null) PinAsset(mat.mainTexture);
                        }
                    }
                }
                foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr == null) continue;
                    if (sr.sprite != null)
                    {
                        PinAsset(sr.sprite);
                        if (sr.sprite.texture != null) PinAsset(sr.sprite.texture);
                    }
                }
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf != null && mf.sharedMesh != null)
                    {
                        PinAsset(mf.sharedMesh);
                    }
                }
                foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps == null) continue;
                    var psr = ps.GetComponent<ParticleSystemRenderer>();
                    if (psr != null && psr.sharedMaterial != null)
                    {
                        PinAsset(psr.sharedMaterial);
                        if (psr.sharedMaterial.shader != null)
                            PinAsset(psr.sharedMaterial.shader);
                        if (psr.sharedMaterial.HasProperty("_MainTex") && psr.sharedMaterial.mainTexture != null)
                            PinAsset(psr.sharedMaterial.mainTexture);
                    }
                }
            }
            catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L472", __apiEx_19); }
        }

        private static void StorePinnedHandleInAllRepackers(string pinKey, object handleObj)
        {
            if (string.IsNullOrEmpty(pinKey) || handleObj == null) return;
            try
            {
                if (!_permanentPinnedHandles.Contains(handleObj))
                    _permanentPinnedHandles.Add(handleObj);

                for (int i = 0; i < _allPinnedHandlesFields.Count; i++)
                {
                    if (_allPinnedHandlesFields[i].GetValue(null) is IDictionary dict && !dict.Contains(pinKey))
                        dict[pinKey] = handleObj;
                }
            }
            catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L489", __apiEx_20); }
        }

        private static bool IsSafeSceneForBatchExtraction(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return string.Equals(sceneName, "Menu_Title", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldSkipPharloomsGlorySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // NOTE: Silksong loads gameplay rooms (Bellway_08, Coral_03, Coral_32, etc.) with LoadSceneMode.Additive!
            // Never filter out main gameplay rooms; only skip while background scene extraction is active,
            // on boot/menu scenes, or when an additive sub-scene (e.g., Coral_19_base, Coral_35_secret, Coral_32_arrow)
            // loads inside an active Coral main room (which would otherwise cause SceneModifier.Modify to see
            // !CORAL_SCENES.Contains(subScene) and call SetActive(false), wiping out custom sprites/colors!).
            if (IsExtractingPharloomsGloryScene || IsAnyRepackerBusy()) return true;
            string sn = scene.name ?? string.Empty;
            if (string.IsNullOrEmpty(sn)) return true;
            if (string.Equals(sn, "Pre_Menu_Intro", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sn, "PermaDeath_Unlock", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sn, "Opening_Sequence", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sn, "Menu_Title", StringComparison.OrdinalIgnoreCase) ||
                IsAdditiveCoralSubSceneName(sn))
            {
                return true;
            }
            if (!IsCurrentSceneInCoral(sn))
            {
                // Check if sn is an additive sub-scene (e.g. Coral_35_secret, Coral_19_base, Coral_42_top, *_boss*)
                // while a Coral main scene is currently loaded:
                bool anyCoralMainLoaded = false;
                int scCount = SceneManager.sceneCount;
                for (int i = 0; i < scCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.IsValid() && s.isLoaded && IsCurrentSceneInCoral(s.name))
                    {
                        anyCoralMainLoaded = true;
                        break;
                    }
                }
                if (anyCoralMainLoaded &&
                    (sn.StartsWith("Coral_", StringComparison.OrdinalIgnoreCase) ||
                     sn.IndexOf("_boss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_base", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_top", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_mid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_join", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sn.IndexOf("_arrow", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
            // Ensure extracted templates are synced into PharloomsGlory.Managers.AssetManager.instance.assets right before SceneModifier.Modify runs!
            try { SyncExtractedTemplatesIntoPharloomsGlory(); } catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L547", __apiEx_21); }
            return false;
        }

        private static Type _androidRuntimeBridgeType;
        private static MethodInfo _ensureBundleAndDepsLoadedMi;
        private static MethodInfo _findLoadedBundleByFileNameMi;
        private static Type _weaverCombatTypeCache;
        private static FieldInfo _weaverAllOutBundlesField;
        private static FieldInfo _weaverReadyClipField;
        private static FieldInfo _weaverTowerClipField;

        private static void EnsureAndroidRuntimeBridgeResolved()
        {
            if (_androidRuntimeBridgeType != null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    var t = asm.GetType("Silksong.AssetHelper.Internal.AndroidRuntimeBridge", false);
                    if (t != null)
                    {
                        _androidRuntimeBridgeType = t;
                        _ensureBundleAndDepsLoadedMi = t.GetMethod("EnsureBundleAndDepsLoaded", SFlags);
                        _findLoadedBundleByFileNameMi = t.GetMethod("FindLoadedBundleByFileName", SFlags);
                        break;
                    }
                }
            }
            catch (System.Exception __apiEx_22) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L578", __apiEx_22); }
        }

        private static readonly HashSet<string> _weaverMissingBundleCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static AssetBundle WeaverGetAllOutBundle(string bundleRelativePath, bool logIfMissing)
        {
            if (string.IsNullOrEmpty(bundleRelativePath)) return null;
            if (_weaverMissingBundleCache.Contains(bundleRelativePath)) return null;
            try
            {
                EnsureAndroidRuntimeBridgeResolved();
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (_weaverCombatTypeCache == null)
                {
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (asm == null) continue;
                        var wt = asm.GetType("WeaverCrestMod.WeaverCombat", false);
                        if (wt != null)
                        {
                            _weaverCombatTypeCache = wt;
                            _weaverAllOutBundlesField = wt.GetField("AllOutLoadedBundles", SFlags);
                            _weaverReadyClipField = wt.GetField("crossSlashReadyClip", SFlags);
                            _weaverTowerClipField = wt.GetField("crossSlashTowerClip", SFlags);
                            break;
                        }
                    }
                }

                var cacheDict = _weaverAllOutBundlesField?.GetValue(null) as IDictionary;
                if (cacheDict != null && cacheDict.Contains(bundleRelativePath))
                {
                    var cached = cacheDict[bundleRelativePath] as AssetBundle;
                    if (cached != null) return cached;
                }

                string norm = bundleRelativePath.Replace('\\', '/').Trim();
                string fileName = Path.GetFileName(norm);
                string flatName = norm.Replace('/', '_');
                string stem = fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - 7)
                    : fileName;
                string flatStem = flatName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                    ? flatName.Substring(0, flatName.Length - 7)
                    : flatName;

                AssetBundle found = null;
                if (_findLoadedBundleByFileNameMi != null)
                {
                    try { found = _findLoadedBundleByFileNameMi.Invoke(null, new object[] { fileName }) as AssetBundle; } catch (System.Exception __apiEx_23) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L628", __apiEx_23); }
                    if (found == null)
                    {
                        try { found = _findLoadedBundleByFileNameMi.Invoke(null, new object[] { flatName }) as AssetBundle; } catch (System.Exception __apiEx_24) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L631", __apiEx_24); }
                    }
                }

                if (found == null)
                {
                    foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                    {
                        if (b == null || b.isStreamedSceneAssetBundle) continue;
                        string bn = b.name ?? string.Empty;
                        if (string.IsNullOrEmpty(bn)) continue;
                        if (bn.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            bn.IndexOf(flatStem, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = b;
                            break;
                        }
                    }
                }

                if (found == null)
                {
                    _weaverMissingBundleCache.Add(bundleRelativePath);
                }

                if (found != null && !found.isStreamedSceneAssetBundle)
                {
                    if (cacheDict != null)
                    {
                        try { cacheDict[bundleRelativePath] = found; } catch (System.Exception __apiEx_25) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L660", __apiEx_25); }
                    }
                    return found;
                }
            }
            catch (System.Exception __apiEx_26) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L665", __apiEx_26); }
            return null;
        }

        public static UnityEngine.Object WeaverTryLoadAllOutAsset(AssetBundle bundle, string assetPath)
        {
            if (bundle == null || string.IsNullOrEmpty(assetPath)) return null;
            try
            {
                if (bundle.isStreamedSceneAssetBundle) return null;
                var direct = bundle.LoadAsset(assetPath);
                if (direct != null) return direct;

                string shortName = Path.GetFileNameWithoutExtension(assetPath);
                string fileName = Path.GetFileName(assetPath);
                var allNames = bundle.GetAllAssetNames();
                if (allNames != null)
                {
                    for (int i = 0; i < allNames.Length; i++)
                    {
                        string an = allNames[i];
                        if (string.IsNullOrEmpty(an)) continue;
                        if (an.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                            an.EndsWith("/" + shortName + ".prefab", StringComparison.OrdinalIgnoreCase) ||
                            an.EndsWith("/" + shortName + ".asset", StringComparison.OrdinalIgnoreCase) ||
                            an.EndsWith("/" + shortName + ".wav", StringComparison.OrdinalIgnoreCase) ||
                            an.EndsWith("/" + shortName + ".ogg", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileNameWithoutExtension(an), shortName, StringComparison.OrdinalIgnoreCase))
                        {
                            var loaded = bundle.LoadAsset(an);
                            if (loaded != null) return loaded;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_27) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L700", __apiEx_27); }
            return null;
        }

        public static void EnsureWeaverCrossSlashClipsReady()
        {
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (_weaverCombatTypeCache == null)
                {
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (asm == null) continue;
                        var wt = asm.GetType("WeaverCrestMod.WeaverCombat", false);
                        if (wt != null)
                        {
                            _weaverCombatTypeCache = wt;
                            _weaverReadyClipField = wt.GetField("crossSlashReadyClip", SFlags);
                            _weaverTowerClipField = wt.GetField("crossSlashTowerClip", SFlags);
                            break;
                        }
                    }
                }
                if (_weaverReadyClipField != null && _weaverTowerClipField != null)
                {
                    var readyClip = _weaverReadyClipField.GetValue(null) as UnityEngine.Object;
                    var towerClip = _weaverTowerClipField.GetValue(null) as UnityEngine.Object;
                    if (towerClip == null && readyClip != null)
                    {
                        _weaverTowerClipField.SetValue(null, readyClip);
                    }
                    else if (readyClip == null && towerClip != null)
                    {
                        _weaverReadyClipField.SetValue(null, towerClip);
                    }
                }
            }
            catch (System.Exception __apiEx_28) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L738", __apiEx_28); }
        }

        public static GradientColorKey CreateGradientColorKey(Color color, float time)
        {
            GradientColorKey k = default;
            k.color = color;
            k.time = time;
            return k;
        }

        public static GradientAlphaKey CreateGradientAlphaKey(float alpha, float time)
        {
            GradientAlphaKey k = default;
            k.alpha = alpha;
            k.time = time;
            return k;
        }

        public static bool WeaverIsAnySkillActive(HeroController hero)
        {
            if (hero == null) return false;
            try
            {
                if (_weaverCombatTypeCache == null)
                {
                    foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        var aName = a.GetName().Name;
                        if (aName == "WeaverCrest" || aName == "Weaver_Crest")
                        {
                            _weaverCombatTypeCache = a.GetType("WeaverCrestMod.WeaverCombat", false);
                            if (_weaverCombatTypeCache != null) break;
                        }
                    }
                }
                if (_weaverCombatTypeCache != null)
                {
                    var forMethod = _weaverCombatTypeCache.GetMethod("For", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var combat = forMethod?.Invoke(null, new object[] { hero });
                    if (combat != null)
                    {
                        var csField = _weaverCombatTypeCache.GetField("crossSlashSkillActive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var pdField = _weaverCombatTypeCache.GetField("phantomDiveActive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var bsField = _weaverCombatTypeCache.GetField("brutalSmashActive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var exField = _weaverCombatTypeCache.GetField("executionActive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if ((bool)(csField?.GetValue(combat) ?? false) ||
                            (bool)(pdField?.GetValue(combat) ?? false) ||
                            (bool)(bsField?.GetValue(combat) ?? false) ||
                            (bool)(exField?.GetValue(combat) ?? false))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_29) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L794", __apiEx_29); }
            return false;
        }

        public static bool BonemasterBundleFileNameMatches(string actualFileName, string expectedFileName)
        {
            if (string.Equals(actualFileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrEmpty(actualFileName) || string.IsNullOrEmpty(expectedFileName))
                return false;

            // Handle SpriteAtlas bundle on Android
            if (expectedFileName.IndexOf("ant_traps", StringComparison.OrdinalIgnoreCase) >= 0 &&
                actualFileName.IndexOf("ant_traps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Handle individual Android audio bundles for BossKit Sound Picks
            string[] soundPicks = { "crypt_worm_emerge_from_ground", "driller_boss_emerge", "bone_hunter_throw_emerge", "stick_insect_flyer_spin_attack", "skull_king_hard_land" };
            foreach (var pick in soundPicks)
            {
                if (expectedFileName.IndexOf(pick, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    actualFileName.IndexOf(pick, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            string a = Path.GetFileName(actualFileName.Replace('\\', '/'));
            string e = Path.GetFileName(expectedFileName.Replace('\\', '/'));
            if (string.Equals(a, e, StringComparison.OrdinalIgnoreCase))
                return true;
            if (a.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                a = a.Substring(0, a.Length - 7);
            if (e.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                e = e.Substring(0, e.Length - 7);
            if (string.Equals(a, e, StringComparison.OrdinalIgnoreCase))
                return true;
            if (a.StartsWith(e + "_", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(e, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static readonly string[] _bonemasterFixedBossNames = new string[]
        {
            "Bell Eater", "Bell Centipede", "Giant Centipede", "Flower Queen", "Nyleth", "Bonemaster", "Bone Master", "Bone Hunter"
        };

        public static bool WeaverIsFixedEnemyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var fixedName in _bonemasterFixedBossNames)
            {
                if (name.IndexOf(fixedName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static void BonemasterResetOrientationAndGravity(GameObject boss, Rigidbody2D body)
        {
            if (boss != null)
            {
                boss.transform.rotation = Quaternion.identity;
                var s = boss.transform.localScale;
                if (s.y < 0f)
                {
                    boss.transform.localScale = new Vector3(s.x, Mathf.Abs(s.y), s.z);
                }
            }
            if (body != null)
            {
                body.rotation = 0f;
                if (body.gravityScale <= 0f)
                {
                    body.gravityScale = 2f;
                }
            }
        }

        public static float BonemasterSafePositiveSign(float val) => 1f;

        private static readonly Dictionary<string, UnityEngine.Sprite> _bmSpriteCache = new Dictionary<string, UnityEngine.Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, UnityEngine.Sprite> _proceduralAntTrapSprites = new Dictionary<string, UnityEngine.Sprite>(StringComparer.OrdinalIgnoreCase);
        private static string _bmSpriteIndexedScene = "";
        private static UnityEngine.Sprite _fallbackBoneSpikeSprite;

        private static UnityEngine.Sprite GetOrCreateProceduralAntTrapSprite(string spriteName = null)
        {
            string key = string.IsNullOrEmpty(spriteName) ? "ant_trap_large_spikes0004" : spriteName;
            if (_proceduralAntTrapSprites.TryGetValue(key, out var existing) && existing != null)
                return existing;

            try
            {
                bool isAntic = key.IndexOf("antic", StringComparison.OrdinalIgnoreCase) >= 0;
                int frame = 4;
                for (int i = key.Length - 1; i >= 0; i--)
                {
                    if (char.IsDigit(key[i]))
                    {
                        frame = key[i] - '0';
                        break;
                    }
                }

                int w = 160, h = isAntic ? 96 : 224;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var pixels = new Color32[w * h];
                float progress = isAntic
                    ? Mathf.Clamp01((frame + 1) / 4f) * 0.28f
                    : (frame <= 4 ? Mathf.Lerp(0.35f, 1.0f, frame / 4f) : Mathf.Lerp(1.0f, 0.22f, (frame - 4) / 4f));

                int maxH = Mathf.Clamp(Mathf.RoundToInt(h * progress), 14, h - 4);
                // Draw 3 jagged curved bone prongs (left jaw, center tooth, right jaw) with dark umber outline & warm bone fill
                float[] baseCenters = new float[] { w * 0.26f, w * 0.50f, w * 0.74f };
                float[] tipShifts = new float[] { w * 0.16f, 0f, -w * 0.16f };
                float[] heightScales = new float[] { 0.95f, 0.68f, 1.0f };
                float[] baseRadii = new float[] { w * 0.16f, w * 0.12f, w * 0.16f };

                for (int p = 0; p < 3; p++)
                {
                    int prongH = Mathf.Max(10, Mathf.RoundToInt(maxH * heightScales[p]));
                    for (int y = 0; y < prongH; y++)
                    {
                        float ty = (float)y / Mathf.Max(1, prongH - 1);
                        float curve = ty * ty;
                        float cx = baseCenters[p] + tipShifts[p] * curve + Mathf.Sin(ty * 9.5f + p) * 2.2f;
                        float notch = 1f + 0.18f * Mathf.Sin(ty * 15.7f);
                        float halfW = Mathf.Max(1.5f, baseRadii[p] * (1f - 0.88f * Mathf.Pow(ty, 0.85f)) * notch);
                        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - halfW - 2f));
                        int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(cx + halfW + 2f));
                        for (int x = x0; x <= x1; x++)
                        {
                            float dx = Mathf.Abs(x - cx);
                            if (dx > halfW) continue;
                            float edge = dx / Mathf.Max(0.5f, halfW);
                            Color32 col;
                            if (edge > 0.76f)
                            {
                                col = new Color32(48, 36, 28, 255); // Dark chitin/bone outline
                            }
                            else if (edge > 0.48f)
                            {
                                col = new Color32(142, 118, 94, 255); // Warm brown-bone midtone
                            }
                            else
                            {
                                byte hi = (byte)Mathf.Clamp(Mathf.RoundToInt(222f - ty * 24f), 175, 232);
                                col = new Color32(hi, (byte)(hi - 18), (byte)(hi - 42), 255); // Ivory bone core
                            }
                            if (y < 5) col.a = (byte)(y * 51);
                            pixels[y * w + x] = col;
                        }
                    }
                }

                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.08f), 64f);
                sp.name = key;
                sp.hideFlags = HideFlags.HideAndDontSave;
                _proceduralAntTrapSprites[key] = sp;
                return sp;
            }
            catch (System.Exception __apiEx_30)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L932", __apiEx_30);
                return _fallbackBoneSpikeSprite;
            }
        }

        private static UnityEngine.Sprite GetOrCreateFallbackBoneSpikeSprite()
        {
            if (_fallbackBoneSpikeSprite != null) return _fallbackBoneSpikeSprite;
            _fallbackBoneSpikeSprite = GetOrCreateProceduralAntTrapSprite("ant_trap_large_spikes0004");
            return _fallbackBoneSpikeSprite;
        }

        public static void InitGradientAlphaKey(ref UnityEngine.GradientAlphaKey k, float alpha, float time)
        {
            k.alpha = alpha;
            k.time = time;
        }

        public static void InitGradientColorKey(ref UnityEngine.GradientColorKey k, UnityEngine.Color color, float time)
        {
            k.color = color;
            k.time = time;
        }

        private static bool _bmGlobalCachesPrewarmed;
        private static readonly HashSet<string> _bmMissingSprites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Material> _bmMaterialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bmMissingMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, tk2dSpriteAnimation> _bmLibraryCache = new Dictionary<string, tk2dSpriteAnimation>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bmMissingLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite> _bmCutoutCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AssetBundle> _bmOpenedBundles = new List<AssetBundle>();
        private static readonly HashSet<AssetBundle> _bmIndexedBundles = new HashSet<AssetBundle>();
        private static readonly Dictionary<string, List<KeyValuePair<AssetBundle, string>>> _bmBundleAssetPathIndex =
            new Dictionary<string, List<KeyValuePair<AssetBundle, string>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bmFailedWants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AsyncOperationHandle> _pinnedBonemasterBundleHandles = new List<AsyncOperationHandle>();
        private static readonly HashSet<string> _heldSceneDepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Material[] _bmCachedMaterialsArray;
        private static EnemyDeathEffectsProfile[] _bmCachedDeathProfilesArray;
        private static tk2dSpriteAnimation[] _bmCachedLibrariesArray;
        private static Sprite[] _bmCachedSpritesArray;
        private static AudioClip[] _bmCachedAudioClipsArray;
        private static Material _fallbackParticleMat;

        public static void ResetBonemasterRuntimeCaches()
        {
            // Keep all preloaded sprite, sound, material, library, cutout, and pool caches intact across scene transitions!
            // Only clear per-instance runtime combat state.
            try { _bonemasterSpritePropColliderSet.Clear(); _bmSpritePropTurnStates.Clear(); } catch (System.Exception __apiEx_31) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L950", __apiEx_31); }
        }

        private static void IndexAssetBundlePaths(AssetBundle b)
        {
            if (b == null) return;
            try
            {
                if (b.isStreamedSceneAssetBundle || !_bmIndexedBundles.Add(b)) return;
                if (!_bmOpenedBundles.Contains(b)) _bmOpenedBundles.Add(b);
                var names = b.GetAllAssetNames();
                if (names == null) return;
                bool hasAntTrapOrAtlas = false;
                for (int i = 0; i < names.Length; i++)
                {
                    string assetPath = names[i];
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    if (!hasAntTrapOrAtlas &&
                        (assetPath.IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         assetPath.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase) ||
                         assetPath.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasAntTrapOrAtlas = true;
                    }
                    string shortName = Path.GetFileNameWithoutExtension(assetPath.Replace('\\', '/'));
                    if (string.IsNullOrEmpty(shortName)) continue;
                    if (!_bmBundleAssetPathIndex.TryGetValue(shortName, out var list))
                    {
                        list = new List<KeyValuePair<AssetBundle, string>>(2);
                        _bmBundleAssetPathIndex[shortName] = list;
                    }
                    bool exists = false;
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (list[j].Key == b && string.Equals(list[j].Value, assetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                        list.Add(new KeyValuePair<AssetBundle, string>(b, assetPath));
                }

                if (hasAntTrapOrAtlas)
                {
                    try
                    {
                        var atlases = b.LoadAllAssets<UnityEngine.U2D.SpriteAtlas>();
                        if (atlases != null)
                        {
                            for (int a = 0; a < atlases.Length; a++)
                            {
                                var sa = atlases[a];
                                if (sa == null) continue;
                                PrecacheAtlasSprites(sa);
                                if (!string.IsNullOrEmpty(sa.name))
                                    _addressableAssetCache[sa.name] = sa;
                                if ((sa.name ?? "").IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0)
                                    _addressableAssetCache["ant_traps"] = sa;
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            string ap = names[i];
                            if (string.IsNullOrEmpty(ap) || ap.IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            var subSprites = b.LoadAssetWithSubAssets<UnityEngine.Sprite>(ap);
                            if (subSprites != null)
                            {
                                for (int sIdx = 0; sIdx < subSprites.Length; sIdx++)
                                {
                                    var sp = subSprites[sIdx];
                                    if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
                                    string clean = sp.name.Replace("(Clone)", "");
                                    _bmSpriteCache[clean] = sp;
                                    _bmSpriteCache[sp.name] = sp;
                                    _bmSpriteCache["ant_traps/" + clean] = sp;
                                    _bmMissingSprites.Remove(clean);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                _bmOpenedBundles.Remove(b);
            }
        }

        private static UnityEngine.Object TryLoadFromIndexedBundles(string wantName, Type wantKind, string wantBundle = null)
        {
            if (string.IsNullOrEmpty(wantName)) return null;
            if (wantKind == null) wantKind = typeof(UnityEngine.Object);

            if (_addressableAssetCache.TryGetValue(wantName, out var cachedObj) && cachedObj != null && wantKind.IsAssignableFrom(cachedObj.GetType()))
                return cachedObj;

            string cleanName = Path.GetFileNameWithoutExtension(wantName.Replace('\\', '/'));
            string[] keys = (!string.IsNullOrEmpty(cleanName) && !string.Equals(cleanName, wantName, StringComparison.OrdinalIgnoreCase))
                ? new string[] { wantName, cleanName }
                : new string[] { wantName };

            for (int k = 0; k < keys.Length; k++)
            {
                if (!_bmBundleAssetPathIndex.TryGetValue(keys[k], out var entries) || entries == null) continue;
                for (int i = 0; i < entries.Count; i++)
                {
                    var bundle = entries[i].Key;
                    string path = entries[i].Value;
                    if (bundle == null || string.IsNullOrEmpty(path)) continue;

                    if (wantKind == typeof(AudioClip))
                    {
                        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
                            !path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) &&
                            !path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) &&
                            !path.EndsWith(".aif", StringComparison.OrdinalIgnoreCase) &&
                            !path.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    else if (wantKind == typeof(GameObject))
                    {
                        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    try
                    {
                        UnityEngine.Object loaded = null;
                        if (wantKind == typeof(Sprite))
                        {
                            var subs = bundle.LoadAssetWithSubAssets<Sprite>(path);
                            if (subs != null && subs.Length > 0)
                            {
                                for (int sIdx = 0; sIdx < subs.Length; sIdx++)
                                {
                                    var sp = subs[sIdx];
                                    if (sp == null) continue;
                                    string clean = (sp.name ?? "").Replace("(Clone)", "");
                                    if (!string.IsNullOrEmpty(clean))
                                    {
                                        _bmSpriteCache[clean] = sp;
                                        _bmSpriteCache["ant_traps/" + clean] = sp;
                                    }
                                    if (string.Equals(clean, wantName, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(clean, cleanName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        loaded = sp;
                                    }
                                }
                                if (loaded == null) loaded = subs[0];
                            }
                        }
                        if (loaded == null)
                        {
                            loaded = bundle.LoadAsset(path, wantKind);
                        }
                        if (loaded != null)
                        {
                            _addressableAssetCache[wantName] = loaded;
                            if (loaded is AudioClip ac) _bmSoundCache[wantName] = ac;
                            else if (loaded is Sprite spLoaded)
                            {
                                _bmSpriteCache[wantName] = spLoaded;
                                _bmSpriteCache["ant_traps/" + wantName] = spLoaded;
                            }
                            else if (loaded is GameObject go)
                            {
                                _bmPoolCache[wantName] = go;
                                var anim = go.GetComponent<tk2dSpriteAnimation>() ?? go.GetComponentInChildren<tk2dSpriteAnimation>(true);
                                if (anim != null) _bmLibraryCache[wantName] = anim;
                            }
                            else if (loaded is UnityEngine.U2D.SpriteAtlas sa)
                            {
                                PrecacheAtlasSprites(sa);
                            }
                            return loaded;
                        }
                    }
                    catch
                    {
                        _bmOpenedBundles.Remove(bundle);
                    }
                }
            }

            if (wantKind == typeof(UnityEngine.U2D.SpriteAtlas) || wantName.IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                for (int i = _bmOpenedBundles.Count - 1; i >= 0; i--)
                {
                    var b = _bmOpenedBundles[i];
                    if (b == null) continue;
                    try
                    {
                        var atlases = b.LoadAllAssets<UnityEngine.U2D.SpriteAtlas>();
                        if (atlases != null && atlases.Length > 0)
                        {
                            for (int a = 0; a < atlases.Length; a++)
                            {
                                var sa = atlases[a];
                                if (sa == null) continue;
                                PrecacheAtlasSprites(sa);
                                if (!string.IsNullOrEmpty(sa.name))
                                    _addressableAssetCache[sa.name] = sa;
                                if ((sa.name ?? "").IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    string.Equals(wantName, "ant_traps", StringComparison.OrdinalIgnoreCase))
                                {
                                    _addressableAssetCache["ant_traps"] = sa;
                                    if (wantKind == typeof(UnityEngine.U2D.SpriteAtlas) || wantKind == typeof(UnityEngine.Object))
                                        return sa;
                                }
                            }
                        }
                    }
                    catch
                    {
                        _bmOpenedBundles.RemoveAt(i);
                    }
                }
            }

            return null;
        }

        public static void PrewarmBonemasterGlobalCaches(bool forceRefreshArrays = false)
        {
            if (_bmGlobalCachesPrewarmed && !forceRefreshArrays) return;
            _bmGlobalCachesPrewarmed = true;

            try
            {
                var loadedBundles = UnityEngine.AssetBundle.GetAllLoadedAssetBundles();
                if (loadedBundles != null)
                {
                    foreach (var b in loadedBundles)
                    {
                        IndexAssetBundlePaths(b);
                    }
                }
            }
            catch { }

            try
            {
                var atlases = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.U2D.SpriteAtlas>();
                if (atlases != null)
                {
                    for (int i = 0; i < atlases.Length; i++)
                    {
                        var atlas = atlases[i];
                        if (atlas == null) continue;
                        string aname = atlas.name ?? "";
                        if (aname.IndexOf("ant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            aname.IndexOf("trap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            aname.IndexOf("bone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            aname.IndexOf("skarr", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            PrecacheAtlasSprites(atlas);
                        }
                    }
                }
            }
            catch { }

            try
            {
                var allSprites = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Sprite>();
                if (allSprites != null)
                {
                    _bmCachedSpritesArray = allSprites;
                    for (int i = 0; i < allSprites.Length; i++)
                    {
                        var s = allSprites[i];
                        if (s != null && !string.IsNullOrEmpty(s.name))
                        {
                            string clean = s.name.Replace("(Clone)", "").Trim();
                            if (!_bmSpriteCache.ContainsKey(clean) || _bmSpriteCache[clean] == null)
                                _bmSpriteCache[clean] = s;
                            if (!_bmSpriteCache.ContainsKey(s.name) || _bmSpriteCache[s.name] == null)
                                _bmSpriteCache[s.name] = s;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var clips = UnityEngine.Resources.FindObjectsOfTypeAll<AudioClip>();
                if (clips != null)
                {
                    _bmCachedAudioClipsArray = clips;
                    for (int i = 0; i < clips.Length; i++)
                    {
                        var c = clips[i];
                        if (c != null && !string.IsNullOrEmpty(c.name) && !_bmSoundCache.ContainsKey(c.name))
                        {
                            _bmSoundCache[c.name] = c;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var mats = UnityEngine.Resources.FindObjectsOfTypeAll<Material>();
                if (mats != null)
                {
                    _bmCachedMaterialsArray = mats;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        string mName = m.name;
                        if (!_bmMaterialCache.TryGetValue(mName, out var existing) || existing == null ||
                            (m.shader != null && m.shader.name != null && m.shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            _bmMaterialCache[mName] = m;
                        }
                        if (_fallbackParticleMat == null && mName.StartsWith("Particle", StringComparison.OrdinalIgnoreCase))
                        {
                            _fallbackParticleMat = m;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var libs = UnityEngine.Resources.FindObjectsOfTypeAll<tk2dSpriteAnimation>();
                if (libs != null)
                {
                    _bmCachedLibrariesArray = libs;
                    for (int i = 0; i < libs.Length; i++)
                    {
                        var lib = libs[i];
                        if (lib != null && !string.IsNullOrEmpty(lib.name) && !_bmLibraryCache.ContainsKey(lib.name))
                        {
                            _bmLibraryCache[lib.name] = lib;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var profiles = UnityEngine.Resources.FindObjectsOfTypeAll<EnemyDeathEffectsProfile>();
                if (profiles != null)
                {
                    _bmCachedDeathProfilesArray = profiles;
                }
            }
            catch { }
        }

        public static Material[] BonemasterGetCachedMaterialsArray()
        {
            if (_bmCachedMaterialsArray == null) PrewarmBonemasterGlobalCaches();
            return _bmCachedMaterialsArray ?? Array.Empty<Material>();
        }

        public static EnemyDeathEffectsProfile[] BonemasterGetCachedDeathProfilesArray()
        {
            if (_bmCachedDeathProfilesArray == null) PrewarmBonemasterGlobalCaches();
            return _bmCachedDeathProfilesArray ?? Array.Empty<EnemyDeathEffectsProfile>();
        }

        public static tk2dSpriteAnimation[] BonemasterGetCachedLibrariesArray()
        {
            if (_bmCachedLibrariesArray == null) PrewarmBonemasterGlobalCaches();
            return _bmCachedLibrariesArray ?? Array.Empty<tk2dSpriteAnimation>();
        }

        public static Sprite[] BonemasterGetCachedSpritesArray()
        {
            if (_bmCachedSpritesArray == null) PrewarmBonemasterGlobalCaches();
            return _bmCachedSpritesArray ?? Array.Empty<Sprite>();
        }

        public static AudioClip[] BonemasterGetCachedAudioClipsArray()
        {
            if (_bmCachedAudioClipsArray == null) PrewarmBonemasterGlobalCaches();
            return _bmCachedAudioClipsArray ?? Array.Empty<AudioClip>();
        }

        public static tk2dSpriteAnimation BonemasterLibraryGet(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (_bmLibraryCache.TryGetValue(name, out var cached) && cached != null && (UnityEngine.Object)cached != null)
                return cached;

            if (string.Equals(name, "Knight", StringComparison.OrdinalIgnoreCase) && HeroController.instance != null)
            {
                try
                {
                    var heroAnim = HeroController.instance.GetComponent<tk2dSpriteAnimator>();
                    if (heroAnim != null && heroAnim.Library != null)
                    {
                        _bmLibraryCache[name] = heroAnim.Library;
                        return heroAnim.Library;
                    }
                }
                catch { }
            }

            if (name.EndsWith("(Bonemaster)", StringComparison.Ordinal))
            {
                try
                {
                    var bm = GameObject.Find("Bonemaster");
                    if (bm != null)
                    {
                        var child = bm.transform.Find(name);
                        if (child != null)
                        {
                            var anim = child.GetComponent<tk2dSpriteAnimation>();
                            if (anim != null)
                            {
                                _bmLibraryCache[name] = anim;
                                return anim;
                            }
                        }
                    }
                }
                catch { }
            }

            if (_bmMissingLibraries.Contains(name))
                return null;

            var obj = TryLoadFromIndexedBundles(name, typeof(GameObject)) ?? TryLoadFromIndexedBundles(name, typeof(tk2dSpriteAnimation));
            if (obj is tk2dSpriteAnimation directAnim)
            {
                _bmLibraryCache[name] = directAnim;
                return directAnim;
            }
            if (obj is GameObject go)
            {
                var anim = go.GetComponent<tk2dSpriteAnimation>() ?? go.GetComponentInChildren<tk2dSpriteAnimation>(true);
                if (anim != null)
                {
                    _bmLibraryCache[name] = anim;
                    return anim;
                }
            }

            if (!_bmGlobalCachesPrewarmed)
            {
                PrewarmBonemasterGlobalCaches();
                if (_bmLibraryCache.TryGetValue(name, out cached) && cached != null)
                    return cached;
            }

            _bmMissingLibraries.Add(name);
            return null;
        }

        private static int _bmMaterialScanSceneHandle = int.MinValue;
        private static int _bmMaterialScanSceneCount = -1;
        private static Material _fallbackSpriteAdditiveMat;
        private static readonly HashSet<string> _bmLoggedMaterialLookups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void LogBonemasterMaterialOnce(string requested, Material resolved, string source)
        {
            if (string.IsNullOrEmpty(requested)) return;
            string key = requested + "|" + source;
            if (!_bmLoggedMaterialLookups.Add(key)) return;
            try
            {
                string matName = (resolved != null && (UnityEngine.Object)resolved != null) ? resolved.name : "null";
                string shName = (resolved != null && (UnityEngine.Object)resolved != null && resolved.shader != null) ? resolved.shader.name : "null";
                string texName = (resolved != null && (UnityEngine.Object)resolved != null && resolved.HasProperty("_MainTex") && resolved.mainTexture != null) ? resolved.mainTexture.name : "null";
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[BonemasterFindMaterial] '{requested}' -> mat='{matName}', shader='{shName}', tex='{texName}', src={source}");
            }
            catch { }
        }

        public static Material BonemasterFindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Particle_WhiteOrb";

            if (_bmMaterialCache.TryGetValue(name, out var cached) && cached != null && (UnityEngine.Object)cached != null)
            {
                LogBonemasterMaterialOnce(name, cached, "cache");
                return cached;
            }

            if (!_bmGlobalCachesPrewarmed)
            {
                PrewarmBonemasterGlobalCaches();
                if (_bmMaterialCache.TryGetValue(name, out cached) && cached != null)
                {
                    LogBonemasterMaterialOnce(name, cached, "prewarm");
                    return cached;
                }
            }

            var indexedMat = TryLoadFromIndexedBundles(name, typeof(Material)) as Material;
            if (indexedMat != null)
            {
                _bmMaterialCache[name] = indexedMat;
                LogBonemasterMaterialOnce(name, indexedMat, "bundle");
                return indexedMat;
            }

            // Refresh live scene materials once per (activeScene.handle, sceneCount) change on cache miss
            // so additive boss scenes (e.g. Hang_04 + Hang_04_boss) surface their materials cleanly.
            int activeHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (_bmMaterialScanSceneHandle != activeHandle || _bmMaterialScanSceneCount != sceneCount)
            {
                _bmMaterialScanSceneHandle = activeHandle;
                _bmMaterialScanSceneCount = sceneCount;
                try
                {
                    var mats = UnityEngine.Resources.FindObjectsOfTypeAll<Material>();
                    if (mats != null)
                    {
                        _bmCachedMaterialsArray = mats;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var m = mats[i];
                            if (m == null || string.IsNullOrEmpty(m.name)) continue;
                            string mName = m.name;
                            if (!_bmMaterialCache.TryGetValue(mName, out var existing) || existing == null ||
                                (m.shader != null && m.shader.name != null && m.shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                _bmMaterialCache[mName] = m;
                            }
                            if (_fallbackParticleMat == null && mName.StartsWith("Particle", StringComparison.OrdinalIgnoreCase))
                            {
                                _fallbackParticleMat = m;
                            }
                        }
                    }
                }
                catch { }

                if (_bmMaterialCache.TryGetValue(name, out cached) && cached != null && (UnityEngine.Object)cached != null)
                {
                    LogBonemasterMaterialOnce(name, cached, "sceneScan");
                    return cached;
                }
            }

            // NEVER substitute a Particle material (e.g. Particle_WhiteOrb white circle) for SpriteRenderer materials ("Sprites-*").
            // For "Sprites-Screen" / "Sprites-Additive" misses, use a textureless additive shader so light_effect_v02 blends softly
            // instead of opaque-looking alpha circles; otherwise return null so Pictures.Make uses Sprites/Default.
            if (name.StartsWith("Sprites", StringComparison.OrdinalIgnoreCase))
            {
                if (name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (_fallbackSpriteAdditiveMat == null || (UnityEngine.Object)_fallbackSpriteAdditiveMat == null)
                    {
                        try
                        {
                            Shader addSh = null;
                            string[] addCandidates = new[] { "Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive", "Sprites/Additive" };
                            for (int si = 0; si < addCandidates.Length; si++)
                            {
                                var cand = Shader.Find(addCandidates[si]);
                                if (cand != null && cand.isSupported) { addSh = cand; break; }
                            }
                            if (addSh != null)
                            {
                                _fallbackSpriteAdditiveMat = new Material(addSh)
                                {
                                    name = "IRIS_FallbackSpriteAdditive",
                                    hideFlags = HideFlags.HideAndDontSave
                                };
                            }
                        }
                        catch { }
                    }
                    if (_fallbackSpriteAdditiveMat != null && (UnityEngine.Object)_fallbackSpriteAdditiveMat != null)
                    {
                        LogBonemasterMaterialOnce(name, _fallbackSpriteAdditiveMat, "spriteAdditiveFallback");
                        return _fallbackSpriteAdditiveMat;
                    }
                }
                LogBonemasterMaterialOnce(name, null, "spriteNullFallback");
                return null;
            }

            // Return fallback particle material without permanently poisoning _bmMaterialCache[name] across future scenes.
            if (_fallbackParticleMat != null && (UnityEngine.Object)_fallbackParticleMat != null)
            {
                LogBonemasterMaterialOnce(name, _fallbackParticleMat, "particleFallback");
                return _fallbackParticleMat;
            }

            try
            {
                var sh = GetFallbackAndroidShader("Particle Additive");
                if (sh != null)
                {
                    _fallbackParticleMat = new Material(sh) { name = "IRIS_FallbackParticle", hideFlags = HideFlags.HideAndDontSave };
                    LogBonemasterMaterialOnce(name, _fallbackParticleMat, "particleShaderFallback");
                    return _fallbackParticleMat;
                }
            }
            catch { }

            LogBonemasterMaterialOnce(name, null, "missNull");
            return null;
        }

        public static Sprite BonemasterCutoutMake(tk2dSpriteCollectionData sheet, int spriteId, Rect region, object keep, out string why)
        {
            why = "";
            if (sheet == null || sheet.spriteDefinitions == null || spriteId < 0 || spriteId >= sheet.spriteDefinitions.Length)
            {
                why = "invalid sheet or spriteId";
                return null;
            }

            string key = $"{sheet.name}/{spriteId}/{region.xMin:F2},{region.yMin:F2},{region.width:F2},{region.height:F2}";
            if (_bmCutoutCache.TryGetValue(key, out var cached) && cached != null && (UnityEngine.Object)cached != null)
            {
                return cached;
            }

            try
            {
                var def = sheet.spriteDefinitions[spriteId];
                if (def == null || def.positions == null || def.positions.Length < 4 || def.uvs == null || def.uvs.Length < 4)
                {
                    why = "not a plain quad";
                    return null;
                }
                var mat = def.materialInst != null ? def.materialInst : def.material;
                var tex = (mat != null ? mat.mainTexture : null) as Texture2D;
                if (tex == null)
                {
                    why = "no sheet texture";
                    return null;
                }

                float uMin = float.MaxValue, uMax = float.MinValue;
                float vMin = float.MaxValue, vMax = float.MinValue;
                for (int i = 0; i < def.uvs.Length; i++)
                {
                    Vector2 uv = def.uvs[i];
                    if (uv.x < uMin) uMin = uv.x;
                    if (uv.x > uMax) uMax = uv.x;
                    if (uv.y < vMin) vMin = uv.y;
                    if (uv.y > vMax) vMax = uv.y;
                }

                float pxX = Mathf.Clamp(uMin * tex.width, 0f, Mathf.Max(0f, tex.width - 2f));
                float pxY = Mathf.Clamp(vMin * tex.height, 0f, Mathf.Max(0f, tex.height - 2f));
                float pxW = Mathf.Clamp((uMax - uMin) * tex.width, 2f, tex.width - pxX);
                float pxH = Mathf.Clamp((vMax - vMin) * tex.height, 2f, tex.height - pxY);
                float ppu = def.texelSize.x > 1e-5f ? Mathf.Clamp(1f / def.texelSize.x, 8f, 512f) : 64f;

                var sp = Sprite.Create(tex, new Rect(pxX, pxY, pxW, pxH), new Vector2(0.5f, 0.5f), ppu);
                sp.name = "Cutout " + (def.name ?? spriteId.ToString());
                sp.hideFlags = HideFlags.HideAndDontSave;
                _bmCutoutCache[key] = sp;
                return sp;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return null;
            }
        }

        private static int _bmSpriteScanSceneHandle = int.MinValue;
        private static int _bmSpriteScanSceneCount = -1;

        public static UnityEngine.Sprite BonemasterPicturesFind(string name, out string why)
        {
            why = "";
            if (string.IsNullOrEmpty(name)) return null;

            if (_bmSpriteCache.TryGetValue(name, out var cachedSprite) && cachedSprite != null && (UnityEngine.Object)cachedSprite != null)
            {
                return cachedSprite;
            }

            int activeHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (_bmSpriteScanSceneHandle != activeHandle || _bmSpriteScanSceneCount != sceneCount)
            {
                _bmSpriteScanSceneHandle = activeHandle;
                _bmSpriteScanSceneCount = sceneCount;
                _bmMissingSprites.Clear();
                try
                {
                    var liveSprites = UnityEngine.Resources.FindObjectsOfTypeAll<Sprite>();
                    if (liveSprites != null)
                    {
                        for (int i = 0; i < liveSprites.Length; i++)
                        {
                            var sp = liveSprites[i];
                            if (sp != null && !string.IsNullOrEmpty(sp.name) &&
                                (!_bmSpriteCache.TryGetValue(sp.name, out var existingSp) || existingSp == null || (UnityEngine.Object)existingSp == null))
                            {
                                _bmSpriteCache[sp.name] = sp;
                            }
                        }
                    }
                }
                catch { }

                if (_bmSpriteCache.TryGetValue(name, out cachedSprite) && cachedSprite != null && (UnityEngine.Object)cachedSprite != null)
                {
                    return cachedSprite;
                }
            }

            if (_bmMissingSprites.Contains(name))
            {
                why = "sprite " + name + " not found";
                return null;
            }

            if (!_bmGlobalCachesPrewarmed)
            {
                PrewarmBonemasterGlobalCaches();
                if (_bmSpriteCache.TryGetValue(name, out cachedSprite) && cachedSprite != null)
                    return cachedSprite;
            }

            var bundleSprite = (TryLoadFromIndexedBundles(name, typeof(Sprite))
                             ?? ResolveAddressableAsset(name, typeof(Sprite))) as Sprite;
            if (bundleSprite != null)
            {
                _bmSpriteCache[name] = bundleSprite;
                _bmSpriteCache["ant_traps/" + name] = bundleSprite;
                return bundleSprite;
            }

            if (name.StartsWith("ant_trap", StringComparison.OrdinalIgnoreCase))
            {
                var trapAtlas = (TryLoadFromIndexedBundles("ant_traps", typeof(UnityEngine.U2D.SpriteAtlas))
                              ?? ResolveAddressableAsset("ant_traps", typeof(UnityEngine.U2D.SpriteAtlas))) as UnityEngine.U2D.SpriteAtlas;
                if (trapAtlas != null)
                {
                    PrecacheAtlasSprites(trapAtlas);
                    if (_bmSpriteCache.TryGetValue(name, out cachedSprite) && cachedSprite != null)
                        return cachedSprite;
                    var sp = trapAtlas.GetSprite(name);
                    if (sp != null)
                    {
                        sp.name = name;
                        _bmSpriteCache[name] = sp;
                        _bmSpriteCache["ant_traps/" + name] = sp;
                        return sp;
                    }
                }

                var fb = GetOrCreateProceduralAntTrapSprite(name);
                if (fb != null)
                {
                    _bmSpriteCache[name] = fb;
                    _bmSpriteCache["ant_traps/" + name] = fb;
                    return fb;
                }
            }

            _bmMissingSprites.Add(name);
            why = "sprite " + name + " not found";
            return null;
        }

        private static readonly Dictionary<string, Sprite> _bmCroppedSpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        public static Sprite BonemasterDrawingCropBottom(Sprite from, float cut, float fade)
        {
            if (from == null || from.texture == null) return from;
            if (cut <= 0f && fade <= 0f) return from;

            string key = $"{from.GetInstanceID()}:{cut:F2}:{fade:F2}";
            if (_bmCroppedSpriteCache.TryGetValue(key, out var cached))
            {
                if (cached != null && (UnityEngine.Object)cached != null)
                    return cached;
                _bmCroppedSpriteCache.Remove(key);
            }

            try
            {
                Rect r = from.textureRect;
                float trim = Mathf.Clamp01(cut) * r.height;
                float keepH = Mathf.Max(2f, r.height - trim);
                Rect subRect = new Rect(r.x, r.y + trim, r.width, keepH);
                Vector2 pivot = new Vector2(
                    r.width > 0f ? (from.pivot.x / r.width) : 0.5f,
                    keepH > 0f ? Mathf.Clamp01((from.pivot.y - trim) / keepH) : 0.1f);
                var cropped = Sprite.Create(from.texture, subRect, pivot, from.pixelsPerUnit);
                cropped.name = from.name + "_cropped";
                cropped.hideFlags = HideFlags.HideAndDontSave;
                _bmCroppedSpriteCache[key] = cropped;
                return cropped;
            }
            catch
            {
                return from;
            }
        }

        private static readonly Dictionary<string, GameObject> _bmPoolCache =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bmMissingPoolNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _bmPoolCacheScene = "";

        public static GameObject BonemasterPoolFind(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (_bmPoolCache.TryGetValue(name, out var cached) && cached != null && (UnityEngine.Object)cached != null)
            {
                return cached;
            }

            if (_harvestedPoolPrefabs.TryGetValue(name, out var harvested) && harvested != null && (UnityEngine.Object)harvested != null)
            {
                _bmPoolCache[name] = harvested;
                return harvested;
            }

            string activeScene = SceneManager.GetActiveScene().name ?? "";
            if (_bmPoolCacheScene != activeScene)
            {
                _bmPoolCacheScene = activeScene;
                _bmMissingPoolNames.Clear();
            }

            if (_bmMissingPoolNames.Contains(name))
                return null;

            try
            {
                if (PersonalObjectPool._activePoolManagers != null)
                {
                    for (int i = 0; i < PersonalObjectPool._activePoolManagers.Count; i++)
                    {
                        var manager = PersonalObjectPool._activePoolManagers[i];
                        if (manager == null || manager.startupPool == null) continue;
                        for (int j = 0; j < manager.startupPool.Count; j++)
                        {
                            var prefab = manager.startupPool[j].prefab;
                            if (prefab != null && string.Equals(prefab.name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                _bmPoolCache[name] = prefab;
                                return prefab;
                            }
                        }
                    }
                }

                var bundleGo = TryLoadFromIndexedBundles(name, typeof(GameObject)) as GameObject;
                if (bundleGo != null)
                {
                    _bmPoolCache[name] = bundleGo;
                    return bundleGo;
                }
            }
            catch (System.Exception __apiEx_33) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1145", __apiEx_33); }

            _bmMissingPoolNames.Add(name);
            return null;
        }

        private static readonly Dictionary<string, AudioClip> _bmSoundCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bmMissingSounds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _bmSoundIndexedScene = "";

        public static AudioClip BonemasterSoundInMemory(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (_bmSoundCache.TryGetValue(name, out var cached) && cached != null && (UnityEngine.Object)cached != null)
            {
                return cached;
            }

            if (!_bmGlobalCachesPrewarmed)
            {
                PrewarmBonemasterGlobalCaches();
                if (_bmSoundCache.TryGetValue(name, out var found) && found != null)
                    return found;
            }

            var bundleClip = TryLoadFromIndexedBundles(name, typeof(AudioClip)) as AudioClip;
            if (bundleClip != null)
            {
                _bmSoundCache[name] = bundleClip;
                return bundleClip;
            }

            // Return and cache a 1-sample silent clip so Bonemaster never re-queries missing clips mid-combat
            var silent = GetOrCreateSilentFallbackClip(name);
            if (silent != null)
            {
                _bmSoundCache[name] = silent;
                return silent;
            }
            return null;
        }

        private static AudioClip _sharedSilentClip;
        private static AudioClip GetOrCreateSilentFallbackClip(string name)
        {
            try
            {
                if (_sharedSilentClip == null)
                {
                    _sharedSilentClip = AudioClip.Create("SilentFallbackClip", 64, 1, 22050, false);
                    if (_sharedSilentClip != null)
                        _sharedSilentClip.hideFlags = HideFlags.HideAndDontSave;
                }
                return _sharedSilentClip;
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerator BonemasterBorrowOpenFast(string bundle)
        {
            if (!string.IsNullOrEmpty(bundle))
            {
                try
                {
                    foreach (var loaded in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                    {
                        if (loaded != null && !loaded.isStreamedSceneAssetBundle)
                        {
                            IndexAssetBundlePaths(loaded);
                            if (string.Equals(loaded.name, bundle, StringComparison.OrdinalIgnoreCase))
                            {
                                string[] borrowTypeNames = new[] { "BossKit.World.Borrow, Bonemaster", "ThirdAcolyte.World.Borrow, ThirdAcolyte" };
                                for (int bi = 0; bi < borrowTypeNames.Length; bi++)
                                {
                                    var borrowType = Type.GetType(borrowTypeNames[bi], false);
                                    if (borrowType != null)
                                    {
                                        var openedField = borrowType.GetField("opened", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                                        if (openedField?.GetValue(null) is IList<UnityEngine.AssetBundle> list)
                                        {
                                            if (!list.Contains(loaded)) list.Add(loaded);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (System.Exception __apiEx_36) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1249", __apiEx_36); }
            }
            yield break;
        }

        public static IEnumerator EmptyCoroutine()
        {
            yield break;
        }

        // =========================================================================
        // Bonemaster / BossKit ValueTuple Neutralization Bridges (v2.9.0)
        // =========================================================================
        private static readonly Dictionary<string, string> _bonemasterWords = new Dictionary<string, string>(StringComparer.Ordinal);
        public static void BonemasterWordsSet(string sheet, string key, string text)
        {
            if (string.IsNullOrEmpty(key)) return;
            _bonemasterWords[(sheet ?? "") + ":" + key] = text;
        }
        public static void BonemasterWordsGet(string key, string sheetTitle, ref string result)
        {
            if (_bonemasterWords.Count == 0 || string.IsNullOrEmpty(key)) return;
            if (_bonemasterWords.TryGetValue((sheetTitle ?? "") + ":" + key, out string val))
            {
                result = val;
            }
        }
        public static void BonemasterWordsHas(string key, string sheetTitle, ref bool result)
        {
            if (result || _bonemasterWords.Count == 0 || string.IsNullOrEmpty(key)) return;
            if (_bonemasterWords.ContainsKey((sheetTitle ?? "") + ":" + key))
            {
                result = true;
            }
        }

        private static readonly Dictionary<string, Action<PlayMakerFSM>> _bonemasterBuilders = new Dictionary<string, Action<PlayMakerFSM>>(StringComparer.Ordinal);
        public static void BonemasterMachinesOn(string objectName, string fsmName, Action<PlayMakerFSM> build)
        {
            if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(fsmName) || build == null) return;
            _bonemasterBuilders[objectName + ":" + fsmName] = build;
        }
        public static void BonemasterMachinesOnFsmStart(PlayMakerFSM instance)
        {
            if (_bonemasterBuilders.Count == 0 || instance == null) return;
            var go = instance.gameObject;
            if (go == null) return;
            string key = go.name + ":" + (instance.FsmName ?? "");
            if (_bonemasterBuilders.TryGetValue(key, out var builder) && builder != null)
            {
                try
                {
                    builder(instance);
                    var rigType = Type.GetType("BossKit.Fight.Rig, Bonemaster", false);
                    var relinkMi = rigType?.GetMethod("Relink", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    relinkMi?.Invoke(null, new object[] { instance });
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] BonemasterMachines build failed: {ex.Message}");
                }
            }
        }

        private static readonly Dictionary<string, Action<HealthManager, HitInstance>> _bonemasterHits = new Dictionary<string, Action<HealthManager, HitInstance>>(StringComparer.Ordinal);
        private static readonly HashSet<string> _bonemasterHitFailed = new HashSet<string>(StringComparer.Ordinal);
        public static void BonemasterMachinesOnHit(string objectName, Action<HealthManager, HitInstance> onHit)
        {
            if (string.IsNullOrEmpty(objectName) || onHit == null) return;
            _bonemasterHits[objectName] = onHit;
        }
        public static void BonemasterMachinesOnHitPrefix(HealthManager instance, HitInstance hitInstance)
        {
            if (_bonemasterHits.Count == 0 || instance == null) return;
            var go = instance.gameObject;
            if (go == null) return;
            string name = go.name ?? "";
            if (_bonemasterHits.TryGetValue(name, out var handler) && handler != null)
            {
                try
                {
                    handler(instance, hitInstance);
                }
                catch (Exception ex)
                {
                    if (_bonemasterHitFailed.Add(name))
                        SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] BonemasterMachinesOnHitPrefix '{name}' failed: {ex.Message}");
                }
            }
        }

        private static readonly Dictionary<string, string[]> _bonemasterTitles = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public static void BonemasterTitleSet(string key, string super, string main, string sub)
        {
            if (string.IsNullOrEmpty(key)) return;
            _bonemasterTitles[key] = new string[] { super ?? "", main ?? "", sub ?? "" };
        }
        public static void BonemasterTitleGet(string key, ref string result)
        {
            if (_bonemasterTitles.Count == 0 || string.IsNullOrEmpty(key)) return;
            foreach (var kvp in _bonemasterTitles)
            {
                if (key.StartsWith(kvp.Key, StringComparison.Ordinal))
                {
                    string suffix = key.Substring(kvp.Key.Length);
                    if (suffix == "_SUPER") result = kvp.Value[0];
                    else if (suffix == "_MAIN") result = kvp.Value[1];
                    else if (suffix == "_SUB") result = kvp.Value[2];
                    break;
                }
            }
        }
        public static bool BonemasterTitleShowBig(string key)
        {
            try
            {
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (a != null && a.GetName().Name == "Bonemaster")
                    {
                        var titleType = a.GetType("BossKit.Fight.Title", false);
                        var showMi = titleType?.GetMethod("Show", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        showMi?.Invoke(null, new object[] { key, false });
                        break;
                    }
                }
            }
            catch { }
            return false;
        }

        private static readonly Dictionary<string, CameraShakeProfile> _bonemasterShortenedShakes = new Dictionary<string, CameraShakeProfile>(StringComparer.Ordinal);
        public static CameraShakeProfile BonemasterShakeShortened(string name, float seconds)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var camType = typeof(GlobalSettings.Camera);
                var rawObj = camType.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null)
                          ?? camType.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                var profile = rawObj as CameraShakeProfile;
                if (profile == null || seconds <= 0f) return profile;

                string cacheKey = $"{name}:{seconds:F2}";
                if (_bonemasterShortenedShakes.TryGetValue(cacheKey, out var cached) && cached != null)
                    return cached;

                var durField = typeof(CameraShakeProfile).GetField("duration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (durField == null || durField.FieldType != typeof(float))
                    return profile;

                var clone = UnityEngine.Object.Instantiate(profile);
                clone.name = $"{profile.name} ({seconds:0.##} s)";
                clone.hideFlags = HideFlags.HideAndDontSave;
                durField.SetValue(clone, seconds);
                _bonemasterShortenedShakes[cacheKey] = clone;
                return clone;
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<string, UnityEngine.Object> _addressableAssetCache = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _failedAddressableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<string>> _addressableKeysByShortName;

        public static UnityEngine.Networking.UnityWebRequest GetTextureWithParams(string uri, UnityEngine.Networking.DownloadedTextureParams parameters)
        {
            return UnityEngine.Networking.UnityWebRequestTexture.GetTexture(uri, !parameters.readable);
        }

        private static void EnsureAddressableShortNameIndex()
        {
            if (_addressableKeysByShortName != null && _addressableKeysByShortName.Count > 100) return;
            if (_addressableKeysByShortName == null)
                _addressableKeysByShortName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var locator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                {
                    if (locator == null || locator.Keys == null) continue;
                    foreach (var kObj in locator.Keys)
                    {
                        if (!(kObj is string keyStr) || keyStr.Length == 0) continue;
                        if (keyStr.IndexOf('/') < 0 && keyStr.IndexOf('.') < 0) continue;
                        string shortName = Path.GetFileNameWithoutExtension(keyStr.Replace('\\', '/'));
                        if (string.IsNullOrEmpty(shortName)) continue;
                        if (!_addressableKeysByShortName.TryGetValue(shortName, out var list))
                        {
                            list = new List<string>(2);
                            _addressableKeysByShortName[shortName] = list;
                        }
                        if (list.Count < 6 && !list.Contains(keyStr))
                            list.Add(keyStr);
                    }
                }
                if (_addressableKeysByShortName.Count > 100)
                {
                    _failedAddressableNames.Clear();
                }
            }
            catch (System.Exception __apiEx_37) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1292", __apiEx_37); }
        }

        public static UnityEngine.Object ResolveAddressableAsset(string name, Type kind)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_addressableAssetCache.TryGetValue(name, out var cached) && cached != null)
            {
                return cached;
            }
            if (_failedAddressableNames.Contains(name))
            {
                return null;
            }

            EnsureAddressableShortNameIndex();
            var candidateKeys = new List<string>(16);
            string cleanName = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
            if (_addressableKeysByShortName != null)
            {
                if (_addressableKeysByShortName.TryGetValue(name, out var mapped1))
                    candidateKeys.AddRange(mapped1);
                if (!string.IsNullOrEmpty(cleanName) && !string.Equals(cleanName, name, StringComparison.OrdinalIgnoreCase) &&
                    _addressableKeysByShortName.TryGetValue(cleanName, out var mapped2))
                {
                    foreach (var m in mapped2)
                        if (!candidateKeys.Contains(m)) candidateKeys.Add(m);
                }
            }

            string[] directKeys = new string[]
            {
                name,
                name + ".spriteatlas",
                name + ".prefab",
                name + ".ogg",
                name + ".wav",
                name + ".asset",
                name + ".controller",
                name + ".png",
                name.ToLowerInvariant(),
                name.ToLowerInvariant() + ".spriteatlas",
                name.ToLowerInvariant() + ".prefab",
                name.ToLowerInvariant() + ".ogg",
                name.ToLowerInvariant() + ".wav",
                name.ToLowerInvariant() + ".png"
            };
            foreach (var dk in directKeys)
            {
                if (!candidateKeys.Contains(dk))
                    candidateKeys.Add(dk);
            }

            foreach (var key in candidateKeys)
            {
                if (!IsAddressableKeyRegistered(key))
                    continue;
                if (kind == typeof(UnityEngine.U2D.SpriteAtlas))
                {
                    try
                    {
                        var hSa = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<UnityEngine.U2D.SpriteAtlas>(key);
                        var sa = hSa.WaitForCompletion();
                        if (sa != null)
                        {
                            PrecacheAtlasSprites(sa);
                            _addressableAssetCache[name] = sa;
                            return sa;
                        }
                    }
                    catch { }
                }
                else if (kind == typeof(Sprite))
                {
                    try
                    {
                        var hSp = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>(key);
                        var sp = hSp.WaitForCompletion();
                        if (sp != null)
                        {
                            _addressableAssetCache[name] = sp;
                            _bmSpriteCache[name] = sp;
                            return sp;
                        }
                    }
                    catch { }
                }
                else if (kind == typeof(GameObject))
                {
                    try
                    {
                        var hGo = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(key);
                        var go = hGo.WaitForCompletion();
                        if (go != null)
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Successfully loaded Addressable GameObject '{name}' via key '{key}'");
                            _addressableAssetCache[name] = go;
                            return go;
                        }
                    }
                    catch (System.Exception __apiEx_38) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1358", __apiEx_38); }
                }

                try
                {
                    var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<UnityEngine.Object>(key);
                    var obj = handle.WaitForCompletion();
                    if (obj != null)
                    {
                        if (obj is UnityEngine.U2D.SpriteAtlas saObj)
                            PrecacheAtlasSprites(saObj);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Successfully loaded Addressable '{name}' via key '{key}' ({obj.GetType().Name})");
                        _addressableAssetCache[name] = obj;
                        return obj;
                    }
                }
                catch (System.Exception __apiEx_39) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1372", __apiEx_39); }
            }

            _failedAddressableNames.Add(name);
            return null;
        }

        private static bool IsAddressableKeyRegistered(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                var locators = UnityEngine.AddressableAssets.Addressables.ResourceLocators;
                if (locators == null) return true;
                foreach (var loc in locators)
                {
                    if (loc != null && loc.Locate(key, null, out var locs) && locs != null && locs.Count > 0)
                        return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool _inRegisterSpearPrefab;
        public static void RegisterBonemasterSpearPrefab(GameObject spearPrefab)
        {
            if (spearPrefab == null || _inRegisterSpearPrefab) return;
            _inRegisterSpearPrefab = true;
            try
            {
                _bmPoolCache["Hunter_Queen_ground_spear_set"] = spearPrefab;
                _bmPoolCache["Sickle Shot"] = spearPrefab;

                Assembly bmAsm = null;
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (a != null && a.GetName().Name == "Bonemaster")
                    {
                        bmAsm = a;
                        break;
                    }
                }

                try
                {
                    var comps = spearPrefab.GetComponentsInChildren<Component>(true);
                    if (comps != null)
                    {
                        for (int i = 0; i < comps.Length; i++)
                        {
                            var c = comps[i];
                            if (c != null && c.GetType().Name == "CameraControlAnimationEvents")
                            {
                                UnityEngine.Object.DestroyImmediate(c, true);
                            }
                        }
                    }
                }
                catch { }

                if (bmAsm != null)
                {
                    try
                    {
                        var borrowType = bmAsm.GetType("BossKit.World.Borrow", false);
                        var wantsField = borrowType?.GetField("wants", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (wantsField?.GetValue(null) is IEnumerable wantsList)
                        {
                            foreach (var w in wantsList)
                            {
                                if (w == null) continue;
                                var wt = w.GetType();
                                var nF = wt.GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                var gF = wt.GetField("Got", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                var tF = wt.GetField("Tried", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                string wName = nF?.GetValue(w) as string;
                                if (string.Equals(wName, "Hunter_Queen_ground_spear_set", StringComparison.OrdinalIgnoreCase))
                                {
                                    gF?.SetValue(w, spearPrefab);
                                    tF?.SetValue(w, true);
                                }
                            }
                        }
                    }
                    catch (System.Exception __apiEx_40) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1456", __apiEx_40); }
                }

                var poolType = bmAsm?.GetType("BossKit.Core.Pool", false);
                var foundField = poolType?.GetField("found", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (foundField?.GetValue(null) is IDictionary dict)
                {
                    dict["Hunter_Queen_ground_spear_set"] = spearPrefab;
                }

                try
                {
                    ObjectPool.CreatePool(spearPrefab, 10);
                }
                catch (System.Exception __apiEx_41) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1470", __apiEx_41); }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] RegisterBonemasterSpearPrefab error: {ex.Message}");
            }
            finally
            {
                _inRegisterSpearPrefab = false;
            }
        }

        private sealed class BorrowWantFields
        {
            public FieldInfo Name;
            public FieldInfo Got;
            public FieldInfo Kind;
            public FieldInfo Bundle;
            public FieldInfo Tried;
            public FieldInfo Also;
        }

        private static readonly Dictionary<Type, BorrowWantFields> _borrowWantFieldsByType = new Dictionary<Type, BorrowWantFields>();

        private static BorrowWantFields GetBorrowWantFields(Type wt)
        {
            if (wt == null) return null;
            lock (_borrowWantFieldsByType)
            {
                if (_borrowWantFieldsByType.TryGetValue(wt, out var cached))
                    return cached;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var wf = new BorrowWantFields
                {
                    Name = wt.GetField("Name", flags),
                    Got = wt.GetField("Got", flags),
                    Kind = wt.GetField("Kind", flags),
                    Bundle = wt.GetField("Bundle", flags),
                    Tried = wt.GetField("Tried", flags),
                    Also = wt.GetField("Also", flags)
                };
                _borrowWantFieldsByType[wt] = wf;
                return wf;
            }
        }

        public static UnityEngine.Object BonemasterBorrowFindLoaded(object wantObj)
        {
            if (wantObj == null) return null;
            try
            {
                var wf = GetBorrowWantFields(wantObj.GetType());
                var nameField = wf?.Name;
                var kindField = wf?.Kind;
                var bundleField = wf?.Bundle;
                var triedField = wf?.Tried;

                string wantName = nameField?.GetValue(wantObj) as string ?? "";
                Type wantKind = kindField?.GetValue(wantObj) as Type ?? typeof(UnityEngine.Object);
                string wantBundle = bundleField?.GetValue(wantObj) as string ?? "";

                if (string.IsNullOrEmpty(wantName)) return null;

                // 0. Check preloaded caches in O(1) first
                if (_addressableAssetCache.TryGetValue(wantName, out var cachedAsset) && cachedAsset != null && wantKind.IsAssignableFrom(cachedAsset.GetType()))
                {
                    triedField?.SetValue(wantObj, true);
                    return cachedAsset;
                }
                if (wantKind == typeof(AudioClip) && _bmSoundCache.TryGetValue(wantName, out var cachedClip) && cachedClip != null)
                {
                    triedField?.SetValue(wantObj, true);
                    return cachedClip;
                }
                if (wantKind == typeof(GameObject) && _bmPoolCache.TryGetValue(wantName, out var cachedGo) && cachedGo != null)
                {
                    triedField?.SetValue(wantObj, true);
                    return cachedGo;
                }

                if (_bmFailedWants.Contains(wantName))
                {
                    triedField?.SetValue(wantObj, true);
                    if (wantKind == typeof(AudioClip))
                        return GetOrCreateSilentFallbackClip(wantName);
                    return null;
                }

                // 1. Try fast O(1) indexed bundle path lookup (never calls b.LoadAllAssets across all bundles!)
                var indexedObj = TryLoadFromIndexedBundles(wantName, wantKind, wantBundle);
                if (indexedObj != null)
                {
                    triedField?.SetValue(wantObj, true);
                    if (string.Equals(wantName, "Hunter_Queen_ground_spear_set", StringComparison.OrdinalIgnoreCase) && indexedObj is GameObject go1)
                    {
                        RegisterBonemasterSpearPrefab(go1);
                    }
                    return indexedObj;
                }

                // 2. Try Addressables (only if not yet marked failed)
                var addrObj = ResolveAddressableAsset(wantName, wantKind);
                if (addrObj != null)
                {
                    triedField?.SetValue(wantObj, true);
                    if (string.Equals(wantName, "Hunter_Queen_ground_spear_set", StringComparison.OrdinalIgnoreCase) && addrObj is GameObject go2)
                    {
                        RegisterBonemasterSpearPrefab(go2);
                    }
                    return addrObj;
                }

                triedField?.SetValue(wantObj, true);
                _bmFailedWants.Add(wantName);

                if (wantKind == typeof(AudioClip))
                {
                    var fallbackClip = BonemasterSoundInMemory(wantName) ?? GetOrCreateSilentFallbackClip(wantName);
                    if (fallbackClip != null)
                        return fallbackClip;
                }
            }
            catch (System.Exception __apiEx_48) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1633", __apiEx_48); }
            return null;
        }

        private static void PrecacheAtlasSprites(UnityEngine.U2D.SpriteAtlas atlas)
        {
            if (atlas == null) return;
            try
            {
                int count = atlas.spriteCount;
                if (count > 0)
                {
                    var sprites = new UnityEngine.Sprite[count];
                    int got = atlas.GetSprites(sprites);
                    for (int i = 0; i < got; i++)
                    {
                        var s = sprites[i];
                        if (s != null && !string.IsNullOrEmpty(s.name))
                        {
                            string clean = s.name.Replace("(Clone)", "");
                            _bmSpriteCache[clean] = s;
                            _bmSpriteCache[s.name] = s;
                            _bmSpriteCache["ant_traps/" + clean] = s;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_49) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1660", __apiEx_49); }
        }

        public static int BonemasterCatalogKeyIndexOf(string catalogKey, string requestedBundle, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(catalogKey) || string.IsNullOrEmpty(requestedBundle))
                return -1;

            if (catalogKey.IndexOf(requestedBundle, comparison) >= 0)
                return 0;

            if (requestedBundle.IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (catalogKey.IndexOf("ant_trap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 catalogKey.IndexOf("spriteatlas", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return 0;
            }

            if (requestedBundle.IndexOf("areacrawl", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (catalogKey.IndexOf("crypt_worm_emerge_from_ground", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 catalogKey.IndexOf("bone_hunter_throw_emerge", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return 0;
            }
            if (requestedBundle.IndexOf("areacoral", StringComparison.OrdinalIgnoreCase) >= 0 &&
                catalogKey.IndexOf("driller_boss_emerge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (requestedBundle.IndexOf("areashellwood", StringComparison.OrdinalIgnoreCase) >= 0 &&
                catalogKey.IndexOf("stick_insect_flyer_spin_attack", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (requestedBundle.IndexOf("areabone", StringComparison.OrdinalIgnoreCase) >= 0 &&
                catalogKey.IndexOf("skull_king_hard_land", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (requestedBundle.IndexOf("shared", StringComparison.OrdinalIgnoreCase) >= 0 &&
                catalogKey.IndexOf("misc_rumble_impact", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            return -1;
        }

        private static void EnsurePharloomsGloryLightingHooksInstalled()
        {
            if (_lightingHooksInstalled) return;
            _lightingHooksInstalled = true;
            try
            {
                var csmType = HarmonyLib.AccessTools.TypeByName("CustomSceneManager");
                if (csmType != null)
                {
                    var adjSatMi = csmType.GetMethod("AdjustSaturation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var postAdjSat = typeof(AssetHelperBridge).GetMethod(nameof(CustomSceneManager_AdjustSaturation_Postfix), BindingFlags.Static | BindingFlags.Public);
                    if (adjSatMi != null && postAdjSat != null)
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("PharloomsGloryLightingBridge", adjSatMi, null, postAdjSat, null);
                }

                var swrType = HarmonyLib.AccessTools.TypeByName("SurfaceWaterRegion");
                if (swrType != null)
                {
                    var swrAwakeMi = swrType.GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var preSwrAwake = typeof(AssetHelperBridge).GetMethod(nameof(SurfaceWaterRegion_Awake_SafePrefix), BindingFlags.Static | BindingFlags.Public);
                    if (swrAwakeMi != null && preSwrAwake != null)
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("PharloomsGloryWaterGuard", swrAwakeMi, preSwrAwake, null, null, HarmonyLib.Priority.First);
                }

                SceneManager.activeSceneChanged -= OnActiveSceneChanged;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Lighting hook warning: {ex.Message}");
            }
        }

        private static FieldInfo _swrDripperPrefabField;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Sprite, string> _pgOrigSpriteNameBySprite = new System.Runtime.CompilerServices.ConditionalWeakTable<Sprite, string>();
        private static readonly Dictionary<int, string> _pgOrigSpriteNameByRendererId = new Dictionary<int, string>(512);

        public static bool SurfaceWaterRegion_Awake_SafePrefix(object __instance)
        {
            if (__instance == null) return false;
            try
            {
                if (_swrDripperPrefabField == null)
                    _swrDripperPrefabField = __instance.GetType().GetField("dripperPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_swrDripperPrefabField != null)
                {
                    var dripper = _swrDripperPrefabField.GetValue(__instance) as UnityEngine.Object;
                    if (dripper == null)
                    {
                        // Cloned Hang_02 SurfaceWaterRegion has null dripperPrefab; skip Awake so it never throws NRE mid-AddClonedObjects!
                        return false;
                    }
                }
            }
            catch (System.Exception __apiEx_50) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1761", __apiEx_50); }
            return true;
        }

        public static AudioClip TryParseWavBytesToAudioClip(byte[] wavBytes, string clipName)
        {
            if (wavBytes == null || wavBytes.Length < 44) return null;
            try
            {
                int channels = BitConverter.ToInt16(wavBytes, 22);
                int sampleRate = BitConverter.ToInt32(wavBytes, 24);
                int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
                if (channels <= 0) channels = 1;
                if (sampleRate <= 0) sampleRate = 44100;
                int pos = 12;
                int dataOffset = -1;
                int dataSize = 0;
                while (pos + 8 <= wavBytes.Length)
                {
                    int chunkSize = BitConverter.ToInt32(wavBytes, pos + 4);
                    if (wavBytes[pos] == 'd' && wavBytes[pos + 1] == 'a' && wavBytes[pos + 2] == 't' && wavBytes[pos + 3] == 'a')
                    {
                        dataOffset = pos + 8;
                        dataSize = chunkSize;
                        break;
                    }
                    if (chunkSize < 0) break;
                    pos += 8 + chunkSize;
                }
                if (dataOffset < 0 || dataOffset >= wavBytes.Length)
                {
                    dataOffset = 44;
                    dataSize = wavBytes.Length - 44;
                }
                int maxLen = Math.Min(dataSize, wavBytes.Length - dataOffset);
                if (maxLen <= 0) return null;
                float[] samples;
                if (bitsPerSample == 16)
                {
                    int totalSamples = maxLen / 2;
                    samples = new float[totalSamples];
                    for (int i = 0; i < totalSamples; i++)
                        samples[i] = BitConverter.ToInt16(wavBytes, dataOffset + i * 2) / 32768f;
                }
                else if (bitsPerSample == 8)
                {
                    samples = new float[maxLen];
                    for (int i = 0; i < maxLen; i++)
                        samples[i] = (wavBytes[dataOffset + i] - 128) / 128f;
                }
                else if (bitsPerSample == 32)
                {
                    int totalSamples = maxLen / 4;
                    samples = new float[totalSamples];
                    for (int i = 0; i < totalSamples; i++)
                        samples[i] = BitConverter.ToSingle(wavBytes, dataOffset + i * 4);
                }
                else
                {
                    return null;
                }
                int lengthSamples = samples.Length / channels;
                if (lengthSamples <= 0) return null;
                var clip = AudioClip.Create(clipName ?? "ModWav", lengthSamples, channels, sampleRate, false);
                if (clip != null)
                {
                    var setDataMi = typeof(AudioClip).GetMethod(
                        "SetData",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new Type[] { typeof(float[]), typeof(int) },
                        null);
                    setDataMi?.Invoke(clip, new object[] { samples, 0 });
                }
                return clip;
            }
            catch { return null; }
        }

        private static Scene _currentPharloomsGloryModifyingScene;

        public static Scene PharloomsGloryGetActiveScene()
        {
            try
            {
                if (_currentPharloomsGloryModifyingScene.IsValid() && _currentPharloomsGloryModifyingScene.isLoaded)
                    return _currentPharloomsGloryModifyingScene;
            }
            catch (System.Exception __apiEx_51) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1849", __apiEx_51); }
            return SceneManager.GetActiveScene();
        }

        public static void PharloomsGloryBeforeSceneModify(Scene scene)
        {
            try
            {
                if (!scene.IsValid()) return;
                _currentPharloomsGloryModifyingScene = scene;
                SyncExtractedTemplatesIntoPharloomsGlory();

                if (_pharloomsGloryAssembly != null)
                {
                    var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                    var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    if (smInst != null)
                    {
                        var listField = smType.GetField("currentlyAddedGameObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (listField?.GetValue(smInst) is IList prevList && prevList.Count > 0)
                        {
                            for (int i = prevList.Count - 1; i >= 0; i--)
                            {
                                if (prevList[i] is GameObject oldGo && oldGo != null)
                                    UnityEngine.Object.Destroy(oldGo);
                            }
                            prevList.Clear();
                        }
                    }
                }

                if (scene.isLoaded)
                {
                    var roots = scene.GetRootGameObjects();
                    if (roots != null)
                    {
                        for (int r = 0; r < roots.Length; r++)
                        {
                            var root = roots[r];
                            if (root == null) continue;
                            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                            {
                                if (mb != null && mb.GetType().Name == "WindCameraRegion" &&
                                    mb.gameObject.name.IndexOf("Camera Wind Region", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    mb.StopAllCoroutines();
                                    mb.enabled = false;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_52) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1902", __apiEx_52); }
        }

        public static void PharloomsGloryAfterSceneModify(Scene scene)
        {
            try
            {
                if (_pharloomsGloryAssembly != null && scene.IsValid() && scene.isLoaded)
                {
                    var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                    var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    if (smInst != null)
                    {
                        var listField = smType.GetField("currentlyAddedGameObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (listField?.GetValue(smInst) is IList addedList)
                        {
                            for (int i = 0; i < addedList.Count; i++)
                            {
                                if (addedList[i] is GameObject go && go != null)
                                {
                                    if (!go.activeSelf) go.SetActive(true);
                                    if (go.transform.parent == null && go.scene != scene)
                                    {
                                        try { SceneManager.MoveGameObjectToScene(go, scene); } catch (System.Exception __apiEx_53) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1925", __apiEx_53); }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_54) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1933", __apiEx_54); }
            finally
            {
                _currentPharloomsGloryModifyingScene = default;
            }
        }

        private static void UnusedLegacyPharloomsGloryCache(Scene scene)
        {
            try
            {
                var roots = scene.GetRootGameObjects();
                if (roots == null) return;
                for (int i = 0; i < roots.Length; i++)
                {
                    var r = roots[i];
                    if (r == null) continue;
                    var srs = r.GetComponentsInChildren<SpriteRenderer>(true);
                    if (srs == null) continue;
                    for (int j = 0; j < srs.Length; j++)
                    {
                        var sr = srs[j];
                        if (sr == null || sr.sprite == null) continue;
                        int rid = sr.GetInstanceID();
                        if (_pgOrigSpriteNameByRendererId.TryGetValue(rid, out var existingName) && !string.IsNullOrEmpty(existingName))
                        {
                            lock (_pgOrigSpriteNameBySprite)
                            {
                                if (!_pgOrigSpriteNameBySprite.TryGetValue(sr.sprite, out _))
                                    _pgOrigSpriteNameBySprite.Add(sr.sprite, existingName);
                            }
                            continue;
                        }
                        string computed = ComputePharloomsGloryRawSpriteName(sr.sprite);
                        if (!string.IsNullOrEmpty(computed))
                        {
                            _pgOrigSpriteNameByRendererId[rid] = computed;
                            lock (_pgOrigSpriteNameBySprite)
                            {
                                if (!_pgOrigSpriteNameBySprite.TryGetValue(sr.sprite, out _))
                                    _pgOrigSpriteNameBySprite.Add(sr.sprite, computed);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_55) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1979", __apiEx_55); }
        }

        public static void PharloomsGloryRecordRendererBeforeReplace(SpriteRenderer sr)
        {
            if (sr == null || sr.sprite == null) return;
            try
            {
                int rid = sr.GetInstanceID();
                if (!_pgOrigSpriteNameByRendererId.TryGetValue(rid, out var origName) || string.IsNullOrEmpty(origName))
                {
                    origName = ComputePharloomsGloryRawSpriteName(sr.sprite);
                    if (!string.IsNullOrEmpty(origName))
                        _pgOrigSpriteNameByRendererId[rid] = origName;
                }
            }
            catch (System.Exception __apiEx_56) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L1995", __apiEx_56); }
        }

        public static void PharloomsGloryRecordRendererAfterReplace(SpriteRenderer sr)
        {
            if (sr == null || sr.sprite == null) return;
            try
            {
                int rid = sr.GetInstanceID();
                if (_pgOrigSpriteNameByRendererId.TryGetValue(rid, out var origName) && !string.IsNullOrEmpty(origName))
                {
                    lock (_pgOrigSpriteNameBySprite)
                    {
                        if (!_pgOrigSpriteNameBySprite.TryGetValue(sr.sprite, out _))
                            _pgOrigSpriteNameBySprite.Add(sr.sprite, origName);
                    }
                }
            }
            catch (System.Exception __apiEx_57) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2013", __apiEx_57); }
        }

        private static string ComputePharloomsGloryRawSpriteName(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return "";
            string texName = sprite.texture.name;
            if (string.IsNullOrEmpty(texName)) return "";
            string areaName = "";
            if (texName.Contains("Bellshrine")) areaName = "Bellshrine";
            else if (texName.Contains("Coral_Judge_Arena")) areaName = "Coral_Judge_Arena";
            else if (texName.Contains("Coral")) areaName = "Coral";
            else if (texName.Contains("Gorge")) areaName = "Gorge";
            else if (texName.Contains("Hornet_Bone")) areaName = "HBone";
            else if (texName.Contains("Bone")) areaName = "Bone";
            else if (texName.Contains("Crawl")) areaName = "Crawl";
            else if (texName.Contains(" Peak") || texName.Contains("_Peak")) areaName = "Peak";
            else if (texName.Contains("Steps")) areaName = "Steps";
            else if (texName.Contains("Tower")) areaName = "Tower";
            else if (texName.Contains("Common")) areaName = "Common";
            else if (texName.Contains("Shell")) areaName = "Shell";
            else if (texName.Contains("Greymoor")) areaName = "Greymoor";
            else if (texName.Contains("Glow")) areaName = "Glow";
            else if (texName.Contains("Fields")) areaName = "Fields";
            else if (texName.Contains("Dust")) areaName = "Dust";
            else if (texName.Contains("Song")) areaName = "Song";
            else if (texName.Contains("Hang")) areaName = "Hang";
            else if (texName.Contains("Arborium")) areaName = "Arborium";
            else if (texName.Contains("Docks")) areaName = "Docks";
            else if (texName.Contains("Weaver")) areaName = "Weaver";
            else if (texName.Contains("Surface")) areaName = "Surface";
            else if (texName.Contains("Cradle")) areaName = "Cradle";
            if (string.IsNullOrEmpty(areaName)) return "";
            Rect r = sprite.textureRect;
            return $"{areaName}-sprite-x{Mathf.RoundToInt(r.x)}y{Mathf.RoundToInt(r.y)}w{Mathf.RoundToInt(r.width)}h{Mathf.RoundToInt(r.height)}";
        }

        public static string PharloomsGloryGetSpriteName(Sprite sprite)
        {
            if (sprite == null) return "";
            try
            {
                lock (_pgOrigSpriteNameBySprite)
                {
                    if (_pgOrigSpriteNameBySprite.TryGetValue(sprite, out var cached) && !string.IsNullOrEmpty(cached))
                        return cached;
                }
                string computed = ComputePharloomsGloryRawSpriteName(sprite);
                if (!string.IsNullOrEmpty(computed))
                {
                    lock (_pgOrigSpriteNameBySprite)
                    {
                        if (!_pgOrigSpriteNameBySprite.TryGetValue(sprite, out _))
                            _pgOrigSpriteNameBySprite.Add(sprite, computed);
                    }
                    return computed;
                }
            }
            catch (System.Exception __apiEx_58) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2071", __apiEx_58); }
            return "";
        }

        public static bool IsAdditiveCoralSubSceneName(string scName)
        {
            if (string.IsNullOrEmpty(scName)) return false;
            return scName.IndexOf("_boss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_base", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_top", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_mid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_join", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   scName.IndexOf("_arrow", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsCurrentSceneInCoral(string scName)
        {
            if (string.IsNullOrEmpty(scName)) return false;
            if (IsAdditiveCoralSubSceneName(scName)) return false;
            if (scName.StartsWith("Coral_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Bellway_08", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Bellshrine_Coral", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Room_Pinstress", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (_pharloomsGloryAssembly != null)
            {
                try
                {
                    var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                    var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    var coralScenesField = smType?.GetField("CORAL_SCENES", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var coralScenes = coralScenesField?.GetValue(smInst) as string[];
                    if (coralScenes != null && Array.IndexOf(coralScenes, scName) >= 0)
                        return true;
                }
                catch (System.Exception __apiEx_59) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2110", __apiEx_59); }
            }
            return false;
        }

        public static bool IsSandsOfKarakScene(string scName)
        {
            if (string.IsNullOrEmpty(scName)) return false;
            if (string.Equals(scName, "Coral_25", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Coral_26", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Coral_27", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Coral_28", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scName, "Coral_Tower_01", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            try
            {
                var gm = GameManager.instance;
                var csm = gm != null ? gm.sm : null;
                if (csm != null && ((int)csm.mapZone == 5 || (int)csm.mapZone == 41)) // RED_CORAL_GORGE=5, CORAL_CAVERNS=41
                {
                    return true;
                }
            }
            catch (System.Exception __apiEx_60) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2135", __apiEx_60); }
            return false;
        }

        public static void SyncPharloomsGloryAudioAliases()
        {
            if (_pharloomsGloryAssembly == null) return;
            try
            {
                var amType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.AudioModifier", false);
                var amInst = amType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (amInst == null) return;

                var cfgType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Managers.ConfigurationManager", false);
                var cfgInst = cfgType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

                int blastedCfg = 1; // default CORAL_STEPS
                int sandsCfg = 2;   // default RED_CORAL_GORGE
                if (cfgInst != null)
                {
                    var bProp = cfgType.GetProperty("BlastedStepsMusic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (bProp != null) blastedCfg = Convert.ToInt32(bProp.GetValue(cfgInst, null));
                    var sProp = cfgType.GetProperty("SandsOfKarakMusic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sProp != null) sandsCfg = Convert.ToInt32(sProp.GetValue(cfgInst, null));
                }

                string blastedTrack = blastedCfg switch
                {
                    1 => "H116 Coral Steps-42 MAIN ALT",
                    2 => "Coral Ruins Main",
                    _ => string.Empty
                };
                string sandsTrack = sandsCfg switch
                {
                    1 => "H116 Coral Steps-42 MAIN ALT",
                    2 => "Coral Ruins Main",
                    _ => string.Empty
                };

                var dictField = amType.GetField("overriddenFileNames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (dictField?.GetValue(amInst) is Dictionary<string, string> dict)
                {
                    if (!string.IsNullOrEmpty(blastedTrack))
                    {
                        dict["Steps-42"] = blastedTrack;
                        dict["steps-42"] = blastedTrack;
                        dict["Steps-42 MAIN ALT"] = blastedTrack;
                        dict["H116 Coral Steps-42 MAIN ALT"] = blastedTrack;
                    }
                    if (!string.IsNullOrEmpty(sandsTrack))
                    {
                        dict["CORAL GORGE"] = sandsTrack;
                        dict["coral gorge"] = sandsTrack;
                        dict["Coral Ruins Main"] = sandsTrack;
                    }
                }
            }
            catch (System.Exception __apiEx_61) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2192", __apiEx_61); }
        }

        private static AudioSource _pgMusicSource;
        private static AudioSource _pgSubSource;
        private static AudioClip _lastAppliedPgMainClip;
        private static AudioClip _lastAppliedPgSubClip;
        private static string _lastAppliedPgScene;
        private static bool _pharloomsGloryAudioReloaded;
        private static bool _bonemasterShelfPreloaded;

        public static void EnsurePharloomsGloryDedicatedAudioSources()
        {
            if (_pgMusicSource != null) return;
            try
            {
                var go = new GameObject("SilksongMobileAPI_PG_AudioPool");
                UnityEngine.Object.DontDestroyOnLoad(go);

                _pgMusicSource = go.AddComponent<AudioSource>();
                _pgMusicSource.playOnAwake = false;
                _pgMusicSource.loop = true;
                _pgMusicSource.spatialBlend = 0f;
                _pgMusicSource.volume = 1f;

                _pgSubSource = go.AddComponent<AudioSource>();
                _pgSubSource.playOnAwake = false;
                _pgSubSource.loop = true;
                _pgSubSource.spatialBlend = 0f;
                _pgSubSource.volume = 1f;

                var audioMgr = AudioManager.Instance;
                if (audioMgr != null)
                {
                    var ms = audioMgr.MusicSources ?? audioMgr.musicSources;
                    if (ms != null && ms.Length > 0 && ms[0] != null)
                        _pgMusicSource.outputAudioMixerGroup = ms[0].outputAudioMixerGroup;
                    if (ms != null && ms.Length > 2 && ms[2] != null)
                        _pgSubSource.outputAudioMixerGroup = ms[2].outputAudioMixerGroup;
                }
            }
            catch (System.Exception __apiEx_62) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2233", __apiEx_62); }
        }

        public static void EnsurePharloomsGloryAudioReloaded()
        {
            if (_pharloomsGloryAudioReloaded || _pharloomsGloryAssembly == null) return;
            try
            {
                var amType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.AudioModifier", false);
                var amInst = amType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (amInst != null)
                {
                    var overridesField = amType.GetField("overriddenFileNames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var overridesDict = overridesField?.GetValue(amInst) as IDictionary;
                    if (overridesDict == null || overridesDict.Count == 0)
                    {
                        amType.GetMethod("LoadOverridesFromConfig", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(amInst, null);
                    }
                    var updatedField = amType.GetField("updatedClips", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var updatedDict = updatedField?.GetValue(amInst) as IDictionary;
                    if (updatedDict == null || updatedDict.Count == 0)
                    {
                        var loadClipsMi = amType.GetMethod("LoadAudioClips", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        loadClipsMi?.Invoke(amInst, null);
                    }
                    _pharloomsGloryAudioReloaded = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Verified Pharloom's Glory AudioModifier custom Coral audio clips loaded.");
                }
            }
            catch (System.Exception __apiEx_63) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2262", __apiEx_63); }
        }

        public static IEnumerator CoEnsureBonemasterShelfPreloaded(bool forcePreload = false)
        {
            if (forcePreload && _bonemasterShelfPreloaded) yield break;

            if (!forcePreload)
            {
                // Safety check: wait until gameplay is active and player is inside Ant_Queen
                for (int i = 0; i < 25; i++)
                {
                    if (HeroController.instance != null && GameManager.instance != null && !GameManager.instance.isLoading)
                        break;
                    yield return new WaitForSeconds(0.1f);
                }

                if (HeroController.instance == null) yield break;

                var curActive = SceneManager.GetActiveScene();
                if (!curActive.IsValid() ||
                    (!string.Equals(curActive.name, "Ant_Queen", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(curActive.name, "Memory_Ant_Queen", StringComparison.OrdinalIgnoreCase)))
                    yield break;
            }

            Assembly bmAsm = null;
            foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
            {
                if (a != null && a.GetName().Name == "Bonemaster")
                {
                    bmAsm = a;
                    break;
                }
            }
            if (bmAsm == null) yield break;

            EnsureBonemasterAlwaysHisEnabled();

            var shelfType = bmAsm.GetType("BossKit.World.Shelf", false);
            if (shelfType == null) yield break;

            bool isHunterReady = false;
            var readyMi = shelfType.GetMethod("Ready", BindingFlags.Static | BindingFlags.Public);
            if (readyMi != null)
            {
                try
                {
                    if ((bool)readyMi.Invoke(null, new object[] { "Bone Hunter" }))
                    {
                        _bonemasterShelfPreloaded = true;
                        isHunterReady = true;
                    }
                }
                catch (System.Exception __apiEx_64) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2315", __apiEx_64); }
            }

            var wantsField = shelfType.GetField("wants", BindingFlags.Static | BindingFlags.NonPublic);
            var wantsList = wantsField?.GetValue(null) as IList;
            if (!isHunterReady && wantsList == null) yield break;

            object wantedItem = null;
            FieldInfo gotField = null;
            if (!isHunterReady && wantsList != null)
            {
                foreach (var item in wantsList)
                {
                    if (item == null) continue;
                    var enemyField = item.GetType().GetField("Enemy", BindingFlags.Instance | BindingFlags.Public);
                    string en = enemyField?.GetValue(item) as string;
                    if (en == "Bone Hunter")
                    {
                        wantedItem = item;
                        gotField = item.GetType().GetField("Got", BindingFlags.Instance | BindingFlags.Public);
                        break;
                    }
                }
            }

            var borrowingField = shelfType.GetField("borrowing", BindingFlags.Static | BindingFlags.NonPublic);
            var holdMi = shelfType.GetMethod("Hold", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            // Candidate scenes known to contain Bone Hunter
            string[] candidateScenes = isHunterReady ? Array.Empty<string>() : new string[] { "Scenes/Ant_04", "Scenes/Bone_East_16", "Scenes/Bone_East_26" };
            foreach (var sceneKey in candidateScenes)
            {
                string bareBmScene = sceneKey.Replace("Scenes/", "");
                if (borrowingField != null)
                {
                    try { borrowingField.SetValue(null, bareBmScene); } catch (System.Exception __apiEx_65) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2346", __apiEx_65); }
                }
                if (holdMi != null)
                {
                    IEnumerator holdEnum = null;
                    try { holdEnum = holdMi.Invoke(null, new object[] { bareBmScene }) as IEnumerator; } catch { }
                    if (holdEnum != null)
                    {
                        yield return holdEnum;
                    }
                }

                SetAllRepackersExtracting(true, bareBmScene);
                AsyncOperationHandle<SceneInstance> handle = default;
                bool startedBm = false;
                try
                {
                    handle = Addressables.LoadSceneAsync((object)sceneKey, LoadSceneMode.Additive, true, 100, SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
                    startedBm = true;
                }
                catch (System.Exception __apiEx_66) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2357", __apiEx_66); }
                if (!startedBm)
                {
                    SetAllRepackersExtracting(false, string.Empty);
                    continue;
                }

                int bmWait = 0;
                while (!handle.IsDone && bmWait++ < 600)
                {
                    SetAllRepackersExtracting(true, bareBmScene);
                    yield return null;
                }
                if (!handle.IsValid() || handle.Status != AsyncOperationStatus.Succeeded)
                {
                    SetAllRepackersExtracting(false, string.Empty);
                    continue;
                }

                Scene loadedScene = handle.Result.Scene;
                if (loadedScene.IsValid() && loadedScene.isLoaded)
                {
                    StorePinnedHandleInAllRepackers(bareBmScene.ToLowerInvariant() + "_sceneHandle", handle);
                    GameObject foundHunter = null;
                    var roots = loadedScene.GetRootGameObjects();
                    foreach (var root in roots)
                    {
                        if (root == null) continue;
                        var hms = root.GetComponentsInChildren<HealthManager>(true);
                        foreach (var hm in hms)
                        {
                            if (hm != null && hm.gameObject != null && hm.gameObject.name == "Bone Hunter")
                            {
                                foundHunter = hm.gameObject;
                                break;
                            }
                        }
                        if (foundHunter != null) break;
                    }

                    if (foundHunter != null)
                    {
                        PinAllAssetsOnHierarchy(foundHunter);
                        var shelfRootProp = shelfType.GetProperty("ShelfRoot", BindingFlags.Static | BindingFlags.NonPublic);
                        var shelfTransform = shelfRootProp?.GetValue(null, null) as Transform;
                        if (shelfTransform != null && shelfTransform.gameObject.activeSelf)
                            shelfTransform.gameObject.SetActive(false);

                        var clone = UnityEngine.Object.Instantiate(foundHunter, shelfTransform);
                        clone.name = "Bone Hunter";
                        // Match BossKit.World.Shelf.Take (rva=0x71d8): keep clone.activeSelf == true while parented under
                        // inactive ShelfRoot ("Bonemaster Shelf") so Body.Spawn -> Shelf.Copy spawns an active boss!
                        clone.SetActive(true);
                        RestoreTemplateComponentsEnabled(clone);
                        PinAllAssetsOnHierarchy(clone);

                        if (gotField != null && wantedItem != null)
                        {
                            gotField.SetValue(wantedItem, clone);
                        }
                        _bonemasterShelfPreloaded = true;
                        PrewarmBonemasterGlobalCaches(forceRefreshArrays: true);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Successfully cloned 'Bone Hunter' from {sceneKey} onto Bonemaster Shelf!");
                    }

                    foreach (var root in roots)
                    {
                        if (root != null && root.activeSelf) root.SetActive(false);
                    }
                    try
                    {
                        CleanupStrayBoneThumperClones();
                    }
                    catch (System.Exception __apiEx_67) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2425", __apiEx_67); }
                }

                if (borrowingField != null)
                {
                    try { borrowingField.SetValue(null, null); } catch (System.Exception __apiEx_68) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2430", __apiEx_68); }
                }

                AsyncOperation unloadBmOp = null;
                try
                {
                    if (loadedScene.IsValid())
                        unloadBmOp = SceneManager.UnloadSceneAsync(loadedScene);
                }
                catch (System.Exception __apiEx_69) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2439", __apiEx_69); }
                if (unloadBmOp != null)
                {
                    int uw = 0;
                    while (!unloadBmOp.isDone && uw++ < 200)
                    {
                        SetAllRepackersExtracting(true, bareBmScene);
                        yield return null;
                    }
                }
                SetAllRepackersExtracting(false, string.Empty);
                if (_bonemasterShelfPreloaded) break;
            }

            if (forcePreload)
            {
                yield return CoOpenAndPreloadAllBossKitBorrowWants(bmAsm);
            }

            EnsureBonemasterAlwaysHisEnabled();

            // Give Bonemaster's own Corpse.Lay(scene) coroutine 2 frames to run first in Ant_Queen, then verify corpse & entry exist
            if (!forcePreload)
            {
                yield return null;
                yield return null;
            }
            try
            {
                var sceneNow = SceneManager.GetActiveScene();
                if (sceneNow.IsValid() && string.Equals(sceneNow.name, "Ant_Queen", StringComparison.OrdinalIgnoreCase))
                {
                    var corpseType = bmAsm.GetType("Bonemaster.World.Corpse", false);
                    if (GameObject.Find("Bonemaster Corpse") == null)
                    {
                        var bodyMi = corpseType?.GetMethod("Body", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (bodyMi != null)
                        {
                            bodyMi.Invoke(null, new object[] { sceneNow });
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Spawned Bonemaster corpse in Ant_Queen.");
                        }
                    }
                    if (GameObject.Find("Bonemaster Entry") == null && corpseType != null)
                    {
                        var findMi = corpseType.GetMethod("Find", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        var entryMi = corpseType.GetMethod("Entry", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var hereObj = findMi?.Invoke(null, new object[] { sceneNow, "Memory Group/here" }) as GameObject;
                        if (hereObj != null && entryMi != null)
                        {
                            var corpseInst = Activator.CreateInstance(corpseType);
                            entryMi.Invoke(corpseInst, new object[] { sceneNow, hereObj });
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Spawned Bonemaster memory entry in Ant_Queen.");
                        }
                        var msgType = bmAsm.GetType("Bonemaster.World.Message", false);
                        var layMsgMi = msgType?.GetMethod("Lay", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (GameObject.Find("Bonemaster Message") == null && layMsgMi != null)
                        {
                            layMsgMi.Invoke(null, new object[] { sceneNow, 35f, 20f });
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_70) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2470", __apiEx_70); }
        }

        public static void EnsureBonemasterAlwaysHisEnabled()
        {
            try
            {
                Assembly bmAsm = null;
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (a != null && a.GetName().Name == "Bonemaster")
                    {
                        bmAsm = a;
                        break;
                    }
                }

                if (bmAsm == null) return;

                var settingsType = bmAsm.GetType("Bonemaster.Settings", false);
                foreach (string flagField in new[] { "AlwaysHis", "Abandoned" })
                {
                    var f = settingsType?.GetField(flagField, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var entry = f?.GetValue(null);
                    if (entry != null)
                    {
                        var valProp = entry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                        valProp?.SetValue(entry, true, null);
                    }
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Enabled Bonemaster AlwaysHis & Abandoned modes.");

                // Enforce mobile-friendly graphics: disable SharpDrawings and set Shade to Stock
                var sharpField = settingsType?.GetField("Sharp", BindingFlags.Static | BindingFlags.Public);
                var sharpEntry = sharpField?.GetValue(null);
                if (sharpEntry != null)
                {
                    var valProp = sharpEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    valProp?.SetValue(sharpEntry, false, null);
                }

                var shadeField = settingsType?.GetField("Shade", BindingFlags.Static | BindingFlags.Public);
                var shadeEntry = shadeField?.GetValue(null);
                if (shadeEntry != null)
                {
                    var valProp = shadeEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    valProp?.SetValue(shadeEntry, "Stock", null);
                }
            }
            catch (System.Exception __apiEx_71) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2519", __apiEx_71); }
        }

        public static IEnumerator CoHoldSceneDependencyBundles(string bareSceneName)
        {
            if (string.IsNullOrEmpty(bareSceneName) || !_heldSceneDepNames.Add(bareSceneName))
                yield break;

            AsyncOperationHandle<IList<IResourceLocation>> find = default;
            bool startedFind = false;
            try
            {
                find = Addressables.LoadResourceLocationsAsync((object)("Scenes/" + bareSceneName), (Type)null);
                startedFind = true;
            }
            catch { }
            if (!startedFind) yield break;

            int w = 0;
            while (!find.IsDone && w++ < 300)
                yield return null;

            if (!find.IsValid() || find.Status != AsyncOperationStatus.Succeeded || find.Result == null || find.Result.Count == 0 || !find.Result[0].HasDependencies)
                yield break;

            AsyncOperationHandle<IList<IAssetBundleResource>> files = default;
            bool startedFiles = false;
            try
            {
                files = Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(find.Result[0].Dependencies, null);
                startedFiles = true;
            }
            catch { }
            if (!startedFiles) yield break;

            w = 0;
            while (!files.IsDone && w++ < 300)
                yield return null;

            if (files.IsValid() && files.Status == AsyncOperationStatus.Succeeded && files.Result != null)
            {
                _pinnedBonemasterBundleHandles.Add(files);
                foreach (var res in files.Result)
                {
                    var ab = res != null ? res.GetAssetBundle() : null;
                    if (ab != null)
                    {
                        IndexAssetBundlePaths(ab);
                    }
                }
            }
        }

        private static bool _bonemasterBorrowPreloaded;
        public static IEnumerator CoOpenAndPreloadAllBossKitBorrowWants(Assembly bmAsm)
        {
            if (_bonemasterBorrowPreloaded || bmAsm == null) yield break;
            _bonemasterBorrowPreloaded = true;

            ModPreloaderScreen.StatusText = "Preloading BossKit Arena & Attack Bundles...";
            ModPreloaderScreen.Progress = Mathf.Max(ModPreloaderScreen.Progress, 0.72f);

            // 1. Hold scene dependency bundles for Ant_04 and Memory_Ant_Queen so all Karmelita & Ant spears/sickles/voices stay open
            yield return CoHoldSceneDependencyBundles("Ant_04");
            yield return CoHoldSceneDependencyBundles("Memory_Ant_Queen");

            var borrowType = bmAsm.GetType("BossKit.World.Borrow", false);
            var wantsField = borrowType?.GetField("wants", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var openedField = borrowType?.GetField("opened", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var openedNamesField = borrowType?.GetField("openedNames", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var wantsList = wantsField?.GetValue(null) as IEnumerable;
            var borrowOpenedList = openedField?.GetValue(null) as IList<AssetBundle>;
            var borrowOpenedNames = openedNamesField?.GetValue(null) as HashSet<string>;

            // 2. Collect all requested bundle names from Borrow.wants plus BossKit.libraries.json core bundles
            HashSet<string> requestedBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "localpoolprefabs_assets_areaantareamemory",
                "animations_assets_areaantareamemory",
                "textures_assets_areaantareamemory",
                "tk2danimations_assets_areaantareamemory",
                "tk2dcollections_assets_areaantareamemory",
                "localpoolprefabs_assets_areaant",
                "tk2danimations_assets_areaant",
                "tk2dcollections_assets_areaant",
                "localpoolprefabs_assets_vampiregnat",
                "tk2danimations_assets_vampiregnat",
                "tk2dcollections_assets_vampiregnat",
                "ant_traps.spriteatlas",
                "spriteatlases_assets_all",
                "textures_assets_areaant",
                "textures_assets_all",
                "herodynamic_assets_all",
                "herocollections_assets_crestbeast",
                "herocollections_assets_shared",
                "herocollections_assets_crestreaper",
                "audiocuesstatic_assets_areaant",
                "sfxstatic_assets_areaant",
                "sfxstatic_assets_areaantareamemory",
                "sfxstatic_assets_areabone",
                "sfxstatic_assets_areaboneareamossareaslabareawilds",
                "sfxstatic_assets_areacrawl",
                "sfxstatic_assets_areacoral",
                "sfxstatic_assets_areashellwood",
                "sfxstatic_assets_enemyheavy",
                "sfxstatic_assets_mapper",
                "sfxstatic_assets_shared",
                "sfxstatic_assets_boss",
                "herosfxstatic_assets_all",
                "sharedeffects_assets_all"
            };

            if (wantsList != null)
            {
                foreach (var w in wantsList)
                {
                    if (w == null) continue;
                    var wf = GetBorrowWantFields(w.GetType());
                    string b = wf?.Bundle?.GetValue(w) as string;
                    if (!string.IsNullOrEmpty(b)) requestedBundles.Add(b);
                    if (wf?.Also?.GetValue(w) is string[] alsoArr)
                    {
                        for (int i = 0; i < alsoArr.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(alsoArr[i])) requestedBundles.Add(alsoArr[i]);
                        }
                    }
                }
            }

            // 3. Locate and open all matching IAssetBundleResource locations in Addressables in one batch
            List<IResourceLocation> bundleLocations = new List<IResourceLocation>();
            try
            {
                foreach (var locator in Addressables.ResourceLocators)
                {
                    if (locator == null || locator.Keys == null) continue;
                    foreach (var keyObj in locator.Keys)
                    {
                        if (!(keyObj is string catalogKey) || string.IsNullOrEmpty(catalogKey)) continue;
                        bool match = false;
                        foreach (var reqBundle in requestedBundles)
                        {
                            string cleanReq = reqBundle.EndsWith("_", StringComparison.Ordinal) ? reqBundle.TrimEnd('_') : reqBundle;
                            if (BonemasterCatalogKeyIndexOf(catalogKey, cleanReq, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                match = true;
                                break;
                            }
                        }
                        if (!match) continue;
                        if (locator.Locate(catalogKey, null, out var locs) && locs != null)
                        {
                            for (int i = 0; i < locs.Count; i++)
                            {
                                var loc = locs[i];
                                if (loc == null) continue;
                                if (loc.ResourceType == typeof(IAssetBundleResource) ||
                                    (loc.ResourceType != null && typeof(IAssetBundleResource).IsAssignableFrom(loc.ResourceType)))
                                {
                                    if (!bundleLocations.Contains(loc))
                                        bundleLocations.Add(loc);
                                }
                                if (loc.HasDependencies && loc.Dependencies != null)
                                {
                                    for (int d = 0; d < loc.Dependencies.Count; d++)
                                    {
                                        var dep = loc.Dependencies[d];
                                        if (dep != null &&
                                            (dep.ResourceType == typeof(IAssetBundleResource) ||
                                             (dep.ResourceType != null && typeof(IAssetBundleResource).IsAssignableFrom(dep.ResourceType))) &&
                                            !bundleLocations.Contains(dep))
                                        {
                                            bundleLocations.Add(dep);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Catalog scan warning: {ex.Message}");
            }

            if (bundleLocations.Count > 0)
            {
                AsyncOperationHandle<IList<IAssetBundleResource>> openHandle = default;
                bool startedOpen = false;
                try
                {
                    openHandle = Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(bundleLocations, null);
                    startedOpen = true;
                }
                catch { }
                if (startedOpen)
                {
                    int w = 0;
                    while (!openHandle.IsDone && w++ < 600)
                        yield return null;

                    if (openHandle.IsValid() && openHandle.Status == AsyncOperationStatus.Succeeded && openHandle.Result != null)
                    {
                        _pinnedBonemasterBundleHandles.Add(openHandle);
                        foreach (var item in openHandle.Result)
                        {
                            var ab = item != null ? item.GetAssetBundle() : null;
                            if (ab != null)
                            {
                                IndexAssetBundlePaths(ab);
                                if (borrowOpenedList != null && !borrowOpenedList.Contains(ab))
                                    borrowOpenedList.Add(ab);
                            }
                        }
                    }
                }
            }

            if (borrowOpenedNames != null)
            {
                foreach (var rb in requestedBundles)
                    borrowOpenedNames.Add(rb);
            }

            // 4. Refresh global caches now that all bundles are open in memory
            PrewarmBonemasterGlobalCaches(forceRefreshArrays: true);
            yield return null;

            // 5. Resolve and populate every Want in Borrow.wants at the loading screen
            int resolvedWants = 0;
            int totalWants = 0;
            if (wantsList != null)
            {
                int step = 0;
                foreach (var w in wantsList)
                {
                    if (w == null) continue;
                    totalWants++;
                    var wf = GetBorrowWantFields(w.GetType());
                    var nF = wf?.Name;
                    var gF = wf?.Got;
                    var kF = wf?.Kind;
                    var bF = wf?.Bundle;
                    var tF = wf?.Tried;

                    string wName = nF?.GetValue(w) as string;
                    Type wKind = kF?.GetValue(w) as Type ?? typeof(UnityEngine.Object);
                    string wBundle = bF?.GetValue(w) as string;

                    var existing = gF?.GetValue(w) as UnityEngine.Object;
                    if (existing == null && !string.IsNullOrEmpty(wName))
                    {
                        var res = TryLoadFromIndexedBundles(wName, wKind, wBundle)
                               ?? ResolveAddressableAsset(wName, wKind);
                        if (res == null && wKind == typeof(AudioClip))
                        {
                            if (_bmSoundCache.TryGetValue(wName, out var sc) && sc != null)
                                res = sc;
                            else
                                res = GetOrCreateSilentFallbackClip(wName);
                        }
                        if (res != null)
                        {
                            try { res.hideFlags |= (HideFlags)0x20; } catch { }
                            gF?.SetValue(w, res);
                            resolvedWants++;
                            if (string.Equals(wName, "Hunter_Queen_ground_spear_set", StringComparison.OrdinalIgnoreCase) && res is GameObject spearGo)
                            {
                                RegisterBonemasterSpearPrefab(spearGo);
                            }
                        }
                        else
                        {
                            _bmFailedWants.Add(wName);
                        }
                    }
                    else if (existing != null)
                    {
                        resolvedWants++;
                    }
                    tF?.SetValue(w, true);

                    if (++step % 8 == 0)
                        yield return null;
                }
            }

            // 6. Pre-warm key tk2dSpriteAnimation libraries ("Hornet CrestWeapon Warrior Anim", "Tools Anim", "Hunter Queen Anim", "Knight")
            foreach (string libName in new[] { "Hornet CrestWeapon Warrior Anim", "Tools Anim", "Hunter Queen Anim", "Knight", "Bone Hunter Child Anim" })
            {
                try { BonemasterLibraryGet(libName); } catch { }
            }

            // 7. Pre-build BossKit.Fight.Prop.Blueprint for Bonemaster.Boss.Bones.Spear at the loading screen
            try
            {
                var bonesType = bmAsm.GetType("Bonemaster.Boss.Bones", false);
                var spearField = bonesType?.GetField("Spear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var spearSpec = spearField?.GetValue(null);
                var propType = bmAsm.GetType("BossKit.Fight.Prop", false);
                var bpMi = propType?.GetMethod("Blueprint", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (spearSpec != null && bpMi != null)
                {
                    object[] bpArgs = new object[] { spearSpec, null, 0f, "" };
                    bool ok = (bool)bpMi.Invoke(null, bpArgs);
                    if (ok && bpArgs[1] is GameObject bpGo && bpGo != null)
                    {
                        // Warm up Animator graph at loading screen so first spear attack has zero shader/playable-graph stall
                        var warmGo = UnityEngine.Object.Instantiate(bpGo);
                        warmGo.SetActive(true);
                        var anims = warmGo.GetComponentsInChildren<Animator>(true);
                        if (anims != null)
                        {
                            for (int i = 0; i < anims.Length; i++)
                            {
                                if (anims[i] != null)
                                {
                                    anims[i].Rebind();
                                    anims[i].Update(0f);
                                }
                            }
                        }
                        warmGo.SetActive(false);
                        UnityEngine.Object.Destroy(warmGo);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Spear Blueprint prewarm note: {ex.Message}");
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Preloaded {resolvedWants}/{totalWants} BossKit Borrow wants & {_bmBundleAssetPathIndex.Count} bundle asset keys at loading screen.");
        }

        private static bool _thirdAcolyteShelfPreloading;

        public static IEnumerator CoEnsureThirdAcolyteShelfPreloaded()
        {
            if (SilksongMobileAPIPlugin.CfgEnableThirdAcolyteBridge != null && !SilksongMobileAPIPlugin.CfgEnableThirdAcolyteBridge.Value)
                yield break;
            if (_thirdAcolyteShelfPreloading) yield break;
            _thirdAcolyteShelfPreloading = true;

            Assembly taAsm = null;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a != null && string.Equals(a.GetName().Name, "ThirdAcolyte", StringComparison.OrdinalIgnoreCase))
                    {
                        taAsm = a;
                        break;
                    }
                }
            }
            catch { }
            if (taAsm == null)
            {
                _thirdAcolyteShelfPreloading = false;
                yield break;
            }

            try
            {
                var shelfType = taAsm.GetType("ThirdAcolyte.World.Shelf", false);
                bool alreadyReady = false;
                if (shelfType != null)
                {
                    var readyProp = shelfType.GetProperty("Ready", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (readyProp != null && readyProp.GetValue(null) is bool bReady)
                        alreadyReady = bReady;
                }

                if (!alreadyReady && shelfType != null)
                {
                    ModPreloaderScreen.StatusText = "Preloading Third Acolyte (Song Reed Grand)...";
                    string[] candidateRooms = new[] { "Hang_07", "Song_25" };
                    for (int ri = 0; ri < candidateRooms.Length && !alreadyReady; ri++)
                    {
                        string room = candidateRooms[ri];
                        string sceneKey = "Scenes/" + room;

                        // Hold room dependencies open so Grand Reed keeps its effect animations
                        AsyncOperationHandle<IList<IResourceLocation>> depFind = default;
                        bool depFindOk = false;
                        try
                        {
                            depFind = Addressables.LoadResourceLocationsAsync(sceneKey, null);
                            depFindOk = depFind.IsValid();
                        }
                        catch { }
                        if (depFindOk)
                        {
                            yield return depFind;
                            IList<IResourceLocation> deps = null;
                            if (depFind.Status == AsyncOperationStatus.Succeeded && depFind.Result != null && depFind.Result.Count > 0 && depFind.Result[0].HasDependencies)
                                deps = depFind.Result[0].Dependencies;

                            if (deps != null)
                            {
                                AsyncOperationHandle<IList<IAssetBundleResource>> depFiles = default;
                                bool depFilesOk = false;
                                try
                                {
                                    depFiles = Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(deps, null);
                                    depFilesOk = depFiles.IsValid();
                                }
                                catch { }
                                if (depFilesOk)
                                {
                                    yield return depFiles;
                                    if (depFiles.Status == AsyncOperationStatus.Succeeded)
                                    {
                                        _pinnedBonemasterBundleHandles.Add(depFiles);
                                        try
                                        {
                                            var heldFi = shelfType.GetField("held", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                                            if (heldFi?.GetValue(null) is HashSet<string> heldSet)
                                                heldSet.Add(room);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            try { if (depFind.IsValid()) Addressables.Release(depFind); } catch { }
                        }

                        // Load candidate scene additively, clone Song Reed Grand onto inactive ThirdAcolyte Shelf, then unload
                        SetAllRepackersExtracting(true, room);
                        try
                        {
                            AsyncOperationHandle<SceneInstance> loadHandle = default;
                            bool startedLoad = false;
                            try
                            {
                                loadHandle = Addressables.LoadSceneAsync(sceneKey, LoadSceneMode.Additive, true, 100);
                                startedLoad = loadHandle.IsValid();
                            }
                            catch { }

                            if (!startedLoad) continue;
                            yield return loadHandle;

                            if (loadHandle.Status == AsyncOperationStatus.Succeeded)
                            {
                                var loadedScene = loadHandle.Result.Scene;
                                if (loadedScene.IsValid() && loadedScene.isLoaded)
                                {
                                    try
                                    {
                                        var roots = loadedScene.GetRootGameObjects();
                                        GameObject foundReed = null;
                                        for (int r = 0; r < roots.Length && foundReed == null; r++)
                                        {
                                            if (roots[r] == null) continue;
                                            var hms = roots[r].GetComponentsInChildren<HealthManager>(true);
                                            if (hms == null) continue;
                                            for (int hi = 0; hi < hms.Length; hi++)
                                            {
                                                if (hms[hi] != null && hms[hi].gameObject != null &&
                                                    string.Equals(hms[hi].gameObject.name, "Song Reed Grand", StringComparison.Ordinal))
                                                {
                                                    foundReed = hms[hi].gameObject;
                                                    break;
                                                }
                                            }
                                        }

                                        if (foundReed != null)
                                        {
                                            Transform shelfRoot = null;
                                            var rootProp = shelfType.GetProperty("ShelfRoot", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                                            if (rootProp != null) shelfRoot = rootProp.GetValue(null) as Transform;
                                            if (shelfRoot == null)
                                            {
                                                var go = new GameObject("ThirdAcolyte Shelf");
                                                go.SetActive(false);
                                                UnityEngine.Object.DontDestroyOnLoad(go);
                                                shelfRoot = go.transform;
                                            }
                                            else if (shelfRoot.gameObject.activeSelf)
                                            {
                                                shelfRoot.gameObject.SetActive(false);
                                            }

                                            PinAllAssetsOnHierarchy(foundReed);
                                            var clone = UnityEngine.Object.Instantiate(foundReed, shelfRoot);
                                            clone.name = "Song Reed Grand";
                                            clone.SetActive(true);
                                            RestoreTemplateComponentsEnabled(clone);
                                            PinAllAssetsOnHierarchy(clone);

                                            var grandReedProp = shelfType.GetProperty("GrandReed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (grandReedProp != null && grandReedProp.GetSetMethod(true) != null)
                                                grandReedProp.GetSetMethod(true).Invoke(null, new object[] { clone });
                                            else
                                                shelfType.GetField("<GrandReed>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, clone);

                                            alreadyReady = true;
                                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ThirdAcolyteBridge] Cloned 'Song Reed Grand' from {sceneKey} onto ThirdAcolyte Shelf!");
                                        }

                                        for (int r = 0; r < roots.Length; r++)
                                        {
                                            if (roots[r] != null) roots[r].SetActive(false);
                                        }
                                    }
                                    catch (Exception exClone)
                                    {
                                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ThirdAcolyteBridge] Shelf clone warning ({sceneKey}): {exClone.Message}");
                                    }
                                }

                                yield return null;
                                AsyncOperationHandle<SceneInstance> unloadHandle = default;
                                bool startedUnload = false;
                                try
                                {
                                    unloadHandle = Addressables.UnloadSceneAsync(loadHandle, true);
                                    startedUnload = unloadHandle.IsValid();
                                }
                                catch { }
                                if (startedUnload) yield return unloadHandle;
                            }
                        }
                        finally
                        {
                            SetAllRepackersExtracting(false, string.Empty);
                        }
                    }
                }

                yield return CoOpenAndPreloadAllThirdAcolyteBorrowWants(taAsm);
            }
            finally
            {
                _thirdAcolyteShelfPreloading = false;
            }
        }

        private static IEnumerator CoOpenAndPreloadAllThirdAcolyteBorrowWants(Assembly taAsm)
        {
            if (taAsm == null) yield break;
            ModPreloaderScreen.StatusText = "Preloading Third Acolyte Asset Bundles...";

            var borrowType = taAsm.GetType("ThirdAcolyte.World.Borrow", false);
            if (borrowType == null) yield break;

            var wantsList = borrowType.GetField("wants", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as System.Collections.IList;
            var openedList = borrowType.GetField("opened", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as IList<UnityEngine.AssetBundle>;

            var requestedBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "herodynamic_assets_all",
                "herocollections_assets_shared",
                "sharedeffects_assets_all",
                "tk2danimations_assets_areasong",
                "tk2dcollections_assets_areasong"
            };

            if (wantsList != null)
            {
                for (int i = 0; i < wantsList.Count; i++)
                {
                    var w = wantsList[i];
                    if (w == null) continue;
                    var wf = GetBorrowWantFields(w.GetType());
                    string b = wf?.Bundle?.GetValue(w) as string;
                    if (!string.IsNullOrEmpty(b)) requestedBundles.Add(b.TrimEnd('_'));
                    if (wf?.Also?.GetValue(w) is string[] alsoArr)
                    {
                        for (int ai = 0; ai < alsoArr.Length; ai++)
                            if (!string.IsNullOrEmpty(alsoArr[ai])) requestedBundles.Add(alsoArr[ai].TrimEnd('_'));
                    }
                }
            }

            var toOpenLocs = new List<IResourceLocation>();
            try
            {
                foreach (var locator in Addressables.ResourceLocators)
                {
                    if (locator == null) continue;
                    foreach (var keyObj in locator.Keys)
                    {
                        if (!(keyObj is string catalogKey)) continue;
                        bool matched = false;
                        foreach (var req in requestedBundles)
                        {
                            if (BonemasterCatalogKeyIndexOf(catalogKey, req, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matched = true;
                                break;
                            }
                        }
                        if (!matched) continue;
                        if (locator.Locate(catalogKey, typeof(IAssetBundleResource), out var locs) && locs != null)
                        {
                            for (int li = 0; li < locs.Count; li++)
                                if (locs[li] != null && !toOpenLocs.Contains(locs[li])) toOpenLocs.Add(locs[li]);
                        }
                    }
                }
            }
            catch { }

            if (toOpenLocs.Count > 0)
            {
                AsyncOperationHandle<IList<IAssetBundleResource>> openHandle = default;
                bool openStarted = false;
                try
                {
                    openHandle = Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(toOpenLocs, null);
                    openStarted = openHandle.IsValid();
                }
                catch { }
                if (openStarted)
                {
                    yield return openHandle;
                    if (openHandle.Status == AsyncOperationStatus.Succeeded && openHandle.Result != null)
                    {
                        _pinnedBonemasterBundleHandles.Add(openHandle);
                        foreach (var res in openHandle.Result)
                        {
                            var ab = res?.GetAssetBundle();
                            if (ab != null && !ab.isStreamedSceneAssetBundle)
                            {
                                IndexAssetBundlePaths(ab);
                                if (openedList != null && !openedList.Contains(ab)) openedList.Add(ab);
                            }
                        }
                    }
                }
            }

            PrewarmBonemasterGlobalCaches(forceRefreshArrays: true);

            int totalWants = wantsList != null ? wantsList.Count : 0;
            int resolvedWants = 0;
            if (wantsList != null)
            {
                for (int i = 0; i < wantsList.Count; i++)
                {
                    var w = wantsList[i];
                    if (w == null) continue;
                    try
                    {
                        var wf = GetBorrowWantFields(w.GetType());
                        var existing = wf?.Got?.GetValue(w) as UnityEngine.Object;
                        if (existing == null)
                        {
                            var got = BonemasterBorrowFindLoaded(w);
                            if (got != null) wf?.Got?.SetValue(w, got);
                            existing = got;
                        }
                        wf?.Tried?.SetValue(w, true);
                        if (existing != null) resolvedWants++;
                    }
                    catch { }
                }
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ThirdAcolyteBridge] Preloaded {resolvedWants}/{totalWants} ThirdAcolyte Borrow wants & {toOpenLocs.Count} Android bundle(s).");
        }

        private static IEnumerator CoEnsureBonemasterArenaActive()
        {
            ResetBonemasterRuntimeCaches();
            // Give scene transition and ResidentRoom.Stage a moment to complete
            yield return null;
            yield return null;

            for (int retry = 0; retry < 25; retry++)
            {
                var curScene = SceneManager.GetActiveScene();
                if (!curScene.IsValid() || !string.Equals(curScene.name, "Memory_Ant_Queen", StringComparison.OrdinalIgnoreCase))
                    yield break;

                // 1. Ensure Bone Hunter is preloaded onto shelf (already done at Menu_Title)
                if (!_bonemasterShelfPreloaded)
                {
                    yield return CoEnsureBonemasterShelfPreloaded(forcePreload: true);
                }

                Assembly bmAsm = null;
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (a != null && a.GetName().Name == "Bonemaster")
                    {
                        bmAsm = a;
                        break;
                    }
                }

                // Fallback: Only if after 6 retries (1.2s) Bonemaster is still not in the scene at all, trigger ResidentRoom.Stage once
                if (retry == 6 && bmAsm != null && GameObject.Find("Arena Bonemaster") == null && GameObject.Find("Bonemaster") == null)
                {
                    try
                    {
                        var rrType = bmAsm.GetType("BossKit.World.ResidentRoom", false);
                        var roomsField = rrType?.GetField("rooms", BindingFlags.Static | BindingFlags.NonPublic);
                        if (roomsField?.GetValue(null) is IList roomList)
                        {
                            foreach (var roomObj in roomList)
                            {
                                if (roomObj == null) continue;
                                var rField = roomObj.GetType().GetField("Room", BindingFlags.Instance | BindingFlags.Public);
                                string rName = rField?.GetValue(roomObj) as string;
                                if (string.Equals(rName, curScene.name, StringComparison.OrdinalIgnoreCase))
                                {
                                    var stageMi = roomObj.GetType().GetMethod("Stage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    stageMi?.Invoke(roomObj, new object[] { curScene });
                                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Fallback: Invoked ResidentRoom.Stage for '{curScene.name}'.");

                                    var vanilla = GameObject.Find("Boss Scene/Hunter Queen Boss") ?? GameObject.Find("Hunter Queen Boss");
                                    if (vanilla != null && vanilla.activeSelf)
                                    {
                                        vanilla.SetActive(false);
                                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Deactivated vanilla Hunter Queen Boss in favor of Bonemaster.");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Fallback Stage exception: {ex.Message}");
                    }
                }

                // 2. Locate Bonemaster GameObject in arena
                GameObject bonemasterObj = null;
                Transform arenaWave = null;
                var roots = curScene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    var tArena = r.transform.Find("Arena Bonemaster");
                    if (tArena != null)
                    {
                        var wave = tArena.Find("Wave") ?? tArena;
                        arenaWave = wave;
                        var bm = wave.Find("Bonemaster");
                        if (bm != null) bonemasterObj = bm.gameObject;
                    }
                    else if (r.name == "Arena Bonemaster")
                    {
                        var wave = r.transform.Find("Wave") ?? r.transform;
                        arenaWave = wave;
                        var bm = wave.Find("Bonemaster");
                        if (bm != null) bonemasterObj = bm.gameObject;
                    }

                    if (bonemasterObj == null)
                    {
                        var hms = r.GetComponentsInChildren<HealthManager>(true);
                        foreach (var hm in hms)
                        {
                            if (hm != null && hm.gameObject != null && hm.gameObject.name == "Bonemaster")
                            {
                                bonemasterObj = hm.gameObject;
                                arenaWave = hm.gameObject.transform.parent;
                                break;
                            }
                        }
                    }
                    if (bonemasterObj != null) break;
                }

                if (bonemasterObj != null)
                {
                    // Ensure arena wave hierarchy is active
                    if (arenaWave != null && !arenaWave.gameObject.activeSelf)
                    {
                        arenaWave.gameObject.SetActive(true);
                    }

                    // Activate Bonemaster GameObject
                    if (!bonemasterObj.activeSelf)
                    {
                        bonemasterObj.SetActive(true);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Activated Bonemaster GameObject in arena!");
                    }

                    // Ensure vanilla boss is deactivated
                    var vanillaBoss = GameObject.Find("Boss Scene/Hunter Queen Boss") ?? GameObject.Find("Hunter Queen Boss");
                    if (vanillaBoss != null && vanillaBoss.activeSelf)
                    {
                        vanillaBoss.SetActive(false);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Deactivated vanilla Hunter Queen Boss in favor of Bonemaster.");
                    }

                    // Verify and awaken PlayMakerFSM
                    var fsms = bonemasterObj.GetComponents<PlayMakerFSM>();
                    PlayMakerFSM controlFsm = null;
                    foreach (var fsm in fsms)
                    {
                        if (fsm != null && fsm.FsmName == "Control")
                        {
                            controlFsm = fsm;
                            break;
                        }
                    }

                    if (controlFsm != null)
                    {
                        if (!controlFsm.enabled) controlFsm.enabled = true;

                        // Verify Brain.Build has been executed
                        if (controlFsm.Fsm.GetState("Stun Start") == null)
                        {
                            try
                            {
                                var brainType = bmAsm?.GetType("Bonemaster.Boss.Brain", false);
                                var buildMi = brainType?.GetMethod("Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                if (buildMi != null)
                                {
                                    buildMi.Invoke(null, new object[] { controlFsm });
                                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Triggered Brain.Build on Bonemaster Control FSM!");
                                }
                            }
                            catch (Exception ex)
                            {
                                var innerEx = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Error executing Brain.Build: {innerEx.GetType().Name}: {innerEx.Message}");
                            }
                        }

                        // Clean up any remaining PC components on Bonemaster
                        try
                        {
                            var allComps = bonemasterObj.GetComponentsInChildren<MonoBehaviour>(true);
                            foreach (var comp in allComps)
                            {
                                if (comp == null) continue;
                                string tName = comp.GetType().Name;
                                if (tName == "SharpDrawing" || tName == "Repaint" || tName == "Cylinder")
                                {
                                    UnityEngine.Object.Destroy(comp);
                                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Stripped '{tName}' from Bonemaster.");
                                }
                            }
                        }
                        catch (System.Exception __apiEx_73) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2745", __apiEx_73); }

                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Bonemaster verified active in Memory_Ant_Queen (pos={bonemasterObj.transform.position}, state='{controlFsm.ActiveStateName}', active={bonemasterObj.activeInHierarchy})");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(0.2f);
            }
        }

        private static bool _weaverAbilitiesWarmedUp;

        public static IEnumerator CoWarmupWeaverCrestAbilities()
        {
            if (_weaverAbilitiesWarmedUp) yield break;
            _weaverAbilitiesWarmedUp = true;

            Type wcType = null;
            foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
            {
                if (a == null) continue;
                string aName = a.GetName().Name ?? "";
                if (aName.IndexOf("WeaverCrest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    aName.IndexOf("Weaver_Crest", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    wcType = a.GetType("WeaverCrestMod.WeaverCombat", false);
                    if (wcType != null) break;
                }
            }

            if (wcType == null)
            {
                yield break;
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Starting Weaver Crest ability warmup...");
            const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // 1. Cross Slash zone assets & clips
            try
            {
                var m = wcType.GetMethod("EnsureCrossSlashZoneAssets", SFlags);
                m?.Invoke(null, null);
                EnsureWeaverCrossSlashClipsReady();
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Cross Slash zone assets & animation clips.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Cross Slash warmup warning: {ex.Message}");
            }
            yield return null;

            // 2. Phantom Dive animators, sprites, materials & audio
            try
            {
                var m = wcType.GetMethod("EnsurePhantomDiveAssets", SFlags);
                m?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Phantom Dive assets.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Phantom Dive warmup warning: {ex.Message}");
            }
            yield return null;

            // 3. Brutal Smash handmaiden explosion prefab
            try
            {
                var m = wcType.GetMethod("GetBrutalSmashHandmaidenExplosionPrefab", SFlags);
                m?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Brutal Smash handmaiden explosion assets.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Brutal Smash warmup warning: {ex.Message}");
            }
            yield return null;

            // 4. Air Spin feedback prefabs
            try
            {
                var m = wcType.GetMethod("EnsureAirSpinFeedbackPrefabs", SFlags);
                m?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Air Spin feedback prefabs.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Air Spin warmup warning: {ex.Message}");
            }
            yield return null;

            // 5. Cocoon audio & rune sprites
            try
            {
                var m1 = wcType.GetMethod("EnsureCocoonAudio", SFlags);
                m1?.Invoke(null, null);
                var m2 = wcType.GetMethod("EnsureCocoonRuneSprites", SFlags);
                m2?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Cocoon audio & rune sprites.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Cocoon warmup warning: {ex.Message}");
            }
            yield return null;

            // 6. Silk Bullet audio
            try
            {
                var m = wcType.GetMethod("EnsureSilkBulletAudio", SFlags);
                m?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Silk Bullet audio.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Silk Bullet warmup warning: {ex.Message}");
            }
            yield return null;

            // 7. Lace Downstab Burst clip
            try
            {
                var m = wcType.GetMethod("EnsureLaceDownstabBurst", SFlags);
                m?.Invoke(null, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver Lace Downstab Burst animation clip.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Lace Downstab Burst warmup warning: {ex.Message}");
            }
            yield return null;

            // 8. Prewarm Weaver voices, dash audio, ripple distortion, and embedded hornet sprites
            try
            {
                var mVoice = wcType.GetMethod("PrewarmDashChargeVoices", SFlags);
                mVoice?.Invoke(null, null);
                var mHVoice = wcType.GetMethod("PrewarmEmbeddedHornetVoices", SFlags);
                mHVoice?.Invoke(null, null);
                var mDash = wcType.GetMethod("PrewarmImportedHornetDashSound", SFlags);
                mDash?.Invoke(null, null);
                var mExec = wcType.GetMethod("PrewarmFinalExecutionAudio", SFlags);
                mExec?.Invoke(null, null);

                var weaverAssembly = wcType.Assembly;
                var rippleType = weaverAssembly.GetType("WeaverCrestMod.WeaverRippleDistortion", false);
                rippleType?.GetMethod("Prewarm", SFlags)?.Invoke(null, null);

                // Note: Skip WeaverHealingSilkOrb.PrewarmAssets / WeaverSilkHealBloomEffect.PrewarmAssets on Android
                // because they call AssetBundle.LoadFromFile on base.apk!/assets/aa/Android/sfxstatic_assets_bench.bundle directly.

                var maskType = weaverAssembly.GetType("WeaverCrestMod.WeaverMaskHud", false);
                maskType?.GetMethod("PrewarmTransformFrames", SFlags)?.Invoke(null, null);

                var bodyType = weaverAssembly.GetType("WeaverCrestMod.WeaverImportedHornetBody", false);
                if (bodyType != null)
                {
                    var mPrewarm = bodyType.GetMethod("PrewarmFrames", SFlags);
                    mPrewarm?.Invoke(null, new object[] { "WeaverCrest.Assets.Imported.HK1HornetCombat." });
                    mPrewarm?.Invoke(null, new object[] { "WeaverCrest.Assets.Effects.AirSpinBody." });
                    mPrewarm?.Invoke(null, new object[] { "WeaverCrest.Assets.Imported.HK1HornetCounter." });
                }

                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Warmed up Weaver voices, audio, ripple distortion, and sprite frames.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Weaver combat asset warmup warning: {ex.Message}");
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Weaver Crest ability warmup finished — all bundles & assets resident in memory!");
        }

        public static string ResolveAndroidBundlePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                if (File.Exists(path)) return path;
                string normPath = path.Replace('\\', '/');
                string fileName = Path.GetFileName(normPath);
                if (string.IsNullOrEmpty(fileName)) return path;
                if (!fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    fileName += ".bundle";
                if (normPath.IndexOf("scenes_scenes_scenes/", StringComparison.OrdinalIgnoreCase) >= 0)
                    fileName = Path.Combine("scenes_scenes_scenes", fileName);

                string addrRt = null;
                try { addrRt = UnityEngine.AddressableAssets.Addressables.RuntimePath; } catch { }
                if (!string.IsNullOrEmpty(addrRt))
                {
                    string cand1 = Path.Combine(addrRt, "Android", fileName);
                    if (cand1 != path) return cand1;
                }
                string streamPath = Application.streamingAssetsPath;
                if (!string.IsNullOrEmpty(streamPath))
                {
                    return Path.Combine(streamPath, "aa", "Android", fileName);
                }
            }
            catch { }
            return path;
        }

        public static AssetBundle LoadAssetBundleFromFileSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string shortName = Path.GetFileNameWithoutExtension(path);
                foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loaded != null && string.Equals(loaded.name, shortName, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }
                string resolved = ResolveAndroidBundlePath(path);
                return AssetBundle.LoadFromFile(resolved);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] LoadAssetBundleFromFileSafe('{path}') warning: {ex.Message}");
                return null;
            }
        }

        public static AssetBundleCreateRequest LoadAssetBundleFromFileAsyncSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string resolved = ResolveAndroidBundlePath(path);
                return AssetBundle.LoadFromFileAsync(resolved);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] LoadAssetBundleFromFileAsyncSafe('{path}') warning: {ex.Message}");
                return null;
            }
        }

        public static IEnumerator AwakenedGmsManuallyLoadBundlesSafe()
        {
            yield break;
        }

        public static IEnumerator AwakenedGmsInitializeSafe(Type assetManagerType)
        {
            if (assetManagerType == null) yield break;
            const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo initField = null;
            MethodInfo tryAddOpen = null;
            try
            {
                initField = assetManagerType.GetField("_initialized", SFlags);
                if (initField != null && initField.GetValue(null) is bool b && b)
                    yield break;
                initField?.SetValue(null, true);
                tryAddOpen = assetManagerType.GetMethod("TryAdd", SFlags);
            }
            catch { yield break; }

            MethodInfo tryAddObj = null;
            try
            {
                if (tryAddOpen != null && tryAddOpen.IsGenericMethodDefinition)
                    tryAddObj = tryAddOpen.MakeGenericMethod(typeof(UnityEngine.Object));
            }
            catch { }

            try
            {
                string[] wantNames = assetManagerType.GetField("_assetNames", SFlags)?.GetValue(null) as string[]
                                  ?? new string[] { "Audio Player Actor Simple" };

                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null) continue;
                    string gName = go.name;
                    for (int i = 0; i < wantNames.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(wantNames[i]) && string.Equals(gName, wantNames[i], StringComparison.Ordinal))
                        {
                            try { tryAddObj?.Invoke(null, new object[] { go }); } catch { }
                        }
                    }
                }

                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    bool isSceneBundle = false;
                    try { isSceneBundle = bundle.isStreamedSceneAssetBundle; } catch { continue; }
                    if (isSceneBundle) continue;

                    string[] assetNames = null;
                    try { assetNames = bundle.GetAllAssetNames(); } catch { continue; }
                    if (assetNames == null) continue;

                    for (int a = 0; a < assetNames.Length; a++)
                    {
                        string assetPath = assetNames[a];
                        if (string.IsNullOrEmpty(assetPath)) continue;
                        for (int w = 0; w < wantNames.Length; w++)
                        {
                            if (!string.IsNullOrEmpty(wantNames[w]) && assetPath.IndexOf(wantNames[w], StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    var loadedAsset = bundle.LoadAsset(assetPath);
                                    if (loadedAsset != null)
                                        tryAddObj?.Invoke(null, new object[] { loadedAsset });
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] AwakenedGmsInitializeSafe notice: {ex.Message}");
            }

            try
            {
                var assetsField = assetManagerType.GetField("Assets", SFlags);
                if (assetsField?.GetValue(null) is IDictionary topDict)
                {
                    if (topDict.Contains(typeof(Texture2D)) && topDict[typeof(Texture2D)] is IDictionary texDict &&
                        texDict.Contains("Silk_Title_Image") &&
                        !texDict.Contains("Silk_Title_Image_EN"))
                    {
                        var defaultTitleTex = texDict["Silk_Title_Image"];
                        if (defaultTitleTex != null)
                        {
                            texDict["Silk_Title_Image_EN"] = defaultTitleTex;
                        }
                    }

                    // Populate or alias silk_boss_move_1..4 in Assets[typeof(AudioClip)] so Nuke.PlayTripleDashAudio()
                    // never logs 'Sub-dictionary for type "UnityEngine.AudioClip" does not contain key "silk_boss_move_..."'
                    foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                    {
                        if (clip == null) continue;
                        string cName = clip.name;
                        if (!string.IsNullOrEmpty(cName) && cName.StartsWith("silk_boss_move_", StringComparison.OrdinalIgnoreCase))
                        {
                            try { tryAddObj?.Invoke(null, new object[] { clip }); } catch { }
                        }
                    }

                    if (topDict.Contains(typeof(AudioClip)) && topDict[typeof(AudioClip)] is IDictionary audioDict)
                    {
                        AudioClip fallbackClipA = null;
                        AudioClip fallbackClipB = null;
                        if (audioDict.Contains("silk_boss_move_1")) fallbackClipA = audioDict["silk_boss_move_1"] as AudioClip;
                        if (fallbackClipA == null && audioDict.Contains("silkboss_nuke_throw_A")) fallbackClipA = audioDict["silkboss_nuke_throw_A"] as AudioClip;
                        if (audioDict.Contains("silkboss_nuke_throw_B")) fallbackClipB = audioDict["silkboss_nuke_throw_B"] as AudioClip;
                        if (fallbackClipA == null) fallbackClipA = fallbackClipB;
                        if (fallbackClipB == null) fallbackClipB = fallbackClipA;
                        if (fallbackClipA == null)
                        {
                            foreach (DictionaryEntry de in audioDict)
                            {
                                if (de.Value is AudioClip ac && ac != null)
                                {
                                    fallbackClipA = ac;
                                    fallbackClipB = ac;
                                    break;
                                }
                            }
                        }
                        if (fallbackClipA != null)
                        {
                            for (int m = 1; m <= 4; m++)
                            {
                                string key = "silk_boss_move_" + m;
                                if (!audioDict.Contains(key) || audioDict[key] == null)
                                {
                                    audioDict[key] = (m % 2 == 0 && fallbackClipB != null) ? fallbackClipB : fallbackClipA;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            yield break;
        }

        public static void SetAudioSourceVolumeSafe(AudioSource source, float volume)
        {
            if (source == null) return;
            try
            {
                source.volume = volume;
            }
            catch { }
        }

        public static void AwakenedGmsUnloadManualBundlesSafe(Type assetManagerType)
        {
            if (assetManagerType == null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var manualList = assetManagerType.GetField("_manuallyLoadedBundles", SFlags)?.GetValue(null) as IList;
                manualList?.Clear();
            }
            catch { }
        }

        private static readonly HashSet<int> _notifiedSpecialDeathBossIds = new HashSet<int>();

        public static void NotifySpecialBossZeroHpIfNeeded(HealthManager hm)
        {
            if (hm == null) return;
            try
            {
                int id = hm.GetInstanceID();
                if (!_notifiedSpecialDeathBossIds.Add(id)) return;
                var sendDeathMi = typeof(HealthManager).GetMethod("SendDeathEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                sendDeathMi?.Invoke(hm, null);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Dispatched SendDeathEvent for zero-HP special-death boss '{hm.gameObject?.name}'.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] NotifySpecialBossZeroHpIfNeeded notice: {ex.Message}");
            }
        }

        public static void PrePrimedSentinelOnFsmEnabled(PlayMakerFSM fsm, Type primedSentinelType)
        {
            if (fsm == null || primedSentinelType == null) return;
            try
            {
                if (!string.Equals(fsm.FsmName, "Control", StringComparison.Ordinal)) return;
                var go = fsm.gameObject;
                if (go == null || !string.Equals(go.scene.name, "Hang_17b", StringComparison.Ordinal)) return;

                // CRITICAL: Only reset if THIS PlayMakerFSM is Song Knight's own "Control" FSM (which contains "First Idle"),
                // NEVER when Boss Scene, arena gates, Song Knight Projectile, or Song Knight CrossSlash enable their "Control" FSMs!
                var states = fsm.FsmStates;
                if (states == null) return;
                bool hasFirstIdle = false;
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i] != null && string.Equals(states[i].Name, "First Idle", StringComparison.Ordinal))
                    {
                        hasFirstIdle = true;
                        break;
                    }
                }
                if (!hasFirstIdle) return;

                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var ctrlField = primedSentinelType.GetField("control", SFlags);
                if (ctrlField == null) return;
                var curCtrl = ctrlField.GetValue(null) as PlayMakerFSM;
                if ((object)curCtrl != null && !ReferenceEquals(curCtrl, fsm))
                {
                    var resetMi = primedSentinelType.GetMethod("deathReset", SFlags);
                    resetMi?.Invoke(null, null);
                }
            }
            catch { }
        }

        public static void PrimedSentinelSafeSetState(PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName)) return;
            try
            {
                if (!fsm.isActiveAndEnabled) return;
                var go = fsm.gameObject;
                var hm = go != null ? go.GetComponent<HealthManager>() : null;
                if (hm != null && (hm.isDead || hm.hp <= 0))
                {
                    hm.IsInvincible = false;
                    try { fsm.StopAllCoroutines(); } catch { }
                    return;
                }
                string activeSt = fsm.ActiveStateName ?? "";
                if (activeSt.StartsWith("Death", StringComparison.OrdinalIgnoreCase) ||
                    activeSt.StartsWith("Defeat", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(activeSt, "Battle End", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                // Do not let a delayed telegraphy/followup coroutine started before a parry-stun yank Song Knight out of an active stun state
                if (activeSt == "Stun Start" || activeSt == "Stun Air" || activeSt == "Stun Land" ||
                    activeSt == "Stunned" || activeSt == "Stun Damage" || activeSt == "Stun Recover" ||
                    activeSt == "Damage Recover")
                {
                    return;
                }
                fsm.SetState(stateName);
            }
            catch { }
        }

        public static void PostPrimedSentinelFixedUpdate(Type primedSentinelType)
        {
            if (primedSentinelType == null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var enableField = primedSentinelType.GetField("enable", SFlags);
                if (enableField == null || !(enableField.GetValue(null) is bool en) || !en) return;

                var hm = primedSentinelType.GetField("healthManager", SFlags)?.GetValue(null) as HealthManager;
                var ctrl = primedSentinelType.GetField("control", SFlags)?.GetValue(null) as PlayMakerFSM;
                var go = primedSentinelType.GetField("gameObj", SFlags)?.GetValue(null) as GameObject;
                if (hm == null || ctrl == null) return;

                var phase2Field = primedSentinelType.GetField("phase2", SFlags);
                var stunCounterField = primedSentinelType.GetField("stunCounter", SFlags);
                bool isPhase2 = phase2Field?.GetValue(null) is bool p2 && p2;
                bool isCross = primedSentinelType.GetField("isCrossStitching", SFlags)?.GetValue(null) is bool cs && cs;
                int initHp = primedSentinelType.GetField("INITIAL_HP", SFlags)?.GetValue(null) is int ih ? ih : 1800;
                int phase2Thresh = primedSentinelType.GetField("PHASE_2_THRESHOLD", SFlags)?.GetValue(null) is int p2t ? p2t : 1500;

                // 1. If Pantheon of Pharloom scaled Song Knight's HP above INITIAL_HP (e.g., 1800 -> 2908),
                // clamp it back to INITIAL_HP so PHASE_2_THRESHOLD (1500) is reachable after 300 damage as intended.
                if (!isPhase2 && hm.hp > initHp && initHp > 0)
                {
                    hm.hp = initHp;
                }

                string st = ctrl.ActiveStateName ?? "";

                // 2. Once Song Knight is already in a stun state, reset stunCounter to 0 immediately so parrying during stun cannot re-fire STUN
                if (!isPhase2 && (st == "Stun Start" || st == "Stun Air" || st == "Stun Land" || st == "Stunned" || st == "Stun Damage"))
                {
                    if (stunCounterField != null && stunCounterField.GetValue(null) is int sc && sc > 0)
                    {
                        stunCounterField.SetValue(null, 0);
                    }
                }

                // 3. Fallback Phase 2 transition guard if Stun Recover / Damage Recover is active at <= PHASE_2_THRESHOLD
                if (!isPhase2 && (st == "Stun Recover" || st == "Damage Recover"))
                {
                    stunCounterField?.SetValue(null, 0);
                    if (hm.hp <= phase2Thresh)
                    {
                        hm.hp = phase2Thresh;
                        phase2Field?.SetValue(null, true);
                        isPhase2 = true;
                        hm.IsInvincible = false;
                        if (go != null)
                        {
                            SafeDisableWatcherStunControlFsm(FSMUtility.LocateMyFSM(go, "Stun Control"));
                        }
                        ctrl.SetState("Rising Slash Antic");
                    }
                }

                // 4. Prevent risingSlashing from staying permanently true if Rising Slash exits to another state
                var risingField = primedSentinelType.GetField("risingSlashing", SFlags);
                if (risingField != null && risingField.GetValue(null) is bool rs && rs)
                {
                    if (st != "Rising Slash Antic" && st != "Rising Slash" && st != "Rising Slash Followup" &&
                        st != "Near Air Attack" && st != "Far Air Attack" &&
                        st != "Dive Antic" && st != "Dive Dir" && st != "Dive")
                    {
                        risingField.SetValue(null, false);
                    }
                }

                // 5. Ensure Phase 2 is never stuck invincible after Stun Recover / Damage Recover
                if (isPhase2 && !isCross && hm.IsInvincible)
                {
                    hm.IsInvincible = false;
                }
            }
            catch { }
        }

        public static void PrePrimedSentinelDeathReset(Type primedSentinelType)
        {
            if (primedSentinelType == null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var hm = primedSentinelType.GetField("healthManager", SFlags)?.GetValue(null) as HealthManager;
                var go = primedSentinelType.GetField("gameObj", SFlags)?.GetValue(null) as GameObject;
                var ctrl = primedSentinelType.GetField("control", SFlags)?.GetValue(null) as PlayMakerFSM;
                if (ctrl != null)
                {
                    try { ctrl.StopAllCoroutines(); } catch { }
                }
                if (hm != null && hm.hp <= 0 && go != null)
                {
                    hm.IsInvincible = false;
                    if (!hm.isDead)
                    {
                        try { FSMUtility.SendEventToGameObject(go, "ZERO HP", false); } catch { }
                        try { ctrl?.SendEvent("ZERO HP"); } catch { }
                        NotifySpecialBossZeroHpIfNeeded(hm);
                    }
                }
            }
            catch { }
        }

        public static void HKS1in10000LoadOverlayFramesFast(object pluginInstance)
        {
            if (pluginInstance == null) return;
            try
            {
                var pType = pluginInstance.GetType();
                const BindingFlags IFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var framesList = pType.GetField("foxyFrames", IFlags)?.GetValue(pluginInstance) as IList;
                if (framesList == null) return;

                string dllLoc = (pluginInstance as BaseUnityPlugin)?.Info?.Location;
                if (string.IsNullOrEmpty(dllLoc))
                    dllLoc = HarmonyBridge.GetAssemblyLocation(pType.Assembly);
                string dir = !string.IsNullOrEmpty(dllLoc) ? Path.GetDirectoryName(dllLoc) : null;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                string[] files = Directory.GetFiles(dir, "tile*.png", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    string parentDir = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        files = Directory.GetFiles(parentDir, "tile*.png", SearchOption.AllDirectories);
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(files[i]);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (UnityEngine.ImageConversion.LoadImage(tex, bytes))
                        {
                            var spr = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                            framesList.Add(spr);
                        }
                    }
                    catch { }
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Loaded {framesList.Count} overlay frame(s) for HKS1in10000 via ImageConversion.");

                // Also initialize audioSource and start LoadAudioClipCoroutine with the verified jumpscare.ogg path
                try
                {
                    string[] oggs = Directory.GetFiles(dir, "jumpscare.ogg", SearchOption.AllDirectories);
                    if (oggs.Length == 0)
                    {
                        string parentDir = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                            oggs = Directory.GetFiles(parentDir, "jumpscare.ogg", SearchOption.AllDirectories);
                    }
                    if (oggs.Length > 0)
                    {
                        var canvasGo = pType.GetField("overlayCanvas", IFlags)?.GetValue(pluginInstance) as GameObject;
                        if (canvasGo != null)
                        {
                            var aud = canvasGo.GetComponent<AudioSource>() ?? canvasGo.AddComponent<AudioSource>();
                            aud.playOnAwake = false;
                            aud.loop = false;
                            var volCfg = pType.GetField("volume", IFlags)?.GetValue(pluginInstance) as BepInEx.Configuration.ConfigEntry<int>;
                            aud.volume = volCfg != null ? (volCfg.Value / 100f) : 0.5f;
                            pType.GetField("audioSource", IFlags)?.SetValue(pluginInstance, aud);
                            var loadCoroMi = pType.GetMethod("LoadAudioClipCoroutine", IFlags);
                            if (loadCoroMi != null && pluginInstance is MonoBehaviour mb)
                            {
                                var enumerator = loadCoroMi.Invoke(pluginInstance, new object[] { oggs[0] }) as IEnumerator;
                                if (enumerator != null) mb.StartCoroutine(enumerator);
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] HKS1in10000LoadOverlayFramesFast notice: {ex.Message}");
            }
        }

        private static bool _weaverHeroCombatPrewarmed;
        private static bool _weaverCombatSearchDone;
        private static HeroController _lastPrewarmedWeaverHero;
        public static void PrewarmWeaverHeroCombat(HeroController hero)
        {
            if ((object)hero == null || (_weaverHeroCombatPrewarmed && ReferenceEquals(_lastPrewarmedWeaverHero, hero))) return;
            if (_weaverCombatSearchDone && _weaverCombatTypeCache == null) return;
            if (GameManager.instance != null && GameManager.instance.isLoading) return;
            _weaverHeroCombatPrewarmed = true;
            _lastPrewarmedWeaverHero = hero;
            try
            {
                if (_weaverCombatTypeCache == null)
                {
                    _weaverCombatSearchDone = true;
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (asm == null) continue;
                        var wt = asm.GetType("WeaverCrestMod.WeaverCombat", false);
                        if (wt != null) { _weaverCombatTypeCache = wt; break; }
                    }
                }
                if (_weaverCombatTypeCache == null) return;

                const BindingFlags SFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                // WeaverCombat is a plain C# class (not a UnityEngine.Component), so ALWAYS call WeaverCombat.For(hero) first!
                object wcComponent = null;
                var forMethod = _weaverCombatTypeCache.GetMethod("For", SFlags);
                if (forMethod != null)
                {
                    wcComponent = forMethod.Invoke(null, new object[] { hero });
                }
                else if (typeof(Component).IsAssignableFrom(_weaverCombatTypeCache))
                {
                    wcComponent = hero.GetComponent(_weaverCombatTypeCache);
                }
                if (wcComponent == null) return;

                var moveDefs = _weaverCombatTypeCache.Assembly.GetType("WeaverCrestMod.WeaverMoveDefinitions", false);
                var prewarmMove = _weaverCombatTypeCache.GetMethod("PrewarmNormalMoveAssets", SFlags);
                if (moveDefs != null && prewarmMove != null)
                {
                    var norm1 = moveDefs.GetField("NormalOne", SFlags)?.GetValue(null);
                    var norm2 = moveDefs.GetField("NormalTwo", SFlags)?.GetValue(null);
                    var norm3 = moveDefs.GetField("NormalThree", SFlags)?.GetValue(null);
                    var upMove = moveDefs.GetField("Up", SFlags)?.GetValue(null);

                    try { if (norm1 != null) prewarmMove.Invoke(wcComponent, new object[] { norm1, false }); } catch (System.Exception __apiEx_74) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2964", __apiEx_74); }
                    try { if (norm2 != null) prewarmMove.Invoke(wcComponent, new object[] { norm2, true }); } catch (System.Exception __apiEx_75) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2965", __apiEx_75); }
                    try { if (norm3 != null) prewarmMove.Invoke(wcComponent, new object[] { norm3, false }); } catch (System.Exception __apiEx_76) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2966", __apiEx_76); }
                    try { if (upMove != null) prewarmMove.Invoke(wcComponent, new object[] { upMove, false }); } catch (System.Exception __apiEx_77) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2967", __apiEx_77); }
                }

                try { _weaverCombatTypeCache.GetMethod("PrewarmAirSpinVisual", SFlags)?.Invoke(wcComponent, null); } catch (System.Exception __apiEx_78) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2970", __apiEx_78); }
                try { _weaverCombatTypeCache.GetMethod("PrewarmCounterSwingClip", SFlags)?.Invoke(wcComponent, null); } catch (System.Exception __apiEx_79) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2971", __apiEx_79); }
                try { _weaverCombatTypeCache.GetMethod("PrewarmBrutalSmashAimMarker", SFlags)?.Invoke(wcComponent, null); } catch (System.Exception __apiEx_80) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2972", __apiEx_80); }
                try { _weaverCombatTypeCache.GetMethod("PrewarmFinalExecutionAudio", SFlags)?.Invoke(wcComponent, null); } catch (System.Exception __apiEx_81) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2973", __apiEx_81); }

                try
                {
                    var slashSkinType = _weaverCombatTypeCache.Assembly.GetType("WeaverCrestMod.WeaverSlashSkin", false);
                    var getArt = slashSkinType?.GetMethod("GetArt", SFlags);
                    var getArtParams = getArt?.GetParameters();
                    if (getArt != null && getArtParams != null && getArtParams.Length == 1 && getArtParams[0].ParameterType == typeof(string))
                    {
                        foreach (string moveKey in new[] { "normal_1", "normal_2", "normal_3", "up" })
                        {
                            try { getArt.Invoke(null, new object[] { moveKey }); } catch (System.Exception __apiEx_82) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2983", __apiEx_82); }
                        }
                    }
                }
                catch (System.Exception __apiEx_83) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L2987", __apiEx_83); }

                _lastPrewarmedWeaverHero = hero;
                _weaverHeroCombatPrewarmed = true;
                EnsureWeaverCrossSlashClipsReady();
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Prewarmed Weaver normal moves (1, 2, 3, Up), slash skins, cross-slash clips & air spin visual for active HeroController.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Hero combat prewarm warning: {ex.Message}");
            }
        }

        public static volatile bool IsPharloomsGloryBatchComplete;
        public static volatile int PharloomsGloryScenesDone;
        public static volatile int PharloomsGloryScenesTotal = 4;
        public static volatile string PharloomsGloryCurrentExtractingScene = "";

        private static int _pgLastAsmCount = -1;
        public static bool HasPharloomsGlory()
        {
            if (_pharloomsGloryAssembly != null) return true;
            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (asms.Length == _pgLastAsmCount) return false;
            _pgLastAsmCount = asms.Length;
            for (int i = 0; i < asms.Length; i++)
            {
                var a = asms[i];
                if (a != null && a.GetName().Name == "PharloomsGlory")
                {
                    _pharloomsGloryAssembly = a;
                    return true;
                }
            }
            return false;
        }

        private static void CollectAllAssetHelperRequestedSceneAssets()
        {
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    var apiType = asm.GetType("Silksong.AssetHelper.Plugin.AssetRequestAPI", false)
                               ?? asm.GetType("Silksong.AssetHelper.AssetHelperPlugin", false)
                               ?? asm.GetType("Silksong.AssetHelper.Plugin", false);
                    if (apiType == null) continue;

                    object reqObj = null;
                    var staticReqField = apiType.GetField("Request", SFlags);
                    var staticReqProp = apiType.GetProperty("Request", SFlags);
                    if (staticReqField != null || staticReqProp != null)
                    {
                        reqObj = staticReqField != null ? staticReqField.GetValue(null) : staticReqProp?.GetValue(null, null);
                    }
                    if (reqObj == null)
                    {
                        var reqApiField = apiType.GetField("AssetRequestAPI", SFlags);
                        var reqApiProp = apiType.GetProperty("AssetRequestAPI", SFlags);
                        object reqApi = reqApiField != null ? reqApiField.GetValue(null) : reqApiProp?.GetValue(null, null);
                        if (reqApi != null)
                        {
                            var reqType = reqApi.GetType();
                            var reqField = reqType.GetField("Request", SFlags);
                            var reqProp = reqType.GetProperty("Request", SFlags);
                            reqObj = reqField != null ? reqField.GetValue(reqApi) : reqProp?.GetValue(reqApi, null);
                        }
                    }
                    if (reqObj == null) continue;

                    var saField = reqObj.GetType().GetField("SceneAssets", SFlags);
                    var saProp = reqObj.GetType().GetProperty("SceneAssets", SFlags);
                    var saDict = (saField != null ? saField.GetValue(reqObj) : saProp?.GetValue(reqObj, null)) as IDictionary;
                    if (saDict == null) continue;

                    lock (_pendingScenePathsByScene)
                    {
                        foreach (DictionaryEntry de in saDict)
                        {
                            string scName = de.Key as string;
                            if (string.IsNullOrEmpty(scName) || !(de.Value is IEnumerable pathsEnum)) continue;
                            if (!_pendingScenePathsByScene.TryGetValue(scName, out var list) || list == null)
                            {
                                list = new List<string>();
                                _pendingScenePathsByScene[scName] = list;
                            }
                            foreach (var pObj in pathsEnum)
                            {
                                if (pObj is string pStr && !string.IsNullOrEmpty(pStr) && !list.Contains(pStr))
                                    list.Add(pStr);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_84) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3083", __apiEx_84); }
        }

        private static readonly List<FieldInfo> _cachedManagedAssetStaticFields = new List<FieldInfo>();
        private static int _lastManagedAssetScanAsmCount = -1;

        public static void BindAllModManagedAssetsFromTemplateCache()
        {
            try
            {
                EnsureRepackerMembersResolved();
                var templatesDict = (_extractedTemplatesField?.GetValue(null) as IDictionary) ?? _bridgeFallbackTemplates;
                var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
                if (asms.Length != _lastManagedAssetScanAsmCount)
                {
                    _lastManagedAssetScanAsmCount = asms.Length;
                    _cachedManagedAssetStaticFields.Clear();
                    const BindingFlags AllFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    for (int a = 0; a < asms.Length; a++)
                    {
                        var asm = asms[a];
                        if (asm == null) continue;
                        string an = asm.GetName().Name ?? "";
                        if (string.IsNullOrEmpty(an) ||
                            an.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("PlayMaker", StringComparison.OrdinalIgnoreCase) ||
                            an.StartsWith("SilksongMobileAPI", StringComparison.OrdinalIgnoreCase))
                            continue;

                        Type[] types = null;
                        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; } catch (System.Exception __apiEx_85) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3114", __apiEx_85); }
                        if (types == null) continue;
                        for (int i = 0; i < types.Length; i++)
                        {
                            var t = types[i];
                            if (t == null) continue;
                            FieldInfo[] fields = null;
                            try { fields = t.GetFields(AllFlags); } catch (System.Exception __apiEx_86) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3121", __apiEx_86); }
                            if (fields == null) continue;
                            for (int f = 0; f < fields.Length; f++)
                            {
                                var fi = fields[f];
                                if (fi != null && fi.IsStatic && fi.FieldType != null &&
                                    fi.FieldType.Name.StartsWith("ManagedAsset`1", StringComparison.Ordinal))
                                {
                                    _cachedManagedAssetStaticFields.Add(fi);
                                }
                            }
                        }
                    }
                }

                for (int i = 0; i < _cachedManagedAssetStaticFields.Count; i++)
                {
                    try
                    {
                        object ma = _cachedManagedAssetStaticFields[i].GetValue(null);
                        if (ma != null) TryBindManagedAssetFromTemplateCache(ma, templatesDict);
                    }
                    catch (System.Exception __apiEx_87) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3143", __apiEx_87); }
                }
                lock (_allKnownManagedAssets)
                {
                    for (int i = _allKnownManagedAssets.Count - 1; i >= 0; i--)
                    {
                        var wr = _allKnownManagedAssets[i];
                        if (wr == null || !wr.IsAlive)
                        {
                            _allKnownManagedAssets.RemoveAt(i);
                            continue;
                        }
                        try { TryBindManagedAssetFromTemplateCache(wr.Target, templatesDict); } catch (System.Exception __apiEx_88) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3155", __apiEx_88); }
                    }
                }
            }
            catch (System.Exception __apiEx_89) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3159", __apiEx_89); }
        }

        public static IEnumerator RunPreloaderPharloomsGloryBatchExtract()
        {
            bool hasPg = HasPharloomsGlory();
            bool hasEw = HasAeternalEverwatcher();
            bool hasPa = HasPaleAutomaton();
            bool hasFs = HasPhantinelBoss();
            bool hasLc = HasLostAndChained();
            CollectAllAssetHelperRequestedSceneAssets();

            if (hasPa || hasFs || hasLc)
            {
                lock (_pendingScenePathsByScene)
                {
                    void EnsurePendingScenePath(string defaultSceneKey, string assetPath)
                    {
                        string resolvedKey = defaultSceneKey;
                        foreach (var key in _pendingScenePathsByScene.Keys)
                        {
                            if (string.Equals(key, defaultSceneKey, StringComparison.OrdinalIgnoreCase))
                            {
                                resolvedKey = key;
                                break;
                            }
                        }
                        if (!_pendingScenePathsByScene.TryGetValue(resolvedKey, out var list) || list == null)
                        {
                            list = new List<string>();
                            _pendingScenePathsByScene[resolvedKey] = list;
                        }
                        if (!list.Contains(assetPath))
                            list.Add(assetPath);
                    }

                    if (hasPa || hasFs)
                    {
                        EnsurePendingScenePath("hang_17b", "Boss Scene - To Additive Load");
                    }
                    if (hasPa)
                    {
                        EnsurePendingScenePath("cradle_03", "Boss Scene/Boss Title");
                        EnsurePendingScenePath("song_24", "sc_wall_spiked");
                    }
                    if (hasLc)
                    {
                        EnsurePendingScenePath("Cradle_03", "Boss Scene");
                    }
                }
            }

            if (hasPg && _pharloomsGloryAssetManager == null)
            {
                try
                {
                    var amType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Managers.AssetManager", false);
                    var amInst = amType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    if (amInst != null)
                    {
                        LoadPharloomsGloryAssets(amInst);
                    }
                }
                catch (System.Exception __apiEx_90) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3204", __apiEx_90); }
            }

            if (hasEw)
            {
                lock (_pendingScenePathsByScene)
                {
                    if (!_pendingScenePathsByScene.TryGetValue("Memory_Coral_Tower", out var mctList) || mctList == null)
                    {
                        mctList = new List<string>();
                        _pendingScenePathsByScene["Memory_Coral_Tower"] = mctList;
                    }
                    if (!mctList.Contains("Boss Scene/Uppercut Spear"))
                        mctList.Add("Boss Scene/Uppercut Spear");
                }
            }

            bool hasAnyPending = false;
            lock (_pendingScenePathsByScene)
            {
                hasAnyPending = _pendingScenePathsByScene.Count > 0;
            }
            if (!hasPg && !hasEw && !hasPa && !hasFs && !hasLc && !hasAnyPending)
            {
                IsPharloomsGloryBatchComplete = true;
                yield break;
            }

            if (_pharloomsGloryBatchRunning)
            {
                int waitExisting = 0;
                while (_pharloomsGloryBatchRunning && waitExisting++ < 600)
                    yield return new WaitForSeconds(0.05f);
            }
            else if (!IsPharloomsGloryBatchComplete)
            {
                _pharloomsGloryBatchRunning = true;
                _pharloomsGloryBatchStarted = true;
                try
                {
                    yield return RunPharloomsGloryBatchExtractCoroutine(_pharloomsGloryAssetManager);
                }
                finally
                {
                    _pharloomsGloryBatchRunning = false;
                }
            }
            SyncExtractedTemplatesIntoAeternalEverwatcher();
            BindAllModManagedAssetsFromTemplateCache();
            IsPharloomsGloryBatchComplete = true;
        }

        private static IEnumerator CoEnsurePharloomsGloryMusic(string scName, float initialWait = 0.2f)
        {
            if (initialWait > 0f)
                yield return new WaitForSeconds(initialWait);

            for (int step = 0; step < 8; step++)
            {
                var curScene = SceneManager.GetActiveScene();
                if (!curScene.IsValid() || !IsCurrentSceneInCoral(curScene.name)) yield break;
                ApplyPharloomsGloryMusicImmediately(curScene.name);
                yield return new WaitForSeconds(0.4f);
            }
        }

        public static void ApplyPharloomsGloryMusicImmediately(string scName)
        {
            if (_pharloomsGloryAssembly == null) return;
            try
            {
                var amType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.AudioModifier", false);
                var amInst = amType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (amInst == null) return;

                var clipsField = amType.GetField("updatedClips", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var updatedClips = clipsField?.GetValue(amInst) as Dictionary<string, AudioClip>;
                if (updatedClips == null || updatedClips.Count == 0) return;

                var cfgType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Managers.ConfigurationManager", false);
                var cfgInst = cfgType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

                int blastedCfg = 1;
                int sandsCfg = 2;
                if (cfgInst != null)
                {
                    var bProp = cfgType.GetProperty("BlastedStepsMusic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (bProp != null) blastedCfg = Convert.ToInt32(bProp.GetValue(cfgInst, null));
                    var sProp = cfgType.GetProperty("SandsOfKarakMusic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sProp != null) sandsCfg = Convert.ToInt32(sProp.GetValue(cfgInst, null));
                }

                bool isSands = IsSandsOfKarakScene(scName);
                int choice = isSands ? sandsCfg : blastedCfg;
                if (choice == 0) return; // VANILLA: leave untouched

                string mainKey = (choice == 2) ? "Coral Ruins Main" : "H116 Coral Steps-42 MAIN ALT";
                string subKey  = (choice == 2) ? "Coral Ruins Sub"  : "H116 Coral Steps-42 SUB";

                updatedClips.TryGetValue(mainKey, out AudioClip targetMainClip);
                updatedClips.TryGetValue(subKey, out AudioClip targetSubClip);

                // Unmute music mixer if needed
                try
                {
                    if (UIManager.instance != null && UIManager.instance.defaultMusicSnapshot != null)
                    {
                        UIManager.instance.defaultMusicSnapshot.TransitionTo(0.4f);
                    }
                }
                catch (System.Exception __apiEx_91) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3300", __apiEx_91); }

                var audioMgr = AudioManager.Instance;
                if (audioMgr != null)
                {
                    try
                    {
                        var cue = audioMgr.currentMusicCue;
                        if (cue != null && cue.channelInfos != null)
                        {
                            if (cue.channelInfos.Length > 0 && targetMainClip != null)
                                cue.channelInfos[0].clip = targetMainClip;
                            if (cue.channelInfos.Length > 2 && targetSubClip != null)
                                cue.channelInfos[2].clip = targetSubClip;
                        }
                    }
                    catch (System.Exception __apiEx_92) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3316", __apiEx_92); }
                }

                EnsurePharloomsGloryDedicatedAudioSources();

                if (targetMainClip != null)
                {
                    bool mainClipChanged = (_lastAppliedPgMainClip != targetMainClip) || !string.Equals(_lastAppliedPgScene, scName, StringComparison.OrdinalIgnoreCase);
                    if (_pgMusicSource != null)
                    {
                        if (mainClipChanged || _pgMusicSource.clip != targetMainClip)
                        {
                            try { _pgMusicSource.resource = targetMainClip; } catch (System.Exception __apiEx_93) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3328", __apiEx_93); }
                            _pgMusicSource.clip = targetMainClip;
                            if (_lastAppliedPgMainClip != targetMainClip)
                                _pgMusicSource.time = 0f;
                            _pgMusicSource.volume = 1f;
                            if (!_pgMusicSource.isPlaying)
                                _pgMusicSource.Play();
                            if (mainClipChanged)
                            {
                                _lastAppliedPgMainClip = targetMainClip;
                                _lastAppliedPgScene = scName;
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Applied Pharloom's Glory custom Coral Main track '{targetMainClip.name}' in '{scName}'");
                            }
                        }
                        else if (!_pgMusicSource.isPlaying)
                        {
                            _pgMusicSource.volume = 1f;
                            _pgMusicSource.Play();
                        }
                    }

                    if (audioMgr != null)
                    {
                        var ms = audioMgr.MusicSources ?? audioMgr.musicSources;
                        var mainSrc = (ms != null && ms.Length > 0) ? ms[0] : null;
                        if (mainSrc != null)
                        {
                            if (mainClipChanged || mainSrc.clip != targetMainClip)
                            {
                                try { mainSrc.resource = targetMainClip; } catch (System.Exception __apiEx_94) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3357", __apiEx_94); }
                                mainSrc.clip = targetMainClip;
                                mainSrc.volume = 1f;
                                if (!mainSrc.isPlaying) mainSrc.Play();
                            }
                            else if (!mainSrc.isPlaying)
                            {
                                mainSrc.volume = 1f;
                                mainSrc.Play();
                            }
                        }
                    }
                }

                if (targetSubClip != null)
                {
                    bool subClipChanged = (_lastAppliedPgSubClip != targetSubClip);
                    if (_pgSubSource != null)
                    {
                        if (subClipChanged || _pgSubSource.clip != targetSubClip)
                        {
                            try { _pgSubSource.resource = targetSubClip; } catch (System.Exception __apiEx_95) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3378", __apiEx_95); }
                            _pgSubSource.clip = targetSubClip;
                            if (_pgMusicSource != null && _pgMusicSource.isPlaying)
                                _pgSubSource.timeSamples = _pgMusicSource.timeSamples;
                            else if (subClipChanged)
                                _pgSubSource.time = 0f;
                            _pgSubSource.volume = 1f;
                            if (!_pgSubSource.isPlaying)
                                _pgSubSource.Play();
                            if (subClipChanged)
                            {
                                _lastAppliedPgSubClip = targetSubClip;
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Applied Pharloom's Glory custom Coral Sub track '{targetSubClip.name}' in '{scName}'");
                            }
                        }
                        else if (!_pgSubSource.isPlaying)
                        {
                            if (_pgMusicSource != null && _pgMusicSource.isPlaying)
                                _pgSubSource.timeSamples = _pgMusicSource.timeSamples;
                            _pgSubSource.volume = 1f;
                            _pgSubSource.Play();
                        }
                    }

                    if (audioMgr != null)
                    {
                        var ms = audioMgr.MusicSources ?? audioMgr.musicSources;
                        var subSrc = (ms != null && ms.Length > 2) ? ms[2] : null;
                        if (subSrc != null)
                        {
                            if (subClipChanged || subSrc.clip != targetSubClip)
                            {
                                try { subSrc.resource = targetSubClip; } catch (System.Exception __apiEx_96) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3410", __apiEx_96); }
                                subSrc.clip = targetSubClip;
                                subSrc.volume = 1f;
                                if (!subSrc.isPlaying) subSrc.Play();
                            }
                            else if (!subSrc.isPlaying)
                            {
                                subSrc.volume = 1f;
                                subSrc.Play();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] ApplyPharloomsGloryMusicImmediately warning: {ex.Message}");
            }
        }

        private static IEnumerator CoEnsureForsakenSentinelsArenaActive()
        {
            const float ArenaMinX = 72.15f;
            const float ArenaMaxX = 96.85f;
            const float ArenaFloorY = 104.0f;

            bool fixedSentiOnce = false;
            bool wokeSentiOnce = false;

            for (int step = 0; step < 120; step++)
            {
                yield return new WaitForSeconds(0.45f);
                var organScene = SceneManager.GetSceneByName("Organ_01");
                if (!organScene.IsValid() || !organScene.isLoaded)
                    yield break;

                try
                {
                    if (fixedSentiOnce && wokeSentiOnce)
                        yield break;

                    GameObject phantomGo = GameObject.Find("Phantom");
                    GameObject sentiGo = GameObject.Find("Second Sentinel") ?? GameObject.Find("Song Knight");
                    GameObject bossSceneGo = GameObject.Find("Boss Scene");
                    PlayMakerFSM phanControl = phantomGo != null ? FSMUtility.LocateMyFSM(phantomGo, "Control") : null;
                    PlayMakerFSM senControl = sentiGo != null ? FSMUtility.LocateMyFSM(sentiGo, "Control") : null;
                    PlayMakerFSM bossSceneFsm = bossSceneGo != null ? FSMUtility.LocateMyFSM(bossSceneGo, "Control") : null;

                    if (sentiGo == null && phantomGo != null)
                    {
                        try
                        {
                            var phantinelCompType = HarmonyBridge.ResolveTypeRobust("PhantinelBoss.PhantinelComponent");
                            if (phantinelCompType != null && phantomGo.GetComponent(phantinelCompType) == null)
                            {
                                phantomGo.AddComponent(phantinelCompType);
                                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Attached PhantinelBoss.PhantinelComponent to Phantom in Organ_01.");
                            }
                        }
                        catch (System.Exception __apiEx_97) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3469", __apiEx_97); }
                    }

                    if (sentiGo == null || senControl == null) continue;

                    if (fixedSentiOnce && wokeSentiOnce)
                        yield break;

                    if (!fixedSentiOnce && senControl.Fsm != null && senControl.Fsm.States != null && senControl.Fsm.States.Length > 0)
                    {
                        // 1. Fix ConstrainPosition components cloned from hang_17b once so Second Sentinel is never clamped outside Organ_01!
                        var allBehaviours = sentiGo.GetComponentsInChildren<MonoBehaviour>(true);
                        if (allBehaviours != null)
                        {
                            for (int i = 0; i < allBehaviours.Length; i++)
                            {
                                var mb = allBehaviours[i];
                                if (mb == null) continue;
                                var mbType = mb.GetType();
                                if (mbType.Name == "ConstrainPosition")
                                {
                                    mbType.GetField("constrainX")?.SetValue(mb, true);
                                    mbType.GetField("xMin")?.SetValue(mb, ArenaMinX);
                                    mbType.GetField("xMax")?.SetValue(mb, ArenaMaxX);
                                    mbType.GetField("constrainY")?.SetValue(mb, true);
                                    mbType.GetField("yMin")?.SetValue(mb, ArenaFloorY);
                                    mbType.GetField("yMax")?.SetValue(mb, 200f);
                                }
                            }
                        }
                        PlayMakerHybridCLRBridge.EnsurePlayMakerFsmPreprocessedAndLoaded(senControl);
                        var p2Bool = senControl.FsmVariables?.FindFsmBool("P2");

                        // 2. Fix hang_17b X/Y float variables on _senControl and Save Hero FSM
                        var floatVars = senControl.FsmVariables?.FloatVariables;
                        if (floatVars != null)
                        {
                            for (int i = 0; i < floatVars.Length; i++)
                            {
                                var fv = floatVars[i];
                                if (fv == null) continue;
                                string vn = (fv.Name ?? "").ToLowerInvariant();
                                if (vn.Contains("left") || vn.Contains("min x") || vn.Contains("minx"))
                                    fv.Value = ArenaMinX;
                                else if (vn.Contains("right") || vn.Contains("max x") || vn.Contains("maxx"))
                                    fv.Value = ArenaMaxX;
                                else if (vn.Contains("centre") || vn.Contains("center") || vn.Contains("mid"))
                                    fv.Value = 84.5f;
                                else if (vn.Contains("floor") || vn.Contains("ground") || vn == "y")
                                {
                                    if (fv.Value < 90f) fv.Value = ArenaFloorY + 2f;
                                }
                            }
                        }

                        // 3. Fix shared BoolTest(isFalse="WALL") across "Idle", "Move Choice", and "Do Move" so "Do Move" is never aborted in Phase 1!
                        string[] checkStates = new[] { "Idle", "Move Choice", "Do Move" };
                        foreach (var stName in checkStates)
                        {
                            var st = senControl.Fsm.GetState(stName);
                            if (st == null || st.Actions == null) continue;
                            for (int a = 0; a < st.Actions.Length; a++)
                            {
                                var act = st.Actions[a];
                                if (act == null || act.GetType().Name != "BoolTest") continue;
                                var btType = act.GetType();
                                var isTrueF = btType.GetField("isTrue");
                                var isFalseF = btType.GetField("isFalse");
                                var boolVarF = btType.GetField("boolVariable");
                                var evTrue = isTrueF?.GetValue(act) as HutongGames.PlayMaker.FsmEvent;
                                var evFalse = isFalseF?.GetValue(act) as HutongGames.PlayMaker.FsmEvent;
                                if ((evTrue != null && evTrue.Name == "WAKE") || (evFalse != null && evFalse.Name == "WALL"))
                                {
                                    // Create a dedicated BoolTest instance per state with isFalse = null
                                    var replacement = Activator.CreateInstance(btType) as HutongGames.PlayMaker.FsmStateAction;
                                    if (replacement != null)
                                    {
                                        boolVarF?.SetValue(replacement, p2Bool ?? boolVarF?.GetValue(act));
                                        isTrueF?.SetValue(replacement, HutongGames.PlayMaker.FsmEvent.GetFsmEvent("WAKE"));
                                        isFalseF?.SetValue(replacement, null);
                                        btType.GetField("everyFrame")?.SetValue(replacement, false);
                                        replacement.Init(st);
                                        st.Actions[a] = replacement;
                                    }
                                    else
                                    {
                                        isFalseF?.SetValue(act, null);
                                    }
                                }
                            }
                            PlayMakerHybridCLRBridge.FinalizeSingleFsmStateAfterModPatch(st);
                        }

                        // 4. Ensure "Init" UNENCOUNTERED transitions to "Encountered"
                        var initSt = senControl.Fsm.GetState("Init");
                        if (initSt != null && initSt.Transitions != null)
                        {
                            for (int t = 0; t < initSt.Transitions.Length; t++)
                            {
                                var tr = initSt.Transitions[t];
                                if (tr != null && tr.EventName == "UNENCOUNTERED")
                                {
                                    tr.ToState = "Encountered";
                                    tr.ToFsmState = senControl.Fsm.GetState("Encountered");
                                }
                            }
                        }

                        PlayMakerHybridCLRBridge.FinalizePlayMakerFsmStatesAfterModPatch(senControl);
                        if (phanControl != null)
                            PlayMakerHybridCLRBridge.FinalizePlayMakerFsmStatesAfterModPatch(phanControl);
                        if (bossSceneFsm != null)
                            PlayMakerHybridCLRBridge.FinalizePlayMakerFsmStatesAfterModPatch(bossSceneFsm);

                        fixedSentiOnce = true;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Applied Forsaken Sentinels arena bounds & BoolTest fixes on Second Sentinel in Organ_01.");
                    }

                    // 5. Keep Second Sentinel inside the Organ_01 arena if it ever drifts out of bounds
                    var pos = sentiGo.transform.position;
                    if (pos.x < ArenaMinX - 2f || pos.x > ArenaMaxX + 2f || pos.y < ArenaFloorY - 3f)
                    {
                        float clampedX = Mathf.Clamp(pos.x, ArenaMinX + 1f, ArenaMaxX - 1f);
                        float clampedY = Mathf.Max(pos.y, ArenaFloorY + 2f);
                        sentiGo.transform.position = new Vector3(clampedX, clampedY, pos.z);
                    }

                    // 6. Wake Second Sentinel if Phantom is already fighting or Boss Scene has passed FG Column
                    if (!sentiGo.activeSelf) sentiGo.SetActive(true);
                    if (!senControl.enabled) senControl.enabled = true;
                    string curSenState = senControl.ActiveStateName ?? "";
                    bool isSenDormant = string.IsNullOrEmpty(curSenState) ||
                                        curSenState == "Init" ||
                                        curSenState == "Dormant" ||
                                        curSenState == "Encountered" ||
                                        curSenState == "Enc Dormant" ||
                                        curSenState == "Sleep" ||
                                        curSenState == "Rest" ||
                                        curSenState == "Pause";
                    if (isSenDormant)
                    {
                        wokeSentiOnce = false;
                        bool phantomActive = false;
                        if (phanControl != null)
                        {
                            string phState = phanControl.ActiveStateName ?? "";
                            phantomActive = !string.IsNullOrEmpty(phState) &&
                                            phState != "Init" && phState != "Rest" && phState != "Dormant" &&
                                            phState != "Pause" && phState != "Wait";
                        }
                        bool bossSceneStarted = false;
                        if (bossSceneFsm != null)
                        {
                            string bsState = bossSceneFsm.ActiveStateName ?? "";
                            bossSceneStarted = bsState == "Battle Start" || bsState == " Start Battle" || bsState == " fight" ||
                                               bsState.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        if (phantomActive || bossSceneStarted || step >= 4)
                        {
                            if (curSenState == "Init" || curSenState == "Dormant" || curSenState == "Encountered" || string.IsNullOrEmpty(curSenState))
                            {
                                try { senControl.SetState("Enc Dormant"); } catch (System.Exception __apiEx_98) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3630", __apiEx_98); }
                            }
                            try { senControl.SendEvent("WAKE"); } catch (System.Exception __apiEx_99) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3632", __apiEx_99); }
                            try { senControl.SendEvent("START"); } catch (System.Exception __apiEx_100) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3633", __apiEx_100); }
                            try { senControl.SendEvent("BATTLE START"); } catch (System.Exception __apiEx_101) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3634", __apiEx_101); }
                            string afterWake = senControl.ActiveStateName ?? "";
                            if (string.IsNullOrEmpty(afterWake) || afterWake == "Init" || afterWake == "Dormant" || afterWake == "Encountered" || afterWake == "Enc Dormant")
                            {
                                if (senControl.Fsm.GetState("Enc Wake") != null)
                                    senControl.SetState("Enc Wake");
                                else if (senControl.Fsm.GetState("Wake") != null)
                                    senControl.SetState("Wake");
                                else if (senControl.Fsm.GetState("First Idle") != null)
                                    senControl.SetState("First Idle");
                                else if (senControl.Fsm.GetState("Idle") != null)
                                    senControl.SetState("Idle");
                            }
                            string finalSenState = senControl.ActiveStateName ?? "";
                            if (!string.IsNullOrEmpty(finalSenState) &&
                                finalSenState != "Init" && finalSenState != "Dormant" &&
                                finalSenState != "Encountered" && finalSenState != "Enc Dormant")
                            {
                                wokeSentiOnce = true;
                            }
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Woke Second Sentinel in Organ_01 (prev='{curSenState}' -> now='{finalSenState}').");
                        }
                    }
                    else
                    {
                        wokeSentiOnce = true;
                    }
                }
                catch (System.Exception __apiEx_102) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3662", __apiEx_102); }
            }
        }

        public static void AssignCatchFishPopupSprite(object msgObj, object spriteObj)
        {
            if (msgObj == null || !(spriteObj is Sprite sp) || sp == null) return;
            try
            {
                if (msgObj is CollectableUIMsg uiMsg && uiMsg != null)
                {
                    var iconField = typeof(CollectableUIMsg).GetField("icon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var sr = iconField?.GetValue(uiMsg) as SpriteRenderer;
                    if (sr != null)
                    {
                        sr.sprite = sp;
                    }
                }
            }
            catch (System.Exception __apiEx_103) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3681", __apiEx_103); }
        }

        private static void StripPaleAutomatonDeactivateComponents(GameObject root)
        {
            if (root == null) return;
            try
            {
                var comps = root.GetComponentsInChildren<Component>(true);
                if (comps == null) return;
                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i];
                    if (comp == null) continue;
                    string tName = comp.GetType().Name;
                    if (tName == "DeactivateIfPlayerdataTrue" || tName == "DeactivateIfPlayerdataFalse")
                    {
                        UnityEngine.Object.DestroyImmediate(comp);
                    }
                    else if (comp is PersistentBoolItem pbi)
                    {
                        pbi.enabled = false;
                    }
                }
            }
            catch { }
        }

        private static bool _paleAutomatonCoroutineRunning;
        private static FieldInfo _paBossSceneField;
        private static bool _paBossSceneFieldLookedUp;
        private static FieldInfo _paDisableParryFreezeField;
        private static bool _paDisableParryFreezeFieldLookedUp;

        public static void SyncPaleAutomatonParryFreezeSetting()
        {
            try
            {
                if (!_paDisableParryFreezeFieldLookedUp)
                {
                    var settingsType = HarmonyBridge.ResolveTypeRobust("PaleAutomaton.Settings");
                    if (settingsType == null) return; // Mod not loaded yet; retry when loaded or on arena entry
                    _paDisableParryFreezeFieldLookedUp = true;
                    _paDisableParryFreezeField = settingsType.GetField("DISABLE_PARRY_FREEZE", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_paDisableParryFreezeField != null)
                {
                    bool enableFreeze = SilksongMobileAPIPlugin.CfgPaleAutomatonParryFreeze?.Value ?? true;
                    _paDisableParryFreezeField.SetValue(null, !enableFreeze);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomaton] Synced DISABLE_PARRY_FREEZE={!enableFreeze} (PaleAutomatonParryFreeze={enableFreeze}).");
                }
            }
            catch (System.Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "SyncPaleAutomatonParryFreezeSetting", ex);
            }
        }

        private static FieldInfo _paSongKnightField;

        public static bool IsPaleAutomatonBossSceneActive(DamageHero dh = null)
        {
            try
            {
                // Pale Automaton exclusively fights in Arborium_11 (never Hang_17b or other Second Sentinel arenas).
                var activeScene = SceneManager.GetActiveScene();
                if (!activeScene.IsValid() || !string.Equals(activeScene.name, "Arborium_11", StringComparison.Ordinal))
                    return false;

                if (!_paBossSceneFieldLookedUp)
                {
                    var pluginType = HarmonyBridge.ResolveTypeRobust("PaleAutomaton.PaleAutomatonPlugin");
                    if (pluginType == null) return false;
                    _paBossSceneFieldLookedUp = true;
                    _paBossSceneField = pluginType.GetField("bossScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _paSongKnightField = pluginType.GetField("songKnight", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_paBossSceneField == null || !(_paBossSceneField.GetValue(null) is bool isBoss) || !isBoss)
                    return false;

                if (_paSongKnightField == null)
                    return false;

                var sk = _paSongKnightField.GetValue(null) as GameObject;
                if (sk == null || (UnityEngine.Object)sk == null || !sk.activeInHierarchy ||
                    !string.Equals(sk.scene.name, "Arborium_11", StringComparison.Ordinal))
                    return false;

                if (dh != null)
                {
                    var dhGo = dh.gameObject;
                    if (dhGo == null)
                        return false;
                    // Allow both Arborium_11 hitboxes and DontDestroyOnLoad pooled projectiles (CrossSlash, Wind Slash, etc.)
                    string dhScene = dhGo.scene.name ?? "";
                    if (!string.Equals(dhScene, "Arborium_11", StringComparison.Ordinal) &&
                        !string.Equals(dhScene, "DontDestroyOnLoad", StringComparison.Ordinal))
                        return false;
                }

                return true;
            }
            catch { }
            return false;
        }

        private static bool _paPhaseFieldsLookedUp;
        private static FieldInfo _paHealthManagerField;
        private static FieldInfo _paControlFsmField;
        private static FieldInfo _paPhase2Field;
        private static FieldInfo _paPhase3Field;
        private static FieldInfo _paPhase4Field;
        private static FieldInfo _paPhase2ThreshField;
        private static FieldInfo _paPhase3ThreshField;
        private static FieldInfo _paPhase4ThreshField;
        private static FieldInfo _paDeadField;
        private static FieldInfo _paCustomBehavCrossSlashField;
        private static FieldInfo _paCustomBehavTeleportingField;
        private static MethodInfo _paPhaseCheckMi;
        private static bool _paPhase2WatchRunning;
        private static int _paDiagEventCount;
        private static int _paBelowP2Hits;
        private static float _paBelowP2FirstTime;
        private static int _paBelowP3Hits;
        private static float _paBelowP3FirstTime;
        private static bool _paPhase2FallbackFired;
        private static bool _paPhase3FallbackFired;
        private static readonly string[] _paPhaseCheckStateNames = new string[]
        {
            "Set DiveSlash", "Set Dash Attack", "Set Wind Slash", "Set CrossSlash", "Set Rising Slash"
        };

        private static void EnsurePaleAutomatonPhaseFieldsResolved()
        {
            if (_paPhaseFieldsLookedUp) return;
            _paPhaseFieldsLookedUp = true;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var pluginType = HarmonyBridge.ResolveTypeRobust("PaleAutomaton.PaleAutomatonPlugin");
                if (pluginType != null)
                {
                    _paHealthManagerField = pluginType.GetField("healthManager", SFlags);
                    _paControlFsmField = pluginType.GetField("controlFsm", SFlags);
                    _paPhase2Field = pluginType.GetField("PHASE_2", SFlags);
                    _paPhase3Field = pluginType.GetField("PHASE_3", SFlags);
                    _paPhase4Field = pluginType.GetField("PHASE_4", SFlags);
                    _paPhase2ThreshField = pluginType.GetField("PHASE_2_THRESHOLD", SFlags);
                    _paPhase3ThreshField = pluginType.GetField("PHASE_3_THRESHOLD", SFlags);
                    _paPhase4ThreshField = pluginType.GetField("PHASE_4_THRESHOLD", SFlags);
                    _paDeadField = pluginType.GetField("dead", SFlags);
                    _paPhaseCheckMi = pluginType.GetMethod("PhaseCheck", SFlags, null, Type.EmptyTypes, null);
                }
                var cbType = HarmonyBridge.ResolveTypeRobust("PaleAutomaton.CustomBehaviour");
                if (cbType != null)
                {
                    _paCustomBehavCrossSlashField = cbType.GetField("crossSlashSetup", SFlags);
                    _paCustomBehavTeleportingField = cbType.GetField("teleporting", SFlags);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "EnsurePaleAutomatonPhaseFieldsResolved", ex);
            }
        }

        public static void ResetPaleAutomatonPhaseDiagState()
        {
            _paDiagEventCount = 0;
            _paBelowP2Hits = 0;
            _paBelowP2FirstTime = -1f;
            _paBelowP3Hits = 0;
            _paBelowP3FirstTime = -1f;
            _paPhase2FallbackFired = false;
            _paPhase3FallbackFired = false;
        }

        private static bool IsFsmImmediateTransitionAction(HutongGames.PlayMaker.FsmStateAction act)
        {
            if (act == null) return false;
            string tn = act.GetType().Name;
            return tn.StartsWith("SendEvent", StringComparison.Ordinal) ||
                   tn.StartsWith("SendRandomEvent", StringComparison.Ordinal) ||
                   string.Equals(tn, "NextFrameEvent", StringComparison.Ordinal);
        }

        private static bool InspectAndHoistPaleAutomatonPhaseCheckStates(PlayMakerFSM ctrlFsm, out int hoistedCount, out string summary)
        {
            hoistedCount = 0;
            bool anyMissingLambda = false;
            var parts = new List<string>(_paPhaseCheckStateNames.Length);
            if (ctrlFsm == null || ctrlFsm.Fsm == null)
            {
                summary = "no-fsm";
                return false;
            }
            for (int i = 0; i < _paPhaseCheckStateNames.Length; i++)
            {
                string stName = _paPhaseCheckStateNames[i];
                var st = ctrlFsm.Fsm.GetState(stName);
                if (st == null)
                {
                    parts.Add($"{stName}=MISSING");
                    continue;
                }
                var acts = PlayMakerHybridCLRBridge.GetStateActionsSafe(st);
                if (acts == null || acts.Length == 0)
                {
                    anyMissingLambda = true;
                    parts.Add($"{stName}=0");
                    continue;
                }
                int firstSendIdx = -1;
                int lastLambdaIdx = -1;
                int lambdaCount = 0;
                for (int a = 0; a < acts.Length; a++)
                {
                    if (acts[a] == null) continue;
                    if (firstSendIdx < 0 && IsFsmImmediateTransitionAction(acts[a]))
                        firstSendIdx = a;
                    if (acts[a] is Silksong.FsmUtil.Actions.LambdaAction)
                    {
                        lambdaCount++;
                        lastLambdaIdx = a;
                    }
                }
                if (lambdaCount == 0)
                {
                    anyMissingLambda = true;
                }
                else if (firstSendIdx >= 0 && lastLambdaIdx > firstSendIdx &&
                         (SilksongMobileAPIPlugin.CfgPaleAutomatonPhaseGuard?.Value ?? true))
                {
                    var reordered = new HutongGames.PlayMaker.FsmStateAction[acts.Length];
                    int dst = 0;
                    for (int a = 0; a < acts.Length; a++)
                    {
                        if (acts[a] is Silksong.FsmUtil.Actions.LambdaAction)
                            reordered[dst++] = acts[a];
                    }
                    for (int a = 0; a < acts.Length; a++)
                    {
                        if (!(acts[a] is Silksong.FsmUtil.Actions.LambdaAction))
                            reordered[dst++] = acts[a];
                    }
                    PlayMakerHybridCLRBridge.SetStateActionsSafe(st, reordered);
                    hoistedCount++;
                }
                parts.Add($"{stName}:{acts.Length}(L={lambdaCount},S={firstSendIdx})");
            }
            summary = string.Join(", ", parts);
            return anyMissingLambda;
        }

        public static void OnPaleAutomatonCombatEvent(HealthManager hm, DamageHero dh, string source)
        {
            if (!IsPaleAutomatonBossSceneActive(dh)) return;
            try
            {
                EnsurePaleAutomatonPhaseFieldsResolved();
                var paHm = _paHealthManagerField?.GetValue(null) as HealthManager;
                if (paHm == null || (UnityEngine.Object)paHm == null) return;
                if (hm != null && !ReferenceEquals(hm, paHm)) return;

                int p2Thresh = _paPhase2ThreshField != null && _paPhase2ThreshField.GetValue(null) is int t2 ? t2 : 1600;
                int p3Thresh = _paPhase3ThreshField != null && _paPhase3ThreshField.GetValue(null) is int t3 ? t3 : 800;
                int p4Thresh = _paPhase4ThreshField != null && _paPhase4ThreshField.GetValue(null) is int t4 ? t4 : 400;
                bool p2 = _paPhase2Field != null && _paPhase2Field.GetValue(null) is bool b2 && b2;
                bool p3 = _paPhase3Field != null && _paPhase3Field.GetValue(null) is bool b3 && b3;
                bool p4 = _paPhase4Field != null && _paPhase4Field.GetValue(null) is bool b4 && b4;

                if (string.Equals(source, "NailClash", StringComparison.Ordinal))
                {
                    int minParry = SilksongMobileAPIPlugin.CfgPaleAutomatonMinParryDamage?.Value ?? 10;
                    if (minParry > 0)
                    {
                        int baseParry = (PlayerData.instance != null) ? (PlayerData.instance.nailDamage / 2) : 0;
                        int extra = minParry - baseParry;
                        if (extra > 0)
                        {
                            int floor = p4 ? 1 : (p3 ? p4Thresh : (p2 ? p3Thresh : p2Thresh));
                            if (paHm.hp > floor)
                            {
                                paHm.hp = Math.Max(floor, paHm.hp - extra);
                            }
                        }
                    }
                }

                int hp = paHm.hp;
                var ctrlFsm = _paControlFsmField?.GetValue(null) as PlayMakerFSM;
                string curState = ctrlFsm != null ? (ctrlFsm.ActiveStateName ?? "") : "";

                if (_paDiagEventCount < 5)
                {
                    _paDiagEventCount++;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonDiag] {source}[{_paDiagEventCount}]: hp={hp}, p2T={p2Thresh}, p3T={p3Thresh}, p2={p2}, p3={p3}, state='{curState}'.");
                }

                if (p2 && !p3 && !_paPhase2WatchRunning && (SilksongMobileAPIPlugin.CfgPaleAutomatonPhaseGuard?.Value ?? true) && SilksongMobileAPIPlugin.Instance != null)
                {
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoWatchPaleAutomatonPhase2Bounds());
                }

                if (!p2 && hp <= p2Thresh)
                {
                    _paBelowP2Hits++;
                    if (_paBelowP2FirstTime < 0f)
                    {
                        _paBelowP2FirstTime = Time.unscaledTime;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonDiag] Crossed Phase 2 threshold via {source}: hp={hp} <= {p2Thresh} (state='{curState}').");
                    }
                    if ((SilksongMobileAPIPlugin.CfgPaleAutomatonPhaseGuard?.Value ?? true) &&
                        !_paPhase2FallbackFired &&
                        _paPhaseCheckMi != null)
                    {
                        _paPhase2FallbackFired = true;
                        GroundPaleAutomatonForPhase2(paHm.gameObject, false, null);
                        _paPhaseCheckMi.Invoke(null, null);
                        bool p2Now = _paPhase2Field != null && _paPhase2Field.GetValue(null) is bool b2n && b2n;
                        string stNow = ctrlFsm != null ? (ctrlFsm.ActiveStateName ?? "") : "";
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonGuard] Invoked fallback PhaseCheck() for Phase 2 via {source} (hp={hp}, thresh2={p2Thresh}, p2Now={p2Now}, state='{stNow}').");
                        if (p2Now && !_paPhase2WatchRunning && SilksongMobileAPIPlugin.Instance != null)
                        {
                            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoWatchPaleAutomatonPhase2Bounds());
                        }
                    }
                }
                else if (p2 && !p3 && hp <= p3Thresh)
                {
                    var csSetup = _paCustomBehavCrossSlashField?.GetValue(null) as UnityEngine.Object;
                    if (csSetup != null)
                    {
                        _paBelowP3Hits++;
                        if (_paBelowP3FirstTime < 0f)
                        {
                            _paBelowP3FirstTime = Time.unscaledTime;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonDiag] Crossed Phase 3 threshold via {source}: hp={hp} <= {p3Thresh} (state='{curState}').");
                        }
                        if ((SilksongMobileAPIPlugin.CfgPaleAutomatonPhaseGuard?.Value ?? true) &&
                            !_paPhase3FallbackFired &&
                            _paPhaseCheckMi != null)
                        {
                            _paPhase3FallbackFired = true;
                            _paPhaseCheckMi.Invoke(null, null);
                            bool p3Now = _paPhase3Field != null && _paPhase3Field.GetValue(null) is bool b3n && b3n;
                            string stNow = ctrlFsm != null ? (ctrlFsm.ActiveStateName ?? "") : "";
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonGuard] Invoked fallback PhaseCheck() for Phase 3 via {source} (hp={hp}, thresh3={p3Thresh}, p3Now={p3Now}, state='{stNow}').");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "OnPaleAutomatonCombatEvent", ex);
            }
        }

        private static void GroundPaleAutomatonForPhase2(GameObject skGo, bool repositionNearHero, PlayMakerFSM ctrlFsm)
        {
            if (skGo == null || (UnityEngine.Object)skGo == null) return;
            try
            {
                var rb = skGo.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.velocity = Vector2.zero;
                    if (rb.gravityScale <= 0.01f)
                        rb.gravityScale = 1f;
                }
                skGo.transform.localRotation = Quaternion.identity;
                var pos = skGo.transform.position;
                if (repositionNearHero && HeroController.instance != null)
                {
                    float hx = HeroController.instance.transform.position.x;
                    float side = (pos.x >= hx) ? 10f : -10f;
                    skGo.transform.position = new Vector3(hx + side, 12.9413f, pos.z);
                    if (ctrlFsm != null && ctrlFsm.Fsm != null)
                    {
                        ctrlFsm.Fsm.ManualUpdate = false;
                        ctrlFsm.SetState("First Idle");
                    }
                }
                else if (pos.y > 14.5f || pos.y < 10.5f)
                {
                    skGo.transform.position = new Vector3(pos.x, 12.9413f, pos.z);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "GroundPaleAutomatonForPhase2", ex);
            }
        }

        private static IEnumerator CoWatchPaleAutomatonPhase2Bounds()
        {
            if (_paPhase2WatchRunning) yield break;
            _paPhase2WatchRunning = true;
            var wait = new WaitForSeconds(0.5f);
            float lastRescueTime = -10f;
            try
            {
                while (true)
                {
                    yield return wait;
                    if (!(SilksongMobileAPIPlugin.CfgPaleAutomatonPhaseGuard?.Value ?? true))
                        break;
                    var sc = SceneManager.GetActiveScene();
                    if (!sc.IsValid() || !string.Equals(sc.name, "Arborium_11", StringComparison.Ordinal))
                        break;

                    EnsurePaleAutomatonPhaseFieldsResolved();
                    bool p3 = _paPhase3Field != null && _paPhase3Field.GetValue(null) is bool b3 && b3;
                    bool dead = _paDeadField != null && _paDeadField.GetValue(null) is bool bd && bd;
                    if (p3 || dead) break;

                    bool p2 = _paPhase2Field != null && _paPhase2Field.GetValue(null) is bool b2 && b2;
                    if (!p2) continue;

                    bool tp = _paCustomBehavTeleportingField != null && _paCustomBehavTeleportingField.GetValue(null) is bool bt && bt;
                    if (tp) continue;

                    var sk = _paSongKnightField?.GetValue(null) as GameObject;
                    var hc = HeroController.instance;
                    if (sk == null || (UnityEngine.Object)sk == null || !sk.activeInHierarchy || hc == null)
                        continue;

                    var pos = sk.transform.position;
                    float dx = Math.Abs(pos.x - hc.transform.position.x);
                    bool oob = pos.y > 30f || pos.y < 8f || dx > 45f;
                    if (oob && (Time.unscaledTime - lastRescueTime) >= 1.0f)
                    {
                        lastRescueTime = Time.unscaledTime;
                        var ctrlFsm = _paControlFsmField?.GetValue(null) as PlayMakerFSM;
                        string prevState = ctrlFsm != null ? (ctrlFsm.ActiveStateName ?? "") : "";
                        GroundPaleAutomatonForPhase2(sk, true, ctrlFsm);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonGuard] Phase 2 OOB rescue: pos=({pos.x:F1},{pos.y:F1}), dx={dx:F1}, prevState='{prevState}' -> grounded at {sk.transform.position}.");
                    }
                }
            }
            finally
            {
                _paPhase2WatchRunning = false;
            }
        }

        public sealed class RebelliousSentinelArenaGuard : MonoBehaviour
        {
            // Trigger re-entry ONLY when Song Knight is strictly outside Hang_17b's outer walls/floor/ceiling.
            // Never touches position, velocity, or FSM during normal attacks inside [27.5..51.5, 4.0..25.0].
            private const float OobMinX = 27.5f;
            private const float OobMaxX = 51.5f;
            private const float OobMinY = 4.0f;
            private const float OobMaxY = 25.0f;
            private const float SafeMinX = 32.0f;
            private const float SafeMaxX = 47.0f;
            private const float ReentryFloorY = 6.2f;
            private const float RescueCooldown = 0.75f;

            private PlayMakerFSM _controlFsm;
            private FieldInfo _hpRecheckField;
            private FieldInfo _rotateDoneField;
            private bool _armed;
            private float _lastRescueTime = -10f;
            private int _rescueCount;

            private void Awake()
            {
                try
                {
                    _controlFsm = FSMUtility.LocateMyFSM(gameObject, "Control");
                    var ssType = HarmonyBridge.ResolveTypeRobust("RebelliousSecondSentinel.SecondSentinel");
                    if (ssType != null)
                    {
                        const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                        _hpRecheckField = ssType.GetField("hpRecheck", SFlags);
                        _rotateDoneField = ssType.GetField("rotateDone", SFlags);
                    }
                }
                catch { }
            }

            private void LateUpdate()
            {
                var tr = transform;
                Vector3 pos = tr.position;

                // Stay disarmed during intro/spawn so Song Knight enters the arena naturally.
                if (!_armed)
                {
                    bool fightStarted = _hpRecheckField == null ||
                        (_hpRecheckField.GetValue(null) is bool b && b);
                    if (fightStarted && pos.x >= SafeMinX && pos.x <= SafeMaxX && pos.y >= 5.5f && pos.y <= 19.0f)
                        _armed = true;
                    return;
                }

                if (pos.x < OobMinX || pos.x > OobMaxX || pos.y < OobMinY || pos.y > OobMaxY)
                {
                    float now = Time.time;
                    if (now - _lastRescueTime < RescueCooldown) return;
                    _lastRescueTime = now;

                    // Cleanly stop RotateOverTime if it was the coroutine carrying him out of bounds.
                    try { _rotateDoneField?.SetValue(null, true); } catch { }

                    float rx = Mathf.Clamp(pos.x, SafeMinX, SafeMaxX);
                    tr.localEulerAngles = Vector3.zero;
                    tr.position = new Vector3(rx, ReentryFloorY, pos.z);

                    // Re-enter the room immediately performing an attack so he never stalls or slides.
                    if (_controlFsm != null)
                    {
                        string reentryState = ((_rescueCount++ & 1) == 0) ? "Dash Slash Antic" : "CS Jump Antic";
                        try { _controlFsm.SetState(reentryState); } catch { }
                    }
                }
            }
        }

        private static Transform FindChildNamedRecursive(Transform parent, string targetName)
        {
            if (parent == null) return null;
            if (string.Equals(parent.name, targetName, StringComparison.Ordinal)) return parent;
            int childCount = parent.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var found = FindChildNamedRecursive(parent.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }

        public static IEnumerator CoAttachRebelliousSentinelArenaGuard(Scene hangScene)
        {
            if (SilksongMobileAPIPlugin.CfgClampRebelliousSentinelArena != null &&
                !SilksongMobileAPIPlugin.CfgClampRebelliousSentinelArena.Value)
                yield break;

            if (HarmonyBridge.ResolveTypeRobust("RebelliousSecondSentinel.SecondSentinel") == null)
                yield break;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!hangScene.IsValid() || !hangScene.isLoaded) yield break;

                Transform skTr = null;
                try
                {
                    var roots = hangScene.GetRootGameObjects();
                    if (roots != null)
                    {
                        for (int i = 0; i < roots.Length && skTr == null; i++)
                        {
                            if (roots[i] != null)
                                skTr = FindChildNamedRecursive(roots[i].transform, "Song Knight");
                        }
                    }
                }
                catch { }

                if (skTr != null && skTr.gameObject != null)
                {
                    var go = skTr.gameObject;
                    if (go.GetComponent<RebelliousSentinelArenaGuard>() == null)
                    {
                        go.AddComponent<RebelliousSentinelArenaGuard>();
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[RebelliousSentinelGuard] Attached OOB attack re-entry guard (trigger outside [27.5..51.5, 4.0..25.0], arms after First Idle) to Song Knight in Hang_17b.");
                    }
                    yield break;
                }

                yield return new WaitForSeconds(0.25f);
            }
        }

        private static IEnumerator CoEnsurePaleAutomatonArenaActive()
        {
            if (_paleAutomatonCoroutineRunning) yield break;
            var paPluginType = HarmonyBridge.ResolveTypeRobust("PaleAutomaton.PaleAutomatonPlugin");
            if (paPluginType == null) yield break;

            _paleAutomatonCoroutineRunning = true;
            ResetPaleAutomatonPhaseDiagState();
            try
            {
                SyncPaleAutomatonParryFreezeSetting();
                if (PlayerData.instance != null)
                    PlayerData.instance.encounteredSongChevalierBoss = true;
                BindAllModManagedAssetsFromTemplateCache();
            }
            catch (System.Exception __apiEx_104) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3699", __apiEx_104); }

            bool configuredFsmOnce = false;
            bool nudgedFromPauseOnce = false;
            bool wokeAutomatonOnce = false;
            int postWakeSteps = 0;

            for (int step = 0; step < 240; step++)
            {
                yield return new WaitForSeconds(0.25f);
                var arbScene = SceneManager.GetSceneByName("Arborium_11");
                if (!arbScene.IsValid() || !arbScene.isLoaded)
                {
                    _paleAutomatonCoroutineRunning = false;
                    yield break;
                }

                try
                {
                    if (configuredFsmOnce && wokeAutomatonOnce && postWakeSteps >= 16)
                    {
                        _paleAutomatonCoroutineRunning = false;
                        yield break;
                    }

                    if (PlayerData.instance != null)
                        PlayerData.instance.encounteredSongChevalierBoss = true;

                    if (step <= 2)
                    {
                        BindAllModManagedAssetsFromTemplateCache();
                    }

                    var merchantParent = GameObject.Find("Merchant Quest Parent");
                    if (merchantParent != null && merchantParent.transform.childCount > 0)
                    {
                        var child0 = merchantParent.transform.GetChild(0);
                        if (child0 != null && child0.gameObject.activeSelf)
                        {
                            child0.gameObject.SetActive(false);
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Deactivated Merchant Quest Parent child 0 for Pale Automaton arena.");
                        }
                    }

                    const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var skGo = paPluginType.GetField("songKnight", SFlags)?.GetValue(null) as GameObject;
                    var ctrlFsm = paPluginType.GetField("controlFsm", SFlags)?.GetValue(null) as PlayMakerFSM;
                    if (skGo == null && step <= 8)
                    {
                        var allFsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
                        if (allFsms != null)
                        {
                            for (int i = 0; i < allFsms.Length; i++)
                            {
                                var f = allFsms[i];
                                if (f != null && f.gameObject != null && f.FsmName == "Control" &&
                                    (f.gameObject.name.StartsWith("Song Knight", StringComparison.OrdinalIgnoreCase) ||
                                     f.gameObject.name.StartsWith("Second Sentinel", StringComparison.OrdinalIgnoreCase)))
                                {
                                    skGo = f.gameObject;
                                    ctrlFsm = f;
                                    break;
                                }
                            }
                        }
                    }
                    if (skGo == null && step == 2)
                    {
                        // If PaleAutomaton's sceneLoaded handler missed spawning because template wasn't bound yet, invoke its sceneLoaded callback once
                        MethodInfo awakeSceneMi = null;
                        foreach (var m in paPluginType.GetMethods(SFlags))
                        {
                            var pars = m.GetParameters();
                            if (pars.Length == 2 && pars[0].ParameterType == typeof(Scene) && pars[1].ParameterType == typeof(LoadSceneMode))
                            {
                                awakeSceneMi = m;
                                break;
                            }
                        }
                        if (awakeSceneMi == null)
                        {
                            foreach (var nested in paPluginType.GetNestedTypes(SFlags))
                            {
                                foreach (var m in nested.GetMethods(SFlags))
                                {
                                    var pars = m.GetParameters();
                                    if (pars.Length == 2 && pars[0].ParameterType == typeof(Scene) && pars[1].ParameterType == typeof(LoadSceneMode))
                                    {
                                        awakeSceneMi = m;
                                        break;
                                    }
                                }
                                if (awakeSceneMi != null) break;
                            }
                        }
                        var instProp = paPluginType.GetProperty("Instance", SFlags);
                        var instField = paPluginType.GetField("Instance", SFlags) ?? paPluginType.GetField("instance", SFlags);
                        object paInst = instProp != null ? instProp.GetValue(null, null) : instField?.GetValue(null);
                        if (paInst == null && SilksongMobileAPIPlugin.Instance != null)
                            paInst = UnityEngine.Object.FindObjectOfType(paPluginType);
                        if (awakeSceneMi != null && (awakeSceneMi.IsStatic || paInst != null))
                        {
                            try { awakeSceneMi.Invoke(awakeSceneMi.IsStatic ? null : paInst, new object[] { arbScene, LoadSceneMode.Additive }); } catch (System.Exception __apiEx_105) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3809", __apiEx_105); }
                        }
                    }
                    if (skGo == null || ctrlFsm == null || ctrlFsm.Fsm == null) continue;

                    if (!configuredFsmOnce)
                    {
                        var skBossSceneGo = paPluginType.GetField("songKnightBossScene", SFlags)?.GetValue(null) as GameObject;
                        if (skBossSceneGo != null)
                        {
                            StripPaleAutomatonDeactivateComponents(skBossSceneGo);
                            if (skBossSceneGo.scene != arbScene && arbScene.IsValid() && arbScene.isLoaded)
                            {
                                try { SceneManager.MoveGameObjectToScene(skBossSceneGo, arbScene); } catch { }
                            }
                            if (!skBossSceneGo.activeSelf) skBossSceneGo.SetActive(true);
                        }
                        StripPaleAutomatonDeactivateComponents(skGo);
                        if (!skGo.activeSelf) skGo.SetActive(true);
                        if (!skGo.activeInHierarchy)
                        {
                            skGo.transform.SetParent(null, true);
                            if (arbScene.IsValid() && arbScene.isLoaded && skGo.scene != arbScene)
                            {
                                try { SceneManager.MoveGameObjectToScene(skGo, arbScene); } catch { }
                            }
                            skGo.SetActive(true);
                        }
                        RestoreTemplateComponentsEnabled(skGo);
                    }

                    bool forcedFsmInitThisTick = false;
                    if (!ctrlFsm.Fsm.Started)
                    {
                        if (!ctrlFsm.enabled) ctrlFsm.enabled = true;
                        if (step >= 2)
                        {
                            try
                            {
                                ctrlFsm.Fsm.Init(ctrlFsm);
                                ctrlFsm.Fsm.Start();
                                forcedFsmInitThisTick = true;
                            }
                            catch { }
                        }
                        if (!ctrlFsm.Fsm.Started) continue;
                    }

                    if (!configuredFsmOnce)
                    {
                        configuredFsmOnce = true;
                        var sprites = skGo.GetComponentsInChildren<tk2dBaseSprite>(true);
                        if (sprites != null)
                        {
                            for (int i = 0; i < sprites.Length; i++)
                            {
                                if (sprites[i] != null) sprites[i].SortingOrder = 500;
                            }
                        }

                        var initSt = ctrlFsm.Fsm.GetState("Init");
                        if (initSt != null && initSt.Transitions != null)
                        {
                            for (int t = 0; t < initSt.Transitions.Length; t++)
                            {
                                var tr = initSt.Transitions[t];
                                if (tr != null && tr.EventName == "UNENCOUNTERED")
                                {
                                    tr.ToState = "Encountered";
                                    tr.ToFsmState = ctrlFsm.Fsm.GetState("Encountered");
                                }
                            }
                        }

                        // Verify SetupPaleAutomaton() FsmUtil modifications survived Fsm.Init/Start.
                        // If Enc Wake lacks its LambdaAction (e.g., wiped by Fsm.Init or aborted before init),
                        // re-invoke SetupPaleAutomaton() once BEFORE WAKE so custom attacks, FancyZoomOut, and SetupArena run!
                        int encWakeLambdas = 0;
                        bool reappliedSetup = false;
                        try
                        {
                            var encWakeSt = ctrlFsm.Fsm.GetState("Enc Wake");
                            if (encWakeSt != null)
                            {
                                var ewActs = PlayMakerHybridCLRBridge.GetStateActionsSafe(encWakeSt);
                                if (ewActs != null)
                                {
                                    for (int ai = 0; ai < ewActs.Length; ai++)
                                    {
                                        if (ewActs[ai] is Silksong.FsmUtil.Actions.LambdaAction) encWakeLambdas++;
                                    }
                                }
                            }
                            if (encWakeLambdas == 0)
                            {
                                var setupMi = paPluginType.GetMethod("SetupPaleAutomaton", SFlags);
                                if (setupMi != null)
                                {
                                    setupMi.Invoke(null, null);
                                    reappliedSetup = true;
                                    encWakeLambdas = 0;
                                    if (encWakeSt != null)
                                    {
                                        var ewActsAfter = PlayMakerHybridCLRBridge.GetStateActionsSafe(encWakeSt);
                                        if (ewActsAfter != null)
                                        {
                                            for (int ai = 0; ai < ewActsAfter.Length; ai++)
                                            {
                                                if (ewActsAfter[ai] is Silksong.FsmUtil.Actions.LambdaAction) encWakeLambdas++;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception exSetup)
                        {
                            SilksongMobileAPIPlugin.LogModException("PaleAutomaton", "VerifyOrReapplySetupPaleAutomaton", exSetup);
                        }
                        int hoistedPhaseStates = 0;
                        string phaseStatesSummary = "";
                        try
                        {
                            bool missingPhaseLambda = InspectAndHoistPaleAutomatonPhaseCheckStates(ctrlFsm, out hoistedPhaseStates, out phaseStatesSummary);
                            if (missingPhaseLambda && !reappliedSetup)
                            {
                                var setupMi2 = paPluginType.GetMethod("SetupPaleAutomaton", SFlags);
                                if (setupMi2 != null)
                                {
                                    setupMi2.Invoke(null, null);
                                    reappliedSetup = true;
                                    InspectAndHoistPaleAutomatonPhaseCheckStates(ctrlFsm, out hoistedPhaseStates, out phaseStatesSummary);
                                }
                            }
                            EnsurePaleAutomatonPhaseFieldsResolved();
                            var hmDiag = _paHealthManagerField?.GetValue(null) as HealthManager;
                            int hpDiag = hmDiag != null ? hmDiag.hp : -1;
                            int p2TDiag = _paPhase2ThreshField != null && _paPhase2ThreshField.GetValue(null) is int dt2 ? dt2 : -1;
                            bool p2Diag = _paPhase2Field != null && _paPhase2Field.GetValue(null) is bool db2 && db2;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PaleAutomatonDiag] Pre-wake: hp={hpDiag}, p2Thresh={p2TDiag}, p2={p2Diag}, hoisted={hoistedPhaseStates}, states=[{phaseStatesSummary}].");
                        }
                        catch (Exception exPhaseDiag)
                        {
                            SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "PaleAutomatonPhaseStateInspect", exPhaseDiag);
                        }
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Configured PaleAutomaton Song Knight in Arborium_11 after Fsm.Started (state='{ctrlFsm.ActiveStateName}', pos={skGo.transform.position}, forcedInit={forcedFsmInitThisTick}, encWakeLambdas={encWakeLambdas}, reappliedSetup={reappliedSetup}).");
                    }

                    if (!wokeAutomatonOnce)
                    {
                        string curState = ctrlFsm.ActiveStateName ?? "";
                        if (curState == "Pause" && step >= 3)
                        {
                            if (!nudgedFromPauseOnce)
                            {
                                nudgedFromPauseOnce = true;
                                var pauseSt = ctrlFsm.Fsm.GetState("Pause");
                                if (pauseSt != null && pauseSt.Transitions != null && pauseSt.Transitions.Length > 0 && pauseSt.Transitions[0] != null)
                                {
                                    string ev = pauseSt.Transitions[0].EventName;
                                    if (!string.IsNullOrEmpty(ev)) ctrlFsm.SendEvent(ev);
                                }
                                if (ctrlFsm.ActiveStateName == "Pause")
                                {
                                    ctrlFsm.SendEvent("FINISHED");
                                }
                                if (ctrlFsm.ActiveStateName == "Pause")
                                {
                                    if (ctrlFsm.Fsm.GetState("Init") != null)
                                        ctrlFsm.SetState("Init");
                                    else if (ctrlFsm.Fsm.GetState("Encountered") != null)
                                        ctrlFsm.SetState("Encountered");
                                }
                                var mr = skGo.GetComponent<MeshRenderer>();
                                if (mr != null)
                                {
                                    mr.enabled = true;
                                    mr.forceRenderingOff = false;
                                }
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Nudged PaleAutomaton Song Knight out of 'Pause' -> '{ctrlFsm.ActiveStateName}' at {skGo.transform.position}.");
                            }
                            else if (step >= 6)
                            {
                                if (ctrlFsm.Fsm.GetState("Encountered") != null)
                                    ctrlFsm.SetState("Encountered");
                                else if (ctrlFsm.Fsm.GetState("Init") != null)
                                    ctrlFsm.SetState("Init");
                            }
                            curState = ctrlFsm.ActiveStateName ?? "";
                        }

                        if (curState == "Dormant" || curState == "Encountered" || curState == "Enc Dormant" || curState == "Init")
                        {
                            var gspGo = paPluginType.GetField("groundSpikesParent", SFlags)?.GetValue(null) as GameObject;
                            bool spikesReady = gspGo != null && gspGo.transform.childCount >= 18;
                            var hc = HeroController.SilentInstance;
                            if (hc != null && (spikesReady || step >= 8))
                            {
                                float dist = Mathf.Abs(hc.transform.position.x - skGo.transform.position.x);
                                if (dist < 22f || hc.transform.position.x >= 102f)
                                {
                                    if (arbScene.IsValid() && arbScene.isLoaded && SceneManager.GetActiveScene() != arbScene)
                                    {
                                        try { SceneManager.SetActiveScene(arbScene); } catch { }
                                    }
                                    ctrlFsm.SendEvent("WAKE");
                                    ctrlFsm.SendEvent("CHALLENGE");
                                    ctrlFsm.SendEvent("BATTLE START");
                                    wokeAutomatonOnce = true;
                                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Sent WAKE to PaleAutomaton Song Knight in Arborium_11 (prev='{curState}' -> now='{ctrlFsm.ActiveStateName}', spikesReady={spikesReady}).");
                                }
                            }
                        }
                        else if (!string.IsNullOrEmpty(curState) && curState != "Pause")
                        {
                            wokeAutomatonOnce = true;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] PaleAutomaton Song Knight active in state='{curState}'.");
                        }
                    }
                    else
                    {
                        postWakeSteps++;
                        // Ensure renderer & tk2dSpriteAnimator stay enabled after Enc Wake
                        var mrPost = skGo.GetComponent<MeshRenderer>();
                        if (mrPost != null)
                        {
                            if (!mrPost.enabled) mrPost.enabled = true;
                            if (mrPost.forceRenderingOff) mrPost.forceRenderingOff = false;
                        }
                        var animPost = skGo.GetComponent<tk2dSpriteAnimator>();
                        if (animPost != null && !animPost.enabled) animPost.enabled = true;
                        var sprPost = skGo.GetComponent<tk2dSprite>();
                        if (sprPost != null && sprPost.color.a < 0.05f)
                        {
                            var col = sprPost.color;
                            col.a = 1f;
                            sprPost.color = col;
                        }

                        if (postWakeSteps == 2 || postWakeSteps == 10)
                        {
                            var hcPost = HeroController.SilentInstance;
                            string clipName = (animPost != null && animPost.CurrentClip != null) ? animPost.CurrentClip.name : "null";
                            Vector3 bsz = mrPost != null ? mrPost.bounds.size : Vector3.zero;
                            Vector3 hPos = hcPost != null ? hcPost.transform.position : Vector3.zero;
                            float curZoom = (GameCameras.instance != null && GameCameras.instance.tk2dCam != null) ? GameCameras.instance.tk2dCam.ZoomFactor : -1f;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] PaleAutomaton post-wake[{postWakeSteps}]: state='{ctrlFsm.ActiveStateName}', pos={skGo.transform.position}, active={skGo.activeInHierarchy}, mr={(mrPost != null && mrPost.enabled)}, bounds={bsz}, anim={(animPost != null && animPost.enabled)}/{clipName}, zoom={curZoom:F3}, heroPos={hPos}.");
                        }

                        // If stuck in Enc Wake after 2.5s, nudge into Battle Start / Idle cleanly
                        if (postWakeSteps == 10 && ctrlFsm.ActiveStateName == "Enc Wake")
                        {
                            ctrlFsm.SendEvent("FINISHED");
                            if (ctrlFsm.ActiveStateName == "Enc Wake")
                            {
                                if (ctrlFsm.Fsm.GetState("Battle Start") != null)
                                    ctrlFsm.SetState("Battle Start");
                                else if (ctrlFsm.Fsm.GetState("Idle") != null)
                                    ctrlFsm.SetState("Idle");
                            }
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Nudged stalled 'Enc Wake' -> '{ctrlFsm.ActiveStateName}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException("PaleAutomaton", "CoEnsurePaleAutomatonArenaActive", ex);
                }
            }
            _paleAutomatonCoroutineRunning = false;
        }

        private static string _lastHandledSceneName = "";
        private static int _lastHandledSceneFrame = -999;

        public static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            try
            {
                string scName = newScene.name;
                if (string.IsNullOrEmpty(scName)) return;
                int curFrame = Time.frameCount;
                if (string.Equals(_lastHandledSceneName, scName, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(curFrame - _lastHandledSceneFrame) <= 15)
                {
                    return;
                }
                _lastHandledSceneName = scName;
                _lastHandledSceneFrame = curFrame;

                CleanupLingeringExtractionScenesAndModObjects(scName);

                // Clean up or fix any lingering BossRush banner across scene transitions
                try
                {
                    var bannerGo = GameObject.Find("BossRushBanner");
                    if (bannerGo != null)
                    {
                        var texts = bannerGo.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                        if (texts != null)
                        {
                            foreach (var txt in texts)
                            {
                                if (txt != null)
                                {
                                    if (string.Equals(scName, "Menu_Title", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var c = txt.color;
                                        c.a = 0f;
                                        txt.color = c;
                                    }
                                    else
                                    {
                                        var c = txt.color;
                                        c.r = 1f; c.g = 1f; c.b = 1f;
                                        txt.color = c;
                                        if (txt.material != null && txt.material != UnityEngine.UI.Graphic.defaultGraphicMaterial)
                                        {
                                            txt.material = null;
                                        }
                                    }
                                    if (txt.font == null || txt.font.name.IndexOf("LegacyRuntime", StringComparison.OrdinalIgnoreCase) >= 0 || txt.font.material == null || txt.font.material.mainTexture == null)
                                    {
                                        var goodFont = HarmonyBridge.GetFallbackFont();
                                        if (goodFont != null)
                                        {
                                            txt.font = goodFont;
                                            if (!string.IsNullOrEmpty(txt.text))
                                            {
                                                try { goodFont.RequestCharactersInTexture(txt.text, txt.fontSize > 0 ? txt.fontSize : 36); } catch (System.Exception __apiEx_106) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3939", __apiEx_106); }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (string.Equals(scName, "Menu_Title", StringComparison.OrdinalIgnoreCase))
                        {
                            var outlines = bannerGo.GetComponentsInChildren<UnityEngine.UI.Outline>(true);
                            if (outlines != null)
                            {
                                foreach (var ol in outlines)
                                {
                                    if (ol != null)
                                    {
                                        var ec = ol.effectColor;
                                        ec.a = 0f;
                                        ol.effectColor = ec;
                                    }
                                }
                            }

                            try
                            {
                                var bossBannerType = HarmonyLib.AccessTools.TypeByName("BossRush.Run.BossBanner");
                                if (bossBannerType != null)
                                {
                                    var hideTitleMi = bossBannerType.GetMethod("HideTitle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                    hideTitleMi?.Invoke(null, null);
                                }
                            }
                            catch (System.Exception __apiEx_107) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3972", __apiEx_107); }
                        }
                    }
                }
                catch (System.Exception __apiEx_108) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L3976", __apiEx_108); }

                if (string.Equals(scName, "Ant_Queen", StringComparison.OrdinalIgnoreCase))
                {
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureBonemasterShelfPreloaded());
                    }
                }
                else if (string.Equals(scName, "Memory_Ant_Queen", StringComparison.OrdinalIgnoreCase))
                {
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureBonemasterArenaActive());
                    }
                }
                else if (string.Equals(scName, "Song_Tower_01", StringComparison.OrdinalIgnoreCase))
                {
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureLaceBattleActive());
                    }
                }
                else if (string.Equals(scName, "Organ_01", StringComparison.OrdinalIgnoreCase))
                {
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureForsakenSentinelsArenaActive());
                    }
                }
                else if (string.Equals(scName, "Arborium_11", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (PlayerData.instance != null)
                            PlayerData.instance.encounteredSongChevalierBoss = true;
                    }
                    catch (System.Exception __apiEx_109) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4013", __apiEx_109); }
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsurePaleAutomatonArenaActive());
                    }
                }
                else if (string.Equals(scName, "Coral_39", StringComparison.OrdinalIgnoreCase))
                {
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureAeternalEverwatcherArenaActive());
                    }
                }

                if (string.Equals(scName, "Hang_04", StringComparison.OrdinalIgnoreCase) &&
                    SilksongMobileAPIPlugin.Instance != null)
                {
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureNonStopHallsGauntletReady());
                }

                if (!string.IsNullOrEmpty(scName) &&
                    !string.Equals(scName, "Pre_Menu_Intro", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scName, "Menu_Title", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scName, "Quit_To_Menu", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scName, "PermaDeath", StringComparison.OrdinalIgnoreCase) &&
                    SilksongMobileAPIPlugin.Instance != null)
                {
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureBossRushAndFinalJudgeArenaActive(scName));
                }

                if (_pharloomsGloryAssembly == null) return;

                if (IsCurrentSceneInCoral(scName))
                {
                    EnsurePharloomsGloryAudioReloaded();
                    SyncPharloomsGloryAudioAliases();
                    if (SilksongMobileAPIPlugin.Instance != null)
                    {
                        SyncExtractedTemplatesIntoPharloomsGlory();
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsurePharloomsGloryMusic(scName, 0.25f));
                    }
                }
                else
                {
                    if (_pgMusicSource != null && _pgMusicSource.isPlaying) _pgMusicSource.Stop();
                    if (_pgSubSource != null && _pgSubSource.isPlaying) _pgSubSource.Stop();

                    var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                    var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    if (smInst != null)
                    {
                        var setActiveMi = smType.GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (setActiveMi != null)
                        {
                            try { setActiveMi.Invoke(smInst, new object[] { false }); } catch (System.Exception __apiEx_110) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4059", __apiEx_110); }
                        }
                        else
                        {
                            var activeField = smType.GetField("active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            activeField?.SetValue(smInst, false);
                        }

                        var listField = smType.GetField("currentlyAddedGameObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var list = listField?.GetValue(smInst) as IList;
                        if (list != null && list.Count > 0)
                        {
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                var go = list[i] as GameObject;
                                if (go != null) UnityEngine.Object.Destroy(go);
                            }
                            list.Clear();
                        }
                    }

                    var pmType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Managers.PatcherManager", false);
                    var pmInst = pmType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    if (pmInst != null)
                    {
                        var resetColorsMi = pmType.GetMethod("ResetColorValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        try { resetColorsMi?.Invoke(pmInst, null); } catch (System.Exception __apiEx_111) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4085", __apiEx_111); }
                    }
                }
            }
            catch (System.Exception __apiEx_112) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4089", __apiEx_112); }
        }

        public static void NormalizeModCanvasScalers()
        {
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                if (canvases == null) return;
                for (int i = 0; i < canvases.Length; i++)
                {
                    var c = canvases[i];
                    if (c == null || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace) continue;
                    string goName = c.gameObject.name ?? "";
                    if (goName.IndexOf("ModCanvas", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var scaler = c.GetComponent<UnityEngine.UI.CanvasScaler>() ?? c.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
                        if (scaler != null && scaler.uiScaleMode == UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize)
                        {
                            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                            scaler.referenceResolution = new Vector2(1920f, 1080f);
                            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                            scaler.matchWidthOrHeight = 0.5f;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_113) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4116", __apiEx_113); }
        }

        private static readonly Dictionary<string, AsyncOperationHandle> _pinnedRevoltBundleHandles =
            new Dictionary<string, AsyncOperationHandle>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _pinnedRevoltSceneHandles =
            new Dictionary<string, AsyncOperationHandle<SceneInstance>>(StringComparer.OrdinalIgnoreCase);
        private static bool _revoltAreabellBundlesIndexed;
        private static GameObject _cachedRevoltWidowTemplate;
        private static bool _revoltWidowPreloadAttempted;

        public static IEnumerator CoPreloadRevoltWidowTemplate()
        {
            if (_revoltWidowPreloadAttempted || _cachedRevoltWidowTemplate != null) yield break;
            if (GetRevoltPluginType() == null) yield break;
            _revoltWidowPreloadAttempted = true;

            SilksongMobileAPIPlugin.LogDiagnosticInfo("[RevoltBridge] Preloading Belltown_Shrine Widow template at Menu_Title...");
            EnsureRevoltAreabellBundlesPinned();
            foreach (var kv in _pinnedRevoltBundleHandles)
            {
                if (kv.Value.IsValid() && !kv.Value.IsDone)
                    yield return kv.Value;
            }

            string addrKey = "Scenes/Belltown_Shrine";
            SetAllRepackersExtracting(true, "Belltown_Shrine");
            var handle = UnityEngine.AddressableAssets.Addressables.LoadSceneAsync(
                (object)addrKey,
                LoadSceneMode.Additive,
                true,
                100,
                SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
            yield return handle;

            if (handle.IsValid())
            {
                _pinnedRevoltSceneHandles["Belltown_Shrine"] = handle;
                StorePinnedHandleInAllRepackers("belltown_shrine_revolt_scene", handle);
            }

            Scene loadedScene = default;
            try
            {
                loadedScene = handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded
                    ? handle.Result.Scene
                    : SceneManager.GetSceneByName("Belltown_Shrine");

                if (loadedScene.IsValid() && loadedScene.isLoaded)
                {
                    GameObject widowOrig = null;
                    foreach (var root in loadedScene.GetRootGameObjects())
                    {
                        if (root == null) continue;
                        if (root.name == "Black Thread States Thread Only Variant")
                        {
                            var bossSceneTr = root.transform.Find("Normal World/Boss Scene");
                            if (bossSceneTr != null) widowOrig = bossSceneTr.gameObject;
                        }
                    }

                    if (widowOrig != null)
                    {
                        PinAllAssetsOnHierarchy(widowOrig);
                        // Deactivate BEFORE Instantiate so Awake/OnEnable NEVER fire during template cloning!
                        widowOrig.SetActive(false);
                        var tpl = UnityEngine.Object.Instantiate(widowOrig);
                        tpl.name = "Boss Scene";
                        tpl.SetActive(false);
                        string[] childDestroy = new[] { "Gates", "CamLock B", "Intro Spinner", "Bell Bench", "Challenge Region", "Speak Region" };
                        foreach (var cn in childDestroy)
                        {
                            var tr = tpl.transform.Find(cn);
                            if (tr != null)
                            {
                                tr.gameObject.SetActive(false);
                                UnityEngine.Object.Destroy(tr.gameObject);
                            }
                        }
                        UnityEngine.Object.DontDestroyOnLoad(tpl);
                        PinAllAssetsOnHierarchy(tpl);
                        _cachedRevoltWidowTemplate = tpl;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[RevoltBridge] Cached inactive Belltown_Shrine Widow template at Menu_Title.");
                    }

                    foreach (var root in loadedScene.GetRootGameObjects())
                    {
                        if (root != null) root.SetActive(false);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RevoltBridge] CoPreloadRevoltWidowTemplate error: {ex.Message}");
            }

            if (loadedScene.IsValid() && loadedScene.isLoaded)
            {
                AsyncOperation unloadOp = null;
                try { unloadOp = SceneManager.UnloadSceneAsync(loadedScene); } catch { }
                if (unloadOp != null)
                {
                    int w = 0;
                    while (!unloadOp.isDone && w++ < 200) yield return null;
                }
            }
            SetAllRepackersExtracting(false, string.Empty);
        }

        public static void PinRevoltBundleByPrefix(string bundlePathOrPrefix)
        {
            if (string.IsNullOrEmpty(bundlePathOrPrefix)) return;
            try
            {
                string clean = Path.GetFileNameWithoutExtension(bundlePathOrPrefix.Replace('\\', '/')).ToLowerInvariant();
                if (clean.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(0, clean.Length - 7);
                if (string.IsNullOrEmpty(clean)) return;

                foreach (var locator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                {
                    if (locator == null || locator.Keys == null) continue;
                    foreach (var kObj in locator.Keys)
                    {
                        if (!(kObj is string keyStr) || !keyStr.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (keyStr.IndexOf(clean, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            !_pinnedRevoltBundleHandles.ContainsKey(keyStr))
                        {
                            try
                            {
                                var bh = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<IAssetBundleResource>(keyStr);
                                _pinnedRevoltBundleHandles[keyStr] = bh;
                            }
                            catch (System.Exception __apiEx_114) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4150", __apiEx_114); }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_115) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4155", __apiEx_115); }
        }

        private static void EnsureRevoltAreabellBundlesPinned()
        {
            if (_revoltAreabellBundlesIndexed && _pinnedRevoltBundleHandles.Count > 5) return;
            _revoltAreabellBundlesIndexed = true;
            string[] prefixes = new[]
            {
                "areabell",
                "belltown_shrine",
                "spinner_thread_pull"
            };
            foreach (var p in prefixes)
                PinRevoltBundleByPrefix(p);
        }

        public static void RevoltLoadAsset(string assetPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    PinRevoltBundleByPrefix(assetPath);
                }
            }
            catch (System.Exception __apiEx_116) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4181", __apiEx_116); }
        }

        public static System.Threading.Tasks.Task<GameObject> RevoltLoadWidowIntoScene(string sourceScene, Scene targetScene)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<GameObject>();
            if (SilksongMobileAPIPlugin.Instance != null)
            {
                SilksongMobileAPIPlugin.Instance.StartCoroutine(CoRevoltLoadWidowIntoScene(sourceScene, targetScene, tcs));
            }
            else
            {
                tcs.TrySetResult(null);
            }
            return tcs.Task;
        }

        private static IEnumerator CoRevoltLoadWidowIntoScene(string sourceScene, Scene targetScene, System.Threading.Tasks.TaskCompletionSource<GameObject> tcs)
        {
            if (_cachedRevoltWidowTemplate != null && string.Equals(sourceScene, "Belltown_Shrine", StringComparison.OrdinalIgnoreCase))
            {
                GameObject fastClone = null;
                try
                {
                    fastClone = UnityEngine.Object.Instantiate(_cachedRevoltWidowTemplate);
                    fastClone.name = "Boss Scene";
                    fastClone.SetActive(false);
                    if (targetScene.IsValid() && targetScene.isLoaded)
                        SceneManager.MoveGameObjectToScene(fastClone, targetScene);
                    RestoreTemplateComponentsEnabled(fastClone);
                    _revoltActiveInSlab10b = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[RevoltBridge] Cloned Widow instantly from Menu_Title template into Slab_10b (0 in-game scene load).");
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RevoltBridge] Fast template clone error: {ex.Message}");
                }
                yield return null;
                tcs.TrySetResult(fastClone);
                yield break;
            }

            GameObject widowClone = null;
            EnsureRevoltAreabellBundlesPinned();

            foreach (var kv in _pinnedRevoltBundleHandles)
            {
                if (kv.Value.IsValid() && !kv.Value.IsDone)
                    yield return kv.Value;
            }

            string addrKey = "Scenes/" + sourceScene;
            SetAllRepackersExtracting(true, sourceScene);
            var handle = UnityEngine.AddressableAssets.Addressables.LoadSceneAsync(
                (object)addrKey,
                LoadSceneMode.Additive,
                true,
                100,
                SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
            yield return handle;

            if (handle.IsValid())
            {
                _pinnedRevoltSceneHandles[sourceScene] = handle;
                StorePinnedHandleInAllRepackers(sourceScene.ToLowerInvariant() + "_revolt_scene", handle);
            }

            Scene loadedScene = default;
            try
            {
                loadedScene = handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded
                    ? handle.Result.Scene
                    : SceneManager.GetSceneByName(sourceScene);

                if (loadedScene.IsValid() && loadedScene.isLoaded)
                {
                    GameObject widowOrig = null;
                    GameObject managersOrig = null;
                    foreach (var root in loadedScene.GetRootGameObjects())
                    {
                        if (root == null) continue;
                        if (root.name == "Black Thread States Thread Only Variant")
                        {
                            var bossSceneTr = root.transform.Find("Normal World/Boss Scene");
                            if (bossSceneTr != null) widowOrig = bossSceneTr.gameObject;
                        }
                        else if (root.name == "_Managers")
                        {
                            managersOrig = root;
                        }
                    }

                    if (widowOrig != null)
                    {
                        PinAllAssetsOnHierarchy(widowOrig);
                        widowOrig.SetActive(false);
                        widowClone = UnityEngine.Object.Instantiate(widowOrig);
                        widowClone.SetActive(false);
                        string[] childDestroy = new[] { "Gates", "CamLock B", "Intro Spinner", "Bell Bench", "Challenge Region", "Speak Region" };
                        foreach (var cn in childDestroy)
                        {
                            var tr = widowClone.transform.Find(cn);
                            if (tr != null)
                            {
                                tr.gameObject.SetActive(false);
                                UnityEngine.Object.Destroy(tr.gameObject);
                            }
                        }
                        if (targetScene.IsValid() && targetScene.isLoaded)
                        {
                            SceneManager.MoveGameObjectToScene(widowClone, targetScene);
                        }
                        PinAllAssetsOnHierarchy(widowClone);
                    }

                    // NOTE: Do NOT clone _Managers from Belltown_Shrine into Slab_10b (duplicating CustomSceneManager causes IL2CPP SIGABRT).
                    // Deactivate remaining roots in temporary additive scene before unloading scene hierarchy.
                    foreach (var root in loadedScene.GetRootGameObjects())
                    {
                        if (root != null) root.SetActive(false);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RevoltBridge] CoRevoltLoadWidowIntoScene error: {ex.Message}");
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[RevoltBridge] Cloned Widow hierarchy from '{sourceScene}' (widowClone={(widowClone != null ? widowClone.name : "null")}); unloading temporary scene.");
            // Unload ONLY the temporary scene hierarchy via SceneManager (NEVER release the Addressables handle, so Widow's bundles/materials/tk2d sprites stay resident!)
            if (loadedScene.IsValid() && loadedScene.isLoaded)
            {
                AsyncOperation unloadOp = null;
                try { unloadOp = SceneManager.UnloadSceneAsync(loadedScene); } catch (System.Exception __apiEx_117) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4293", __apiEx_117); }
                if (unloadOp != null)
                {
                    int w = 0;
                    while (!unloadOp.isDone && w++ < 200) yield return null;
                }
            }
            SetAllRepackersExtracting(false, string.Empty);

            // Restore FSM/Trigger enabled flags before SetupWidow() runs; let SetupWidow() position & clamp Widow before calling widowClone.SetActive(true).
            if (widowClone != null)
            {
                RestoreTemplateComponentsEnabled(widowClone);
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[RevoltBridge] Unloaded '{sourceScene}'; resuming RevoltAndDevotion.SetupWidow().");
            tcs.TrySetResult(widowClone);
        }

        private static Type _revoltPluginType;
        private static Type GetRevoltPluginType()
        {
            if (_revoltPluginType != null) return _revoltPluginType;
            foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
            {
                if (asm == null) continue;
                var t = asm.GetType("RevoltAndDevotion.RevoltAndDevotionPlugin", false);
                if (t != null)
                {
                    _revoltPluginType = t;
                    return t;
                }
            }
            return null;
        }

        private static bool _revoltActiveInSlab10b = false;
        private static bool _revoltSynced = false;
        private static bool _revoltDeathStopped = false;
        private static int _revoltPollFrame = 0;
        private static FieldInfo _revoltFoundWidowField;
        private static FieldInfo _revoltWidowCtrlField;
        public static void ResetRevoltAndDevotionState()
        {
            // Do not wipe state when additive Belltown_Shrine extraction fires SceneManager.sceneLoaded!
            if (IsExtractingPharloomsGloryScene) return;
            _revoltActiveInSlab10b = false;
            _revoltSynced = false;
            _revoltDeathStopped = false;
            try
            {
                var pt = GetRevoltPluginType();
                if (pt == null) return;
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                pt.GetField("widowControl", SFlags)?.SetValue(null, null);
                pt.GetField("sinnerControl", SFlags)?.SetValue(null, null);
                pt.GetField("widowTransform", SFlags)?.SetValue(null, null);
                pt.GetField("widowHealthManager", SFlags)?.SetValue(null, null);
                pt.GetField("sinnerHealthManager", SFlags)?.SetValue(null, null);
            }
            catch (System.Exception __apiEx_118) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4337", __apiEx_118); }
        }

        public static void EnsureRevoltAndDevotionWidowSynced()
        {
            if (!_revoltActiveInSlab10b) return;
            try
            {
                var pt = GetRevoltPluginType();
                if (pt == null) return;
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                if (_revoltFoundWidowField == null) _revoltFoundWidowField = pt.GetField("foundWidowControl", SFlags);
                if (_revoltFoundWidowField == null || !(bool)_revoltFoundWidowField.GetValue(null)) return;

                if (_revoltWidowCtrlField == null) _revoltWidowCtrlField = pt.GetField("widowControl", SFlags);
                var widowCtrl = _revoltWidowCtrlField?.GetValue(null) as PlayMakerFSM;
                if (widowCtrl == null) return;

                if (_revoltSynced)
                {
                    // Mirror original OnFsmStateEntered "Death Stagger" handler without hooking FsmState.OnEnter globally
                    if (!_revoltDeathStopped && widowCtrl.ActiveStateName == "Death Stagger")
                    {
                        _revoltDeathStopped = true;
                        if (widowCtrl.Fsm != null) widowCtrl.Fsm.manualUpdate = true;
                        var instProp = pt.GetProperty("Instance", SFlags);
                        var stopMethod = pt.GetMethod("StopWidowAfterDeath", SFlags);
                        var instObj = instProp?.GetValue(null) as MonoBehaviour;
                        if (instObj != null && stopMethod != null)
                        {
                            var routine = stopMethod.Invoke(null, null) as IEnumerator;
                            if (routine != null) instObj.StartCoroutine(routine);
                        }
                    }
                    return;
                }

                // Throttle GameObject.Find polling to once every 6 frames while waiting for First Weaver wakeup
                if ((++_revoltPollFrame % 6) != 0) return;

                var fWidowClone = pt.GetField("widowClone", SFlags);
                var fSinnerCtrl = pt.GetField("sinnerControl", SFlags);
                var fFoundSinner = pt.GetField("foundSinnerControl", SFlags);
                var fWidowTr = pt.GetField("widowTransform", SFlags);
                var fWidowHm = pt.GetField("widowHealthManager", SFlags);
                var fSinnerHm = pt.GetField("sinnerHealthManager", SFlags);
                var fPhase1Hp = pt.GetField("PHASE_1_HP", SFlags);

                var widowClone = fWidowClone?.GetValue(null) as GameObject;
                var sinnerCtrl = fSinnerCtrl?.GetValue(null) as PlayMakerFSM;
                var widowTr = fWidowTr?.GetValue(null) as Transform;

                if (widowClone != null && !widowClone.activeSelf)
                {
                    widowClone.SetActive(true);
                }

                // If widowControl is ready, check if First Weaver (sinnerControl) is active in Slab_10b using direct name lookup
                if (widowCtrl != null && sinnerCtrl == null)
                {
                    var fwGo = GameObject.Find("First Weaver") ?? GameObject.Find("Spinner Boss");
                    if (fwGo != null)
                    {
                        var fsm = FSMUtility.LocateMyFSM(fwGo, "Control");
                        if (fsm != null)
                        {
                            string st = fsm.ActiveStateName ?? "";
                            if (!string.IsNullOrEmpty(st) && st != "Init" && st != "Rest" && st != "Dormant" && st != "Sleep")
                            {
                                sinnerCtrl = fsm;
                                fSinnerCtrl?.SetValue(null, sinnerCtrl);
                                fSinnerHm?.SetValue(null, sinnerCtrl.gameObject.GetComponent<HealthManager>());
                                fFoundSinner?.SetValue(null, true);
                            }
                        }
                    }
                }

                bool foundWidow = _revoltFoundWidowField != null && (bool)_revoltFoundWidowField.GetValue(null);
                bool foundSinner = fFoundSinner != null && (bool)fFoundSinner.GetValue(null);

                // Complete the wakeup initialization if First Weaver entered Intro Flourish / Refight Appear before SetupWidow() finished!
                if (foundWidow && foundSinner && widowCtrl != null && sinnerCtrl != null && widowTr == null)
                {
                    if (!widowCtrl.gameObject.activeInHierarchy)
                        widowCtrl.gameObject.SetActive(true);
                    if (!widowCtrl.enabled)
                        widowCtrl.enabled = true;

                    widowTr = widowCtrl.gameObject.transform;
                    fWidowTr?.SetValue(null, widowTr);
                    widowTr.position = new Vector3(32f, 12f, 0f);

                    if (widowCtrl.Fsm != null)
                        widowCtrl.Fsm.manualUpdate = false;

                    widowCtrl.SetState("Intro In");

                    try
                    {
                        var sFake = FSMUtility.LocateMyFSM(sinnerCtrl.gameObject, "Fake Death");
                        if (sFake != null) SafeDisableWatcherStunControlFsm(sFake);
                        var sStun = FSMUtility.LocateMyFSM(sinnerCtrl.gameObject, "Stun Control");
                        if (sStun != null) SafeDisableWatcherStunControlFsm(sStun);

                        var wFake = FSMUtility.LocateMyFSM(widowCtrl.gameObject, "Fake Death");
                        if (wFake != null) SafeDisableWatcherStunControlFsm(wFake);
                        var wStun = FSMUtility.LocateMyFSM(widowCtrl.gameObject, "Stun Control");
                        if (wStun != null) SafeDisableWatcherStunControlFsm(wStun);
                    }
                    catch (System.Exception __apiEx_119) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4422", __apiEx_119); }

                    try
                    {
                        var stF = PlayMakerHybridCLRBridge.GetFsmStateSafe(widowCtrl.Fsm, "Death Stagger F");
                        var actsF = PlayMakerHybridCLRBridge.GetStateActionsSafe(stF);
                        if (actsF != null && actsF.Length > 14 && actsF[14] != null)
                            actsF[14].Enabled = false;

                        var stD = PlayMakerHybridCLRBridge.GetFsmStateSafe(widowCtrl.Fsm, "Death Stagger");
                        var actsD = PlayMakerHybridCLRBridge.GetStateActionsSafe(stD);
                        if (actsD != null && actsD.Length > 18 && actsD[18] != null)
                            actsD[18].Enabled = false;
                    }
                    catch (System.Exception __apiEx_120) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4436", __apiEx_120); }

                    int phase1Hp = fPhase1Hp != null ? (int)fPhase1Hp.GetValue(null) : 1300;
                    var wHm = fWidowHm?.GetValue(null) as HealthManager ?? widowCtrl.gameObject.GetComponent<HealthManager>();
                    var sHm = fSinnerHm?.GetValue(null) as HealthManager ?? sinnerCtrl.gameObject.GetComponent<HealthManager>();
                    if (wHm != null) { fWidowHm?.SetValue(null, wHm); wHm.hp = phase1Hp; }
                    if (sHm != null) { fSinnerHm?.SetValue(null, sHm); sHm.hp = phase1Hp; }

                    if (widowCtrl != null && widowCtrl.Fsm != null && widowCtrl.Fsm.States != null) { foreach (var st in widowCtrl.Fsm.States) { if (st != null && st.Actions != null && st.Actions.Length > 0) PlayMakerHybridCLRBridge.SetStateActionsSafe(st, st.Actions); } }
                    PinAllAssetsOnHierarchy(widowClone ?? widowCtrl.gameObject);
                    _revoltSynced = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[RevoltBridge] Synchronized Widow & First Weaver duo boss wakeup (Intro In at (32, 12, 0)).");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RevoltBridge] EnsureRevoltAndDevotionWidowSynced warning: {ex.Message}");
            }
        }

        private static IEnumerator CoEnsureBossRushAndFinalJudgeArenaActive(string targetSceneName)
        {
            // Ensure BossRush.Plugin.Instance.enabled is true so BossRushRun.Tick() and Watchdog() always run
            try
            {
                var brPluginType = HarmonyBridge.ResolveTypeRobust("BossRush.Plugin");
                if (brPluginType != null)
                {
                    var instProp = brPluginType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var instField = brPluginType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var inst = (instProp != null ? instProp.GetValue(null, null) : instField?.GetValue(null)) as MonoBehaviour;
                    if (inst != null && !inst.enabled)
                    {
                        inst.enabled = true;
                    }
                }
            }
            catch (System.Exception __apiEx_121) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4473", __apiEx_121); }

            yield return new WaitForSeconds(0.55f);
            NormalizeModCanvasScalers();

            var curSc = SceneManager.GetSceneByName(targetSceneName);
            if (!curSc.IsValid() || !curSc.isLoaded)
                curSc = SceneManager.GetSceneByName(targetSceneName);
            if (!curSc.IsValid() || !curSc.isLoaded)
                curSc = SceneManager.GetActiveScene();
            if (!curSc.IsValid() || !string.Equals(curSc.name, targetSceneName, StringComparison.OrdinalIgnoreCase))
                yield break;

            // Ensure all FSMActivators in the arena have enabled their FSMs without double-starting ActivateStaggered
            try
            {
                var actActivatedField = typeof(FSMActivator).GetField("activated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var activators = UnityEngine.Object.FindObjectsOfType<FSMActivator>();
                if (activators != null)
                {
                    for (int i = 0; i < activators.Length; i++)
                    {
                        var act = activators[i];
                        if (act == null) continue;
                        if (act.fsms != null)
                        {
                            for (int j = 0; j < act.fsms.Length; j++)
                            {
                                if (act.fsms[j] != null && !act.fsms[j].enabled)
                                    act.fsms[j].enabled = true;
                            }
                        }
                        if (act.spriteAnim != null && !act.spriteAnim.enabled)
                            act.spriteAnim.enabled = true;
                        bool alreadyActivated = actActivatedField != null && (bool)actActivatedField.GetValue(act);
                        if (!alreadyActivated)
                        {
                            try { act.Activate(); } catch (System.Exception __apiEx_122) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4510", __apiEx_122); }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_123) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4515", __apiEx_123); }

            yield return new WaitForSeconds(1.15f);
            curSc = SceneManager.GetActiveScene();
            if (!curSc.IsValid() || !string.Equals(curSc.name, targetSceneName, StringComparison.OrdinalIgnoreCase))
                yield break;

            try
            {
                var gm = GameManager.instance;
                if (gm != null && !gm.HasFinishedEnteringScene)
                {
                    try { gm.OnNextLevelReady(); } catch (System.Exception __apiEx_124) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4527", __apiEx_124); }
                    try { gm.FinishedEnteringScene(); } catch (System.Exception __apiEx_125) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4528", __apiEx_125); }
                }

                var brRunType = HarmonyBridge.ResolveTypeRobust("BossRush.Run.BossRushRun");
                var bannerType = HarmonyBridge.ResolveTypeRobust("BossRush.Run.BossBanner");
                bool isBrActive = false;
                if (brRunType != null)
                {
                    var activeProp = brRunType.GetProperty("Active", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    isBrActive = activeProp != null && (bool)activeProp.GetValue(null, null);
                }

                if (isBrActive ||
                    string.Equals(targetSceneName, "Coral_Judge_Arena", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetSceneName, "Coral_39", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetSceneName, "Arborium_11", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetSceneName, "Organ_01", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetSceneName, "Memory_Ant_Queen", StringComparison.OrdinalIgnoreCase))
                {
                    if (brRunType != null && isBrActive)
                    {
                        var onFinishedMi = brRunType.GetMethod("OnFinishedEnteringScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { onFinishedMi?.Invoke(null, null); } catch (System.Exception __apiEx_126) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4550", __apiEx_126); }

                        var liftBlackMi = brRunType.GetMethod("LiftBlack", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { liftBlackMi?.Invoke(null, null); } catch (System.Exception __apiEx_127) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4553", __apiEx_127); }
                    }

                    if (bannerType != null)
                    {
                        var fadeFromBlackMi = bannerType.GetMethod("FadeFromBlack", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { fadeFromBlackMi?.Invoke(null, null); } catch (System.Exception __apiEx_128) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4559", __apiEx_128); }

                        var setBlackMi = bannerType.GetMethod("SetBlack", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { setBlackMi?.Invoke(null, new object[] { 0f }); } catch (System.Exception __apiEx_129) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4562", __apiEx_129); }

                        var blackField = bannerType.GetField("_black", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                      ?? bannerType.GetField("_blackImage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (blackField?.GetValue(null) is UnityEngine.UI.Image blackImg && blackImg != null)
                        {
                            blackImg.color = new Color(0f, 0f, 0f, 0f);
                        }
                    }

                    try { gm?.FadeSceneIn(); } catch (System.Exception __apiEx_130) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4572", __apiEx_130); }
                    try { gm?.screenFader_fsm?.SendEvent("SCENE FADE IN"); } catch (System.Exception __apiEx_131) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4573", __apiEx_131); }
                    try { UIManager.instance?.BlankScreen(false); } catch (System.Exception __apiEx_132) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4574", __apiEx_132); }
                    try { GameCameras.instance?.cameraFadeFSM?.SendEventSafe("FADE SCENE IN"); } catch (System.Exception __apiEx_133) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4575", __apiEx_133); }
                    try { HeroController.instance?.RegainControl(); } catch (System.Exception __apiEx_134) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4576", __apiEx_134); }
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[BossRushGuard] Verified arena entry & lifted black screen in {targetSceneName}.");
                }
            }
            catch (System.Exception __apiEx_135) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4580", __apiEx_135); }
        }

        private static IEnumerator CoEnsureNonStopHallsGauntletReady()
        {
            if (HarmonyBridge.ResolveTypeRobust("NonStopHallsGauntlet.Plugin") == null)
                yield break;

            try
            {
                if (PlayerData.instance != null && PlayerData.instance.hang04Battle)
                {
                    PlayerData.instance.hang04Battle = false;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[NonStopHallsGauntlet] Reset PlayerData.hang04Battle = false for Hang_04 endless gauntlet.");
                }
            }
            catch { }

            yield return null;
            yield return new WaitForSeconds(0.15f);

            try
            {
                var sc = SceneManager.GetActiveScene();
                if (!sc.IsValid() || !string.Equals(sc.name, "Hang_04", StringComparison.OrdinalIgnoreCase))
                    yield break;

                if (PlayerData.instance != null)
                    PlayerData.instance.hang04Battle = false;

                var bScenes = UnityEngine.Object.FindObjectsOfType<BattleScene>(true);
                const BindingFlags BFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var fCompleted = typeof(BattleScene).GetField("completed", BFlags);
                var fStarted = typeof(BattleScene).GetField("started", BFlags);
                var fBox = typeof(BattleScene).GetField("boxCollider2D", BFlags);
                var fPoly = typeof(BattleScene).GetField("polygonCollider2D", BFlags);

                for (int i = 0; i < bScenes.Length; i++)
                {
                    var bs = bScenes[i];
                    if (bs == null || bs.gameObject == null) continue;
                    if (!string.Equals(bs.gameObject.scene.name, "Hang_04", StringComparison.OrdinalIgnoreCase)) continue;

                    bool wasCompleted = fCompleted != null && fCompleted.GetValue(bs) is bool c && c;
                    if (wasCompleted || !bs.gameObject.activeSelf)
                    {
                        fCompleted?.SetValue(bs, false);
                        fStarted?.SetValue(bs, false);
                        bs.gameObject.SetActive(true);
                        if (fBox?.GetValue(bs) is Collider2D boxC && boxC != null) boxC.enabled = true;
                        if (fPoly?.GetValue(bs) is Collider2D polyC && polyC != null) polyC.enabled = true;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[NonStopHallsGauntlet] Re-armed completed BattleScene '{bs.gameObject.name}' in Hang_04 so endless waves trigger on entry.");
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NonStopHallsGauntlet] Hang_04 arming notice: {ex.Message}");
            }
        }

        private static IEnumerator CoEnsureLaceBattleActive()
        {
            yield return new WaitForSeconds(0.35f);
            for (int retry = 0; retry < 12; retry++)
            {
                var curSc = SceneManager.GetActiveScene();
                if (!curSc.IsValid() || !string.Equals(curSc.name, "Song_Tower_01", StringComparison.OrdinalIgnoreCase))
                    yield break;

                var laceGo = GameObject.Find("Lace Boss2 New") ?? GameObject.Find("Lace Boss2") ?? GameObject.Find("Lace");
                if (laceGo != null)
                {
                    try
                    {
                        var hm = laceGo.GetComponent<HealthManager>();
                        if (hm != null)
                        {
                            hm.invincible = false;
                        }

                        var fsms = laceGo.GetComponents<PlayMakerFSM>();
                        if (fsms != null)
                        {
                            foreach (var fsm in fsms)
                            {
                                if (fsm != null && (fsm.FsmName == "Control" || fsm.FsmName == "Battle Control"))
                                {
                                    string st = fsm.ActiveStateName;
                                    if (st == "Init" || st == "Pause" || st == "Dormant" || st == "Intro" || st == "Sleep" || st == "Wait")
                                    {
                                        fsm.SendEvent("ENTER");
                                        fsm.SendEvent("WAKE");
                                        fsm.SendEvent("START");
                                        fsm.SendEvent("BATTLE START");
                                    }
                                }
                            }
                        }

                        var bossSceneGo = GameObject.Find("Boss Scene");
                        if (bossSceneGo != null)
                        {
                            var bsFsms = bossSceneGo.GetComponents<PlayMakerFSM>();
                            if (bsFsms != null)
                            {
                                foreach (var bfsm in bsFsms)
                                {
                                    if (bfsm != null)
                                    {
                                        bfsm.SendEvent("ENTER");
                                        bfsm.SendEvent("START");
                                        bfsm.SendEvent("StartBattle");
                                    }
                                }
                            }
                        }
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Ensured Lace Boss2 battle is active & vulnerable in Song_Tower_01.");
                    }
                    catch (System.Exception __apiEx_136) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4641", __apiEx_136); }
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        private static bool TryGetPharloomsGloryLightingState(out Color? heroLightColor, out Color? ambientColor, out float? ambientIntensity, out float? saturation)
        {
            heroLightColor = null;
            ambientColor = null;
            ambientIntensity = null;
            saturation = null;
            if (_pharloomsGloryAssembly == null) return false;
            try
            {
                var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (smInst == null) return false;
                var activeField = smType.GetField("active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (activeField == null || !(bool)activeField.GetValue(smInst)) return false;

                var pmType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Managers.PatcherManager", false);
                var pmInst = pmType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (pmInst == null) return false;

                heroLightColor = pmType.GetField("customHeroLightColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pmInst) as Color?;
                ambientColor = pmType.GetField("customAmbientLightColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pmInst) as Color?;
                ambientIntensity = pmType.GetField("customAmbientLightIntensity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pmInst) as float?;
                saturation = pmType.GetField("customSaturation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pmInst) as float?;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CustomSceneManager_SetLighting_Postfix()
        {
            try
            {
                if (!TryGetPharloomsGloryLightingState(out _, out var ambColor, out var ambInt, out _)) return;
                if (!ambColor.HasValue && !ambInt.HasValue) return;

                var csmType = typeof(CustomSceneManager);
                var colField = csmType.GetField("_currentAmbientLightColor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var intField = csmType.GetField("_currentAmbientLightIntensity", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var mixField = csmType.GetField("AmbientIntesityMix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                Color finalColor = ambColor ?? (colField != null ? (Color)colField.GetValue(null) : RenderSettings.ambientLight);
                float finalIntensity = ambInt ?? (intField != null ? (float)intField.GetValue(null) : 1f);
                float mix = mixField != null ? (float)mixField.GetValue(null) : 0.5f;

                colField?.SetValue(null, finalColor);
                intField?.SetValue(null, finalIntensity);
                float factor = Mathf.Lerp(1f, finalIntensity, mix);
                RenderSettings.ambientLight = new Color(finalColor.r * factor, finalColor.g * factor, finalColor.b * factor, 1f);
                RenderSettings.ambientIntensity = 1f;
            }
            catch (System.Exception __apiEx_137) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4701", __apiEx_137); }
        }

        public static void CustomSceneManager_AdjustSaturation_Postfix(CustomSceneManager __instance, ref float __result)
        {
            try
            {
                if (!TryGetPharloomsGloryLightingState(out _, out _, out _, out var sat) || !sat.HasValue) return;
                float val = sat.Value;
                if (__instance != null && !__instance.ignorePlatformSaturationModifiers)
                    val = CustomSceneManager.AdjustSaturationForPlatform(val, __instance.mapZone);
                __result = val;
                ApplyPharloomsGloryLightingImmediately();
            }
            catch (System.Exception __apiEx_138) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4715", __apiEx_138); }
        }

        public static void HeroLight_ApplyColor_Postfix(HeroLight __instance)
        {
            if (__instance == null) return;
            try
            {
                if (!TryGetPharloomsGloryLightingState(out var heroColor, out _, out _, out _) || !heroColor.HasValue) return;
                Color c = heroColor.Value;
                var hlType = typeof(HeroLight);
                var srField = hlType.GetField("spriteRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var donutField = hlType.GetField("heroLightDonut", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var curveField = hlType.GetField("heroLightDonutAlphaCurve", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var sr = srField?.GetValue(__instance) as SpriteRenderer;
                if (sr != null) sr.color = c;

                var donut = donutField?.GetValue(__instance) as SpriteRenderer;
                if (donut != null)
                {
                    var dc = donut.color;
                    var curve = curveField?.GetValue(__instance) as AnimationCurve;
                    if (curve != null) dc.a = curve.Evaluate(c.a);
                    else dc.a = c.a;
                    donut.color = dc;
                }
            }
            catch (System.Exception __apiEx_139) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4743", __apiEx_139); }
        }

        public static void ApplyPharloomsGloryLightingImmediately()
        {
            try
            {
                CustomSceneManager_SetLighting_Postfix();
                var hero = HeroController.instance;
                if (hero != null)
                {
                    var hl = hero.GetComponentInChildren<HeroLight>(true);
                    if (hl != null) HeroLight_ApplyColor_Postfix(hl);
                }
            }
            catch (System.Exception __apiEx_140) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4758", __apiEx_140); }
        }


        private static Assembly _paleAutomatonAssembly;
        private static int _paLastAsmCount = -1;
        private static Assembly _lostAndChainedAssembly;
        private static int _lcLastAsmCount = -1;
        public static bool HasLostAndChained()
        {
            if (_lostAndChainedAssembly != null) return true;
            var allAsms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (allAsms.Length == _lcLastAsmCount) return false;
            _lcLastAsmCount = allAsms.Length;
            foreach (var a in allAsms)
            {
                if (a == null) continue;
                string n = a.GetName().Name ?? "";
                if (string.Equals(n, "LostAndChained", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("LostAndChained", StringComparison.OrdinalIgnoreCase))
                {
                    _lostAndChainedAssembly = a;
                    return true;
                }
            }
            return false;
        }

        private static void LcSetActionFloatField(PlayMakerFSM fsm, string stateName, int actionIndex, string fieldName, float value)
        {
            if (fsm == null) return;
            try
            {
                var act = Silksong.FsmUtil.FsmUtil.GetAction(fsm, stateName, actionIndex);
                if (act == null) return;
                var fi = act.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) return;
                if (fi.FieldType == typeof(HutongGames.PlayMaker.FsmFloat))
                    fi.SetValue(act, (HutongGames.PlayMaker.FsmFloat)value);
                else if (fi.FieldType == typeof(float))
                    fi.SetValue(act, value);
            }
            catch { }
        }

        private static GameObject LcFindChildRecursive(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName)) return null;
            var direct = parent.Find(childName);
            if (direct != null) return direct.gameObject;
            for (int i = 0; i < parent.childCount; i++)
            {
                var found = LcFindChildRecursive(parent.GetChild(i), childName);
                if (found != null) return found;
            }
            return null;
        }

        private sealed class LcHandFieldPatch
        {
            public readonly string State;
            public readonly int Index;
            public readonly string Field;
            public readonly float Value;
            public LcHandFieldPatch(string state, int index, string field, float value)
            {
                State = state;
                Index = index;
                Field = field;
                Value = value;
            }
        }

        // Exact 44 hand FSM action field overrides from LostAndChained.Components.GMSMain.PatchHand + Constants.Constraints
        // Encoded as a compile-time const string + lazy parser (0 instructions in AssetHelperBridge..cctor(), <16 HybridCLR stack slots).
        private const string _lcHandPatchSpec =
            "Stomp Aim|1|minValue|3.5;Stomp Aim|1|maxValue|52.5;" +
            "Stomp|1|minValue|1.0;Stomp|1|maxValue|53.0;" +
            "Stomp Dash L|2|minValue|12.0;Stomp Dash L|2|maxValue|32.5;" +
            "Stomp Dash R|2|minValue|34.5;Stomp Dash R|2|maxValue|47.0;" +
            "Swipe Dir|1|compareTo|44.0;Swipe Dir|2|compareTo|28.0;" +
            "Swipe R|3|min|5.6;Swipe R|3|max|6.1;Swipe R|9|min|6.5;Swipe R|9|max|7.0;Swipe R|15|value1|7.5;Swipe R|15|value2|11.15;" +
            "Swipe L|3|min|5.6;Swipe L|3|max|6.1;Swipe L|9|min|6.5;Swipe L|9|max|7.0;Swipe L|15|value1|7.5;Swipe L|15|value2|11.15;" +
            "Swipe High R|4|floatValue|5.0;Swipe High R|10|floatValue|7.0;Swipe High R|16|floatValue|14.0;" +
            "Swipe High L|4|floatValue|5.0;Swipe High L|10|floatValue|7.0;Swipe High L|16|floatValue|14.0;" +
            "Claw Dir|2|minValue|7.0;Claw Dir|2|maxValue|52.0;Claw Dir|4|compareTo|46.0;Claw Dir|5|compareTo|13.0;" +
            "Claw R|2|floatValue|11.5;Claw R|10|floatValue|12.5;Claw R|18|floatValue|11.5;" +
            "Claw L|2|floatValue|11.5;Claw L|10|floatValue|12.5;Claw L|18|floatValue|11.5;" +
            "Idle|5|minValue|8.0;Idle|5|maxValue|51.0;" +
            "Prepare|5|minValue|8.0;Prepare|5|maxValue|51.0;" +
            "Attacking|6|minValue|8.0;Attacking|6|maxValue|51.0";

        private static LcHandFieldPatch[] _lcHandPatches;

        private static LcHandFieldPatch[] GetLcHandPatches()
        {
            if (_lcHandPatches != null) return _lcHandPatches;
            var rows = _lcHandPatchSpec.Split(';');
            var list = new LcHandFieldPatch[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var parts = rows[i].Split('|');
                list[i] = new LcHandFieldPatch(
                    parts[0],
                    int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    parts[2],
                    float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
            }
            _lcHandPatches = list;
            return list;
        }

        private static void LcPatchHandBladesAndAudio(object gmsMain, PlayMakerFSM handFSM)
        {
            if (gmsMain == null || handFSM == null) return;
            var fbM = LcFindChildRecursive(handFSM.transform, "Finger Blade M");
            var fbL = LcFindChildRecursive(handFSM.transform, "Finger Blade L");
            var fbR = LcFindChildRecursive(handFSM.transform, "Finger Blade R");
            if (fbM == null || fbL == null || fbR == null) return;

            var ctrlM = FSMUtility.LocateMyFSM(fbM, "Control");
            var ctrlL = FSMUtility.LocateMyFSM(fbL, "Control");
            var ctrlR = FSMUtility.LocateMyFSM(fbR, "Control");

            const BindingFlags IFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var gmsType = gmsMain.GetType();
            var patchBladeMi = gmsType.GetMethod("PatchFingerBlade", IFlags);
            if (patchBladeMi != null)
            {
                try { if (ctrlM != null) patchBladeMi.Invoke(gmsMain, new object[] { ctrlM }); } catch (Exception exM) { SilksongMobileAPIPlugin.LogModException("LostAndChained", "PatchFingerBlade(M)", exM); }
                try { if (ctrlL != null) patchBladeMi.Invoke(gmsMain, new object[] { ctrlL }); } catch (Exception exL) { SilksongMobileAPIPlugin.LogModException("LostAndChained", "PatchFingerBlade(L)", exL); }
                try { if (ctrlR != null) patchBladeMi.Invoke(gmsMain, new object[] { ctrlR }); } catch (Exception exR) { SilksongMobileAPIPlugin.LogModException("LostAndChained", "PatchFingerBlade(R)", exR); }
            }
            var patchAudioMi = gmsType.GetMethod("PatchFingerBladesAudio", IFlags);
            if (patchAudioMi != null && GameManager.instance != null)
            {
                try
                {
                    var coro = patchAudioMi.Invoke(gmsMain, new object[] { ctrlM, ctrlL, ctrlR }) as IEnumerator;
                    if (coro != null) GameManager.instance.StartCoroutine(coro);
                }
                catch (Exception exA) { SilksongMobileAPIPlugin.LogModException("LostAndChained", "PatchFingerBladesAudio", exA); }
            }
        }

        private static void LcGateRubbleFieldDust(Transform bossScene)
        {
            if (bossScene == null) return;
            try
            {
                var rubbleRoot = bossScene.Find("Rubble Fields");
                if (rubbleRoot == null) return;
                string[] fieldNames = new string[] { "Rubble Field L", "Rubble Field M", "Rubble Field R" };
                int gatedCount = 0;
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    var fieldTr = rubbleRoot.Find(fieldNames[i]);
                    if (fieldTr == null) continue;
                    var bf = fieldTr.Find("Boulder Field");
                    if (bf == null) continue;
                    var dustTr = bf.Find("Dust");
                    var dustFgTr = bf.Find("Dust FG");
                    GameObject dustGo = dustTr != null ? dustTr.gameObject : null;
                    GameObject dustFgGo = dustFgTr != null ? dustFgTr.gameObject : null;
                    if (dustGo == null && dustFgGo == null) continue;

                    // Hide both full-column white dust quads immediately while field is Idle.
                    if (dustGo != null) dustGo.SetActive(false);
                    if (dustFgGo != null) dustFgGo.SetActive(false);

                    var fieldFsm = FSMUtility.LocateMyFSM(fieldTr.gameObject, "FSM");
                    if (fieldFsm != null && fieldFsm.Fsm != null)
                    {
                        var states = fieldFsm.FsmStates;
                        if (states != null)
                        {
                            for (int s = 0; s < states.Length; s++)
                            {
                                var st = states[s];
                                if (st == null) continue;
                                bool isIdle = string.Equals(st.Name, "Idle", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(st.Name, "Init", StringComparison.OrdinalIgnoreCase);
                                if (isIdle)
                                {
                                    Silksong.FsmUtil.FsmUtil.AddMethod(st, () =>
                                    {
                                        if (dustGo != null) dustGo.SetActive(false);
                                        if (dustFgGo != null) dustFgGo.SetActive(false);
                                    }, false);
                                }
                                else
                                {
                                    Silksong.FsmUtil.FsmUtil.InsertMethod(st, 0, () =>
                                    {
                                        if (dustGo != null) dustGo.SetActive(true);
                                        if (dustFgGo != null) dustFgGo.SetActive(true);
                                    }, false);
                                }
                            }
                        }
                    }
                    gatedCount++;
                }
                if (gatedCount > 0)
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Gated Boulder Field Dust/Dust FG on {gatedCount} Rubble Field(s) (hidden in Idle, shown during rock-fall).");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] LcGateRubbleFieldDust warning: {ex.Message}");
            }
        }

        private static IEnumerator LcDisableTempRoarRubbleOnce(Transform bossScene)
        {
            // Runs right after GMSMain.Init() -> ModifyControlActions() -> Helper.CreateTempRoar() creates Temp Roar.
            LcGateRubbleFieldDust(bossScene);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt == 0) yield return null;
                else yield return new WaitForSeconds(0.5f);
                if (bossScene == null) yield break;
                try
                {
                    Transform tempRoar = bossScene.Find("Silk Boss/Roar Point Intro/Temp Roar");
                    if (tempRoar == null)
                    {
                        var foundGo = LcFindChildRecursive(bossScene, "Temp Roar");
                        if (foundGo != null) tempRoar = foundGo.transform;
                    }
                    if (tempRoar == null) continue;

                    int destroyed = 0;
                    string[] targets = new string[] { "Rubble Fall", "Rubble FG" };
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var child = tempRoar.Find(targets[i]);
                        if (child == null) continue;
                        var pss = child.GetComponentsInChildren<ParticleSystem>(true);
                        if (pss != null)
                        {
                            for (int j = 0; j < pss.Length; j++)
                            {
                                if (pss[j] != null) pss[j].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            }
                        }
                        child.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(child.gameObject);
                        destroyed++;
                    }
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Destroyed {destroyed} persistent Temp Roar rubble object(s) on LostAndChained GMS.");
                    if (destroyed > 0) yield break;
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] LcDisableTempRoarRubbleOnce warning: {ex.Message}");
                    yield break;
                }
            }
        }

        public static void LostAndChained_PatchHandSafe(object gmsMain, PlayMakerFSM handFSM)
        {
            if (handFSM == null) return;
            try
            {
                var patches = GetLcHandPatches();
                for (int i = 0; i < patches.Length; i++)
                {
                    var p = patches[i];
                    LcSetActionFloatField(handFSM, p.State, p.Index, p.Field, p.Value);
                }
                LcPatchHandBladesAndAudio(gmsMain, handFSM);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] LostAndChained_PatchHandSafe completed for '{handFSM.gameObject.name}'.");
                if (string.Equals(handFSM.gameObject.name, "Hand R", StringComparison.OrdinalIgnoreCase) && GameManager.instance != null)
                {
                    Transform bossScene = (handFSM.transform.parent != null) ? handFSM.transform.parent.parent : null;
                    if (bossScene != null) GameManager.instance.StartCoroutine(LcDisableTempRoarRubbleOnce(bossScene));
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("LostAndChained", "LostAndChained_PatchHandSafe", ex);
            }
        }

        public static bool HasPaleAutomaton()
        {
            if (_paleAutomatonAssembly != null) return true;
            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (asms.Length == _paLastAsmCount) return false;
            _paLastAsmCount = asms.Length;
            for (int i = 0; i < asms.Length; i++)
            {
                var asm = asms[i];
                if (asm == null) continue;
                string n = asm.GetName().Name ?? "";
                if (string.Equals(n, "PaleAutomaton", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("PaleAutomaton", StringComparison.OrdinalIgnoreCase))
                {
                    _paleAutomatonAssembly = asm;
                    return true;
                }
            }
            return false;
        }

        private static Assembly _phantinelBossAssembly;
        private static int _fsLastAsmCount = -1;
        public static bool HasPhantinelBoss()
        {
            if (_phantinelBossAssembly != null) return true;
            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (asms.Length == _fsLastAsmCount) return false;
            _fsLastAsmCount = asms.Length;
            for (int i = 0; i < asms.Length; i++)
            {
                var asm = asms[i];
                if (asm == null) continue;
                string n = asm.GetName().Name ?? "";
                if (string.Equals(n, "PhantinelBoss", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "ForsakenSentinels", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("PhantinelBoss", StringComparison.OrdinalIgnoreCase))
                {
                    _phantinelBossAssembly = asm;
                    return true;
                }
            }
            return false;
        }

        private static Assembly _aeternalEverwatcherAssembly;
        private static int _ewLastAsmCount = -1;
        public static bool HasAeternalEverwatcher()
        {
            if (_aeternalEverwatcherAssembly != null) return true;
            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (asms.Length == _ewLastAsmCount) return false;
            _ewLastAsmCount = asms.Length;
            for (int i = 0; i < asms.Length; i++)
            {
                var asm = asms[i];
                if (asm == null) continue;
                string n = asm.GetName().Name ?? "";
                if (string.Equals(n, "AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase))
                {
                    _aeternalEverwatcherAssembly = asm;
                    return true;
                }
            }
            return false;
        }

        public static void SyncExtractedTemplatesIntoAeternalEverwatcher()
        {
            if (!HasAeternalEverwatcher()) return;
            try
            {
                EnsureRepackerMembersResolved();
                var templatesDict = (_extractedTemplatesField?.GetValue(null) as IDictionary) ?? _bridgeFallbackTemplates;

                var cbType = _aeternalEverwatcherAssembly.GetType("AeternalEverwatcher.CustomBehaviour", false);
                if (cbType != null)
                {
                    const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var spearMa = cbType.GetField("khannUcSpear", SFlags)?.GetValue(null);
                    if (spearMa != null)
                    {
                        var hasFbField = spearMa.GetType().GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        bool alreadyBound = hasFbField != null && (bool)hasFbField.GetValue(spearMa);
                        if (!alreadyBound && TryBindManagedAssetFromTemplateCache(spearMa, templatesDict))
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Bound AeternalEverwatcher.CustomBehaviour.khannUcSpear from cached Memory_Coral_Tower template.");
                    }

                    var projMa = cbType.GetField("skProjectile", SFlags)?.GetValue(null);
                    if (projMa != null)
                    {
                        var maType = projMa.GetType();
                        var hasFallbackField = maType.GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        var fallbackResField = maType.GetField("FallbackLoadedResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        if (hasFallbackField != null && fallbackResField != null && !(bool)hasFallbackField.GetValue(projMa))
                        {
                            var projGo = ResolveAddressableAsset("Song Knight Projectile", typeof(GameObject)) as GameObject;
                            if (projGo != null)
                            {
                                PinAllAssetsOnHierarchy(projGo);
                                fallbackResField.SetValue(projMa, projGo);
                                hasFallbackField.SetValue(projMa, true);
                                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Bound AeternalEverwatcher.CustomBehaviour.skProjectile from Addressables.");
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_141) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4873", __apiEx_141); }
        }

        private static Type _aePluginTypeCache;
        private static FieldInfo _aeFoundWatcherField;
        private static FieldInfo _aeWatcherHmField;
        private static FieldInfo _aeControlFsmField;
        private static FieldInfo _aeParryCounterField;
        private static FieldInfo _aeEndFightQuotaField;
        private static FieldInfo _aePhase2Field;
        private static FieldInfo _aePhase3Field;
        private static FieldInfo _aeNormalCombatField;

        private static readonly HashSet<string> _aeParryableStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Slash Combo 1", "Slash Combo 2", "Slash Combo 3",
            "Slash Combo 5", "Slash Combo 6", "Slash Combo 7",
            "Slash Combo 9", "Slash Combo 10",
            "F Slash 2", "F Slash 3", "F Slash 4",
            "Uppercut 1", "Uppercut 2", "Uppercut 3",
            "Dig Out Uppercut",
            "Jump Slash Launch", "Jump Slash Air", "Jump Slash New"
        };

        private static void EnsureAeternalEverwatcherFieldsResolved()
        {
            if (_aePluginTypeCache != null || !HasAeternalEverwatcher()) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _aePluginTypeCache = _aeternalEverwatcherAssembly.GetType("AeternalEverwatcher.AeternalEverwatcherPlugin", false);
                var settingsType = _aeternalEverwatcherAssembly.GetType("AeternalEverwatcher.Settings", false);
                if (_aePluginTypeCache != null)
                {
                    _aeFoundWatcherField = _aePluginTypeCache.GetField("foundWatcher", SFlags);
                    _aeWatcherHmField = _aePluginTypeCache.GetField("watcherHealthManager", SFlags)
                                     ?? _aePluginTypeCache.GetField("healthManager", SFlags);
                    _aeControlFsmField = _aePluginTypeCache.GetField("watcherControl", SFlags)
                                      ?? _aePluginTypeCache.GetField("controlFsm", SFlags);
                    _aeParryCounterField = _aePluginTypeCache.GetField("parryCounter", SFlags);
                    _aeEndFightQuotaField = _aePluginTypeCache.GetField("END_FIGHT_QUOTA", SFlags)
                                         ?? settingsType?.GetField("END_FIGHT_QUOTA", SFlags);
                    _aePhase2Field = _aePluginTypeCache.GetField("PHASE_2", SFlags);
                    _aePhase3Field = _aePluginTypeCache.GetField("PHASE_3", SFlags);
                }
                _aeNormalCombatField = settingsType?.GetField("NORMAL_COMBAT", SFlags)
                                    ?? _aePluginTypeCache?.GetField("NORMAL_COMBAT", SFlags);
            }
            catch (System.Exception __apiEx_142) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L4921", __apiEx_142); }
        }

        public static bool IsAeternalEverwatcherInParryableState(PlayMakerFSM ctrlFsm = null)
        {
            EnsureAeternalEverwatcherFieldsResolved();
            try
            {
                if (ctrlFsm == null)
                    ctrlFsm = _aeControlFsmField?.GetValue(null) as PlayMakerFSM;
                if (ctrlFsm == null || ctrlFsm.Fsm == null) return false;
                string st = ctrlFsm.ActiveStateName ?? "";
                return _aeParryableStates.Contains(st);
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldAeternalEverwatcherKeepParryOnlyHpLock()
        {
            EnsureAeternalEverwatcherFieldsResolved();
            if (_aePluginTypeCache == null) return false;
            try
            {
                if (_aeNormalCombatField != null)
                {
                    object ncObj = _aeNormalCombatField.GetValue(null);
                    if (ncObj is bool ncBool && ncBool) return false;
                    if (ncObj != null && ncObj.GetType().Name.StartsWith("ConfigEntry", StringComparison.Ordinal))
                    {
                        var valProp = ncObj.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                        if (valProp != null && valProp.GetValue(ncObj, null) is bool cfgVal && cfgVal)
                            return false;
                    }
                }
                int parryCount = _aeParryCounterField != null ? (int)_aeParryCounterField.GetValue(null) : 0;
                int endQuota = 9;
                if (_aeEndFightQuotaField != null)
                {
                    object eqObj = _aeEndFightQuotaField.GetValue(null);
                    if (eqObj is int eqInt) endQuota = eqInt;
                    else if (eqObj != null && eqObj.GetType().Name.StartsWith("ConfigEntry", StringComparison.Ordinal))
                    {
                        var valProp = eqObj.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                        if (valProp != null && valProp.GetValue(eqObj, null) is int cfgEq)
                            endQuota = cfgEq;
                    }
                }
                bool p2 = _aePhase2Field != null && (bool)_aePhase2Field.GetValue(null);
                bool p3 = _aePhase3Field != null && (bool)_aePhase3Field.GetValue(null);
                return !(parryCount >= endQuota && p2 && p3);
            }
            catch
            {
                return true;
            }
        }

        public static void EnforceAeternalEverwatcherParryOnlyOnHit(HealthManager hm)
        {
            if (hm == null || !HasAeternalEverwatcher()) return;
            try
            {
                EnsureAeternalEverwatcherFieldsResolved();
                var watcherHm = _aeWatcherHmField?.GetValue(null) as HealthManager;
                if (hm != watcherHm && (hm.gameObject == null || hm.gameObject.name != "Coral Warrior Grey"))
                    return;
                if (ShouldAeternalEverwatcherKeepParryOnlyHpLock())
                {
                    // HealthManager.Hit prefix now runs before IsBlockingByDirection, so invincible must remain true
                    // in Parry-Only mode so nail hits bounce off armor (Invincible) instead of calling TakeDamage!
                    hm.invincible = true;
                    hm.invincibleFromDirection = 0;
                    hm.isDead = false;
                    if (hm.hp < 5000) hm.hp = 5000;
                }
            }
            catch (System.Exception __apiEx_143) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5002", __apiEx_143); }
        }

        internal sealed class AeternalEverwatcherParryGuard : MonoBehaviour
        {
            public HealthManager BossHm;
            public PlayMakerFSM BossCtrlFsm;
            private bool _strippedBlockedHitEvents;

            private void Awake()
            {
                if (BossHm == null) BossHm = GetComponent<HealthManager>();
                if (BossCtrlFsm == null) BossCtrlFsm = FSMUtility.LocateMyFSM(gameObject, "Control");
                if (BossHm != null)
                {
                    try
                    {
                        BossHm.TookDamage += OnBossTookDamage;
                    }
                    catch (System.Exception __apiEx_144) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5021", __apiEx_144); }
                }
                EnforceParryState();
            }

            private void OnBossTookDamage()
            {
                EnforceParryState();
            }

            private void LateUpdate()
            {
                EnforceParryState();
            }

            private void StripAllBlockedHitTransitionsIfNeeded()
            {
                if (_strippedBlockedHitEvents || BossCtrlFsm == null || BossCtrlFsm.Fsm == null) return;
                var states = BossCtrlFsm.Fsm.States;
                if (states == null || states.Length == 0) return;
                _strippedBlockedHitEvents = true;
                try
                {
                    for (int i = 0; i < states.Length; i++)
                    {
                        var st = states[i];
                        if (st == null || st.Transitions == null || st.Transitions.Length == 0) continue;
                        int keepCount = 0;
                        for (int t = 0; t < st.Transitions.Length; t++)
                        {
                            if (st.Transitions[t] != null && st.Transitions[t].EventName != "BLOCKED HIT")
                                keepCount++;
                        }
                        if (keepCount != st.Transitions.Length)
                        {
                            var filtered = new HutongGames.PlayMaker.FsmTransition[keepCount];
                            int idx = 0;
                            for (int t = 0; t < st.Transitions.Length; t++)
                            {
                                if (st.Transitions[t] != null && st.Transitions[t].EventName != "BLOCKED HIT")
                                    filtered[idx++] = st.Transitions[t];
                            }
                            st.Transitions = filtered;
                        }
                    }
                }
                catch (System.Exception __apiEx_145) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5072", __apiEx_145); }
            }

            private void EnforceParryState()
            {
                if (BossHm == null) BossHm = GetComponent<HealthManager>();
                if (BossCtrlFsm == null) BossCtrlFsm = FSMUtility.LocateMyFSM(gameObject, "Control");
                if (BossHm == null) return;
                StripAllBlockedHitTransitionsIfNeeded();
                if (ShouldAeternalEverwatcherKeepParryOnlyHpLock())
                {
                    BossHm.invincible = true;
                    BossHm.invincibleFromDirection = 0;
                    BossHm.isDead = false;
                    if (BossHm.hp != 5000)
                        BossHm.hp = 5000;
                }
            }
        }

        public static IEnumerator CoEnsureAeternalEverwatcherArenaActive()
        {
            if (!HasAeternalEverwatcher()) yield break;
            SyncExtractedTemplatesIntoAeternalEverwatcher();
            EnsureAeternalEverwatcherFieldsResolved();

            for (int step = 0; step < 12; step++)
            {
                yield return new WaitForSeconds(0.25f);
                var coralScene = SceneManager.GetSceneByName("Coral_39");
                if (!coralScene.IsValid() || !coralScene.isLoaded)
                    yield break;

                try
                {
                    SyncExtractedTemplatesIntoAeternalEverwatcher();
                    if (step == 2 || step == 5)
                    {
                        try { GameManager.instance?.FadeSceneIn(); } catch (System.Exception __apiEx_146) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5115", __apiEx_146); }
                        try { GameManager.instance?.screenFader_fsm?.SendEvent("SCENE FADE IN"); } catch (System.Exception __apiEx_147) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5116", __apiEx_147); }
                        try { UIManager.instance?.BlankScreen(false); } catch (System.Exception __apiEx_148) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5117", __apiEx_148); }
                        try { GameCameras.instance?.cameraFadeFSM?.SendEventSafe("FADE SCENE IN"); } catch (System.Exception __apiEx_149) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5118", __apiEx_149); }
                        try { HeroController.instance?.RegainControl(); } catch (System.Exception __apiEx_150) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5119", __apiEx_150); }
                    }
                    GameObject bossGo = null;
                    var roots = coralScene.GetRootGameObjects();
                    if (roots != null)
                    {
                        for (int r = 0; r < roots.Length; r++)
                        {
                            var root = roots[r];
                            if (root == null) continue;
                            if (root.name == "Battle Scene" && !root.activeSelf)
                                root.SetActive(true);
                            var hms = root.GetComponentsInChildren<HealthManager>(true);
                            if (hms != null)
                            {
                                for (int i = 0; i < hms.Length; i++)
                                {
                                    var hm = hms[i];
                                    if (hm != null && hm.gameObject != null && hm.gameObject.name == "Coral Warrior Grey")
                                    {
                                        bossGo = hm.gameObject;
                                        break;
                                    }
                                }
                            }
                            if (bossGo != null) break;
                        }
                    }

                    if (bossGo != null)
                    {
                        if (bossGo.transform.parent != null && !bossGo.transform.parent.gameObject.activeSelf)
                            bossGo.transform.parent.gameObject.SetActive(true);
                        if (!bossGo.activeSelf)
                            bossGo.SetActive(true);

                        var guard = bossGo.GetComponent<AeternalEverwatcherParryGuard>();
                        if (guard == null)
                        {
                            guard = bossGo.AddComponent<AeternalEverwatcherParryGuard>();
                            guard.BossHm = bossGo.GetComponent<HealthManager>();
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Attached AeternalEverwatcherParryGuard to Coral Warrior Grey (Parry-Only HP=5000 enforced).");
                        }

                        bool foundWatcher = _aeFoundWatcherField != null && (bool)_aeFoundWatcherField.GetValue(null);
                        if (foundWatcher)
                        {
                            yield break;
                        }

                        if (step >= 2 && !foundWatcher && _aePluginTypeCache != null)
                        {
                            const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                            var awakeSceneMi = _aePluginTypeCache.GetMethod("<Awake>b__23_0", SFlags);
                            var pluginInst = UnityEngine.Object.FindObjectOfType(_aePluginTypeCache);
                            if (awakeSceneMi != null && (awakeSceneMi.IsStatic || pluginInst != null))
                            {
                                try { awakeSceneMi.Invoke(awakeSceneMi.IsStatic ? null : pluginInst, new object[] { coralScene, LoadSceneMode.Single }); } catch (System.Exception __apiEx_151) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5176", __apiEx_151); }
                                yield break;
                            }
                        }
                    }
                }
                catch (System.Exception __apiEx_152) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5182", __apiEx_152); }
            }
        }

        public static void SafeDisableWatcherStunControlFsm(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is PlayMakerFSM fsm)
                {
                    if (fsm.Fsm != null) fsm.Fsm.ManualUpdate = true;
                    try
                    {
                        var go = fsm.gameObject;
                        var hm = go != null ? go.GetComponent<HealthManager>() : null;
                        if (hm != null && string.Equals(fsm.FsmName, "Stun Control", StringComparison.OrdinalIgnoreCase))
                        {
                            var stunField = typeof(HealthManager).GetField("stunControlFsm", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            stunField?.SetValue(hm, null);
                        }
                    }
                    catch { }
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDisableFsmAfterLateUpdate(fsm));
                    return;
                }
                if (SilksongMobileAPIPlugin.Instance != null)
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDestroyAfterLateUpdate(obj));
            }
            catch (System.Exception __apiEx_153) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5201", __apiEx_153); }
        }

        public static void SafeDeactivateAndDestroyLater(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is GameObject go)
                {
                    // CRITICAL: Deactivate synchronously (so cloned templates like AeternalEverwatcher's sandTelegraph have 'damager' inactive),
                    // but do NOT detach transform.parent synchronously! Detaching synchronously mutates parent.childCount mid-method and
                    // shifts sibling indices in PaleAutomaton.<SpawnSongKnight>d__34.MoveNext (GetChild(0), GetChild(1), GetChild(2)).
                    go.SetActive(false);
                }
                else if (obj is PlayMakerFSM pfsm)
                {
                    if (pfsm.Fsm != null) pfsm.Fsm.ManualUpdate = true;
                    pfsm.enabled = false;
                }
                else if (obj is Behaviour beh)
                {
                    beh.enabled = false;
                }
                else if (obj is Collider2D col2d)
                {
                    col2d.enabled = false;
                }
                if (SilksongMobileAPIPlugin.Instance != null)
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDestroyAfterLateUpdate(obj));
            }
            catch (System.Exception __apiEx_154) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5212", __apiEx_154); }
        }

        private static IEnumerator CoDeferredDisableFsmAfterLateUpdate(PlayMakerFSM fsm)
        {
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            try
            {
                if (fsm != null)
                {
                    if (fsm.Fsm != null) fsm.Fsm.ManualUpdate = true;
                    fsm.enabled = false;
                }
            }
            catch (System.Exception __apiEx_155) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5229", __apiEx_155); }
        }

        private static IEnumerator CoDeferredDestroyAfterLateUpdate(UnityEngine.Object obj)
        {
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            try
            {
                if (obj == null) yield break;
                if (obj is GameObject go)
                {
                    go.SetActive(false);
                    UnityEngine.Object.Destroy(go);
                }
                else if (obj is PlayMakerFSM pfsm)
                {
                    if (pfsm.Fsm != null) pfsm.Fsm.ManualUpdate = true;
                    pfsm.enabled = false;
                }
                else if (obj is Behaviour beh)
                {
                    beh.enabled = false;
                }
            }
            catch (System.Exception __apiEx_156) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5244", __apiEx_156); }
        }

        private static Dictionary<string, List<string>> _cachedFullCatalogTransitiveDeps;
        private static FieldInfo _addrDataCatalogTransitiveDepsField;

        public static bool SkipRedundantBuildFastBundleDeps_Prefix()
        {
            try
            {
                if (_cachedFullCatalogTransitiveDeps != null && _cachedFullCatalogTransitiveDeps.Count > 0 && _addrDataCatalogTransitiveDepsField != null)
                {
                    _addrDataCatalogTransitiveDepsField.SetValue(null, _cachedFullCatalogTransitiveDeps);
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void PruneRedundantAssetHelperTaskList(System.Collections.IList taskList)
        {
            if (taskList == null) return;
            for (int i = taskList.Count - 1; i >= 0; i--)
            {
                string tn = taskList[i]?.GetType()?.Name ?? "";
                if (tn.IndexOf("BundleDepsPrecompute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("SceneRepacking", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    taskList.RemoveAt(i);
                }
            }
        }

        public static void PrepopulateAssetHelperCatalogBundleDeps(Assembly assetHelperAsm)
        {
            try
            {
                if (assetHelperAsm == null) return;
                var addrDataType = assetHelperAsm.GetType("Silksong.AssetHelper.Core.AddressablesData", false);
                var depsField = addrDataType?.GetField("_catalogTransitiveDeps", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (depsField == null) return;
                _addrDataCatalogTransitiveDepsField = depsField;

                if (_cachedFullCatalogTransitiveDeps != null && _cachedFullCatalogTransitiveDeps.Count > 0)
                {
                    depsField.SetValue(null, _cachedFullCatalogTransitiveDeps);
                    return;
                }

                string cacheFile = Path.Combine(Paths.BepInExRootPath ?? Application.persistentDataPath, ".cecil_cache_v2963_bundle_deps.bin");
                var fastDeps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                if (File.Exists(cacheFile))
                {
                    try
                    {
                        using (var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var br = new BinaryReader(fs, System.Text.Encoding.UTF8))
                        {
                            int magic = br.ReadInt32();
                            int count = br.ReadInt32();
                            if (magic == 0x49524953 && count > 1000 && count < 100000)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    string k = br.ReadString();
                                    int dCount = br.ReadInt32();
                                    var dList = new List<string>(dCount);
                                    for (int j = 0; j < dCount; j++)
                                        dList.Add(br.ReadString());
                                    fastDeps[k] = dList;
                                }
                            }
                        }
                    }
                    catch
                    {
                        fastDeps.Clear();
                    }
                }

                if (fastDeps.Count == 0)
                {
                    foreach (var loc in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                    {
                        if (loc == null || loc.Keys == null) continue;
                        foreach (var keyObj in loc.Keys)
                        {
                            if (!(keyObj is string keyStr) || !keyStr.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (fastDeps.ContainsKey(keyStr)) continue;
                            if (loc.Locate(keyStr, typeof(object), out var locations) && locations != null && locations.Count > 0)
                            {
                                var depList = new List<string>();
                                var firstLoc = locations[0];
                                if (firstLoc != null && firstLoc.Dependencies != null)
                                {
                                    for (int i = 0; i < firstLoc.Dependencies.Count; i++)
                                    {
                                        var d = firstLoc.Dependencies[i]?.PrimaryKey;
                                        if (!string.IsNullOrEmpty(d)) depList.Add(d);
                                    }
                                }
                                fastDeps[keyStr] = depList;
                            }
                        }
                    }
                    if (fastDeps.Count > 1000)
                    {
                        try
                        {
                            using (var fs = new FileStream(cacheFile, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8))
                            {
                                bw.Write(0x49524953);
                                bw.Write(fastDeps.Count);
                                foreach (var kv in fastDeps)
                                {
                                    bw.Write(kv.Key ?? "");
                                    var dl = kv.Value;
                                    int dLen = dl != null ? dl.Count : 0;
                                    bw.Write(dLen);
                                    for (int j = 0; j < dLen; j++)
                                        bw.Write(dl[j] ?? "");
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (fastDeps.Count > 0)
                {
                    _cachedFullCatalogTransitiveDeps = fastDeps;
                    depsField.SetValue(null, fastDeps);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Pre-populated AddressablesData._catalogTransitiveDeps ({fastDeps.Count} bundle entries).");
                }

                // Hook BuildFastBundleDepsFromCatalog so AssetHelper never stalls 2,463 ms overwriting _catalogTransitiveDeps with a partial 138-bundle graph
                var buildFastMi = addrDataType.GetMethod("BuildFastBundleDepsFromCatalog", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var skipPreMi = typeof(AssetHelperBridge).GetMethod(nameof(SkipRedundantBuildFastBundleDeps_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (buildFastMi != null && skipPreMi != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(
                        "SilksongMobileAPI.AssetHelperOpt",
                        buildFastMi,
                        skipPreMi,
                        null,
                        null,
                        HarmonyLib.Priority.First);
                }

                // Prune redundant BundleDepsPrecompute and desktop SceneRepacking tasks from StartupOverrideManager._tasks (both static and instance)
                var somType = assetHelperAsm.GetType("Silksong.AssetHelper.Plugin.StartupOverrideManager", false);
                var tasksField = somType?.GetField("_tasks", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (somType != null && tasksField != null)
                {
                    if (tasksField.IsStatic)
                    {
                        PruneRedundantAssetHelperTaskList(tasksField.GetValue(null) as System.Collections.IList);
                    }
                    else
                    {
                        var somInstances = Resources.FindObjectsOfTypeAll(somType);
                        if (somInstances != null)
                        {
                            for (int s = 0; s < somInstances.Length; s++)
                            {
                                if (somInstances[s] != null)
                                    PruneRedundantAssetHelperTaskList(tasksField.GetValue(somInstances[s]) as System.Collections.IList);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_157) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5309", __apiEx_157); }
        }

        private static bool _finalJudgePrefabsRequested;
        public static void EnsureFinalJudgePrefabsReady()
        {
            if (_finalJudgePrefabsRequested) return;
            _finalJudgePrefabsRequested = true;
            try
            {
                const string lavaKey = "Assets/Prefabs/Enemies/Projectiles/DF Lava Rock.prefab";
                if (IsAddressableKeyRegistered(lavaKey))
                {
                    var hLava = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(lavaKey);
                    hLava.Completed += op =>
                    {
                        if (op.Status == AsyncOperationStatus.Succeeded && op.Result != null)
                        {
                            UnityEngine.Object.DontDestroyOnLoad(op.Result);
                            AssignFinalJudgePrefabField("lavaRockPrefab", op.Result);
                        }
                    };
                }
                const string bombKey = "Assets/Prefabs/Enemies/Projectiles/DF Bomb Rock.prefab";
                if (IsAddressableKeyRegistered(bombKey))
                {
                    var hBomb = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(bombKey);
                    hBomb.Completed += op =>
                    {
                        if (op.Status == AsyncOperationStatus.Succeeded && op.Result != null)
                        {
                            UnityEngine.Object.DontDestroyOnLoad(op.Result);
                            AssignFinalJudgePrefabField("bombRockPrefab", op.Result);
                        }
                    };
                }
            }
            catch (System.Exception __apiEx_158) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5346", __apiEx_158); }
        }

        private static GameObject _cachedFinalJudgeOwner;
        private static GameObject[] _cachedFinalJudgeSpinObjects;
        public static void EnsureFinalJudgeSpinEffectsStopped(HutongGames.PlayMaker.FsmState state)
        {
            try
            {
                if (state == null || state.Fsm == null) return;
                string stName = state.Name ?? "";
                if (stName != "Idle" && stName != "Rage Roar 1" && stName != "Rage Roar 2" &&
                    stName != "Throw Antic" && stName != "Stomp JumpAntic" && stName != "Charge Antic 1")
                {
                    return;
                }
                var ownerGo = state.Fsm.GameObject;
                if (ownerGo == null || ownerGo.name.IndexOf("Last Judge", StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                if (!ReferenceEquals(ownerGo, _cachedFinalJudgeOwner) || _cachedFinalJudgeSpinObjects == null)
                {
                    _cachedFinalJudgeOwner = ownerGo;
                    var list = new List<GameObject>(16);
                    var trs = ownerGo.GetComponentsInChildren<Transform>(true);
                    if (trs != null)
                    {
                        for (int i = 0; i < trs.Length; i++)
                        {
                            var t = trs[i];
                            if (t == null || t == ownerGo.transform || t.gameObject == null)
                                continue;
                            string cn = t.gameObject.name ?? "";
                            if (cn.IndexOf("Spin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Chain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Flail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Censer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Thurible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Flame Ring", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cn.IndexOf("Fire Wave", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                list.Add(t.gameObject);
                            }
                        }
                    }
                    _cachedFinalJudgeSpinObjects = list.ToArray();
                }
                for (int i = 0; i < _cachedFinalJudgeSpinObjects.Length; i++)
                {
                    var go = _cachedFinalJudgeSpinObjects[i];
                    if (go != null && go.activeSelf)
                        go.SetActive(false);
                }
            }
            catch (System.Exception __apiEx_159) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5399", __apiEx_159); }
        }

        private static GameObject _fallbackBossDeathFinalHitGo;
        public static void EnsurePaleAutomatonBossDeathFinalHitTemplate()
        {
            try
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go != null && go.name == "Boss Death FinalHit")
                        return;
                }
                if (_fallbackBossDeathFinalHitGo != null) return;
                _fallbackBossDeathFinalHitGo = new GameObject("Boss Death FinalHit");
                _fallbackBossDeathFinalHitGo.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_fallbackBossDeathFinalHitGo);
                foreach (string childName in new[] { "white_solid", "Slash", "Strike L", "Strike R" })
                {
                    var childGo = new GameObject(childName);
                    childGo.transform.SetParent(_fallbackBossDeathFinalHitGo.transform, false);
                }
            }
            catch { }
        }

        public static void PaleAutomaton_PrepareArborium11Objects(GameObject newParent)
        {
            try
            {
                BindAllModManagedAssetsFromTemplateCache();
                var arbScene = SceneManager.GetSceneByName("Arborium_11");
                GameObject btGo = null;
                GameObject merchantParent = null;
                if (arbScene.IsValid() && arbScene.isLoaded)
                {
                    // Ensure Arborium_11 is the active scene immediately (since SceneManager.sceneLoaded(Arborium_11, Additive)
                    // fires while Arborium_03 is still the active scene and about to be unloaded by GameManager.UnloadScene).
                    if (SceneManager.GetActiveScene() != arbScene)
                    {
                        try { SceneManager.SetActiveScene(arbScene); } catch { }
                    }
                    var roots = arbScene.GetRootGameObjects();
                    if (roots != null)
                    {
                        for (int r = 0; r < roots.Length; r++)
                        {
                            var rg = roots[r];
                            if (rg == null) continue;
                            if (rg.name == "Black Thread States") btGo = rg;
                            else if (rg.name == "Merchant Quest Parent") merchantParent = rg;
                        }
                    }
                }
                if (btGo == null) btGo = GameObject.Find("Black Thread States");
                if (btGo != null)
                {
                    if (!btGo.activeSelf) btGo.SetActive(true);
                    // CRITICAL: Do NOT reparent children out of "Black Thread States"!
                    // PaleAutomatonPlugin.<Awake>b__30_0 (lines 537-539) and Helpers.SetupArena() (lines 487-488)
                    // directly index transform.GetChild(0) ("Normal World") and transform.GetChild(1) ("Black Thread World").
                    var btTr = btGo.transform;
                    if (btTr.childCount > 0 && btTr.GetChild(0) != null && btTr.GetChild(0).gameObject != null)
                        btTr.GetChild(0).gameObject.SetActive(true);
                    if (btTr.childCount > 1 && btTr.GetChild(1) != null && btTr.GetChild(1).gameObject != null)
                        btTr.GetChild(1).gameObject.SetActive(false);
                }
                if (merchantParent == null) merchantParent = GameObject.Find("Merchant Quest Parent");
                if (merchantParent != null)
                {
                    // Keep Merchant Quest Parent itself active so GameObject.Find("Merchant Quest Parent") never returns null,
                    // while deactivating all its children so !transform.GetChild(0).gameObject.activeSelf is always true!
                    if (!merchantParent.activeSelf) merchantParent.SetActive(true);
                    for (int c = 0; c < merchantParent.transform.childCount; c++)
                    {
                        var ch = merchantParent.transform.GetChild(c);
                        if (ch != null && ch.gameObject != null && ch.gameObject.activeSelf)
                            ch.gameObject.SetActive(false);
                    }
                }
                EnsurePaleAutomatonBossDeathFinalHitTemplate();

                // Ensure PaleAutomatonPlugin.bossScene, terrainCollider, and cameraLockArea are initialized
                // (Do NOT pre-create an empty groundSpikesParent here — PlaceHornet() creates it and populates 18 spike children!)
                if (HasPaleAutomaton() && _paleAutomatonAssembly != null)
                {
                    const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var paPluginType = _paleAutomatonAssembly.GetType("PaleAutomaton.PaleAutomatonPlugin", false);
                    var paHelpersType = _paleAutomatonAssembly.GetType("PaleAutomaton.Helpers", false);
                    var paMoverType = _paleAutomatonAssembly.GetType("PaleAutomaton.InfiniteTerrainMover", false);
                    if (paPluginType != null)
                    {
                        paPluginType.GetField("bossScene", SFlags)?.SetValue(null, true);
                        if (arbScene.IsValid() && arbScene.isLoaded)
                        {
                            var roots = arbScene.GetRootGameObjects();
                            if (roots != null)
                            {
                                for (int r = 0; r < roots.Length; r++)
                                {
                                    var rg = roots[r];
                                    if (rg == null) continue;
                                    if (rg.name.StartsWith("Alert Range", StringComparison.Ordinal))
                                    {
                                        SafeDeactivateAndDestroyLater(rg);
                                    }
                                    else if (rg.name.StartsWith("Hero Corpse Marker", StringComparison.Ordinal))
                                    {
                                        if (rg.name.EndsWith("(10)", StringComparison.Ordinal))
                                        {
                                            var p = rg.transform.position;
                                            rg.transform.position = new Vector3(129f, p.y, p.z);
                                        }
                                        else
                                        {
                                            SafeDeactivateAndDestroyLater(rg);
                                        }
                                    }
                                    else if (rg.name == "terrain collider" || rg.name == "Terrain Collider")
                                    {
                                        paPluginType.GetField("terrainCollider", SFlags)?.SetValue(null, rg);
                                    }
                                    else if (rg.name == "CameraLockArea (1)")
                                    {
                                        try { paHelpersType?.GetField("cameraLockArea", SFlags)?.SetValue(null, rg); } catch (System.Exception __apiEx_160) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5514", __apiEx_160); }
                                        try
                                        {
                                            var moverClaField = paMoverType?.GetField("cameraLockArea", SFlags);
                                            if (moverClaField != null)
                                            {
                                                if (moverClaField.FieldType == typeof(GameObject))
                                                    moverClaField.SetValue(null, rg);
                                                else
                                                    moverClaField.SetValue(null, rg.GetComponent(moverClaField.FieldType));
                                            }
                                        }
                                        catch (System.Exception __apiEx_161) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5526", __apiEx_161); }
                                    }
                                }
                                var batSwarms = GameObject.Find("citadel_bat_swarms");
                                if (batSwarms != null) SafeDeactivateAndDestroyLater(batSwarms);
                                var batSwarmLeft = GameObject.Find("bat swarm_bg_left");
                                if (batSwarmLeft != null) SafeDeactivateAndDestroyLater(batSwarmLeft);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_162) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5538", __apiEx_162); }
        }

        public static void PaleAutomaton_SafeUpdateSaveHeroClamps()
        {
            if (!HasPaleAutomaton() || _paleAutomatonAssembly == null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var paPluginType = _paleAutomatonAssembly.GetType("PaleAutomaton.PaleAutomatonPlugin", false);
                var skGo = paPluginType?.GetField("songKnight", SFlags)?.GetValue(null) as GameObject;
                var ctrlFsm = paPluginType?.GetField("controlFsm", SFlags)?.GetValue(null) as PlayMakerFSM;
                if (skGo == null) return;
                Vector3 centerPos = HeroController.SilentInstance != null ? HeroController.SilentInstance.transform.position : skGo.transform.position;
                float minX = centerPos.x - 1000f;
                float maxX = centerPos.x + 1000f;

                if (ctrlFsm != null && ctrlFsm.Fsm != null)
                {
                    var ds3 = Silksong.FsmUtil.FsmUtil.GetState(ctrlFsm, "Dash Slash 3");
                    if (ds3 != null)
                    {
                        var dsClamp = Silksong.FsmUtil.FsmUtil.GetFirstActionOfType<HutongGames.PlayMaker.Actions.FloatClamp>(ds3);
                        if (dsClamp != null)
                        {
                            if (dsClamp.minValue != null) dsClamp.minValue.Value = minX;
                            if (dsClamp.maxValue != null) dsClamp.maxValue.Value = maxX;
                        }
                    }
                }

                var saveHeroFsm = FSMUtility.LocateMyFSM(skGo, "Save Hero");
                if (saveHeroFsm != null && saveHeroFsm.Fsm != null)
                {
                    var st2 = Silksong.FsmUtil.FsmUtil.GetState(saveHeroFsm, "State 2");
                    if (st2 != null)
                    {
                        var clampAct = Silksong.FsmUtil.FsmUtil.GetFirstActionOfType<HutongGames.PlayMaker.Actions.FloatClamp>(st2);
                        if (clampAct != null)
                        {
                            if (clampAct.minValue != null) clampAct.minValue.Value = minX;
                            if (clampAct.maxValue != null) clampAct.maxValue.Value = maxX;
                        }
                    }
                }

                var mr = skGo.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.forceRenderingOff = false;
                    mr.bounds = new Bounds(centerPos, new Vector3(1000f, 1000f, 1000f));
                }
            }
            catch (System.Exception __apiEx_163) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5559", __apiEx_163); }
        }

        public static void AssignFinalJudgePrefabField(string fieldName, GameObject prefab)
        {
            try
            {
                if (prefab == null) return;
                var fjType = HarmonyBridge.ResolveTypeRobust("FinalJudge") ?? HarmonyBridge.ResolveTypeRobust("FinalJudge.FinalJudge");
                var f = fjType?.GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return;
                if (f.IsStatic) f.SetValue(null, prefab);
                else
                {
                    var insts = UnityEngine.Object.FindObjectsOfType(fjType);
                    if (insts != null)
                        for (int i = 0; i < insts.Length; i++)
                            if (insts[i] != null) f.SetValue(insts[i], prefab);
                }
            }
            catch (System.Exception __apiEx_164) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5579", __apiEx_164); }
        }

        public static void SafeAeternalEverwatcherModifyTerrain()
        {
            try
            {
                SyncExtractedTemplatesIntoAeternalEverwatcher();
                var coralScene = SceneManager.GetSceneByName("Coral_39");
                if (!coralScene.IsValid() || !coralScene.isLoaded) return;

                var battleRange = GameObject.Find("Battle Range");
                if (battleRange != null)
                {
                    var pos = battleRange.transform.position;
                    pos.x = 75.2f;
                    battleRange.transform.position = pos;
                    battleRange.transform.localScale = new Vector3(5f, 25f, 1f);
                }

                var right1 = GameObject.Find("right1");
                if (right1 != null)
                {
                    var sc = right1.transform.localScale;
                    sc.y = 10f;
                    right1.transform.localScale = sc;
                }

                var cla4 = GameObject.Find("CameraLockArea (4)");
                if (cla4 != null)
                {
                    var pos = cla4.transform.position;
                    pos.x = 81f;
                    cla4.transform.position = pos;
                    var sc = cla4.transform.localScale;
                    sc.x = 7f;
                    cla4.transform.localScale = sc;
                }

                string[] toDeactivate = new string[]
                {
                    "CameraLockArea (5)",
                    "sand_centipede_scuffle",
                    "Sand Centipede Hero Damager",
                    "Sand Centipede Group",
                    "Sand_Centipede_Ambient_Audio Variant",
                    "terrain collider non slider",
                    "terrain collider non slider (1)",
                    "terrain collider non slider (2)",
                    "terrain collider non slider (3)",
                    "terrain collider (13)",
                    "terrain collider (12)",
                    "terrain collider (11)",
                    "terrain collider (10)",
                    "Roof Collider_Basic",
                    "Roof Collider_Basic (2)"
                };
                for (int i = 0; i < toDeactivate.Length; i++)
                {
                    var go = GameObject.Find(toDeactivate[i]);
                    if (go != null) go.SetActive(false);
                }

                var conch = GameObject.Find("Giant_Conch_bg_horn (23)");
                if (conch != null && GameObject.Find("Giant_Conch_bg_horn (23)(Clone)") == null)
                {
                    var conchClone = UnityEngine.Object.Instantiate(conch);
                    conchClone.transform.localScale = new Vector3(4.3223f, 5.3747f, 1f);
                    conchClone.transform.position = new Vector3(159.4092f, 45.6818f, -5.1127f);
                    conchClone.transform.eulerAngles = new Vector3(0f, 0f, 338.352f);
                }

                var tmrd = GameObject.Find("TileMap Render Data");
                if (tmrd != null && tmrd.transform.childCount > 0)
                {
                    var child = tmrd.transform.GetChild(0);
                    string[] disableChunks = new string[] { "Chunk 1 5", "Chunk 1 4", "Chunk 1 3", "Chunk 0 5" };
                    for (int c = 0; c < disableChunks.Length; c++)
                    {
                        var chTr = child.Find(disableChunks[c]);
                        if (chTr != null)
                        {
                            var cols = chTr.gameObject.GetComponents<EdgeCollider2D>();
                            if (cols != null)
                            {
                                for (int k = 0; k < cols.Length; k++)
                                    if (cols[k] != null) cols[k].enabled = false;
                            }
                        }
                    }

                    var chunk04 = child.Find("Chunk 0 4");
                    if (chunk04 != null)
                    {
                        var sc = chunk04.localScale;
                        sc.x = 300f;
                        chunk04.localScale = sc;
                        var pos = chunk04.position;
                        pos.x = 0f;
                        chunk04.position = pos;

                        if (GameObject.Find("BorderLeftHitbox") == null)
                        {
                            var bl = UnityEngine.Object.Instantiate(chunk04, chunk04.parent);
                            bl.transform.position = new Vector3(-2.6f, 5f, 0f);
                            bl.transform.localScale = new Vector3(1f, 65f, 1f);
                            if (bl.gameObject.GetComponent<NonSlider>() == null)
                                bl.gameObject.AddComponent<NonSlider>();
                            var mr = bl.gameObject.GetComponent<MeshRenderer>();
                            if (mr != null) mr.enabled = false;
                            bl.gameObject.name = "BorderLeftHitbox";
                        }

                        if (GameObject.Find("BorderRightHitbox") == null)
                        {
                            var br = UnityEngine.Object.Instantiate(chunk04, chunk04.parent);
                            br.transform.position = new Vector3(173f, -2f, 0f);
                            br.transform.localScale = new Vector3(1f, 65f, 1f);
                            if (br.gameObject.GetComponent<NonSlider>() == null)
                                br.gameObject.AddComponent<NonSlider>();
                            var mr = br.gameObject.GetComponent<MeshRenderer>();
                            if (mr != null) mr.enabled = false;
                            br.gameObject.name = "BorderRightHitbox";

                            var extra = UnityEngine.Object.Instantiate(chunk04, chunk04.parent);
                            extra.transform.position = new Vector3(173.9688f, 3.0291f, chunk04.transform.position.z);
                        }
                    }
                }

                if (GameObject.Find("Terrain Extension 1") == null)
                {
                    var ext1 = new GameObject("Terrain Extension 1");
                    var ext2 = new GameObject("Terrain Extension 2");
                    SceneManager.MoveGameObjectToScene(ext1, coralScene);
                    SceneManager.MoveGameObjectToScene(ext2, coralScene);
                    string[] prefixes = new string[] { "kingdom_gate_0000_sand_dune_ground", "bone_deep", "Bone_floor_02" };
                    var exclude = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "kingdom_gate_0000_sand_dune_ground (28)",
                        "kingdom_gate_0000_sand_dune_ground (19)",
                        "bone_deep_0170_t (14)",
                        "bone_deep_0170_t (12)",
                        "bone_deep_0170_t (16)",
                        "bone_deep_0170_t (15)",
                        "bone_deep_0170_t (13)"
                    };
                    var roots = coralScene.GetRootGameObjects();
                    if (roots != null)
                    {
                        for (int r = 0; r < roots.Length; r++)
                        {
                            var rg = roots[r];
                            if (rg == null || exclude.Contains(rg.name)) continue;
                            bool match = false;
                            for (int p = 0; p < prefixes.Length; p++)
                            {
                                if (rg.name.StartsWith(prefixes[p], StringComparison.Ordinal))
                                {
                                    match = true;
                                    break;
                                }
                            }
                            if (match)
                            {
                                var c1 = UnityEngine.Object.Instantiate(rg, ext1.transform);
                                float x = rg.transform.position.x;
                                float num = x - 108f - 10f;
                                var p1 = rg.transform.position;
                                p1.x = x - num * 2f;
                                c1.transform.position = p1;

                                var c2 = UnityEngine.Object.Instantiate(rg, ext2.transform);
                                var p2 = rg.transform.position;
                                p2.x = x - num * 2f - 54f;
                                c2.transform.position = p2;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] SafeAeternalEverwatcherModifyTerrain warning: {ex.Message}");
            }
        }

        private static void SyncExtractedTemplatesIntoPharloomsGlory()
        {
            SyncExtractedTemplatesIntoAeternalEverwatcher();
            if (_pharloomsGloryAssetManager == null) return;
            try
            {
                EnsureRepackerMembersResolved();
                var templatesDict = _extractedTemplatesField?.GetValue(null) as IDictionary;
                if (templatesDict == null) templatesDict = _bridgeFallbackTemplates;

                var amType = _pharloomsGloryAssetManager.GetType();
                var loadedAssetsField = amType.GetField("LOADED_ASSETS", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var loadedDict = loadedAssetsField?.GetValue(_pharloomsGloryAssetManager) as IDictionary;
                if (loadedDict == null) return;

                foreach (DictionaryEntry de in loadedDict)
                {
                    object ma = de.Value;
                    if (ma == null) continue;
                    TryBindManagedAssetFromTemplateCache(ma, templatesDict);
                }
            }
            catch (System.Exception __apiEx_165) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5640", __apiEx_165); }
        }

        private static FieldInfo GetManagedAssetHandleField(Type maType)
        {
            const BindingFlags FFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type cur = maType;
            while (cur != null && cur != typeof(object))
            {
                var f = cur.GetField("_handle", FFlags);
                if (f != null) return f;
                cur = cur.BaseType;
            }
            return null;
        }

        private static void SetManagedAssetCompletedHandle(object managedAsset, Type maType, GameObject tpl)
        {
            SetManagedAssetCompletedHandleTyped(managedAsset, maType, typeof(GameObject), tpl);
        }

        private static void SetManagedAssetCompletedHandleTyped(object managedAsset, Type maType, Type assetType, UnityEngine.Object assetObj)
        {
            if (managedAsset == null || assetObj == null || maType == null) return;
            try
            {
                var handleField = GetManagedAssetHandleField(maType);
                if (handleField == null) return;
                if (assetType == typeof(GameObject) && assetObj is GameObject go)
                {
                    object curHandleVal = handleField.GetValue(managedAsset);
                    if (curHandleVal is AsyncOperationHandle<GameObject> existingH && existingH.IsValid() && existingH.Result != null)
                        return;
                    handleField.SetValue(managedAsset, Addressables.ResourceManager.CreateCompletedOperation<GameObject>(go, string.Empty));
                }
                else if (assetType == typeof(AudioClip) && assetObj is AudioClip clip)
                {
                    object curHandleVal = handleField.GetValue(managedAsset);
                    if (curHandleVal is AsyncOperationHandle<AudioClip> existingH && existingH.IsValid() && existingH.Result != null)
                        return;
                    handleField.SetValue(managedAsset, Addressables.ResourceManager.CreateCompletedOperation<AudioClip>(clip, string.Empty));
                }
                else if (assetType == typeof(MusicCue) && assetObj is MusicCue cue)
                {
                    object curHandleVal = handleField.GetValue(managedAsset);
                    if (curHandleVal is AsyncOperationHandle<MusicCue> existingH && existingH.IsValid() && existingH.Result != null)
                        return;
                    handleField.SetValue(managedAsset, Addressables.ResourceManager.CreateCompletedOperation<MusicCue>(cue, string.Empty));
                }
            }
            catch (System.Exception __apiEx_166) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5670", __apiEx_166); }
        }

        private static bool TryBindManagedAssetFromTemplateCache(object managedAsset, IDictionary templatesDict = null)
        {
            if (managedAsset == null) return false;
            try
            {
                var maType = managedAsset.GetType();
                var hasFallbackField = maType.GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                var fallbackResField = maType.GetField("FallbackLoadedResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                Type maAssetType = (maType.IsGenericType && maType.GetGenericArguments().Length > 0) ? maType.GetGenericArguments()[0] : typeof(GameObject);
                if (hasFallbackField != null && fallbackResField != null)
                {
                    if ((bool)hasFallbackField.GetValue(managedAsset) && fallbackResField.GetValue(managedAsset) is UnityEngine.Object existingObj && existingObj != null)
                    {
                        SetManagedAssetCompletedHandleTyped(managedAsset, maType, maAssetType, existingObj);
                        return true;
                    }
                }

                EnsureRepackerMembersResolved();
                if (templatesDict == null)
                    templatesDict = _extractedTemplatesField?.GetValue(null) as IDictionary;
                if (templatesDict == null) templatesDict = _bridgeFallbackTemplates;

                var sceneField = maType.GetField("SourceSceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                var pathField = maType.GetField("SourceObjPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                string sceneName = sceneField?.GetValue(managedAsset) as string;
                string objPath = pathField?.GetValue(managedAsset) as string;

                // If SourceSceneName/SourceObjPath were not populated, parse from <Key>k__BackingField if it encodes a scene asset
                if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(objPath) || string.Equals(sceneName, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    var keyProp = maType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    string rawKey = keyProp?.GetValue(managedAsset, null) as string;
                    if (!string.IsNullOrEmpty(rawKey))
                    {
                        string afterPrefix = null;
                        const string repackedPrefix = "AssetHelper-RepackedScenes/Assets/";
                        const string sceneAssetsPrefix = "AssetHelper-SceneAssets/Assets/";
                        const string scenesMarker = "Scenes/";
                        if (rawKey.StartsWith(repackedPrefix, StringComparison.OrdinalIgnoreCase))
                            afterPrefix = rawKey.Substring(repackedPrefix.Length);
                        else if (rawKey.StartsWith(sceneAssetsPrefix, StringComparison.OrdinalIgnoreCase))
                            afterPrefix = rawKey.Substring(sceneAssetsPrefix.Length);
                        else
                        {
                            int scIdx = rawKey.IndexOf(scenesMarker, StringComparison.OrdinalIgnoreCase);
                            if (scIdx >= 0)
                            {
                                afterPrefix = rawKey.Substring(scIdx + scenesMarker.Length);
                                if (afterPrefix.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                                    afterPrefix = afterPrefix.Substring("Assets/".Length);
                            }
                        }
                        if (!string.IsNullOrEmpty(afterPrefix))
                        {
                            if (afterPrefix.StartsWith("Scenes/", StringComparison.OrdinalIgnoreCase))
                                afterPrefix = afterPrefix.Substring("Scenes/".Length);
                            int slashIdx = afterPrefix.IndexOf('/');
                            if (slashIdx > 0 && slashIdx + 1 < afterPrefix.Length)
                            {
                                sceneName = afterPrefix.Substring(0, slashIdx);
                                objPath = afterPrefix.Substring(slashIdx + 1);
                                if (objPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                    objPath = objPath.Substring(0, objPath.Length - 7);
                                try { sceneField?.SetValue(managedAsset, sceneName); } catch (System.Exception __apiEx_167) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5719", __apiEx_167); }
                                try { pathField?.SetValue(managedAsset, objPath); } catch (System.Exception __apiEx_168) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5720", __apiEx_168); }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(sceneName) && !string.IsNullOrEmpty(objPath) && templatesDict != null)
                {
                    string cacheKey = BuildSceneCacheKeySafe(sceneName, objPath);
                    GameObject tpl = null;
                    if (!string.IsNullOrEmpty(cacheKey) && templatesDict.Contains(cacheKey))
                        tpl = templatesDict[cacheKey] as GameObject;
                    if (tpl == null && _bridgeFallbackTemplates != null && !string.IsNullOrEmpty(cacheKey) && _bridgeFallbackTemplates.ContainsKey(cacheKey))
                        tpl = _bridgeFallbackTemplates[cacheKey];
                    if (tpl == null && templatesDict != null)
                    {
                        foreach (DictionaryEntry de in templatesDict)
                        {
                            if (de.Key is string k && string.Equals(k, cacheKey, StringComparison.OrdinalIgnoreCase) && de.Value is GameObject goVal && goVal != null)
                            {
                                tpl = goVal;
                                break;
                            }
                        }
                    }
                    if (tpl != null)
                    {
                        if ((string.Equals(sceneName, "hang_17b", StringComparison.OrdinalIgnoreCase) &&
                             objPath.IndexOf("Boss Scene", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (string.Equals(sceneName, "Cradle_03", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(objPath, "Boss Scene", StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(sceneName, "song_24", StringComparison.OrdinalIgnoreCase))
                        {
                            StripPaleAutomatonDeactivateComponents(tpl);
                        }
                        if (!tpl.activeSelf) tpl.SetActive(true);
                        PinAllAssetsOnHierarchy(tpl);
                        fallbackResField?.SetValue(managedAsset, tpl);
                        hasFallbackField?.SetValue(managedAsset, true);
                        SetManagedAssetCompletedHandle(managedAsset, maType, tpl);
                        return true;
                    }
                }

                // Non-scene bundle asset resolution (e.g. Song Knight Projectile.prefab in localpoolprefabs_assets_areahangareasong,
                // bind_bell_appear.prefab in localpoolprefabs_assets_shared)
                var srcAssetField = maType.GetField("SourceAssetName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                var srcBundleField = maType.GetField("SourceBundleName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                string srcAssetName = srcAssetField?.GetValue(managedAsset) as string;
                string srcBundleName = srcBundleField?.GetValue(managedAsset) as string;
                var keyPropDirect = maType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                string directKey = keyPropDirect?.GetValue(managedAsset, null) as string;
                if (string.IsNullOrEmpty(sceneName) && (!string.IsNullOrEmpty(directKey) || !string.IsNullOrEmpty(srcAssetName)))
                {
                    string rawAssetKey = !string.IsNullOrEmpty(srcAssetName) ? srcAssetName : directKey;
                    const string nonScenePrefix = "AssetHelper-NonSceneAssets/";
                    if (!string.IsNullOrEmpty(rawAssetKey) && rawAssetKey.StartsWith(nonScenePrefix, StringComparison.OrdinalIgnoreCase))
                        rawAssetKey = rawAssetKey.Substring(nonScenePrefix.Length);

                    string shortName = !string.IsNullOrEmpty(rawAssetKey) ? Path.GetFileNameWithoutExtension(rawAssetKey) : string.Empty;
                    if (maAssetType != typeof(GameObject))
                    {
                        UnityEngine.Object resolvedNonGo = null;
                        if (!string.IsNullOrEmpty(rawAssetKey))
                        {
                            _failedAddressableNames.Remove(rawAssetKey);
                            if (!string.IsNullOrEmpty(shortName)) _failedAddressableNames.Remove(shortName);
                            resolvedNonGo = (ResolveAddressableAsset(rawAssetKey, maAssetType) ?? ResolveAddressableAsset(shortName, maAssetType)) as UnityEngine.Object;
                        }
                        if (resolvedNonGo == null && !string.IsNullOrEmpty(srcBundleName))
                        {
                            try
                            {
                                string bundleFile = srcBundleName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? srcBundleName : (srcBundleName + ".bundle");
                                var ab = LoadAssetBundleFromFileSafe(bundleFile);
                                if (ab != null)
                                {
                                    if (!string.IsNullOrEmpty(rawAssetKey))
                                        resolvedNonGo = ab.LoadAsset(rawAssetKey, maAssetType);
                                    if (resolvedNonGo == null && !string.IsNullOrEmpty(shortName))
                                        resolvedNonGo = ab.LoadAsset(shortName, maAssetType);
                                }
                            }
                            catch (System.Exception __apiEx_abNonGo) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "LoadNonGoFromBundle", __apiEx_abNonGo); }
                        }
                        if (resolvedNonGo == null && (!string.IsNullOrEmpty(srcBundleName) || !string.IsNullOrEmpty(rawAssetKey)))
                        {
                            try
                            {
                                var syncFallbackMi = maType.GetMethod("TryResolveFallbackSynchronously", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (syncFallbackMi != null)
                                    resolvedNonGo = syncFallbackMi.Invoke(managedAsset, null) as UnityEngine.Object;
                            }
                            catch (System.Exception __apiEx_syncFbNonGo) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "TryResolveFallbackSynchronouslyNonGo", __apiEx_syncFbNonGo); }
                        }
                        if (resolvedNonGo != null && maAssetType.IsInstanceOfType(resolvedNonGo))
                        {
                            fallbackResField?.SetValue(managedAsset, resolvedNonGo);
                            hasFallbackField?.SetValue(managedAsset, true);
                            SetManagedAssetCompletedHandleTyped(managedAsset, maType, maAssetType, resolvedNonGo);
                            return true;
                        }
                        return false;
                    }

                    GameObject resolvedGo = null;

                    if (!string.IsNullOrEmpty(shortName) && _harvestedPoolPrefabs.TryGetValue(shortName, out var poolGo) && poolGo != null)
                        resolvedGo = poolGo;
                    if (resolvedGo == null && !string.IsNullOrEmpty(rawAssetKey) && _harvestedPoolPrefabs.TryGetValue(rawAssetKey, out var poolGo2) && poolGo2 != null)
                        resolvedGo = poolGo2;

                    if (resolvedGo == null && !string.IsNullOrEmpty(rawAssetKey))
                    {
                        _failedAddressableNames.Remove(rawAssetKey);
                        if (!string.IsNullOrEmpty(shortName)) _failedAddressableNames.Remove(shortName);
                        resolvedGo = (ResolveAddressableAsset(rawAssetKey, typeof(GameObject)) ?? ResolveAddressableAsset(shortName, typeof(GameObject))) as GameObject;
                    }

                    // Invoke ManagedAsset<T>.TryResolveFallbackSynchronously() -> AndroidRuntimeBridge.TryLoadAssetDirect<GameObject>
                    // before ManagedAsset<T>.DoLoad() ever runs so Addressables.LoadAssetAsync("AssetHelper-NonSceneAssets/...") is never called!
                    if (resolvedGo == null && (!string.IsNullOrEmpty(srcBundleName) || !string.IsNullOrEmpty(rawAssetKey)))
                    {
                        try
                        {
                            var syncFallbackMi = maType.GetMethod("TryResolveFallbackSynchronously", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (syncFallbackMi != null)
                            {
                                resolvedGo = syncFallbackMi.Invoke(managedAsset, null) as GameObject;
                            }
                        }
                        catch (System.Exception __apiEx_syncFb) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "TryResolveFallbackSynchronously", __apiEx_syncFb); }
                    }

                    if (resolvedGo != null)
                    {
                        if (!string.IsNullOrEmpty(shortName)) _harvestedPoolPrefabs[shortName] = resolvedGo;
                        if (!string.IsNullOrEmpty(rawAssetKey)) _harvestedPoolPrefabs[rawAssetKey] = resolvedGo;
                        PinAllAssetsOnHierarchy(resolvedGo);
                        fallbackResField?.SetValue(managedAsset, resolvedGo);
                        hasFallbackField?.SetValue(managedAsset, true);
                        SetManagedAssetCompletedHandle(managedAsset, maType, resolvedGo);
                        return true;
                    }
                }

                var isLoadedProp = maType.GetProperty("IsLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                var resProp = maType.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (isLoadedProp != null && (bool)isLoadedProp.GetValue(managedAsset, null))
                {
                    if (resProp != null && resProp.GetValue(managedAsset, null) is GameObject loadedGo && loadedGo != null)
                    {
                        SetManagedAssetCompletedHandle(managedAsset, maType, loadedGo);
                        return true;
                    }
                }
            }
            catch (System.Exception __apiEx_169) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5784", __apiEx_169); }
            return false;
        }

        public static bool FilterLoadedManagedAsset(bool foundInDict, object managedAsset)
        {
            if (!foundInDict || managedAsset == null) return false;
            return TryBindManagedAssetFromTemplateCache(managedAsset);
        }

        public static void LoadPharloomsGloryAssets(object assetManager)
        {
            if (assetManager == null) return;
            _pharloomsGloryAssetManager = assetManager;
            _pharloomsGloryAssembly = assetManager.GetType().Assembly;
            try
            {
                EnsureRepackerMembersResolved();
                EnsurePharloomsGloryLightingHooksInstalled();
                SyncPharloomsGloryAudioAliases();
                var amType = assetManager.GetType();
                var isLoadedField = amType.GetField("isLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                isLoadedField?.SetValue(assetManager, true);

                var objPathsField = amType.GetField("OBJECT_PATHS", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var objPathsDict = objPathsField?.GetValue(assetManager) as IDictionary;
                if (objPathsDict != null)
                {
                    lock (_pendingScenePathsByScene)
                    {
                        foreach (DictionaryEntry de in objPathsDict)
                        {
                            string sc = de.Key as string;
                            if (string.IsNullOrEmpty(sc)) continue;
                            if (de.Value is IEnumerable<string> list)
                            {
                                _pendingScenePathsByScene[sc] = new List<string>(list);
                            }
                        }
                    }
                }

                SyncExtractedTemplatesIntoPharloomsGlory();

                // Batch extraction runs during FastPreloaderUI on Menu_Title.
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] LoadPharloomsGloryAssets warning: {ex.Message}");
            }
        }

        private static IEnumerator RunPharloomsGloryBatchExtractCoroutine(object assetManager)
        {
            // Wait until AssetHelper has finished indexing bundles, registered AndroidSceneResourceProvider,
            // AND the game has reached Menu_Title (or a gameplay scene), never during boot or Pre_Menu_Intro.
            int waitReady = 0;
            while (waitReady++ < 600)
            {
                EnsureRepackerMembersResolved();
                bool reg = (_registeredInAddressablesField == null) || (bool)_registeredInAddressablesField.GetValue(null) || waitReady >= 10;
                var actSc = SceneManager.GetActiveScene();
                bool sceneReady = actSc.IsValid() && IsSafeSceneForBatchExtraction(actSc.name);
                if (reg && sceneReady && !IsAnyRepackerBusy()) break;
                yield return new WaitForSeconds(0.05f);
            }

            // Start immediately on Menu_Title before save-slot selection
            yield return new WaitForSeconds(0.05f);
            int waitIdle = 0;
            while (IsAnyRepackerBusy() && waitIdle++ < 60)
                yield return new WaitForSeconds(0.05f);

            EnsureRepackerMembersResolved();
            try { _ensureAddressableKeysIndexedMethod?.Invoke(null, null); } catch (System.Exception __apiEx_170) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5858", __apiEx_170); }

            var templatesDict = (_extractedTemplatesField?.GetValue(null) as IDictionary) ?? _bridgeFallbackTemplates;
            var containerGo = (_ensureTemplateContainerMethod?.Invoke(null, null) as GameObject) ?? EnsureBridgeTemplateContainer();
            var bundleKeysDict = _cachedSceneBundleKeysField?.GetValue(null) as IDictionary;
            var addrKeysDict = _cachedSceneAddressableKeysField?.GetValue(null) as IDictionary;
            var pinnedHandlesDict = _pinnedSceneBundleHandlesField?.GetValue(null) as IDictionary;
            if (templatesDict == null || containerGo == null) yield break;

            List<KeyValuePair<string, List<string>>> scenesToExtract;
            lock (_pendingScenePathsByScene)
            {
                scenesToExtract = new List<KeyValuePair<string, List<string>>>(_pendingScenePathsByScene);
            }
            scenesToExtract.Sort((a, b) =>
            {
                bool aCoral = a.Key != null && a.Key.IndexOf("coral", StringComparison.OrdinalIgnoreCase) >= 0;
                bool bCoral = b.Key != null && b.Key.IndexOf("coral", StringComparison.OrdinalIgnoreCase) >= 0;
                if (aCoral && !bCoral) return 1;
                if (!aCoral && bCoral) return -1;
                return 0;
            });
            PharloomsGloryScenesTotal = scenesToExtract.Count > 0 ? scenesToExtract.Count : 4;
            PharloomsGloryScenesDone = 0;

            float origAudioVol = 1f;
            try { origAudioVol = AudioListener.volume; AudioListener.volume = 0f; } catch (System.Exception __apiEx_171) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5876", __apiEx_171); }

            foreach (var kv in scenesToExtract)
            {
                string sceneName = kv.Key;
                PharloomsGloryCurrentExtractingScene = sceneName;
                List<string> paths = kv.Value;
                if (string.IsNullOrEmpty(sceneName) || paths == null || paths.Count == 0) continue;

                bool allPresent = true;
                for (int i = 0; i < paths.Count; i++)
                {
                    string ck = BuildSceneCacheKeySafe(sceneName, paths[i]);
                    if (string.IsNullOrEmpty(ck) || !templatesDict.Contains(ck) || templatesDict[ck] as GameObject == null)
                    {
                        allPresent = false;
                        break;
                    }
                }
                if (allPresent)
                {
                    PharloomsGloryScenesDone++;
                    continue;
                }

                int waitSafe = 0;
                while (waitSafe++ < 600)
                {
                    var curSc = SceneManager.GetActiveScene();
                    if (curSc.IsValid() && IsSafeSceneForBatchExtraction(curSc.name) && !IsAnyRepackerBusy())
                        break;
                    yield return new WaitForSeconds(0.1f);
                }

                SetAllRepackersExtracting(true, sceneName);

                // Fast Addressables scene loading (skip slow recursive catalog pin scan)

                string lowerScene = sceneName.ToLowerInvariant();
                Scene extractedScene = default;
                bool loadedViaSceneManager = false;

                // 1. Primary path for production monolithic APK (defaultlocalgroup_scenes_all) & modular APK:
                //    Load scene additively via Addressables ("Scenes/" + sceneName) and pin handle in PinnedSceneBundleHandles.
                var candidateAddrKeys = new List<string>();
                if (addrKeysDict != null)
                {
                    if (addrKeysDict.Contains(sceneName) && addrKeysDict[sceneName] is string ak1 && !string.IsNullOrEmpty(ak1))
                        candidateAddrKeys.Add(ak1);
                    if (addrKeysDict.Contains(lowerScene) && addrKeysDict[lowerScene] is string ak2 && !string.IsNullOrEmpty(ak2) && !candidateAddrKeys.Contains(ak2))
                        candidateAddrKeys.Add(ak2);
                }
                string stdKey = "Scenes/" + sceneName;
                if (!candidateAddrKeys.Contains(stdKey)) candidateAddrKeys.Add(stdKey);
                string stdLowerKey = "Scenes/" + lowerScene;
                if (!candidateAddrKeys.Contains(stdLowerKey)) candidateAddrKeys.Add(stdLowerKey);
                if (!candidateAddrKeys.Contains(sceneName)) candidateAddrKeys.Add(sceneName);

                for (int k = 0; k < candidateAddrKeys.Count && !extractedScene.IsValid(); k++)
                {
                    string addrKey = candidateAddrKeys[k];
                    AsyncOperationHandle<SceneInstance> scHandle = default;
                    bool startedSc = false;
                    try
                    {
                        SetAllRepackersExtracting(true, sceneName);
                        scHandle = Addressables.LoadSceneAsync((object)addrKey, LoadSceneMode.Additive, true, 100, SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
                        startedSc = true;
                    }
                    catch (System.Exception __apiEx_172) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5945", __apiEx_172); }

                    if (!startedSc) continue;

                    int w = 0;
                    while (!scHandle.IsDone && w++ < 600)
                    {
                        SetAllRepackersExtracting(true, sceneName);
                        yield return null;
                    }

                    try
                    {
                        if (scHandle.IsDone && scHandle.Status == AsyncOperationStatus.Succeeded)
                        {
                            extractedScene = scHandle.Result.Scene;
                            if (extractedScene.IsValid())
                            {
                                string pinKey = lowerScene + "_sceneHandle";
                                StorePinnedHandleInAllRepackers(pinKey, scHandle);
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Loaded scene '{sceneName}' via Addressables key '{addrKey}'.");
                                break;
                            }
                        }
                    }
                    catch (System.Exception __apiEx_173) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5970", __apiEx_173); }
                }

                // 2. Fallback path: check individual scene bundle or monolithic scenes_all bundle + SceneManager.LoadSceneAsync
                if (!extractedScene.IsValid())
                {
                    AssetBundle loadedBundle = null;
                    string resolvedScenePath = null;

                    if (bundleKeysDict != null && bundleKeysDict.Contains(lowerScene))
                    {
                        string bundleKey = bundleKeysDict[lowerScene] as string;
                        if (!string.IsNullOrEmpty(bundleKey))
                        {
                            AsyncOperationHandle<IAssetBundleResource> bh = default;
                            bool startedBh = false;
                            try
                            {
                                bh = Addressables.LoadAssetAsync<IAssetBundleResource>(bundleKey);
                                startedBh = true;
                            }
                            catch (System.Exception __apiEx_174) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L5991", __apiEx_174); }

                            if (startedBh)
                            {
                                int w = 0;
                                while (!bh.IsDone && w++ < 300) yield return null;
                                try
                                {
                                    if (bh.IsDone && bh.Result != null)
                                    {
                                        if (pinnedHandlesDict != null && !pinnedHandlesDict.Contains(lowerScene))
                                            pinnedHandlesDict[lowerScene] = bh;
                                        loadedBundle = bh.Result.GetAssetBundle();
                                    }
                                }
                                catch (System.Exception __apiEx_175) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6006", __apiEx_175); }
                            }
                        }
                    }

                    if (loadedBundle == null)
                    {
                        try
                        {
                            foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                            {
                                if (b == null || string.IsNullOrEmpty(b.name)) continue;
                                if (b.name.IndexOf(lowerScene, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    b.name.IndexOf("scenes_all", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var sp = b.GetAllScenePaths();
                                    if (sp != null)
                                    {
                                        for (int s = 0; s < sp.Length; s++)
                                        {
                                            if (sp[s] != null && sp[s].IndexOf(sceneName, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                loadedBundle = b;
                                                resolvedScenePath = sp[s];
                                                break;
                                            }
                                        }
                                    }
                                    if (resolvedScenePath != null) break;
                                }
                            }
                        }
                        catch (System.Exception __apiEx_176) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6038", __apiEx_176); }
                    }

                    if (loadedBundle != null && string.IsNullOrEmpty(resolvedScenePath))
                    {
                        try
                        {
                            var sp = loadedBundle.GetAllScenePaths();
                            if (sp != null && sp.Length > 0) resolvedScenePath = sp[0];
                        }
                        catch (System.Exception __apiEx_177) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6048", __apiEx_177); }
                    }

                    if (!string.IsNullOrEmpty(resolvedScenePath))
                    {
                        AsyncOperation asyncOp = null;
                        try { asyncOp = SceneManager.LoadSceneAsync(resolvedScenePath, LoadSceneMode.Additive); } catch (System.Exception __apiEx_178) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6054", __apiEx_178); }
                        if (asyncOp != null)
                        {
                            int w = 0;
                            while (!asyncOp.isDone && w++ < 400) yield return null;
                            try
                            {
                                if (_findLoadedSceneMethod != null)
                                    extractedScene = (Scene)_findLoadedSceneMethod.Invoke(null, new object[] { sceneName, resolvedScenePath });
                                loadedViaSceneManager = extractedScene.IsValid();
                            }
                            catch (System.Exception __apiEx_179) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6065", __apiEx_179); }
                        }
                    }
                }

                if (extractedScene.IsValid())
                {
                    int extractedCount = 0;
                    try
                    {
                        if (containerGo.activeSelf) containerGo.SetActive(false);
                        for (int i = 0; i < paths.Count; i++)
                        {
                            string p = paths[i];
                            if (string.IsNullOrEmpty(p)) continue;
                            string ck = BuildSceneCacheKeySafe(sceneName, p);
                            if (string.IsNullOrEmpty(ck)) continue;
                            if (templatesDict.Contains(ck) && templatesDict[ck] as GameObject != null)
                            {
                                extractedCount++;
                                continue;
                            }
                            var foundGo = FindGameObjectInSceneSafe(extractedScene, p);
                            if (foundGo != null)
                            {
                                string origName = foundGo.name;
                                var cloned = UnityEngine.Object.Instantiate(foundGo, containerGo.transform, false);
                                cloned.name = origName;
                                cloned.SetActive(true);
                                RestoreTemplateComponentsEnabled(cloned);
                                HarvestPersonalObjectPoolsFromHierarchy(cloned);
                                PinAllAssetsOnHierarchy(cloned);
                                templatesDict[ck] = cloned;
                                StoreTemplateInAllRepackers(ck, cloned);
                                extractedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Batch extract error on '{sceneName}': {ex.Message}");
                    }

                    try
                    {
                        var sceneRoots = extractedScene.GetRootGameObjects();
                        if (sceneRoots != null)
                        {
                            for (int r = 0; r < sceneRoots.Length; r++)
                            {
                                if (sceneRoots[r] != null && sceneRoots[r].activeSelf) sceneRoots[r].SetActive(false);
                            }
                        }
                    }
                    catch (System.Exception __apiEx_180) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6083", __apiEx_180); }

                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Batch-extracted {extractedCount}/{paths.Count} template(s) from scene '{sceneName}' in a single pass.");

                    try
                    {
                        CleanupStrayBoneThumperClones();
                    }
                    catch (System.Exception __apiEx_181) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6126", __apiEx_181); }

                    {
                        AsyncOperation unloadOp = null;
                        try { unloadOp = SceneManager.UnloadSceneAsync(extractedScene); } catch (System.Exception __apiEx_182) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6130", __apiEx_182); }
                        if (unloadOp != null)
                        {
                            int w = 0;
                            while (!unloadOp.isDone && w++ < 200)
                            {
                                SetAllRepackersExtracting(true, sceneName);
                                yield return null;
                            }
                        }
                    }
                }
                else
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Could not load scene '{sceneName}' for batch extraction.");
                }

                yield return null;
                SetAllRepackersExtracting(false, string.Empty);

                SyncExtractedTemplatesIntoPharloomsGlory();
                HarmonyBridge.PruneStaleTransitionPoints();
                PharloomsGloryScenesDone++;
                yield return new WaitForSeconds(0.05f);
            }

            try { AudioListener.volume = origAudioVol; } catch (System.Exception __apiEx_183) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6156", __apiEx_183); }
            PharloomsGloryCurrentExtractingScene = "";
            IsPharloomsGloryBatchComplete = true;

            SyncExtractedTemplatesIntoPharloomsGlory();
            BindAllModManagedAssetsFromTemplateCache();
            HarmonyBridge.PruneStaleTransitionPoints();
            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Loading-screen batch extraction & non-scene prefab binding complete.");

            try
            {
                if (_pharloomsGloryAssembly != null)
                {
                    var smType = _pharloomsGloryAssembly.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                    var smInstField = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var smInst = smInstField?.GetValue(null);
                    if (smInst != null)
                    {
                        var activeScene = SceneManager.GetActiveScene();
                        if (activeScene.IsValid() && IsCurrentSceneInCoral(activeScene.name))
                        {
                            var listField = smType.GetField("currentlyAddedGameObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var list = listField?.GetValue(smInst) as IList;
                            var activeField = smType.GetField("active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (activeField != null && (bool)activeField.GetValue(smInst) && (list == null || list.Count == 0))
                            {
                                const BindingFlags IFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                                smType.GetMethod("AddClonedObjects", IFlags)?.Invoke(smInst, null);
                                smType.GetMethod("AddNewObjects", IFlags)?.Invoke(smInst, null);
                                smType.GetMethod("AddWaterfallObjects", IFlags)?.Invoke(smInst, null);
                                smType.GetMethod("AddWaterfallEffectsObjects", IFlags)?.Invoke(smInst, null);
                                smType.GetMethod("AddRiverObjects", IFlags)?.Invoke(smInst, null);
                            }
                            ApplyPharloomsGloryLightingImmediately();
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_184) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6193", __apiEx_184); }
        }

        public static void TryBootstrapPharloomsGloryAfterAwake(Assembly asm)
        {
            if (asm == null) return;
            _pharloomsGloryAssembly = asm;
            try
            {
                EnsurePharloomsGloryLightingHooksInstalled();
                var pluginType = asm.GetType("PharloomsGlory.Plugin", false);
                var pluginInst = pluginType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as BepInEx.BaseUnityPlugin;

                var cfgType = asm.GetType("PharloomsGlory.Managers.ConfigurationManager", false);
                var cfgInst = cfgType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (cfgInst != null && pluginInst != null)
                {
                    var bProp = cfgType.GetProperty("BlastedStepsMusic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var bindMi = cfgType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    try { bindMi?.Invoke(cfgInst, new object[] { pluginInst.Config }); } catch (System.Exception __apiEx_185) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6212", __apiEx_185); }
                }

                var audioModType = asm.GetType("PharloomsGlory.Modifiers.AudioModifier", false);
                var audioModInst = audioModType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (audioModInst != null)
                {
                    var ovField = audioModType.GetField("overriddenFileNames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var ovDict = ovField?.GetValue(audioModInst) as IDictionary;
                    if (ovDict == null || ovDict.Count == 0)
                    {
                        try { audioModType.GetMethod("LoadOverridesFromConfig", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(audioModInst, null); } catch (System.Exception __apiEx_186) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6223", __apiEx_186); }
                    }
                }

                if (pluginInst != null)
                {
                    var onSceneLoadedMi = pluginType.GetMethod("OnSceneLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (onSceneLoadedMi != null)
                    {
                        try
                        {
                            var del = (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction<Scene, LoadSceneMode>), pluginInst, onSceneLoadedMi);
                            SceneManager.sceneLoaded -= del;
                            SceneManager.sceneLoaded += del;
                        }
                        catch (System.Exception __apiEx_187) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L6238", __apiEx_187); }
                    }
                }

                EnsurePharloomsGloryAudioReloaded();
                SyncPharloomsGloryAudioAliases();
                var patcherType = asm.GetType("PharloomsGlory.Managers.PatcherManager", false);
                var patcherInst = patcherType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                var patchedField = patcherType?.GetField("patched", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (patcherInst != null && patchedField != null && !(bool)patchedField.GetValue(patcherInst))
                {
                    patcherType.GetMethod("Patch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(patcherInst, null);
                }

                var amType = asm.GetType("PharloomsGlory.Managers.AssetManager", false);
                var amInst = amType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (amInst != null)
                {
                    LoadPharloomsGloryAssets(amInst);
                }

                var smType = asm.GetType("PharloomsGlory.Modifiers.SceneModifier", false);
                var smInst = smType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                var modifyMi = smType?.GetMethod("Modify", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var activeScene = SceneManager.GetActiveScene();
                if (smInst != null && modifyMi != null && activeScene.IsValid() && IsCurrentSceneInCoral(activeScene.name))
                {
                    modifyMi.Invoke(smInst, new object[] { activeScene });
                    ApplyPharloomsGloryLightingImmediately();
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] TryBootstrapPharloomsGloryAfterAwake warning: {ex.Message}");
            }
        }

        public static AsyncOperationHandle<T> GetHandle<T>(object managedAsset) where T : UnityEngine.Object
        {
            if (managedAsset == null) return default;
            try
            {
                var type = managedAsset.GetType();
                string key = type.FullName ?? type.Name;
                if (!_handlePropCache.TryGetValue(key, out var prop))
                {
                    prop = type.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    _handlePropCache[key] = prop;
                }
                if (prop != null)
                {
                    var val = prop.GetValue(managedAsset, null);
                    if (val is AsyncOperationHandle<T> direct) return direct;
                    if (val != null)
                    {
                        var vType = val.GetType();
                        var hasValProp = vType.GetProperty("HasValue");
                        var valProp = vType.GetProperty("Value");
                        if (hasValProp != null && valProp != null && (bool)hasValProp.GetValue(val, null))
                        {
                            var unwrapped = valProp.GetValue(val, null);
                            if (unwrapped is AsyncOperationHandle<T> h) return h;
                        }
                    }
                }

                if (!_handleFieldCache.TryGetValue(key, out var field))
                {
                    var cur = type;
                    while (cur != null && cur != typeof(object))
                    {
                        field = cur.GetField("<Handle>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? cur.GetField("_handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? cur.GetField("handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null) break;
                        cur = cur.BaseType;
                    }
                    _handleFieldCache[key] = field;
                }
                if (field != null)
                {
                    var val = field.GetValue(managedAsset);
                    if (val is AsyncOperationHandle<T> direct) return direct;
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] GetHandle<{typeof(T).Name}> failed: {ex.Message}");
            }
            return default;
        }

        // =========================================================================
        // BossRush Run History & Stats Mobile Compatibility (Fixes ValueTuple SIGSEGV)
        // =========================================================================
        private static Assembly _cachedBossRushAsm;
        private static Assembly GetBossRushAssembly()
        {
            if (_cachedBossRushAsm != null) return _cachedBossRushAsm;
            foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
            {
                if (asm.GetName().Name == "BossRush" || asm.GetName().Name.StartsWith("BossRush", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedBossRushAsm = asm;
                    return asm;
                }
            }
            return null;
        }

        private static object GetPageMember(object obj, string memberName)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var f = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return p.GetValue(obj, null);
            return null;
        }

        private static void SetPageMember(object obj, string memberName, object value)
        {
            if (obj == null) return;
            var t = obj.GetType();
            var f = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) { f.SetValue(obj, value); return; }
            var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite) { p.SetValue(obj, value, null); }
        }

        public static void BossRushHistoryBuild(object pageObj)
        {
            try
            {
                if (pageObj == null) return;
                Assembly bossRushAsm = GetBossRushAssembly();
                if (bossRushAsm == null) return;

                Type historyPageType = bossRushAsm.GetType("BossRush.Menu.HistoryPage");
                Type bossRushStorageType = bossRushAsm.GetType("BossRush.Run.BossRushStorage");
                Type menuKitType = bossRushAsm.GetType("BossRush.Menu.MenuKit");
                Type locType = bossRushAsm.GetType("BossRush.Loc");

                if (historyPageType == null || bossRushStorageType == null || menuKitType == null) return;

                MethodInfo loadHistoryMi = bossRushStorageType.GetMethod("LoadHistory", BindingFlags.Static | BindingFlags.Public);
                IList runs = loadHistoryMi?.Invoke(null, null) as IList;

                FieldInfo runsField = historyPageType.GetField("_runs", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo pageField = historyPageType.GetField("_page", BindingFlags.Static | BindingFlags.NonPublic);

                if (runsField != null) runsField.SetValue(null, runs);

                int runsCount = runs != null ? runs.Count : 0;
                int pages = Math.Max(1, (runsCount + 10 - 1) / 10);

                int curPage = (pageField != null) ? (int)pageField.GetValue(null) : 0;
                curPage = Mathf.Clamp(curPage, 0, pages - 1);
                if (pageField != null) pageField.SetValue(null, curPage);

                RectTransform contentRect = GetPageMember(pageObj, "Content") as RectTransform;
                if (contentRect != null)
                {
                    contentRect.anchoredPosition = new Vector2(contentRect.anchoredPosition.x, -420f);
                    contentRect.sizeDelta = new Vector2(contentRect.sizeDelta.x, 70f);
                }

                if (pages > 1)
                {
                    string[] pageOptions = new string[pages];
                    MethodInfo locTMi = locType?.GetMethod("T", new Type[] { typeof(string) });
                    string pageNTemplate = (string)(locTMi?.Invoke(null, new object[] { "common.page_n" }) ?? "Page {n} of {total}");
                    for (int i = 0; i < pages; i++)
                    {
                        pageOptions[i] = pageNTemplate.Replace("{n}", (i + 1).ToString()).Replace("{total}", pages.ToString());
                    }

                    string pageLabel = (string)(locTMi?.Invoke(null, new object[] { "common.page" }) ?? "Page");

                    MethodInfo addOptionMi = menuKitType.GetMethod("AddOption", new Type[] {
                        pageObj.GetType(), typeof(string), typeof(string[]), typeof(int), typeof(Action<int>)
                    });

                    Action<int> onPageChange = (int i) =>
                    {
                        int p = (pageField != null) ? (int)pageField.GetValue(null) : 0;
                        if (p != i)
                        {
                            pageField?.SetValue(null, i);
                            Component screenComp = GetPageMember(pageObj, "Screen") as Component;
                            if (screenComp != null && screenComp.gameObject.activeInHierarchy)
                            {
                                BossRushHistoryDraw(pageObj, false);
                            }
                        }
                    };

                    addOptionMi?.Invoke(null, new object[] { pageObj, pageLabel, pageOptions, curPage, onPageChange });
                }

                BossRushHistoryDraw(pageObj, true);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] Error in BossRushHistoryBuild: {ex}");
            }
        }

        public static void BossRushHistoryDraw(object pageObj, bool initial)
        {
            try
            {
                if (pageObj == null) return;
                Assembly bossRushAsm = GetBossRushAssembly();
                if (bossRushAsm == null) return;

                Type historyPageType = bossRushAsm.GetType("BossRush.Menu.HistoryPage");
                Type menuKitType = bossRushAsm.GetType("BossRush.Menu.MenuKit");
                Type locType = bossRushAsm.GetType("BossRush.Loc");
                Type runStatsType = bossRushAsm.GetType("BossRush.Run.RunStats");
                Type bossCatalogType = bossRushAsm.GetType("BossRush.Run.BossCatalog");

                if (historyPageType == null || menuKitType == null) return;

                MethodInfo clearFreeMi = menuKitType.GetMethod("ClearFree", BindingFlags.Static | BindingFlags.Public);
                clearFreeMi?.Invoke(null, new object[] { pageObj });

                Transform freeTransform = GetPageMember(pageObj, "Free") as Transform;

                FieldInfo runsField = historyPageType.GetField("_runs", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo pageField = historyPageType.GetField("_page", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo openField = historyPageType.GetField("_open", BindingFlags.Static | BindingFlags.NonPublic);

                IList runs = runsField?.GetValue(null) as IList;
                int currentPage = (pageField != null) ? (int)pageField.GetValue(null) : 0;
                object openDelegate = openField?.GetValue(null);

                MethodInfo locTMi = locType?.GetMethod("T", new Type[] { typeof(string) });
                MethodInfo formatTimeMi = runStatsType?.GetMethod("FormatTime", BindingFlags.Static | BindingFlags.Public);
                MethodInfo nameOfMi = bossCatalogType?.GetMethod("NameOf", BindingFlags.Static | BindingFlags.Public);

                if (runs == null || runs.Count == 0)
                {
                    string emptyText = (string)(locTMi?.Invoke(null, new object[] { "history.empty" }) ?? "No runs recorded yet.");
                    MethodInfo addLabelMi = menuKitType.GetMethod("AddLabel", new Type[] {
                        typeof(Transform), typeof(string), typeof(Vector2), typeof(Vector2), typeof(int), typeof(TextAnchor)
                    });
                    addLabelMi?.Invoke(null, new object[] { freeTransform, emptyText, new Vector2(0f, 330f), new Vector2(1500f, 50f), 32, TextAnchor.MiddleCenter });
                }
                else
                {
                    MethodInfo addTextButtonMi = menuKitType.GetMethod("AddTextButton", new Type[] {
                        pageObj.GetType(), typeof(Transform), typeof(Vector2), typeof(Vector2), typeof(string), typeof(int), typeof(Action)
                    });

                    int startIdx = currentPage * 10;
                    int count = Math.Min(10, runs.Count - startIdx);

                    for (int item2 = 0; item2 < count; item2++)
                    {
                        object runObj = runs[startIdx + item2];
                        if (runObj == null) continue;

                        FieldInfo bossesField = runObj.GetType().GetField("bosses");
                        IList bossesList = bossesField?.GetValue(runObj) as IList;
                        int clearedCount = 0;
                        int totalBosses = bossesList != null ? bossesList.Count : 0;

                        FieldInfo bossesTotalField = runObj.GetType().GetField("bossesTotal");
                        if (bossesTotalField != null)
                        {
                            int bt = (int)bossesTotalField.GetValue(runObj);
                            if (bt > 0) totalBosses = bt;
                        }

                        if (bossesList != null)
                        {
                            foreach (var b in bossesList)
                            {
                                if (b != null)
                                {
                                    FieldInfo clearedField = b.GetType().GetField("cleared");
                                    if (clearedField != null && (bool)clearedField.GetValue(b))
                                    {
                                        clearedCount++;
                                    }
                                }
                            }
                        }

                        FieldInfo finishedLocalField = runObj.GetType().GetField("finishedLocal");
                        string finishedLocal = (string)finishedLocalField?.GetValue(runObj) ?? "";

                        FieldInfo modeField = runObj.GetType().GetField("mode");
                        object modeVal = modeField?.GetValue(runObj);
                        string modeStr = modeVal != null ? modeVal.ToString() : "";
                        string modeLabel;
                        if (modeStr == "Single" && bossesList != null && bossesList.Count > 0)
                        {
                            object firstBoss = bossesList[0];
                            string bId = (string)firstBoss.GetType().GetField("id")?.GetValue(firstBoss) ?? "";
                            string bName = (string)firstBoss.GetType().GetField("name")?.GetValue(firstBoss) ?? "";
                            string resolvedName = (string)(nameOfMi?.Invoke(null, new object[] { bId, bName }) ?? bName);
                            string singleTemplate = (string)(locTMi?.Invoke(null, new object[] { "history.single" }) ?? "{boss}");
                            modeLabel = singleTemplate.Replace("{boss}", resolvedName);
                        }
                        else
                        {
                            Type runPlanType = bossRushAsm.GetType("BossRush.Run.RunPlan");
                            MethodInfo modeNameMi = runPlanType?.GetMethod("ModeName", BindingFlags.Static | BindingFlags.Public);
                            modeLabel = (string)(modeNameMi?.Invoke(null, new object[] { modeVal }) ?? modeStr);
                        }

                        FieldInfo outcomeField = runObj.GetType().GetField("outcome");
                        string outcomeVal = (string)outcomeField?.GetValue(runObj) ?? "";
                        FieldInfo diedToField = runObj.GetType().GetField("diedTo");
                        string diedToVal = (string)diedToField?.GetValue(runObj) ?? "";
                        FieldInfo completedField = runObj.GetType().GetField("completed");
                        bool isCompleted = (completedField != null) && (bool)completedField.GetValue(runObj);

                        string outcome;
                        if (string.IsNullOrEmpty(outcomeVal))
                        {
                            outcome = (string)(locTMi?.Invoke(null, new object[] { isCompleted ? "outcome.cleared" : "outcome.unfinished" }) ?? (isCompleted ? "Cleared" : "Unfinished"));
                        }
                        else if (outcomeVal == "Died" && !string.IsNullOrEmpty(diedToVal))
                        {
                            string diedBossName = diedToVal;
                            if (bossesList != null && bossesList.Count > 0)
                            {
                                object lastBoss = bossesList[bossesList.Count - 1];
                                if (lastBoss != null)
                                {
                                    string lbName = (string)lastBoss.GetType().GetField("name")?.GetValue(lastBoss);
                                    if (lbName == diedToVal)
                                    {
                                        string lbId = (string)lastBoss.GetType().GetField("id")?.GetValue(lastBoss);
                                        diedBossName = (string)(nameOfMi?.Invoke(null, new object[] { lbId, diedToVal }) ?? diedToVal);
                                    }
                                }
                            }
                            string diedToTemplate = (string)(locTMi?.Invoke(null, new object[] { "outcome.died_to" }) ?? "Died to {boss}");
                            outcome = diedToTemplate.Replace("{boss}", diedBossName);
                        }
                        else
                        {
                            string key = "outcome." + outcomeVal.ToLowerInvariant();
                            string translated = (string)(locTMi?.Invoke(null, new object[] { key }) ?? "");
                            outcome = !string.IsNullOrEmpty(translated) && translated != key ? translated : outcomeVal;
                        }

                        FieldInfo fightTimeField = runObj.GetType().GetField("fightTime");
                        float fightTime = (fightTimeField != null) ? (float)fightTimeField.GetValue(runObj) : 0f;
                        string formattedTime = (string)(formatTimeMi?.Invoke(null, new object[] { fightTime }) ?? $"{fightTime:F1}s");

                        FieldInfo diffField = runObj.GetType().GetField("difficulty");
                        object diffObj = diffField?.GetValue(runObj);
                        string diffDesc = "";
                        if (diffObj != null)
                        {
                            MethodInfo normalMi = diffObj.GetType().GetMethod("Normal");
                            bool isNormal = (normalMi != null) ? (bool)normalMi.Invoke(diffObj, null) : true;
                            if (!isNormal)
                            {
                                MethodInfo descMi = diffObj.GetType().GetMethod("Describe");
                                string d = (string)descMi?.Invoke(diffObj, null);
                                if (!string.IsNullOrEmpty(d)) diffDesc = "    " + d;
                            }
                        }

                        string label = $"{finishedLocal}    {modeLabel}    {outcome}    {clearedCount}/{totalBosses}    {formattedTime}{diffDesc}";

                        object capturedRun = runObj;
                        Action onSubmit = () =>
                        {
                            if (openDelegate is Delegate del)
                            {
                                del.DynamicInvoke(capturedRun);
                            }
                        };

                        object focusButton = addTextButtonMi?.Invoke(null, new object[] {
                            pageObj, freeTransform, new Vector2(0f, 330f - (float)item2 * 54f), new Vector2(1500f, 54f), label, 30, onSubmit
                        });

                        if (item2 == 0 && initial && focusButton != null)
                        {
                            SetPageMember(pageObj, "Focus", focusButton);
                        }
                    }
                }

                if (!initial)
                {
                    MethodInfo relinkMi = menuKitType.GetMethod("Relink", BindingFlags.Static | BindingFlags.Public);
                    relinkMi?.Invoke(null, new object[] { pageObj });
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] Error in BossRushHistoryDraw: {ex}");
            }
        }

        public static void BossRushStatsBuildTotals(object pageObj, object bossPageObj)
        {
            try
            {
                if (pageObj == null) return;
                Assembly bossRushAsm = GetBossRushAssembly();
                if (bossRushAsm == null) return;

                Type statsPagesType = bossRushAsm.GetType("BossRush.Menu.StatsPages");
                Type bossRushStorageType = bossRushAsm.GetType("BossRush.Run.BossRushStorage");
                Type menuKitType = bossRushAsm.GetType("BossRush.Menu.MenuKit");
                Type locType = bossRushAsm.GetType("BossRush.Loc");
                Type runStatsType = bossRushAsm.GetType("BossRush.Run.RunStats");
                Type historyPageType = bossRushAsm.GetType("BossRush.Menu.HistoryPage");
                Type bossCatalogType = bossRushAsm.GetType("BossRush.Run.BossCatalog");

                if (statsPagesType == null || menuKitType == null) return;

                FieldInfo bossPageField = statsPagesType.GetField("_bossPage", BindingFlags.Static | BindingFlags.NonPublic);
                if (bossPageField != null) bossPageField.SetValue(null, bossPageObj);

                FieldInfo summaryField = statsPagesType.GetField("_summary", BindingFlags.Static | BindingFlags.NonPublic);
                object s = summaryField?.GetValue(null);
                if (s == null && bossRushStorageType != null)
                {
                    MethodInfo loadSummaryMi = bossRushStorageType.GetMethod("LoadSummary", BindingFlags.Static | BindingFlags.Public);
                    s = loadSummaryMi?.Invoke(null, null);
                }
                if (s == null) return;
                if (summaryField != null) summaryField.SetValue(null, s);

                FieldInfo bossesField = s.GetType().GetField("bosses");
                IList bossesList = bossesField?.GetValue(s) as IList;

                FieldInfo diffField = s.GetType().GetField("difficulty");
                object diffObj = diffField?.GetValue(s);

                MethodInfo locTMi = locType?.GetMethod("T", new Type[] { typeof(string) });
                MethodInfo formatTimeMi = runStatsType?.GetMethod("FormatTime", BindingFlags.Static | BindingFlags.Public);
                MethodInfo nameOfMi = bossCatalogType?.GetMethod("NameOf", BindingFlags.Static | BindingFlags.Public);
                MethodInfo modeLabelMi = historyPageType?.GetMethod("ModeLabel", BindingFlags.Static | BindingFlags.Public);
                MethodInfo outcomeMi = historyPageType?.GetMethod("Outcome", BindingFlags.Static | BindingFlags.Public);

                string modeLabel = (string)(modeLabelMi?.Invoke(null, new object[] { s }) ?? "");
                string outcome = (string)(outcomeMi?.Invoke(null, new object[] { s }) ?? "");

                FieldInfo permadeathField = s.GetType().GetField("permadeath");
                bool permadeath = (permadeathField != null) && (bool)permadeathField.GetValue(s);
                string deathMode = (string)(locTMi?.Invoke(null, new object[] { permadeath ? "stats.death_mode.permadeath" : "stats.death_mode.retry" }) ?? (permadeath ? "Permadeath" : "Retry"));

                string diffDesc = "";
                bool isNormalDiff = true;
                if (diffObj != null)
                {
                    MethodInfo normalMi = diffObj.GetType().GetMethod("Normal");
                    isNormalDiff = (normalMi != null) ? (bool)normalMi.Invoke(diffObj, null) : true;
                    MethodInfo descMi = diffObj.GetType().GetMethod("Describe");
                    diffDesc = (string)(descMi?.Invoke(diffObj, null) ?? "");
                }

                FieldInfo finishedLocalField = s.GetType().GetField("finishedLocal");
                string finishedLocal = (string)finishedLocalField?.GetValue(s) ?? "";

                FieldInfo fightTimeField = s.GetType().GetField("fightTime");
                float fightTime = (fightTimeField != null) ? (float)fightTimeField.GetValue(s) : 0f;

                float sumTime = 0f;
                int clearedCount = 0;
                int totalDeaths = 0;
                long totalDamageDealt = 0;
                int totalDamageTaken = 0;
                int totalHitsTaken = 0;
                int totalBinds = 0;
                int totalSilk = 0;

                object fastestBoss = null;
                float minTime = float.MaxValue;
                object toughestBoss = null;
                int maxDeaths = -1;
                int maxDamageTaken = -1;

                FieldInfo completedField = s.GetType().GetField("completed");
                bool isCompleted = (completedField != null) && (bool)completedField.GetValue(s);

                if (bossesList != null)
                {
                    foreach (var b in bossesList)
                    {
                        if (b == null) continue;
                        float t = (float)(b.GetType().GetField("time")?.GetValue(b) ?? 0f);
                        sumTime += t;
                        bool cl = (bool)(b.GetType().GetField("cleared")?.GetValue(b) ?? false);
                        if (cl || isCompleted) clearedCount++;
                        int d = (int)(b.GetType().GetField("deaths")?.GetValue(b) ?? 0);
                        totalDeaths += d;
                        int dd = (int)(b.GetType().GetField("damageDealt")?.GetValue(b) ?? 0);
                        totalDamageDealt += dd;
                        int dt = (int)(b.GetType().GetField("damageTaken")?.GetValue(b) ?? 0);
                        totalDamageTaken += dt;
                        int ht = (int)(b.GetType().GetField("hitsTaken")?.GetValue(b) ?? 0);
                        totalHitsTaken += ht;
                        int bn = (int)(b.GetType().GetField("binds")?.GetValue(b) ?? 0);
                        totalBinds += bn;
                        int sg = (int)(b.GetType().GetField("silkGained")?.GetValue(b) ?? 0);
                        totalSilk += sg;

                        if (t > 0f && t < minTime)
                        {
                            minTime = t;
                            fastestBoss = b;
                        }
                        if (d > maxDeaths || (d == maxDeaths && dt > maxDamageTaken))
                        {
                            maxDeaths = d;
                            maxDamageTaken = dt;
                            toughestBoss = b;
                        }
                    }
                }

                FieldInfo totalTimeField = s.GetType().GetField("totalTime");
                float totalTime = (totalTimeField != null) ? (float)totalTimeField.GetValue(s) : 0f;

                FieldInfo bossesTotalField = s.GetType().GetField("bossesTotal");
                int totalBosses = bossesList != null ? bossesList.Count : 0;
                if (bossesTotalField != null)
                {
                    int bt = (int)bossesTotalField.GetValue(s);
                    if (bt > 0) totalBosses = bt;
                }

                string speedrunTimeStr = (string)(formatTimeMi?.Invoke(null, new object[] { fightTime > 0f ? fightTime : sumTime }) ?? "");
                string realTimeStr = (string)(formatTimeMi?.Invoke(null, new object[] { totalTime }) ?? "");

                string damageTakenTemplate = (string)(locTMi?.Invoke(null, new object[] { "stats.damage_taken_value" }) ?? "{n} masks");
                string damageTakenStr = damageTakenTemplate.Replace("{n}", totalDamageTaken.ToString());

                string[] statKeys = new string[] {
                    "stats.mode", "stats.result", "stats.death_mode", "stats.difficulty", "stats.date",
                    "stats.speedrun_time", "stats.real_time", "stats.bosses_cleared", "stats.deaths",
                    "stats.damage_dealt", "stats.damage_taken", "stats.hits_taken", "stats.binds", "stats.silk_gained"
                };

                string[] statValues = new string[] {
                    modeLabel,
                    outcome,
                    deathMode,
                    diffDesc,
                    finishedLocal,
                    speedrunTimeStr,
                    realTimeStr,
                    $"{clearedCount}/{totalBosses}",
                    totalDeaths.ToString(),
                    totalDamageDealt.ToString("N0"),
                    damageTakenStr,
                    totalHitsTaken.ToString(),
                    totalBinds.ToString(),
                    totalSilk.ToString()
                };

                Transform freeTransform = GetPageMember(pageObj, "Free") as Transform;

                MethodInfo addLabelMi = menuKitType.GetMethod("AddLabel", new Type[] {
                    typeof(Transform), typeof(string), typeof(Vector2), typeof(Vector2), typeof(int), typeof(TextAnchor)
                });

                float y = 340f;
                PropertyInfo maxColorProp = menuKitType.GetProperty("MaxColor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Color maxColor = (maxColorProp != null) ? (Color)maxColorProp.GetValue(null) : Color.yellow;

                for (int i = 0; i < statKeys.Length; i++)
                {
                    string labelKey = statKeys[i];
                    string labelTrans = (string)(locTMi?.Invoke(null, new object[] { labelKey }) ?? labelKey);
                    string valText = statValues[i];

                    Text labelComponent = addLabelMi?.Invoke(null, new object[] { freeTransform, labelTrans, new Vector2(-330f, y), new Vector2(660f, 46f), 34, TextAnchor.MiddleLeft }) as Text;
                    if (labelComponent != null) labelComponent.horizontalOverflow = HorizontalWrapMode.Overflow;

                    Text valComponent = addLabelMi?.Invoke(null, new object[] { freeTransform, valText, new Vector2(330f, y), new Vector2(660f, 46f), 34, TextAnchor.MiddleRight }) as Text;
                    if (valComponent != null)
                    {
                        valComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
                        if (labelKey == "stats.difficulty" && !isNormalDiff)
                        {
                            valComponent.color = maxColor;
                        }
                    }
                    y -= 46f;
                }

                if (fastestBoss != null)
                {
                    string fId = (string)fastestBoss.GetType().GetField("id")?.GetValue(fastestBoss) ?? "";
                    string fName = (string)fastestBoss.GetType().GetField("name")?.GetValue(fastestBoss) ?? "";
                    string fastestResolved = (string)(nameOfMi?.Invoke(null, new object[] { fId, fName }) ?? fName);

                    string tId = toughestBoss != null ? (string)toughestBoss.GetType().GetField("id")?.GetValue(toughestBoss) ?? "" : "";
                    string tName = toughestBoss != null ? (string)toughestBoss.GetType().GetField("name")?.GetValue(toughestBoss) ?? "" : "";
                    string toughestResolved = (string)(nameOfMi?.Invoke(null, new object[] { tId, tName }) ?? tName);
                    int toughestDeaths = toughestBoss != null ? (int)(toughestBoss.GetType().GetField("deaths")?.GetValue(toughestBoss) ?? 0) : 0;

                    string ftTemplate = (string)(locTMi?.Invoke(null, new object[] { "stats.fastest_toughest" }) ?? "Fastest: {fastest} ({time})  |  Toughest: {toughest} ({deaths} deaths)");
                    string ftText = ftTemplate
                        .Replace("{fastest}", fastestResolved)
                        .Replace("{time}", (string)(formatTimeMi?.Invoke(null, new object[] { minTime }) ?? $"{minTime:F1}s"))
                        .Replace("{toughest}", toughestResolved)
                        .Replace("{deaths}", toughestDeaths.ToString());

                    addLabelMi?.Invoke(null, new object[] { freeTransform, ftText, new Vector2(0f, y - 10f), new Vector2(1500f, 44f), 28, TextAnchor.MiddleCenter });
                }

                RectTransform contentRect = GetPageMember(pageObj, "Content") as RectTransform;
                if (contentRect != null)
                {
                    contentRect.anchoredPosition = new Vector2(contentRect.anchoredPosition.x, -400f);
                    contentRect.sizeDelta = new Vector2(contentRect.sizeDelta.x, 80f);
                }

                string perBossText = (string)(locTMi?.Invoke(null, new object[] { "stats.per_boss" }) ?? "Per Boss Stats");
                MethodInfo addButtonMi = menuKitType.GetMethod("AddButton", new Type[] {
                    pageObj.GetType(), typeof(string), typeof(Action)
                });

                FieldInfo statsPageField = statsPagesType.GetField("_page", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo openMi = menuKitType.GetMethod("Open", BindingFlags.Static | BindingFlags.Public);

                Action onPerBoss = () =>
                {
                    statsPageField?.SetValue(null, 0);
                    openMi?.Invoke(null, new object[] { bossPageObj });
                };

                addButtonMi?.Invoke(null, new object[] { pageObj, perBossText, onPerBoss });
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] Error in BossRushStatsBuildTotals: {ex}");
            }
        }

        public static void BossRushStatsBuildBosses(object pageObj)
        {
            try
            {
                if (pageObj == null) return;
                Assembly bossRushAsm = GetBossRushAssembly();
                if (bossRushAsm == null) return;

                Type statsPagesType = bossRushAsm.GetType("BossRush.Menu.StatsPages");
                Type bossRushStorageType = bossRushAsm.GetType("BossRush.Run.BossRushStorage");
                Type menuKitType = bossRushAsm.GetType("BossRush.Menu.MenuKit");
                Type locType = bossRushAsm.GetType("BossRush.Loc");

                if (statsPagesType == null || menuKitType == null) return;

                FieldInfo summaryField = statsPagesType.GetField("_summary", BindingFlags.Static | BindingFlags.NonPublic);
                object s = summaryField?.GetValue(null);
                if (s == null && bossRushStorageType != null)
                {
                    MethodInfo loadSummaryMi = bossRushStorageType.GetMethod("LoadSummary", BindingFlags.Static | BindingFlags.Public);
                    s = loadSummaryMi?.Invoke(null, null);
                }
                if (s == null) return;

                FieldInfo bossesField = s.GetType().GetField("bosses");
                IList bossesList = bossesField?.GetValue(s) as IList;
                int totalCount = bossesList != null ? bossesList.Count : 0;
                int pages = Math.Max(1, (totalCount + 12 - 1) / 12);

                FieldInfo pageField = statsPagesType.GetField("_page", BindingFlags.Static | BindingFlags.NonPublic);
                int curPage = (pageField != null) ? (int)pageField.GetValue(null) : 0;
                curPage = Mathf.Clamp(curPage, 0, pages - 1);
                if (pageField != null) pageField.SetValue(null, curPage);

                RectTransform contentRect = GetPageMember(pageObj, "Content") as RectTransform;
                if (contentRect != null)
                {
                    contentRect.anchoredPosition = new Vector2(contentRect.anchoredPosition.x, -420f);
                    contentRect.sizeDelta = new Vector2(contentRect.sizeDelta.x, 70f);
                }

                MethodInfo locTMi = locType?.GetMethod("T", new Type[] { typeof(string) });
                if (pages > 1)
                {
                    string[] pageOptions = new string[pages];
                    string pageNTemplate = (string)(locTMi?.Invoke(null, new object[] { "common.page_n" }) ?? "Page {n} of {total}");
                    for (int i = 0; i < pages; i++)
                    {
                        pageOptions[i] = pageNTemplate.Replace("{n}", (i + 1).ToString()).Replace("{total}", pages.ToString());
                    }
                    string pageLabel = (string)(locTMi?.Invoke(null, new object[] { "common.page" }) ?? "Page");
                    MethodInfo addOptionMi = menuKitType.GetMethod("AddOption", new Type[] {
                        pageObj.GetType(), typeof(string), typeof(string[]), typeof(int), typeof(Action<int>)
                    });

                    Action<int> onPageChange = (int i) =>
                    {
                        int p = (pageField != null) ? (int)pageField.GetValue(null) : 0;
                        if (p != i)
                        {
                            pageField?.SetValue(null, i);
                            BossRushStatsDrawTable(pageObj, false);
                        }
                    };
                    addOptionMi?.Invoke(null, new object[] { pageObj, pageLabel, pageOptions, curPage, onPageChange });
                }

                BossRushStatsDrawTable(pageObj, true);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] Error in BossRushStatsBuildBosses: {ex}");
            }
        }

        public static void BossRushStatsDrawTable(object pageObj, bool initial)
        {
            try
            {
                if (pageObj == null) return;
                Assembly bossRushAsm = GetBossRushAssembly();
                if (bossRushAsm == null) return;

                Type statsPagesType = bossRushAsm.GetType("BossRush.Menu.StatsPages");
                Type menuKitType = bossRushAsm.GetType("BossRush.Menu.MenuKit");
                Type locType = bossRushAsm.GetType("BossRush.Loc");
                Type runStatsType = bossRushAsm.GetType("BossRush.Run.RunStats");
                Type bossCatalogType = bossRushAsm.GetType("BossRush.Run.BossCatalog");

                if (statsPagesType == null || menuKitType == null) return;

                MethodInfo clearFreeMi = menuKitType.GetMethod("ClearFree", BindingFlags.Static | BindingFlags.Public);
                clearFreeMi?.Invoke(null, new object[] { pageObj });

                Transform freeTransform = GetPageMember(pageObj, "Free") as Transform;

                FieldInfo summaryField = statsPagesType.GetField("_summary", BindingFlags.Static | BindingFlags.NonPublic);
                object s = summaryField?.GetValue(null);
                if (s == null) return;

                FieldInfo pageField = statsPagesType.GetField("_page", BindingFlags.Static | BindingFlags.NonPublic);
                int curPage = (pageField != null) ? (int)pageField.GetValue(null) : 0;

                MethodInfo locTMi = locType?.GetMethod("T", new Type[] { typeof(string) });
                MethodInfo formatTimeMi = runStatsType?.GetMethod("FormatTime", BindingFlags.Static | BindingFlags.Public);
                MethodInfo nameOfMi = bossCatalogType?.GetMethod("NameOf", BindingFlags.Static | BindingFlags.Public);
                MethodInfo addLabelMi = menuKitType.GetMethod("AddLabel", new Type[] {
                    typeof(Transform), typeof(string), typeof(Vector2), typeof(Vector2), typeof(int), typeof(TextAnchor)
                });

                string[] colLabels = new string[] { "stats.col.boss", "stats.col.time", "stats.col.deaths", "stats.col.damage", "stats.col.masks_lost", "stats.col.binds", "stats.col.silk" };
                float[] colX = new float[] { -540f, -40f, 130f, 310f, 500f, 670f, 820f };
                TextAnchor[] colAlign = new TextAnchor[] { TextAnchor.MiddleLeft, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter };

                float topY = 340f;
                for (int i = 0; i < colLabels.Length; i++)
                {
                    string headerText = (string)(locTMi?.Invoke(null, new object[] { colLabels[i] }) ?? colLabels[i]);
                    float x = colX[i] + (i == 0 ? 20f : 0f);
                    float width = (i == 0 ? 520f : 180f);
                    Text header = addLabelMi?.Invoke(null, new object[] { freeTransform, headerText, new Vector2(x, topY), new Vector2(width, 44f), 30, colAlign[i] }) as Text;
                    if (header != null) header.horizontalOverflow = HorizontalWrapMode.Overflow;
                }

                FieldInfo bossesField = s.GetType().GetField("bosses");
                IList bossesList = bossesField?.GetValue(s) as IList;
                if (bossesList != null)
                {
                    int start = curPage * 12;
                    int count = Math.Min(12, bossesList.Count - start);

                    for (int j = 0; j < count; j++)
                    {
                        object b = bossesList[start + j];
                        if (b == null) continue;

                        float rowY = topY - 60f - (float)j * 46f;

                        string bId = (string)b.GetType().GetField("id")?.GetValue(b) ?? "";
                        string bName = (string)b.GetType().GetField("name")?.GetValue(b) ?? "";
                        string bossDispName = (string)(nameOfMi?.Invoke(null, new object[] { bId, bName }) ?? bName);

                        float bTime = (float)(b.GetType().GetField("time")?.GetValue(b) ?? 0f);
                        string timeStr = (string)(formatTimeMi?.Invoke(null, new object[] { bTime }) ?? $"{bTime:F1}s");

                        int deaths = (int)(b.GetType().GetField("deaths")?.GetValue(b) ?? 0);
                        int damageDealt = (int)(b.GetType().GetField("damageDealt")?.GetValue(b) ?? 0);
                        int damageTaken = (int)(b.GetType().GetField("damageTaken")?.GetValue(b) ?? 0);
                        int binds = (int)(b.GetType().GetField("binds")?.GetValue(b) ?? 0);
                        int silk = (int)(b.GetType().GetField("silkGained")?.GetValue(b) ?? 0);

                        string[] rowVals = new string[] {
                            bossDispName, timeStr, deaths.ToString(), damageDealt.ToString("N0"), damageTaken.ToString(), binds.ToString(), silk.ToString()
                        };

                        for (int k = 0; k < rowVals.Length; k++)
                        {
                            float x = colX[k] + (k == 0 ? 20f : 0f);
                            float width = (k == 0 ? 520f : 180f);
                            Text cell = addLabelMi?.Invoke(null, new object[] { freeTransform, rowVals[k], new Vector2(x, rowY), new Vector2(width, 44f), 28, colAlign[k] }) as Text;
                            if (cell != null) cell.horizontalOverflow = HorizontalWrapMode.Overflow;
                        }
                    }
                }

                if (!initial)
                {
                    MethodInfo relinkMi = menuKitType.GetMethod("Relink", BindingFlags.Static | BindingFlags.Public);
                    relinkMi?.Invoke(null, new object[] { pageObj });
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[AssetHelperBridge] Error in BossRushStatsDrawTable: {ex}");
            }
        }

        // =========================================================================================
        // UNIVERSAL ASSETHELPER SCENE-KEY BRIDGE & SHADER REBINDER (v2.4.31 — Zero Native HookMethod)
        // =========================================================================================
        private static bool _universalAssetHelperHooksInstalled;
        private static readonly HashSet<int> _reboundMaterialIds = new HashSet<int>();
        private static readonly Dictionary<string, Shader> _nativeShaderCache = new Dictionary<string, Shader>(StringComparer.OrdinalIgnoreCase);
        private static bool _nativeShadersIndexed;

        private static readonly Dictionary<string, GameObject> _bridgeFallbackTemplates =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static GameObject _bridgeTemplateContainerGo;


        public static GameObject EnsureBridgeTemplateContainer()
        {
            if (_bridgeTemplateContainerGo != null) return _bridgeTemplateContainerGo;
            _bridgeTemplateContainerGo = new GameObject("SilksongMobileAPI_SceneTemplateContainer");
            UnityEngine.Object.DontDestroyOnLoad(_bridgeTemplateContainerGo);
            _bridgeTemplateContainerGo.SetActive(false);
            return _bridgeTemplateContainerGo;
        }

        public static string BuildSceneCacheKeySafe(string sceneName, string objPath)
        {
            if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(objPath)) return string.Empty;
            try
            {
                if (_buildCacheKeyMethod != null)
                {
                    var res = _buildCacheKeyMethod.Invoke(null, new object[] { sceneName, objPath }) as string;
                    if (!string.IsNullOrEmpty(res)) return res;
                }
            }
            catch (System.Exception __apiEx_188) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7094", __apiEx_188); }
            return sceneName.Trim().ToLowerInvariant() + "::" + objPath.Trim().Replace('\\', '/').TrimStart('/');
        }

        public static GameObject FindGameObjectInSceneSafe(Scene scene, string objPath)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(objPath)) return null;
            try
            {
                if (_findGoInSceneMethod != null)
                {
                    var found = _findGoInSceneMethod.Invoke(null, new object[] { scene, objPath }) as GameObject;
                    if (found != null) return found;
                }
            }
            catch (System.Exception __apiEx_189) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7109", __apiEx_189); }
            try
            {
                string norm = objPath.Replace('\\', '/').Trim('/');
                string[] parts = norm.Split('/');
                var roots = scene.GetRootGameObjects();
                if (roots == null || parts.Length == 0) return null;
                for (int r = 0; r < roots.Length; r++)
                {
                    var root = roots[r];
                    if (root == null) continue;
                    if (string.Equals(root.name, parts[0], StringComparison.OrdinalIgnoreCase))
                    {
                        Transform cur = root.transform;
                        for (int p = 1; p < parts.Length && cur != null; p++)
                        {
                            Transform next = cur.Find(parts[p]);
                            if (next == null)
                            {
                                for (int c = 0; c < cur.childCount; c++)
                                {
                                    var ch = cur.GetChild(c);
                                    if (ch != null && string.Equals(ch.name, parts[p], StringComparison.OrdinalIgnoreCase))
                                    {
                                        next = ch;
                                        break;
                                    }
                                }
                            }
                            cur = next;
                        }
                        if (cur != null) return cur.gameObject;
                    }
                }
            }
            catch (System.Exception __apiEx_190) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7144", __apiEx_190); }
            return null;
        }

        public static bool ShouldSkipComponentDuringSceneExtraction(Component comp)
        {
            if (comp == null) return false;
            if (!IsExtractingPharloomsGloryScene && !IsAnyRepackerBusy()) return false;
            try
            {
                var go = comp.gameObject;
                if (go == null) return false;
                var sc = go.scene;
                string scName = sc.name;
                if (string.IsNullOrEmpty(scName)) return false;
                if (!string.IsNullOrEmpty(CurrentExtractingPharloomsGloryScene) &&
                    scName.Equals(CurrentExtractingPharloomsGloryScene, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                for (int i = 0; i < _allCurrentSceneFields.Count; i++)
                {
                    if (_allCurrentSceneFields[i].GetValue(null) is string repSc &&
                        !string.IsNullOrEmpty(repSc) &&
                        scName.Equals(repSc, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                for (int i = 0; i < _allCurrentSceneProps.Count; i++)
                {
                    if (_allCurrentSceneProps[i].GetValue(null, null) is string repSc &&
                        !string.IsNullOrEmpty(repSc) &&
                        scName.Equals(repSc, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception __apiEx_191) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7188", __apiEx_191); }
            return false;
        }

        public static bool SceneExtractionLifecycleGuard_Prefix(Component __instance)
        {
            try
            {
                if ((object)__instance == null || __instance == null) return false;
                if (ShouldSkipComponentDuringSceneExtraction(__instance))
                {
                    // Keep PlayMakerFSM, TriggerEnterEvent, and BossSceneController enabled = true on extracted templates,
                    // but disable GradeMarker so GradeMarker.Update() never runs per-frame during additive scene extraction.
                    if (__instance is GradeMarker gm)
                    {
                        try { gm.enabled = false; } catch { }
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        public static void InstallUniversalAssetHelperHooks()
        {
            EnsureRepackerMembersResolved();
            if (_universalAssetHelperHooksInstalled) return;
            _universalAssetHelperHooksInstalled = true;

            try
            {
                const BindingFlags MFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var guardPre = typeof(AssetHelperBridge).GetMethod(nameof(SceneExtractionLifecycleGuard_Prefix), BindingFlags.Static | BindingFlags.Public);
                string[] guardTypeNames = new[]
                {
                    "TransitionPoint",
                    "CustomSceneManager",
                    "CameraLockArea",
                    "TrackTriggerObjects",
                    "PersistentBoolItem",
                    "PersistentIntItem",
                    "SceneAdditiveLoadConditional",
                    "BossSceneController",
                    "PlayMakerFSM",
                    "RegionSetAudio",
                    "SceneAppearanceRegion",
                    "TriggerEnterEvent",
                    "CaptureAnimationEvent",
                    "Remasker",
                    "AreaTitleController",
                    "GradeMarker",
                    "GrassBehaviour",
                    "AudioSourceGamePause",
                    "EnemyHitEffectsRegular",
                    "EnemyDeathEffectsRegular",
                    "PersonalObjectPool",
                    "HealthManager",
                    "TestGameObjectActivator",
                    "VentRegion",
                    "HeroTouchForce"
                };
                // Do NOT hook "Update" on PlayMakerFSM, HealthManager, etc. (avoids thousands of per-frame native trampoline crossings);
                // only hook "Update" on GradeMarker so additive extraction scenes cannot run uninitialized GradeMarker.Update().
                string[] guardMethodNames = new[] { "Awake", "OnAwake", "OnEnable", "Start", "OnStart", "OnDisable", "Update" };
                foreach (var tn in guardTypeNames)
                {
                    var t = HarmonyLib.AccessTools.TypeByName(tn);
                    if (t == null) continue;
                    foreach (var mn in guardMethodNames)
                    {
                        if (mn == "Update" && tn != "GradeMarker" && tn != "HealthManager")
                            continue;
                        var mi = t.GetMethod(mn, MFlags);
                        if (mi == null && t.BaseType != null && t.BaseType.IsGenericType &&
                            t.BaseType.Name.StartsWith("PersistentItem", StringComparison.Ordinal))
                        {
                            mi = t.BaseType.GetMethod(mn, MFlags);
                        }
                        if (mi != null && guardPre != null)
                        {
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(
                                "SilksongMobileAPI.LifecycleGuard",
                                mi,
                                guardPre,
                                null,
                                null,
                                HarmonyLib.Priority.First);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] Lifecycle guard install warning: {ex.Message}");
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Universal AssetHelper Scene-Key Rewriter & Multiplexed Scene Lifecycle Guards active.");
        }

        private static readonly Dictionary<Type, FieldInfo> _managedAssetAddressFieldCache = new Dictionary<Type, FieldInfo>();

        public static string RewriteSceneAssetAddressKey(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            const string oldPrefix = "AssetHelper-SceneAssets/Assets/";
            const string newPrefix = "AssetHelper-RepackedScenes/Assets/";
            if (str.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                return newPrefix + str.Substring(oldPrefix.Length);
            return str;
        }

        private static readonly List<WeakReference> _allKnownManagedAssets = new List<WeakReference>();
        private static readonly List<WeakReference> _instantiatedManagedAssetObjects = new List<WeakReference>();

        public static void EnsureManagedAssetBoundBeforeLoad(object managedAsset)
        {
            if (managedAsset == null) return;
            try
            {
                FixManagedSceneAssetInPlace(managedAsset);
                TryBindManagedAssetFromTemplateCache(managedAsset);
            }
            catch (System.Exception __apiEx_193) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7285", __apiEx_193); }
        }

        public static void InvokeManagedAssetLoadVoid(object managedAsset)
        {
            if (managedAsset == null) return;
            try
            {
                EnsureManagedAssetBoundBeforeLoad(managedAsset);
                var maType = managedAsset.GetType();
                var hasFallbackField = maType.GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (hasFallbackField != null && (bool)hasFallbackField.GetValue(managedAsset))
                {
                    return;
                }
                var loadMi = maType.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, null, Type.EmptyTypes, null);
                if (loadMi != null)
                {
                    loadMi.Invoke(managedAsset, null);
                }
            }
            catch (System.Exception exLoad)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "InvokeManagedAssetLoadVoid", exLoad);
            }
        }

        public static AsyncOperationHandle<T> InvokeManagedAssetLoad<T>(object managedAsset) where T : UnityEngine.Object
        {
            if (managedAsset == null) return default;
            try
            {
                EnsureManagedAssetBoundBeforeLoad(managedAsset);
                var maType = managedAsset.GetType();
                var hasFallbackField = maType.GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (hasFallbackField != null && (bool)hasFallbackField.GetValue(managedAsset))
                {
                    return GetHandle<T>(managedAsset);
                }
                var loadMi = maType.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, null, Type.EmptyTypes, null);
                if (loadMi != null)
                {
                    object ret = loadMi.Invoke(managedAsset, null);
                    if (ret is AsyncOperationHandle<T> direct) return direct;
                }
                return GetHandle<T>(managedAsset);
            }
            catch (System.Exception exLoad)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "InvokeManagedAssetLoad", exLoad);
                return default;
            }
        }

        public static bool GetManagedAssetIsLoaded(object managedAsset)
        {
            if (managedAsset == null) return false;
            try
            {
                var maType = managedAsset.GetType();
                var hasFallbackField = maType.GetField("HasFallbackResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (hasFallbackField != null && (bool)hasFallbackField.GetValue(managedAsset))
                    return true;
                var isLoadedProp = maType.GetProperty("IsLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (isLoadedProp != null)
                    return (bool)isLoadedProp.GetValue(managedAsset, null);
                var handleProp = maType.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (handleProp != null)
                {
                    object h = handleProp.GetValue(managedAsset, null);
                    if (h != null)
                    {
                        var isDoneProp = h.GetType().GetProperty("IsDone", BindingFlags.Instance | BindingFlags.Public);
                        if (isDoneProp != null) return (bool)isDoneProp.GetValue(h, null);
                    }
                }
            }
            catch (System.Exception exLoaded)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "GetManagedAssetIsLoaded", exLoaded);
            }
            return false;
        }

        private static readonly Dictionary<string, GameObject> _harvestedPoolPrefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        private static void RestoreTemplateComponentsEnabled(GameObject root)
        {
            if (root == null) return;
            try
            {
                var fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
                if (fsms != null)
                {
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        if (fsms[i] != null && !fsms[i].enabled)
                            fsms[i].enabled = true;
                    }
                }
                var triggers = root.GetComponentsInChildren<TriggerEnterEvent>(true);
                if (triggers != null)
                {
                    for (int i = 0; i < triggers.Length; i++)
                    {
                        if (triggers[i] != null && !triggers[i].enabled)
                            triggers[i].enabled = true;
                    }
                }
            }
            catch (System.Exception __apiEx_rest) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "RestoreTemplateComponentsEnabled", __apiEx_rest); }
        }

        private static void HarvestPersonalObjectPoolsFromHierarchy(GameObject root)
        {
            if (root == null) return;
            try
            {
                var pools = root.GetComponentsInChildren<PersonalObjectPool>(true);
                if (pools != null)
                {
                    for (int i = 0; i < pools.Length; i++)
                    {
                        var p = pools[i];
                        if (p == null || p.startupPool == null) continue;
                        for (int j = 0; j < p.startupPool.Count; j++)
                        {
                            var sp = p.startupPool[j];
                            if (sp.prefab != null && !string.IsNullOrEmpty(sp.prefab.name))
                            {
                                _harvestedPoolPrefabs[sp.prefab.name] = sp.prefab;
                                PinAllAssetsOnHierarchy(sp.prefab);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_harv) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "HarvestPersonalObjectPoolsFromHierarchy", __apiEx_harv); }
        }

        public static void OnManagedAssetInstantiated(object instance)
        {
            if (!(instance is GameObject go) || go == null) return;
            try
            {
                lock (_instantiatedManagedAssetObjects)
                {
                    _instantiatedManagedAssetObjects.Add(new WeakReference(go));
                }
                if (!go.activeSelf) go.SetActive(true);
                RestoreTemplateComponentsEnabled(go);
                // If instantiated during an additive scene load while activeScene is still the previous room, move to the newest loaded gameplay scene
                int scCount = SceneManager.sceneCount;
                if (scCount > 1)
                {
                    Scene newest = SceneManager.GetSceneAt(scCount - 1);
                    if (newest.IsValid() && newest.isLoaded &&
                        !string.IsNullOrEmpty(newest.name) &&
                        go.scene != newest &&
                        !IsKnownExtractionOnlyScene(newest.name))
                    {
                        try { SceneManager.MoveGameObjectToScene(go, newest); } catch (System.Exception __apiEx_194) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7307", __apiEx_194); }
                    }
                }
            }
            catch (System.Exception __apiEx_195) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7311", __apiEx_195); }
        }

        private static bool IsKnownExtractionOnlyScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            string gmScene = GameManager._instance != null ? (GameManager._instance.sceneName ?? "") : "";
            if (string.Equals(sceneName, gmScene, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(sceneName, "Memory_Coral_Tower", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Hang_02", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Hang_03", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Coral_26", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Hang_17b", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Cradle_03", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Song_24", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "Ant_04", StringComparison.OrdinalIgnoreCase);
        }

        private static void CleanupStrayBoneThumperClones()
        {
            try
            {
                var allHms = Resources.FindObjectsOfTypeAll<HealthManager>();
                if (allHms == null) return;
                for (int i = 0; i < allHms.Length; i++)
                {
                    var hm = allHms[i];
                    if (hm != null && hm.gameObject != null &&
                        hm.gameObject.name.StartsWith("Bone Thumper(Clone)", StringComparison.OrdinalIgnoreCase))
                    {
                        SafeDestroyImmediateUnityObject(hm.gameObject);
                    }
                }
            }
            catch (System.Exception __apiEx_196) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7344", __apiEx_196); }
        }

        public static void CleanupLingeringExtractionScenesAndModObjects(string currentSceneName)
        {
            if (string.IsNullOrEmpty(currentSceneName)) return;
            try
            {
                HarmonyBridge.PruneStaleTransitionPoints();

                // 1. Clean up any lingering instantiated ManagedAsset GameObjects whose scene is no longer loaded
                lock (_instantiatedManagedAssetObjects)
                {
                    for (int i = _instantiatedManagedAssetObjects.Count - 1; i >= 0; i--)
                    {
                        var wr = _instantiatedManagedAssetObjects[i];
                        if (wr == null || !(wr.Target is GameObject go) || go == null)
                        {
                            _instantiatedManagedAssetObjects.RemoveAt(i);
                            continue;
                        }
                        if (go.scene.IsValid() && !go.scene.isLoaded &&
                            go.transform.parent == null &&
                            !go.name.StartsWith("SilksongMobileAPI", StringComparison.OrdinalIgnoreCase))
                        {
                            SafeDestroyImmediateUnityObject(go);
                            _instantiatedManagedAssetObjects.RemoveAt(i);
                        }
                    }
                }

                // 1b. Unload any lingering background extraction scenes (e.g. Hang_17b, Memory_Coral_Tower, Hang_02, Hang_03, Coral_26)
                if (!IsExtractingPharloomsGloryScene && !IsAnyRepackerBusy())
                {
                    var gm = GameManager._instance;
                    string gmScene = gm != null ? (gm.sceneName ?? "") : "";
                    string gmNext = gm != null ? (gm.nextSceneName ?? "") : "";
                    for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
                    {
                        var sc = SceneManager.GetSceneAt(s);
                        if (!sc.IsValid() || !sc.isLoaded) continue;
                        string sn = sc.name ?? "";
                        if (string.Equals(sn, currentSceneName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, gmScene, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, gmNext, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(sn, "Hang_17b", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Memory_Coral_Tower", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Hang_02", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Hang_03", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Coral_26", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Cradle_03", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Song_24", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(sn, "Ant_04", StringComparison.OrdinalIgnoreCase))
                        {
                            var targetActive = SceneManager.GetSceneByName(currentSceneName);
                            if (targetActive.IsValid() && targetActive.isLoaded && SceneManager.GetActiveScene() == sc)
                            {
                                try { SceneManager.SetActiveScene(targetActive); } catch (System.Exception __apiEx_197) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7402", __apiEx_197); }
                            }
                            try
                            {
                                SceneManager.UnloadSceneAsync(sc);
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AssetHelperBridge] Unloaded lingering extraction scene '{sn}' while in '{currentSceneName}'.");
                            }
                            catch (System.Exception __apiEx_198) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7409", __apiEx_198); }
                        }
                    }
                }

                // 2. If not in Arborium_11, ensure PaleAutomaton bossScene flag is false and arena objects don't linger
                if (!string.Equals(currentSceneName, "Arborium_11", StringComparison.OrdinalIgnoreCase) && HasPaleAutomaton() && _paleAutomatonAssembly != null)
                {
                    var arbSc = SceneManager.GetSceneByName("Arborium_11");
                    if (!arbSc.IsValid() || !arbSc.isLoaded)
                    {
                        const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                        var paPluginType = _paleAutomatonAssembly.GetType("PaleAutomaton.PaleAutomatonPlugin", false);
                        paPluginType?.GetField("bossScene", SFlags)?.SetValue(null, false);
                    }
                }
            }
            catch (System.Exception __apiEx_199) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7426", __apiEx_199); }
        }

        public static void SafeDestroyImmediateUnityObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is PlayMakerFSM fsm)
                {
                    try
                    {
                        if (fsm.Fsm != null) fsm.Fsm.ManualUpdate = true;
                        fsm.enabled = false;
                    }
                    catch (System.Exception __apiEx_200) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7441", __apiEx_200); }
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDisableFsmAfterLateUpdate(fsm));
                    return;
                }
                if (obj is GameObject go)
                {
                    try
                    {
                        var tps = go.GetComponentsInChildren<TransitionPoint>(true);
                        if (tps != null && tps.Length > 0 && TransitionPoint.TransitionPoints != null)
                        {
                            for (int i = 0; i < tps.Length; i++)
                            {
                                if (tps[i] != null)
                                    TransitionPoint.TransitionPoints.Remove(tps[i]);
                            }
                        }
                    }
                    catch (System.Exception __apiEx_201) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7460", __apiEx_201); }
                    try
                    {
                        var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                        if (fsms != null)
                        {
                            for (int i = 0; i < fsms.Length; i++)
                            {
                                var f = fsms[i];
                                if (f != null)
                                {
                                    if (f.Fsm != null) f.Fsm.ManualUpdate = true;
                                    f.enabled = false;
                                }
                            }
                        }
                    }
                    catch (System.Exception __apiEx_202) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7477", __apiEx_202); }
                    try
                    {
                        // Detach from parent immediately so while (t.childCount > 0) DestroyImmediate(t.GetChild(0).gameObject) loops in Bonemaster terminate cleanly!
                        if (go.transform.parent != null)
                            go.transform.SetParent(null, false);
                    }
                    catch (System.Exception __apiEx_203) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7484", __apiEx_203); }
                    try
                    {
                        if (go.activeSelf) go.SetActive(false);
                    }
                    catch (System.Exception __apiEx_204) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7489", __apiEx_204); }
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDestroyAfterLateUpdate(go));
                    return;
                }
                if (obj is Behaviour bh)
                {
                    try { bh.enabled = false; } catch (System.Exception __apiEx_205) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7496", __apiEx_205); }
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDeferredDestroyAfterLateUpdate(bh));
                    return;
                }
                UnityEngine.Object.Destroy(obj);
            }
            catch (System.Exception __apiEx_206) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7503", __apiEx_206); }
        }

        private static readonly Dictionary<int, Vector2[]> _bonemasterSpriteOutlineCache = new Dictionary<int, Vector2[]>();
        private static readonly HashSet<int> _bonemasterSpritePropColliderSet = new HashSet<int>();
        private static FieldInfo _bmSpritePropDrawingField;
        private static FieldInfo _bmSpritePropBoxField;
        private static FieldInfo _bmSpritePropTinkBoxField;
        private static PropertyInfo _bmSpritePropTurnedProp;

        private sealed class BmSpritePropTurnState
        {
            public readonly Queue<Tuple<float, float, Func<float, float>>> Turns = new Queue<Tuple<float, float, Func<float, float>>>();
            public bool HasTurning;
            public float From;
            public float To;
            public float Seconds;
            public Func<float, float> Ease;
            public float Start;
        }
        private static readonly Dictionary<int, BmSpritePropTurnState> _bmSpritePropTurnStates = new Dictionary<int, BmSpritePropTurnState>();

        public static void BonemasterSpritePropTurn(object spriteProp, float to, float seconds, Func<float, float> ease)
        {
            if (spriteProp == null) return;
            try
            {
                int id = (spriteProp as UnityEngine.Object)?.GetInstanceID() ?? spriteProp.GetHashCode();
                if (!_bmSpritePropTurnStates.TryGetValue(id, out var st))
                {
                    st = new BmSpritePropTurnState();
                    _bmSpritePropTurnStates[id] = st;
                }
                st.Turns.Enqueue(Tuple.Create(to, Mathf.Max(0.001f, seconds), ease));
            }
            catch { }
        }

        public static void BonemasterSpritePropStepTurn(object spriteProp)
        {
            if (spriteProp == null || _bmSpritePropTurnStates.Count == 0) return;
            try
            {
                int id = (spriteProp as UnityEngine.Object)?.GetInstanceID() ?? spriteProp.GetHashCode();
                if (!_bmSpritePropTurnStates.TryGetValue(id, out var st)) return;

                if (_bmSpritePropTurnedProp == null)
                {
                    _bmSpritePropTurnedProp = spriteProp.GetType().GetProperty("Turned", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (!st.HasTurning && st.Turns.Count > 0)
                {
                    var next = st.Turns.Dequeue();
                    float curTurned = 0f;
                    if (_bmSpritePropTurnedProp != null)
                        curTurned = (float)_bmSpritePropTurnedProp.GetValue(spriteProp, null);
                    st.From = curTurned;
                    st.To = next.Item1;
                    st.Seconds = next.Item2;
                    st.Ease = next.Item3;
                    st.Start = Time.time;
                    st.HasTurning = true;
                }

                if (st.HasTurning)
                {
                    float u = Mathf.Clamp01((Time.time - st.Start) / st.Seconds);
                    float eased = st.Ease != null ? st.Ease(u) : u;
                    float val = Mathf.LerpUnclamped(st.From, st.To, eased);
                    _bmSpritePropTurnedProp?.SetValue(spriteProp, val, null);
                    if (u >= 1f)
                    {
                        st.HasTurning = false;
                        if (st.Turns.Count == 0)
                            _bmSpritePropTurnStates.Remove(id);
                    }
                }
            }
            catch { }
        }

        public static Vector2[] BonemasterSpritePropOutline(Sprite s, float inset)
        {
            if (s == null) return Array.Empty<Vector2>();
            int id = s.GetInstanceID();
            if (_bonemasterSpriteOutlineCache.TryGetValue(id, out var cached) && cached != null)
                return cached;
            Bounds b = s.bounds;
            Vector2 min = (Vector2)b.min + new Vector2(inset, inset);
            Vector2 max = (Vector2)b.max - new Vector2(inset, inset);
            if (min.x > max.x) { float cx = b.center.x; min.x = cx - 0.1f; max.x = cx + 0.1f; }
            if (min.y > max.y) { float cy = b.center.y; min.y = cy - 0.1f; max.y = cy + 0.1f; }
            var pts = new Vector2[]
            {
                new Vector2(min.x, min.y),
                new Vector2(max.x, min.y),
                new Vector2(max.x, max.y),
                new Vector2(min.x, max.y)
            };
            _bonemasterSpriteOutlineCache[id] = pts;
            return pts;
        }

        public static void BonemasterSpritePropShow(object spriteProp, Sprite s)
        {
            if (spriteProp == null || s == null) return;
            try
            {
                var spType = spriteProp.GetType();
                const BindingFlags IFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (_bmSpritePropDrawingField == null)
                {
                    _bmSpritePropDrawingField = spType.GetField("drawing", IFlags);
                    _bmSpritePropBoxField = spType.GetField("box", IFlags);
                    _bmSpritePropTinkBoxField = spType.GetField("tinkBox", IFlags);
                }
                if (_bmSpritePropDrawingField?.GetValue(spriteProp) is SpriteRenderer sr && sr != null)
                {
                    sr.sprite = s;
                }
                int propId = (spriteProp as UnityEngine.Object)?.GetInstanceID() ?? spriteProp.GetHashCode();
                bool isAnticFrame = !string.IsNullOrEmpty(s.name) && s.name.IndexOf("antic", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isAnticFrame || _bonemasterSpritePropColliderSet.Add(propId))
                {
                    if (_bmSpritePropBoxField?.GetValue(spriteProp) is PolygonCollider2D box && box != null)
                    {
                        box.SetPath(0, BonemasterSpritePropOutline(s, 0.25f));
                    }
                    if (_bmSpritePropTinkBoxField?.GetValue(spriteProp) is PolygonCollider2D tinkBox && tinkBox != null)
                    {
                        tinkBox.SetPath(0, BonemasterSpritePropOutline(s, 0f));
                    }
                }
            }
            catch (System.Exception __apiEx_207) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7564", __apiEx_207); }
        }

        // =========================================================================
        // Bonemaster Parry Brown Screen Effect (LensCloud.Throw / ClearAll Bridge)
        // =========================================================================
        private static GameObject _activeLensCloudGo;
        private static Sprite _lensCloudSolidSprite;
        private static Sprite _lensCloudVeilSprite;
        private static Sprite _lensCloudGrimeSprite;
        private static Sprite _lensCloudPuffClusterSprite;

        private static void EnsureLensCloudSprites()
        {
            if (_lensCloudSolidSprite == null)
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var cols = new Color32[16];
                for (int i = 0; i < 16; i++) cols[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(cols);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                _lensCloudSolidSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                _lensCloudSolidSprite.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_lensCloudVeilSprite == null)
            {
                int sz = 128;
                var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                var cols = new Color32[sz * sz];
                float half = (sz - 1) * 0.5f;
                for (int y = 0; y < sz; y++)
                {
                    for (int x = 0; x < sz; x++)
                    {
                        float dx = (x - half) / half;
                        float dy = (y - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float t = Mathf.Clamp01(1f - r);
                        float a = t * t * (3f - 2f * t);
                        cols[y * sz + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                    }
                }
                tex.SetPixels32(cols);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                _lensCloudVeilSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), (float)sz);
                _lensCloudVeilSprite.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_lensCloudGrimeSprite == null)
            {
                int w = 256, h = 144;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                var cols = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    float ny = (y / (float)(h - 1)) * 2f - 1f;
                    for (int x = 0; x < w; x++)
                    {
                        float nx = (x / (float)(w - 1)) * 2f - 1f;
                        float edgeDist = Mathf.Sqrt(nx * nx * 0.85f + ny * ny * 1.15f);
                        float vig = Mathf.Clamp01((edgeDist - 0.25f) / 0.85f);
                        float n1 = Mathf.PerlinNoise(x * 0.045f + 3.1f, y * 0.045f + 7.3f);
                        float n2 = Mathf.PerlinNoise(x * 0.11f + 19.7f, y * 0.11f + 11.2f);
                        float blotch = Mathf.Clamp01((n1 * 0.65f + n2 * 0.35f - 0.32f) * 2.1f);
                        float a = Mathf.Clamp01(vig * 0.78f + blotch * 0.52f);
                        byte shade = (byte)Mathf.Clamp(Mathf.RoundToInt(175f + n2 * 70f), 140, 255);
                        cols[y * w + x] = new Color32(shade, shade, shade, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                    }
                }
                tex.SetPixels32(cols);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                _lensCloudGrimeSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
                _lensCloudGrimeSprite.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_lensCloudPuffClusterSprite == null)
            {
                int sz = 256;
                var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                var cols = new Color32[sz * sz];
                float half = (sz - 1) * 0.5f;
                for (int y = 0; y < sz; y++)
                {
                    for (int x = 0; x < sz; x++)
                    {
                        float dx = (x - half) / half;
                        float dy = (y - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        if (r >= 1f) continue;
                        float env = Mathf.Clamp01(1f - r * r);
                        float n1 = Mathf.PerlinNoise(x * 0.035f + 5.3f, y * 0.035f + 2.9f);
                        float n2 = Mathf.PerlinNoise(x * 0.085f + 13.7f, y * 0.085f + 17.1f);
                        float puff = Mathf.Clamp01((n1 * 0.6f + n2 * 0.4f) * 1.45f) * env;
                        byte lum = (byte)Mathf.Clamp(Mathf.RoundToInt(185f + n1 * 65f), 160, 255);
                        cols[y * sz + x] = new Color32(lum, lum, lum, (byte)Mathf.Clamp(Mathf.RoundToInt(puff * 255f), 0, 255));
                    }
                }
                tex.SetPixels32(cols);
                tex.Apply(false, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                _lensCloudPuffClusterSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), (float)sz);
                _lensCloudPuffClusterSprite.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        public static void BonemasterLensCloudClearAll()
        {
            if (_activeLensCloudGo != null)
            {
                try { UnityEngine.Object.Destroy(_activeLensCloudGo); } catch { }
                _activeLensCloudGo = null;
            }
        }

        public static void BonemasterLensCloudThrow(object specObj, GameObject owner)
        {
            if (specObj == null) return;
            try
            {
                Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : Camera.main;
                if (cam == null) return;

                BonemasterLensCloudClearAll();
                EnsureLensCloudSprites();

                Type st = specObj.GetType();
                const BindingFlags FFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                string name = st.GetField("Name", FFlags)?.GetValue(specObj) as string ?? "Lens Cloud";
                Color tint = st.GetField("Tint", FFlags)?.GetValue(specObj) is Color c1 ? c1 : new Color(0.16f, 0.1f, 0.06f, 1f);
                Transform centre = st.GetField("Centre", FFlags)?.GetValue(specObj) as Transform;
                float centreFollow = st.GetField("CentreFollow", FFlags)?.GetValue(specObj) is float cf ? cf : 12f;
                string grimeName = st.GetField("Grime", FFlags)?.GetValue(specObj) as string;
                Color grimeTint = st.GetField("GrimeTint", FFlags)?.GetValue(specObj) is Color c2 ? c2 : new Color(0.42f, 0.27f, 0.13f, 1f);
                float grimeOpacity = st.GetField("GrimeOpacity", FFlags)?.GetValue(specObj) is float go ? go : 0.85f;
                float opacity = st.GetField("Opacity", FFlags)?.GetValue(specObj) is float op ? op : 0.9f;
                float veilOpacity = st.GetField("VeilOpacity", FFlags)?.GetValue(specObj) is float vo ? vo : 0.85f;
                float veilSize = st.GetField("VeilSize", FFlags)?.GetValue(specObj) is float vs ? vs : 1.4f;
                float inDur = st.GetField("In", FFlags)?.GetValue(specObj) is float id ? id : 0.15f;
                float holdDur = st.GetField("Hold", FFlags)?.GetValue(specObj) is float hd ? hd : 0.45f;
                float outDur = st.GetField("Out", FFlags)?.GetValue(specObj) is float od ? od : 1.4f;
                float popFrom = st.GetField("PopFrom", FFlags)?.GetValue(specObj) is float pf ? pf : 0.6f;
                float swirl = st.GetField("Swirl", FFlags)?.GetValue(specObj) is float sw ? sw : 20f;
                float spreadOut = st.GetField("SpreadOut", FFlags)?.GetValue(specObj) is float so ? so : 0.35f;
                float sink = st.GetField("Sink", FFlags)?.GetValue(specObj) is float sk ? sk : 0.12f;
                float blinkDur = st.GetField("Blink", FFlags)?.GetValue(specObj) is float bd ? bd : 0.1f;
                float blinkPeak = st.GetField("BlinkPeak", FFlags)?.GetValue(specObj) is float bp ? bp : 0.85f;
                float depth = st.GetField("Depth", FFlags)?.GetValue(specObj) is float dp ? dp : 23.4f;
                string sortingLayer = st.GetField("SortingLayer", FFlags)?.GetValue(specObj) as string ?? "Over";
                int sortingOrder = st.GetField("SortingOrder", FFlags)?.GetValue(specObj) is int sord ? sord : -1;

                GameObject root = new GameObject(name);
                root.transform.SetParent(cam.transform, false);
                Transform screenFlash = cam.transform.Find("Screen Flash");
                int layer = screenFlash != null ? screenFlash.gameObject.layer : 0;
                root.layer = layer;

                var beh = root.AddComponent<BonemasterLensCloudBehaviour>();
                beh.Init(
                    cam, owner, centre, centreFollow,
                    tint, grimeName, grimeTint, grimeOpacity,
                    opacity, veilOpacity, veilSize,
                    inDur, holdDur, outDur, popFrom,
                    swirl, spreadOut, sink,
                    blinkDur, blinkPeak, depth,
                    sortingLayer, sortingOrder, layer);

                _activeLensCloudGo = root;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AssetHelperBridge] BonemasterLensCloudThrow error: {ex.Message}");
            }
        }

        public sealed class BonemasterLensCloudBehaviour : MonoBehaviour
        {
            private Camera _cam;
            private GameObject _owner;
            private Transform _centre;
            private float _centreFollow;
            private Color _tint;
            private Color _grimeTint;
            private float _grimeOpacity;
            private float _opacity;
            private float _veilOpacity;
            private float _veilSize;
            private float _inDur;
            private float _holdDur;
            private float _outDur;
            private float _popFrom;
            private float _swirl;
            private float _spreadOut;
            private float _sink;
            private float _blinkDur;
            private float _blinkPeak;
            private float _depth;
            private float _born;
            private float _height;
            private float _width;
            private float _veilSpan;

            private SpriteRenderer _blinkSr;
            private SpriteRenderer _grimeSr;
            private SpriteRenderer _veilSr;
            private SpriteRenderer _cloudSrA;
            private SpriteRenderer _cloudSrB;

            public void Init(
                Camera cam, GameObject owner, Transform centre, float centreFollow,
                Color tint, string grimeName, Color grimeTint, float grimeOpacity,
                float opacity, float veilOpacity, float veilSize,
                float inDur, float holdDur, float outDur, float popFrom,
                float swirl, float spreadOut, float sink,
                float blinkDur, float blinkPeak, float depth,
                string sortingLayer, int sortingOrder, int layer)
            {
                _cam = cam;
                _owner = owner;
                _centre = centre;
                _centreFollow = centreFollow;
                _tint = tint;
                _grimeTint = grimeTint;
                _grimeOpacity = grimeOpacity;
                _opacity = opacity;
                _veilOpacity = veilOpacity;
                _veilSize = veilSize;
                _inDur = Mathf.Max(0.001f, inDur);
                _holdDur = Mathf.Max(0f, holdDur);
                _outDur = Mathf.Max(0.001f, outDur);
                _popFrom = popFrom;
                _swirl = swirl;
                _spreadOut = spreadOut;
                _sink = sink;
                _blinkDur = blinkDur;
                _blinkPeak = blinkPeak;
                _depth = depth;
                _born = Time.time;

                _height = 2f * _depth * Mathf.Tan(_cam.fieldOfView * 0.5f * (Mathf.PI / 180f));
                _width = _height * _cam.aspect;
                _veilSpan = Mathf.Max(_width, _height) * Mathf.Max(0.8f, _veilSize);

                transform.localPosition = ComputeCentreLocalPos();

                // 1. Grime full-screen vignette layer (parented to camera so it covers the whole screen)
                if (!string.IsNullOrEmpty(grimeName) && _grimeOpacity > 0f)
                {
                    var grimeGo = new GameObject("Lens Cloud Grime");
                    grimeGo.layer = layer;
                    grimeGo.transform.SetParent(_cam.transform, false);
                    grimeGo.transform.localPosition = new Vector3(0f, 0f, _depth + 0.02f);
                    _grimeSr = grimeGo.AddComponent<SpriteRenderer>();
                    Sprite grimeSp = _lensCloudGrimeSprite;
                    if (!string.Equals(grimeName, "lens speckle", StringComparison.OrdinalIgnoreCase))
                    {
                        var mat = BonemasterFindMaterial(grimeName);
                        if (mat != null && mat.mainTexture is Texture2D mTex)
                        {
                            try
                            {
                                grimeSp = Sprite.Create(mTex, new Rect(0, 0, mTex.width, mTex.height), new Vector2(0.5f, 0.5f), 100f);
                            }
                            catch { }
                        }
                    }
                    _grimeSr.sprite = grimeSp;
                    _grimeSr.sortingLayerName = sortingLayer;
                    _grimeSr.sortingOrder = sortingOrder - 2;
                    if (grimeSp != null && grimeSp.bounds.size.x > 0.001f && grimeSp.bounds.size.y > 0.001f)
                    {
                        grimeGo.transform.localScale = new Vector3(
                            (_width * 1.18f) / grimeSp.bounds.size.x,
                            (_height * 1.18f) / grimeSp.bounds.size.y,
                            1f);
                    }
                }

                // 2. Blink full-screen flash layer
                if (_blinkDur > 0f && _blinkPeak > 0f)
                {
                    var blinkGo = new GameObject("Lens Cloud Blink");
                    blinkGo.layer = layer;
                    blinkGo.transform.SetParent(_cam.transform, false);
                    blinkGo.transform.localPosition = new Vector3(0f, 0f, _depth - 0.02f);
                    _blinkSr = blinkGo.AddComponent<SpriteRenderer>();
                    _blinkSr.sprite = _lensCloudSolidSprite;
                    _blinkSr.sortingLayerName = sortingLayer;
                    _blinkSr.sortingOrder = sortingOrder + 1;
                    blinkGo.transform.localScale = new Vector3(_width * 1.25f, _height * 1.25f, 1f);
                }

                // 3. Soft radial veil layer centered on Hornet
                if (_veilOpacity > 0f)
                {
                    var veilGo = new GameObject("Lens Cloud Veil");
                    veilGo.layer = layer;
                    veilGo.transform.SetParent(transform, false);
                    veilGo.transform.localPosition = new Vector3(0f, 0f, 0.01f);
                    _veilSr = veilGo.AddComponent<SpriteRenderer>();
                    _veilSr.sprite = _lensCloudVeilSprite;
                    _veilSr.sortingLayerName = sortingLayer;
                    _veilSr.sortingOrder = sortingOrder - 1;
                }

                // 4. Two counter-rotating dirt puff cloud layers
                var cloudGoA = new GameObject("Lens Cloud Puffs A");
                cloudGoA.layer = layer;
                cloudGoA.transform.SetParent(transform, false);
                cloudGoA.transform.localPosition = Vector3.zero;
                _cloudSrA = cloudGoA.AddComponent<SpriteRenderer>();
                _cloudSrA.sprite = _lensCloudPuffClusterSprite;
                _cloudSrA.sortingLayerName = sortingLayer;
                _cloudSrA.sortingOrder = sortingOrder;

                var cloudGoB = new GameObject("Lens Cloud Puffs B");
                cloudGoB.layer = layer;
                cloudGoB.transform.SetParent(transform, false);
                cloudGoB.transform.localPosition = Vector3.zero;
                _cloudSrB = cloudGoB.AddComponent<SpriteRenderer>();
                _cloudSrB.sprite = _lensCloudPuffClusterSprite;
                _cloudSrB.sortingLayerName = sortingLayer;
                _cloudSrB.sortingOrder = sortingOrder;

                ApplyFrame(0f);
            }

            private Vector3 ComputeCentreLocalPos()
            {
                if (_centre == null || _cam == null)
                    return new Vector3(0f, 0f, _depth);
                Vector3 vp = _cam.WorldToViewportPoint(_centre.position);
                return new Vector3((Mathf.Clamp01(vp.x) - 0.5f) * _width, (Mathf.Clamp01(vp.y) - 0.5f) * _height, _depth);
            }

            private void LateUpdate()
            {
                if (_cam == null || (_owner != null && !_owner.activeInHierarchy))
                {
                    DestroySelfAndChildren();
                    return;
                }

                float age = Time.time - _born;
                float totalBeforeOut = _inDur + _holdDur;
                float totalDur = totalBeforeOut + _outDur;
                if (age >= totalDur)
                {
                    DestroySelfAndChildren();
                    return;
                }

                ApplyFrame(age);
            }

            private void ApplyFrame(float age)
            {
                // Blink flash
                if (_blinkSr != null)
                {
                    if (age >= _blinkDur)
                    {
                        Destroy(_blinkSr.gameObject);
                        _blinkSr = null;
                    }
                    else
                    {
                        float bt = age / _blinkDur;
                        float ba = (bt < 0.25f) ? (bt / 0.25f) : (1f - (bt - 0.25f) / 0.75f);
                        _blinkSr.color = new Color(_tint.r, _tint.g, _tint.b, Mathf.Clamp01(ba) * _blinkPeak);
                    }
                }

                float totalBeforeOut = _inDur + _holdDur;
                float outT = 0f;
                float fade = 1f;
                float grow = 1f;
                if (age < _inDur)
                {
                    float s = Mathf.Sin((age / _inDur) * (Mathf.PI * 0.5f));
                    fade = s;
                    grow = Mathf.Lerp(_popFrom, 1f, s);
                }
                else if (age >= totalBeforeOut)
                {
                    outT = (age - totalBeforeOut) / _outDur;
                    fade = 1f - outT * outT * (3f - 2f * outT);
                    grow = 1f + 0.25f * outT;
                }

                if (_centre != null)
                {
                    transform.localPosition = Vector3.Lerp(
                        transform.localPosition,
                        ComputeCentreLocalPos(),
                        1f - Mathf.Exp((0f - _centreFollow) * Time.deltaTime));
                }

                if (_grimeSr != null)
                {
                    _grimeSr.color = new Color(_grimeTint.r, _grimeTint.g, _grimeTint.b, _grimeOpacity * fade);
                }

                float drop = _sink * _height * outT * outT;
                if (_veilSr != null)
                {
                    float vSpan = _veilSpan * grow;
                    _veilSr.transform.localScale = new Vector3(vSpan, vSpan, 1f);
                    _veilSr.transform.localPosition = new Vector3(0f, -drop, 0.01f);
                    _veilSr.color = new Color(_tint.r, _tint.g, _tint.b, _veilOpacity * fade);
                }

                float puffScale = _veilSpan * 0.92f * grow * (1f + _spreadOut * outT);
                if (_cloudSrA != null)
                {
                    _cloudSrA.transform.localScale = new Vector3(puffScale, puffScale, 1f);
                    _cloudSrA.transform.localPosition = new Vector3(0f, -drop, 0f);
                    _cloudSrA.transform.localRotation = Quaternion.Euler(0f, 0f, _swirl * age);
                    _cloudSrA.color = new Color(_tint.r, _tint.g, _tint.b, _opacity * fade);
                }
                if (_cloudSrB != null)
                {
                    _cloudSrB.transform.localScale = new Vector3(puffScale * 0.84f, puffScale * 0.84f, 1f);
                    _cloudSrB.transform.localPosition = new Vector3(0f, -drop * 1.15f, -0.005f);
                    _cloudSrB.transform.localRotation = Quaternion.Euler(0f, 0f, -_swirl * 0.85f * age + 47f);
                    Color lighter = new Color(
                        Mathf.Clamp01(_tint.r * 1.25f + 0.04f),
                        Mathf.Clamp01(_tint.g * 1.22f + 0.03f),
                        Mathf.Clamp01(_tint.b * 1.18f + 0.02f),
                        _opacity * 0.82f * fade);
                    _cloudSrB.color = lighter;
                }
            }

            private void DestroySelfAndChildren()
            {
                if (_grimeSr != null) { Destroy(_grimeSr.gameObject); _grimeSr = null; }
                if (_blinkSr != null) { Destroy(_blinkSr.gameObject); _blinkSr = null; }
                if (_activeLensCloudGo == gameObject) _activeLensCloudGo = null;
                Destroy(gameObject);
            }

            private void OnDestroy()
            {
                if (_grimeSr != null) { Destroy(_grimeSr.gameObject); _grimeSr = null; }
                if (_blinkSr != null) { Destroy(_blinkSr.gameObject); _blinkSr = null; }
                if (_activeLensCloudGo == gameObject) _activeLensCloudGo = null;
            }
        }

        public static void FixManagedSceneAssetInPlace(object obj)
        {
            if (obj == null) return;
            try
            {
                const string oldPrefix = "AssetHelper-SceneAssets/Assets/";
                const string newPrefix = "AssetHelper-RepackedScenes/Assets/";
                Type t = obj.GetType();
                FieldInfo addrField;
                lock (_managedAssetAddressFieldCache)
                {
                    if (!_managedAssetAddressFieldCache.TryGetValue(t, out addrField))
                    {
                        const BindingFlags FFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        Type cur = t;
                        while (cur != null && cur != typeof(object))
                        {
                            addrField = cur.GetField("<Key>k__BackingField", FFlags)
                                     ?? cur.GetField("<Address>k__BackingField", FFlags)
                                     ?? cur.GetField("_address", FFlags)
                                     ?? cur.GetField("address", FFlags)
                                     ?? cur.GetField("key", FFlags);
                            if (addrField != null && addrField.FieldType == typeof(string)) break;
                            cur = cur.BaseType;
                        }
                        _managedAssetAddressFieldCache[t] = addrField;
                    }
                }
                if (addrField != null)
                {
                    string curVal = addrField.GetValue(obj) as string;
                    if (!string.IsNullOrEmpty(curVal) && curVal.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        addrField.SetValue(obj, newPrefix + curVal.Substring(oldPrefix.Length));
                    }
                }
                lock (_allKnownManagedAssets)
                {
                    _allKnownManagedAssets.Add(new WeakReference(obj));
                }
                TryBindManagedAssetFromTemplateCache(obj);
            }
            catch (System.Exception __apiEx_208) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7609", __apiEx_208); }
        }

        public static object FixManagedSceneAssetOrKey(object obj)
        {
            if (obj == null) return null;
            try
            {
                const string oldPrefix = "AssetHelper-SceneAssets/Assets/";
                const string newPrefix = "AssetHelper-RepackedScenes/Assets/";
                if (obj is string str)
                {
                    if (str.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                        return newPrefix + str.Substring(oldPrefix.Length);
                    return str;
                }

                FixManagedSceneAssetInPlace(obj);
            }
            catch (System.Exception __apiEx_209) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7628", __apiEx_209); }
            return obj;
        }

        private static void EnsureNativeShadersIndexed()
        {
            if (_nativeShadersIndexed && _nativeShaderCache.Count > 4) return;
            _nativeShadersIndexed = true;
            try
            {
                var allShaders = Resources.FindObjectsOfTypeAll<Shader>();
                if (allShaders != null)
                {
                    for (int i = 0; i < allShaders.Length; i++)
                    {
                        var sh = allShaders[i];
                        if (sh != null && !string.IsNullOrEmpty(sh.name) && sh.isSupported)
                        {
                            _nativeShaderCache[sh.name] = sh;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_210) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7651", __apiEx_210); }
        }

        private static Shader FindNativeAndroidShader(string shaderName)
        {
            EnsureNativeShadersIndexed();
            if (!string.IsNullOrEmpty(shaderName))
            {
                if (_nativeShaderCache.TryGetValue(shaderName, out var cached) && cached != null && cached.isSupported)
                    return cached;

                var found = Shader.Find(shaderName);
                if (found != null && found.isSupported)
                {
                    _nativeShaderCache[shaderName] = found;
                    return found;
                }
            }
            return Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        }

        public static void FixMaterialShaderIfUnsupported(Material mat)
        {
            if (mat == null) return;
            try
            {
                var sh = mat.shader;
                bool needsRebind = (sh == null || !sh.isSupported ||
                                    string.Equals(sh.name, "Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase));
                if (!needsRebind) return;
                int id = mat.GetInstanceID();
                if (!_reboundMaterialIds.Add(id)) return;

                string origShaderName = sh != null ? (sh.name ?? "") : "";
                Texture mainTex = null;
                try { mainTex = mat.mainTexture; } catch (System.Exception __apiEx_211) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7686", __apiEx_211); }
                int rq = mat.renderQueue;

                var replacement = FindNativeAndroidShader(origShaderName);
                if (replacement != null)
                {
                    mat.shader = replacement;
                    if (mainTex != null)
                    {
                        try { mat.mainTexture = mainTex; } catch (System.Exception __apiEx_212) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7695", __apiEx_212); }
                    }
                    if (rq > 0)
                    {
                        try { mat.renderQueue = rq; } catch (System.Exception __apiEx_213) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7699", __apiEx_213); }
                    }
                }
            }
            catch (System.Exception __apiEx_214) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7703", __apiEx_214); }
        }

        public static void FixCustomAssetShaders(GameObject root)
        {
            if (root == null) return;
            try
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null) FixMaterialShaderIfUnsupported(mats[i]);
                    }
                }
            }
            catch (System.Exception __apiEx_215) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "L7722", __apiEx_215); }
        }

        // =====================================================================
        // HkCore + HkBossLoader + SsBossLoader IL2CPP Bridges (v2.9.3)
        // =====================================================================
        private static Type _hkLanguageType;
        private static MethodInfo _hkLanguageEnsurePatchedMi;
        private static Type _hkScenesType;
        private static MethodInfo _hkScenesIsHkSceneMi;
        private static MethodInfo _hkScenesIsHkObjectMi;
        private static Type _hkPlayerDataOverlayType;
        private static FieldInfo _hkOverlayHkCodeFi;
        private static FieldInfo _hkOverlayValuesFi;
        private static MethodInfo _hkOverlayEnsureSlotMi;
        private static Type _hkToolRefillsType;
        private static PropertyInfo _hkToolRefillsActivePi;
        private static Type _hkGodseekerModeType;
        private static FieldInfo _hkGodseekerOfferedFi;
        private static Type _ssHallType;
        private static FieldInfo _ssHallActiveFi;

        public static string TextInfoToTitleCaseSafe(System.Globalization.TextInfo ti, string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            var chars = str.ToCharArray();
            bool newWord = true;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (char.IsLetter(c))
                {
                    if (newWord)
                    {
                        chars[i] = char.ToUpperInvariant(c);
                        newWord = false;
                    }
                    else
                    {
                        chars[i] = char.ToLowerInvariant(c);
                    }
                }
                else if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '/' || c == '(')
                {
                    newWord = true;
                }
            }
            return new string(chars);
        }

        private static void EnsureHkCoreTypesResolved()
        {
            if (_hkScenesType != null && _hkPlayerDataOverlayType != null) return;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    string an = asm.GetName().Name ?? "";
                    if (an.StartsWith("HkCore", StringComparison.OrdinalIgnoreCase))
                    {
                        _hkLanguageType = asm.GetType("HkCore.HkLanguage", false);
                        _hkLanguageEnsurePatchedMi = _hkLanguageType?.GetMethod("EnsurePatched", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _hkScenesType = asm.GetType("HkCore.HkScenes", false);
                        _hkScenesIsHkSceneMi = _hkScenesType?.GetMethod("IsHkScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                        _hkScenesIsHkObjectMi = _hkScenesType?.GetMethod("IsHkObject", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
                        _hkPlayerDataOverlayType = asm.GetType("HkCore.PlayerDataOverlay", false);
                        _hkOverlayHkCodeFi = _hkPlayerDataOverlayType?.GetField("hkCode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _hkOverlayValuesFi = _hkPlayerDataOverlayType?.GetField("values", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _hkOverlayEnsureSlotMi = _hkPlayerDataOverlayType?.GetMethod("EnsureSlot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _hkToolRefillsType = asm.GetType("HkCore.HkToolRefills", false);
                        _hkToolRefillsActivePi = _hkToolRefillsType?.GetProperty("Active", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    else if (an.StartsWith("HkBossLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        _hkGodseekerModeType = asm.GetType("HkBossLoader.GodseekerMode", false);
                        _hkGodseekerOfferedFi = _hkGodseekerModeType?.GetField("Offered", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    else if (an.StartsWith("SsBossLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        _ssHallType = asm.GetType("SsBossLoader.SsHall", false);
                        _ssHallActiveFi = _ssHallType?.GetField("Active", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
            }
            catch { }
        }

        public static void HkBossLoaderGetStatusRecordIntPostfix(string key, ref int __result)
        {
            try
            {
                EnsureHkCoreTypesResolved();
                if (_hkGodseekerOfferedFi != null)
                {
                    var cfgObj = _hkGodseekerOfferedFi.GetValue(null);
                    if (cfgObj != null)
                    {
                        var valPi = cfgObj.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                        if (valPi != null && valPi.GetValue(cfgObj) is bool offered && !offered)
                            return;
                    }
                }
                _hkLanguageEnsurePatchedMi?.Invoke(null, null);
                if (string.Equals(key, "RecBossRushMode", StringComparison.Ordinal))
                {
                    __result = 1;
                }
            }
            catch { }
        }

        public static void HkBossLoaderGodseekerInitBridge(object harmonyObj)
        {
            try
            {
                EnsureHkCoreTypesResolved();
                if (!(harmonyObj is HarmonyLib.Harmony h)) return;
                if (_hkGodseekerModeType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm != null && (asm.GetName().Name ?? "").StartsWith("HkBossLoader", StringComparison.OrdinalIgnoreCase))
                        {
                            _hkGodseekerModeType = asm.GetType("HkBossLoader.GodseekerMode", false);
                            _hkGodseekerOfferedFi = _hkGodseekerModeType?.GetField("Offered", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            break;
                        }
                    }
                }
                if (_hkGodseekerModeType == null) return;

                var gmType = HarmonyLib.AccessTools.TypeByName("GameManager");
                var ssbType = HarmonyLib.AccessTools.TypeByName("UnityEngine.UI.SaveSlotButton");
                var uimType = HarmonyLib.AccessTools.TypeByName("UIManager");

                var getStatusMi = gmType != null ? HarmonyLib.AccessTools.Method(gmType, "GetStatusRecordInt", new[] { typeof(string) }) : null;
                var statusPostfixMi = typeof(AssetHelperBridge).GetMethod(nameof(HkBossLoaderGetStatusRecordIntPostfix), BindingFlags.Static | BindingFlags.Public);
                if (getStatusMi != null && statusPostfixMi != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, getStatusMi, null, statusPostfixMi);
                }

                var presentSlotMi = ssbType != null ? HarmonyLib.AccessTools.Method(ssbType, "PresentSaveSlot") : null;
                var slotLocMi = HarmonyLib.AccessTools.Method(_hkGodseekerModeType, "SlotLocation");
                if (presentSlotMi != null && slotLocMi != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, presentSlotMi, null, slotLocMi);
                }

                var startNewGameMi = gmType != null ? HarmonyLib.AccessTools.Method(gmType, "StartNewGame") : null;
                var newSaveMi = HarmonyLib.AccessTools.Method(_hkGodseekerModeType, "NewSave");
                if (startNewGameMi != null && newSaveMi != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, startNewGameMi, null, newSaveMi);
                }

                var readyRespawnMi = gmType != null ? HarmonyLib.AccessTools.Method(gmType, "ReadyForRespawn") : null;
                var startEntranceMi = HarmonyLib.AccessTools.Method(_hkGodseekerModeType, "StartAtEntrance");
                if (readyRespawnMi != null && startEntranceMi != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, readyRespawnMi, startEntranceMi, null);
                }

                var playModeMenuMi = uimType != null ? HarmonyLib.AccessTools.Method(uimType, "UIGoToPlayModeMenu") : null;
                var modeArtMi = HarmonyLib.AccessTools.Method(_hkGodseekerModeType, "ModeArt");
                if (playModeMenuMi != null && modeArtMi != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, playModeMenuMi, modeArtMi, null);
                }

                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Installed IL2CPP-compatible HkBossLoader.GodseekerMode patches.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "HkBossLoaderGodseekerInitBridge", ex);
            }
        }

        private static bool IsHkToolRefillsActive()
        {
            try
            {
                EnsureHkCoreTypesResolved();
                if (_hkToolRefillsActivePi != null && _hkToolRefillsActivePi.GetValue(null) is bool b && b)
                    return true;
                var pd = PlayerData.instance;
                if (pd != null && pd.HasStoredMemoryState)
                    return true;
            }
            catch { }
            return false;
        }

        public static void HkToolRefillsGetCurrencyAmountPostfix(ref int __result)
        {
            if (IsHkToolRefillsActive() && __result < 9999)
                __result = 9999;
        }

        public static bool HkToolRefillsTakeCurrencyPrefix()
        {
            return !IsHkToolRefillsActive();
        }

        public static bool HkToolRefillsTakeLiquidPrefix()
        {
            return !IsHkToolRefillsActive();
        }

        public static void HkCoreToolRefillsInitBridge(object harmonyObj)
        {
            try
            {
                EnsureHkCoreTypesResolved();
                if (!(harmonyObj is HarmonyLib.Harmony h) || _hkToolRefillsType == null) return;

                var fsmOnEnableOrig = HarmonyLib.AccessTools.Method(typeof(PlayMakerFSM), "OnEnable");
                var fsmOnEnablePost = HarmonyLib.AccessTools.Method(_hkToolRefillsType, "FsmOnEnable");
                if (fsmOnEnableOrig != null && fsmOnEnablePost != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, fsmOnEnableOrig, null, fsmOnEnablePost);
                }

                var gmType = HarmonyLib.AccessTools.TypeByName("GameManager");
                var setInvOpenOrig = gmType != null ? HarmonyLib.AccessTools.Method(gmType, "SetIsInventoryOpen") : null;
                var invOpenPost = HarmonyLib.AccessTools.Method(_hkToolRefillsType, "InventoryOpenPostfix");
                if (setInvOpenOrig != null && invOpenPost != null)
                {
                    HarmonyBridge.PatchMethodInfos3MethodInfo(h, setInvOpenOrig, null, invOpenPost);
                }

                var cmType = HarmonyLib.AccessTools.TypeByName("CurrencyManager");
                if (cmType != null)
                {
                    var getCurrMi = HarmonyLib.AccessTools.Method(cmType, "GetCurrencyAmount");
                    var getCurrPost = typeof(AssetHelperBridge).GetMethod(nameof(HkToolRefillsGetCurrencyAmountPostfix), BindingFlags.Static | BindingFlags.Public);
                    if (getCurrMi != null && getCurrPost != null)
                        HarmonyBridge.PatchMethodInfos3MethodInfo(h, getCurrMi, null, getCurrPost);

                    var takeCurrMi = HarmonyLib.AccessTools.Method(cmType, "TakeCurrency");
                    var takeCurrPre = typeof(AssetHelperBridge).GetMethod(nameof(HkToolRefillsTakeCurrencyPrefix), BindingFlags.Static | BindingFlags.Public);
                    if (takeCurrMi != null && takeCurrPre != null)
                        HarmonyBridge.PatchMethodInfos3MethodInfo(h, takeCurrMi, takeCurrPre, null);
                }

                var liquidType = HarmonyLib.AccessTools.TypeByName("ToolItemStatesLiquid");
                if (liquidType != null)
                {
                    var takeLiqMi = HarmonyLib.AccessTools.Method(liquidType, "TakeLiquid");
                    var takeLiqPre = typeof(AssetHelperBridge).GetMethod(nameof(HkToolRefillsTakeLiquidPrefix), BindingFlags.Static | BindingFlags.Public);
                    if (takeLiqMi != null && takeLiqPre != null)
                        HarmonyBridge.PatchMethodInfos3MethodInfo(h, takeLiqMi, takeLiqPre, null);
                }

                SilksongMobileAPIPlugin.LogDiagnosticInfo("[AssetHelperBridge] Installed IL2CPP-compatible HkCore.HkToolRefills patches.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("AssetHelperBridge", "HkCoreToolRefillsInitBridge", ex);
            }
        }

        public static bool HkCorePlayerDataInHkCodeBridge()
        {
            try
            {
                EnsureHkCoreTypesResolved();
                if (_hkOverlayHkCodeFi != null && _hkOverlayHkCodeFi.GetValue(null) is int hkCode && hkCode > 0)
                    return true;

                var execFsm = HutongGames.PlayMaker.FsmExecutionStack.ExecutingFsm;
                if (execFsm != null)
                {
                    var go = execFsm.GameObject;
                    if (go != null && _hkScenesIsHkObjectMi != null && _hkScenesIsHkObjectMi.Invoke(null, new object[] { go }) is bool isHkObj && isHkObj)
                        return true;
                }

                if (_hkScenesIsHkSceneMi != null)
                {
                    string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                    if (!string.IsNullOrEmpty(sceneName) && _hkScenesIsHkSceneMi.Invoke(null, new object[] { sceneName }) is bool isHkScene && isHkScene)
                    {
                        bool ssActive = false;
                        if (_ssHallActiveFi == null)
                        {
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                if (asm != null && (asm.GetName().Name ?? "").StartsWith("SsBossLoader", StringComparison.OrdinalIgnoreCase))
                                {
                                    _ssHallType = asm.GetType("SsBossLoader.SsHall", false);
                                    _ssHallActiveFi = _ssHallType?.GetField("Active", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                    break;
                                }
                            }
                        }
                        if (_ssHallActiveFi != null && _ssHallActiveFi.GetValue(null) is bool b && b)
                            ssActive = true;
                        if (!ssActive)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void HkCoreSyncOverlayToPlayerData()
        {
            try
            {
                EnsureHkCoreTypesResolved();
                var pd = PlayerData.instance;
                if (pd == null || _hkOverlayValuesFi == null) return;
                if (!(_hkOverlayValuesFi.GetValue(null) is System.Collections.IDictionary dict)) return;

                if (dict.Contains("unlockedBossScenes") && dict["unlockedBossScenes"] is List<string> ubs)
                {
                    pd.unlockedBossScenes = new List<string>(ubs);
                }
                if (dict.Contains("currentBossStatueCompletionKey") && dict["currentBossStatueCompletionKey"] is string cbsk)
                {
                    pd.currentBossStatueCompletionKey = cbsk;
                }
                if (dict.Contains("bossStatueTargetLevel") && dict["bossStatueTargetLevel"] is int bstl)
                {
                    pd.bossStatueTargetLevel = bstl;
                }
                if (dict.Contains("bossReturnEntryGate") && dict["bossReturnEntryGate"] is string breg)
                {
                    pd.bossReturnEntryGate = breg;
                }
                if (dict.Contains("hasGodfinder") && dict["hasGodfinder"] is bool hgf)
                {
                    pd.hasGodfinder = hgf;
                }
                if (dict.Contains("unlockedNewBossStatue") && dict["unlockedNewBossStatue"] is bool unbs)
                {
                    pd.unlockedNewBossStatue = unbs;
                }
            }
            catch { }
        }

        private static bool _hkCoreSceneLocatorRegistered;
        private static bool _hkCoreSceneProviderWrapped;
        private static MethodInfo _hkScenesLoadScenePrefixMi;

        public static void EnsureHkCoreAddressablesSceneBridgeRegistered()
        {
            if (_hkCoreSceneLocatorRegistered && _hkCoreSceneProviderWrapped) return;
            try
            {
                EnsureHkCoreTypesResolved();
                if (_hkScenesType == null) return;
                _hkScenesIsHkSceneMi = _hkScenesType.GetMethod("IsHkScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _hkScenesLoadScenePrefixMi = _hkScenesType.GetMethod("LoadScenePrefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (_hkScenesIsHkSceneMi == null || _hkScenesLoadScenePrefixMi == null)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn("[HkCoreSceneBridge] Could not find HkScenes.IsHkScene or LoadScenePrefix.");
                    return;
                }

                object addrImpl = typeof(Addressables).GetProperty("m_Addressables", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null, null)
                               ?? typeof(Addressables).GetField("m_AddressablesInstance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                string wrappedFieldName = "none";
                HkCoreAddressablesSceneProvider provider = null;
                if (addrImpl != null)
                {
                    var implType = addrImpl.GetType();
                    // 1. Scan all instance fields assignable to ISceneProvider (handles <SceneProvider>k__BackingField or any field name)
                    foreach (var fi in implType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (!typeof(ISceneProvider).IsAssignableFrom(fi.FieldType)) continue;
                        var curVal = fi.GetValue(addrImpl) as ISceneProvider;
                        if (curVal is HkCoreAddressablesSceneProvider)
                        {
                            _hkCoreSceneProviderWrapped = true;
                            wrappedFieldName = fi.Name;
                            break;
                        }
                        provider = new HkCoreAddressablesSceneProvider(curVal ?? new SceneProvider());
                        fi.SetValue(addrImpl, provider);
                        _hkCoreSceneProviderWrapped = true;
                        wrappedFieldName = fi.Name;
                        break;
                    }
                    // 2. Fallback: SceneProvider property setter
                    if (!_hkCoreSceneProviderWrapped)
                    {
                        var spProp = implType.GetProperty("SceneProvider", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (spProp != null && spProp.CanWrite)
                        {
                            var curVal = spProp.CanRead ? spProp.GetValue(addrImpl, null) as ISceneProvider : null;
                            provider = new HkCoreAddressablesSceneProvider(curVal ?? new SceneProvider());
                            spProp.SetValue(addrImpl, provider, null);
                            _hkCoreSceneProviderWrapped = true;
                            wrappedFieldName = "prop:SceneProvider";
                        }
                    }
                }
                if (provider == null) provider = new HkCoreAddressablesSceneProvider(new SceneProvider());

                var rm = Addressables.ResourceManager;
                if (rm != null && rm.ResourceProviders != null)
                {
                    bool hasProv = false;
                    for (int i = 0; i < rm.ResourceProviders.Count; i++)
                    {
                        if (rm.ResourceProviders[i] is HkCoreAddressablesSceneProvider) { hasProv = true; break; }
                    }
                    if (!hasProv) rm.ResourceProviders.Add(provider);
                }
                if (!_hkCoreSceneLocatorRegistered)
                {
                    Addressables.AddResourceLocator(new HkCoreAddressablesSceneLocator());
                    _hkCoreSceneLocatorRegistered = true;
                }
                EnsureHkCoreBossChallengeHookInstalled();
                    EnsureHkMemoryGuardInstalled();
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Wrapped ISceneProvider (wrapped={_hkCoreSceneProviderWrapped}, field='{wrappedFieldName}', addrImpl={(addrImpl != null)}) & locatorRegistered={_hkCoreSceneLocatorRegistered}.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] Registration warning: {ex.Message}");
            }
        }

        internal static bool IsHkCoreSceneKey(object key, out string normalizedSceneName)
        {
            normalizedSceneName = null;
            if (key == null || _hkScenesIsHkSceneMi == null) return false;
            string s = key as string;
            if (string.IsNullOrEmpty(s)) return false;
            string sc = s.StartsWith("Scenes/", StringComparison.OrdinalIgnoreCase) ? s.Substring(7) : s;
            if (sc.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) sc = sc.Substring(0, sc.Length - 6);
            try
            {
                object res = _hkScenesIsHkSceneMi.Invoke(null, new object[] { sc });
                if (res is bool b && b)
                {
                    normalizedSceneName = sc;
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool TryInvokeHkCoreLoadScenePrefix(object key, LoadSceneMode loadMode, bool activateOnLoad, int priority, out AsyncOperationHandle<SceneInstance> resultHandle)
        {
            resultHandle = default;
            if (_hkScenesLoadScenePrefixMi == null) return false;
            if ((_directHkSessionActive || Time.realtimeSinceStartup < _directHkBenchReturnUntilRealtime) &&
                key is string ks &&
                (ks.IndexOf("GG_Atrium", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ks.IndexOf("GG_Workshop", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Blocked Godhome hub Addressables load '{ks}' during direct-boss bench return.");
                return false;
            }
            try
            {
                var ps = _hkScenesLoadScenePrefixMi.GetParameters();
                var invokeArgs = new object[ps.Length];
                int resultIdx = -1;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    var pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                    if (p.Name == "__result" || pt == typeof(AsyncOperationHandle<SceneInstance>))
                    {
                        resultIdx = i;
                        invokeArgs[i] = default(AsyncOperationHandle<SceneInstance>);
                    }
                    else if (pt == typeof(object) || pt == typeof(string))
                    {
                        invokeArgs[i] = key;
                    }
                    else if (pt == typeof(LoadSceneMode))
                    {
                        invokeArgs[i] = loadMode;
                    }
                    else if (pt == typeof(bool))
                    {
                        invokeArgs[i] = activateOnLoad;
                    }
                    else if (pt == typeof(int))
                    {
                        invokeArgs[i] = priority;
                    }
                    else if (pt == typeof(SceneReleaseMode))
                    {
                        invokeArgs[i] = SceneReleaseMode.ReleaseSceneWhenSceneUnloaded;
                    }
                }
                _hkScenesLoadScenePrefixMi.Invoke(null, invokeArgs);
                if (resultIdx >= 0 && invokeArgs[resultIdx] is AsyncOperationHandle<SceneInstance> h && h.IsValid())
                {
                    resultHandle = h;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Routed Addressables scene '{key}' -> HkCore.HkScenes.LoadScenePrefix (valid handle!).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[HkCoreSceneBridge] LoadScenePrefix invoke error for '{key}': {ex}");
            }
            return false;
        }

        private sealed class HkCoreAddressablesSceneLocator : IResourceLocator
        {
            public string LocatorId => "IRIS.HkCoreSceneLocator";
            public IEnumerable<object> Keys => Array.Empty<object>();
            public IEnumerable<IResourceLocation> AllLocations => Array.Empty<IResourceLocation>();

            public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
            {
                locations = null;
                if (type != null && type != typeof(SceneInstance) && type != typeof(object))
                    return false;
                if (!IsHkCoreSceneKey(key, out _))
                    return false;
                string keyStr = key as string ?? "";
                var loc = new ResourceLocationBase(keyStr, keyStr, "IRIS.HkCoreSceneProvider", typeof(SceneInstance));
                locations = new List<IResourceLocation>(1) { loc };
                return true;
            }
        }

        private sealed class HkCoreAddressablesSceneProvider : ISceneProvider, IResourceProvider
        {
            private readonly ISceneProvider _fallback;

            public HkCoreAddressablesSceneProvider(ISceneProvider fallback)
            {
                _fallback = fallback ?? new SceneProvider();
            }

            public string ProviderId => "IRIS.HkCoreSceneProvider";
            public ProviderBehaviourFlags BehaviourFlags => ProviderBehaviourFlags.None;

            public Type GetDefaultType(IResourceLocation location) => typeof(SceneInstance);

            public bool CanProvide(Type t, IResourceLocation location)
            {
                return t == typeof(SceneInstance) && location != null && location.ProviderId == "IRIS.HkCoreSceneProvider";
            }

            public void Provide(ProvideHandle provideHandle)
            {
                provideHandle.Complete<object>(null, false, new NotSupportedException("Use ProvideScene for HkCore scenes."));
            }

            public void Release(IResourceLocation location, object asset) { }

            public AsyncOperationHandle<SceneInstance> ProvideScene(UnityEngine.ResourceManagement.ResourceManager resourceManager, IResourceLocation location, LoadSceneMode loadMode, bool activateOnLoad, int priority)
            {
                if (location != null && location.ProviderId == "IRIS.HkCoreSceneProvider" &&
                    TryInvokeHkCoreLoadScenePrefix(location.InternalId, loadMode, activateOnLoad, priority, out var h))
                    return h;
                return _fallback.ProvideScene(resourceManager, location, loadMode, activateOnLoad, priority);
            }

            public AsyncOperationHandle<SceneInstance> ProvideScene(UnityEngine.ResourceManagement.ResourceManager resourceManager, IResourceLocation location, LoadSceneParameters loadSceneParameters, bool activateOnLoad, int priority)
            {
                if (location != null && location.ProviderId == "IRIS.HkCoreSceneProvider" &&
                    TryInvokeHkCoreLoadScenePrefix(location.InternalId, LoadSceneMode.Additive, activateOnLoad, priority, out var h))
                    return h;
                return _fallback.ProvideScene(resourceManager, location, loadSceneParameters, activateOnLoad, priority);
            }

            public AsyncOperationHandle<SceneInstance> ProvideScene(UnityEngine.ResourceManagement.ResourceManager resourceManager, IResourceLocation location, LoadSceneParameters loadSceneParameters, SceneReleaseMode releaseMode, bool activateOnLoad, int priority)
            {
                if (location != null && location.ProviderId == "IRIS.HkCoreSceneProvider" &&
                    TryInvokeHkCoreLoadScenePrefix(location.InternalId, LoadSceneMode.Additive, activateOnLoad, priority, out var h))
                    return h;
                return _fallback.ProvideScene(resourceManager, location, loadSceneParameters, releaseMode, activateOnLoad, priority);
            }

            public AsyncOperationHandle<SceneInstance> ReleaseScene(UnityEngine.ResourceManagement.ResourceManager resourceManager, AsyncOperationHandle<SceneInstance> sceneLoadHandle)
            {
                return _fallback.ReleaseScene(resourceManager, sceneLoadHandle);
            }
        }

        private sealed class SafeStringDictMirror
        {
            public int Version = int.MinValue;
            public Dictionary<string, object> Map;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SafeStringDictMirror> _safeStringDictMirrors =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, SafeStringDictMirror>();

        private static Dictionary<string, object> GetSafeStringDictMirror(object dict)
        {
            if (dict == null) return null;
            var mirror = _safeStringDictMirrors.GetOrCreateValue(dict);
            var dt = dict.GetType();
            var verFi = dt.GetField("version", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? dt.GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);
            int curVer = verFi != null ? (int)verFi.GetValue(dict) : -1;
            lock (mirror)
            {
                if (mirror.Map != null && mirror.Version == curVer && curVer >= 0)
                    return mirror.Map;

                if (mirror.Map == null)
                {
                    var cmpFi = dt.GetField("comparer", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? dt.GetField("_comparer", BindingFlags.Instance | BindingFlags.NonPublic);
                    var cmp = cmpFi?.GetValue(dict) as IEqualityComparer<string>;
                    mirror.Map = new Dictionary<string, object>(cmp ?? StringComparer.Ordinal);
                }
                else
                {
                    mirror.Map.Clear();
                }

                var countFi = dt.GetField("count", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? dt.GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic);
                var entriesFi = dt.GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? dt.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
                if (countFi != null && entriesFi != null)
                {
                    int count = (int)countFi.GetValue(dict);
                    var entries = entriesFi.GetValue(dict) as Array;
                    if (entries != null && count > 0)
                    {
                        var et = entries.GetType().GetElementType();
                        var hashFi = et?.GetField("hashCode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var keyFi = et?.GetField("key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var valFi = et?.GetField("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        int limit = Math.Min(count, entries.Length);
                        for (int i = 0; i < limit; i++)
                        {
                            object entry = entries.GetValue(i);
                            if (entry == null) continue;
                            if (hashFi != null && (int)hashFi.GetValue(entry) < 0) continue;
                            var k = keyFi?.GetValue(entry) as string;
                            if (k != null)
                                mirror.Map[k] = valFi?.GetValue(entry);
                        }
                    }
                }
                mirror.Version = curVer;
                return mirror.Map;
            }
        }

        public static ValueTuple<string, string> SafeDictGet_Str_VT2StrStr(object dict, string key)
        {
            var map = GetSafeStringDictMirror(dict);
            if (map != null && key != null && map.TryGetValue(key, out var val) && val is ValueTuple<string, string> vt)
                return vt;
            throw new KeyNotFoundException($"Key '{key}' was not present in dictionary.");
        }

        public static IEnumerable<string> SafeDictGetKeys_Str_VT2StrStr(object dict)
        {
            var map = GetSafeStringDictMirror(dict);
            return map != null ? (IEnumerable<string>)new List<string>(map.Keys) : Array.Empty<string>();
        }

        public static bool SafeDictTryGet_Str_VT2StrArrStrArr(object dict, string key, out ValueTuple<string[], string[]> value)
        {
            var map = GetSafeStringDictMirror(dict);
            if (map != null && key != null && map.TryGetValue(key, out var val) && val is ValueTuple<string[], string[]> vt)
            {
                value = vt;
                return true;
            }
            value = default;
            return false;
        }

        public static ValueTuple<string, int> SafeDictGet_Str_VT2StrInt(object dict, string key)
        {
            var map = GetSafeStringDictMirror(dict);
            if (map != null && key != null && map.TryGetValue(key, out var val) && val is ValueTuple<string, int> vt)
                return vt;
            throw new KeyNotFoundException($"Key '{key}' was not present in dictionary.");
        }

        public static bool SafeDictContainsKey_Str_VT2StrInt(object dict, string key)
        {
            var map = GetSafeStringDictMirror(dict);
            return map != null && key != null && map.ContainsKey(key);
        }

        private static bool _hkSceneMemHooked;
        private static int _savedMipmapLimit = -1;
        private static int _hkDownscaleToken;
        private static int _hkAudioRestoreToken;
        private static FieldInfo _csmActorSnapshotField;
        private static bool _csmActorSnapshotResolved;

        private static readonly HashSet<int> _downscaledHkTexIds = new HashSet<int>();
        private const int HkMaxBossTextureDim = 1024;
        private const int HkMaxArenaTextureDim = 512;

        private static bool _loggedHkDownscaleError;

        private static bool IsHardwareAndroidCompressedFormat(TextureFormat fmt)
        {
            int id = (int)fmt;
            // Unity TextureFormat ASTC (48..59) and ETC2/EAC (34, 45, 46, 47) are natively hardware-sampled on Android GPUs
            return (id >= 48 && id <= 59) || id == 34 || id == 45 || id == 46 || id == 47;
        }

        private static Texture2D CreateDownscaledCopy(Texture2D srcTex, int maxDim, bool use16Bit, out long savedBytes)
        {
            savedBytes = 0;
            if (srcTex == null || IsHardwareAndroidCompressedFormat(srcTex.format)) return null;
            int w = srcTex.width;
            int h = srcTex.height;
            bool can16Bit = use16Bit && SystemInfo.SupportsTextureFormat(TextureFormat.RGBA4444);
            bool needsResize = w > maxDim || h > maxDim;
            bool needs16BitConvert = can16Bit && srcTex.format != TextureFormat.RGBA4444 && ((long)w * h >= 256L * 256L);
            if (!needsResize && !needs16BitConvert) return null;

            float scale = needsResize ? Mathf.Min((float)maxDim / w, (float)maxDim / h) : 1f;
            int nw = Mathf.Max(4, Mathf.RoundToInt(w * scale));
            int nh = Mathf.Max(4, Mathf.RoundToInt(h * scale));

            RenderTexture rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture prev = RenderTexture.active;
            Texture2D tmp32 = null;
            Texture2D dst16 = null;
            try
            {
                Graphics.Blit(srcTex, rt);
                RenderTexture.active = rt;
                // Unity 6 ReadPixels requires a matching RGBA32 destination; convert to RGBA4444 via SetPixels32 afterward
                tmp32 = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                tmp32.filterMode = srcTex.filterMode;
                tmp32.wrapMode = srcTex.wrapMode;
                tmp32.ReadPixels(new Rect(0f, 0f, nw, nh), 0, 0, false);

                if (can16Bit)
                {
                    try
                    {
                        var pixels = tmp32.GetPixels32();
                        dst16 = new Texture2D(nw, nh, TextureFormat.RGBA4444, false);
                        dst16.name = srcTex.name + "_hk512_16";
                        dst16.filterMode = srcTex.filterMode;
                        dst16.wrapMode = srcTex.wrapMode;
                        dst16.SetPixels32(pixels);
                        dst16.Apply(false, true);
                        UnityEngine.Object.Destroy(tmp32);
                        tmp32 = null;
                        _downscaledHkTexIds.Add(dst16.GetInstanceID());
                        long srcEst16 = (long)w * h * 4L;
                        long dstEst16 = (long)nw * nh * 2L;
                        savedBytes = srcEst16 > dstEst16 ? (srcEst16 - dstEst16) : 0L;
                        return dst16;
                    }
                    catch
                    {
                        if (dst16 != null) { UnityEngine.Object.Destroy(dst16); dst16 = null; }
                        // Fallback cleanly to downscaled RGBA32 if 16-bit conversion fails
                    }
                }

                tmp32.name = srcTex.name + (use16Bit ? "_hk512" : "_hk1024");
                tmp32.Apply(false, true);
                _downscaledHkTexIds.Add(tmp32.GetInstanceID());
                long srcEst32 = (long)w * h * 4L;
                long dstEst32 = (long)nw * nh * 4L;
                savedBytes = srcEst32 > dstEst32 ? (srcEst32 - dstEst32) : 0L;
                var result = tmp32;
                tmp32 = null;
                return result;
            }
            catch (Exception ex)
            {
                if (dst16 != null) UnityEngine.Object.Destroy(dst16);
                if (tmp32 != null) UnityEngine.Object.Destroy(tmp32);
                if (!_loggedHkDownscaleError)
                {
                    _loggedHkDownscaleError = true;
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreMemGuard] CreateDownscaledCopy('{srcTex.name}', {w}x{h}->{nw}x{nh}, 16bit={use16Bit}) failed: {ex.Message}");
                }
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private struct HkTexDownscaleJob
        {
            public Texture2D Tex;
            public int CapDim;
            public bool Use16Bit;
            public bool IsBossTex;
        }

        public static void DownscaleOversizedHkSceneTextures(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            int token = ++_hkDownscaleToken;
            if (SilksongMobileAPIPlugin.Instance != null)
            {
                SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDownscaleHkSceneTexturesBudgeted(scene, token));
            }
        }

        private static IEnumerator CoDownscaleHkSceneTexturesBudgeted(Scene scene, int token)
        {
            string sceneName = scene.IsValid() ? scene.name : "<invalid>";
            var mats = new HashSet<Material>();
            var colls = new HashSet<tk2dSpriteCollectionData>();
            var bossTexIds = new HashSet<int>();
            var jobs = new List<HkTexDownscaleJob>();
            var texToMats = new Dictionary<Texture2D, List<Material>>();
            int disabledBgParticles = 0;

            try
            {
                if (!scene.IsValid() || !scene.isLoaded) yield break;
                var roots = scene.GetRootGameObjects();
                if (roots == null || roots.Length == 0) yield break;

                for (int r = 0; r < roots.Length; r++)
                {
                    var root = roots[r];
                    if (root == null) continue;

                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    if (renderers != null)
                    {
                        for (int i = 0; i < renderers.Length; i++)
                        {
                            var ren = renderers[i];
                            if (ren == null) continue;
                            var sm = ren.sharedMaterials;
                            if (sm == null) continue;
                            for (int m = 0; m < sm.Length; m++)
                                if (sm[m] != null) mats.Add(sm[m]);
                        }
                    }

                    var sprites = root.GetComponentsInChildren<tk2dBaseSprite>(true);
                    if (sprites != null)
                    {
                        for (int i = 0; i < sprites.Length; i++)
                        {
                            var c = sprites[i]?.Collection;
                            if (c != null) colls.Add(c);
                        }
                    }

                    // Identify boss/enemy textures (HealthManager hierarchies) so bosses stay crisp (<=1024px RGBA32) while arena/hub/bg sprites compress to <=512px RGBA4444
                    var hms = root.GetComponentsInChildren<HealthManager>(true);
                    if (hms != null)
                    {
                        for (int h = 0; h < hms.Length; h++)
                        {
                            if (hms[h] == null) continue;
                            var bRends = hms[h].GetComponentsInChildren<Renderer>(true);
                            if (bRends != null)
                            {
                                for (int k = 0; k < bRends.Length; k++)
                                {
                                    var sm = bRends[k]?.sharedMaterials;
                                    if (sm == null) continue;
                                    for (int m = 0; m < sm.Length; m++)
                                        if (sm[m] != null && sm[m].HasProperty("_MainTex") && sm[m].mainTexture is Texture2D bt && bt != null)
                                            bossTexIds.Add(bt.GetInstanceID());
                                }
                            }
                            var bSps = hms[h].GetComponentsInChildren<tk2dBaseSprite>(true);
                            if (bSps != null)
                            {
                                for (int k = 0; k < bSps.Length; k++)
                                {
                                    var c = bSps[k]?.Collection;
                                    if (c?.textures == null) continue;
                                    for (int ti = 0; ti < c.textures.Length; ti++)
                                        if (c.textures[ti] is Texture2D ct && ct != null)
                                            bossTexIds.Add(ct.GetInstanceID());
                                }
                            }
                        }
                    }

                    // Disable purely decorative far-background ParticleSystems (z > 1.0f, non-boss/non-hazard) without altering arena layout or mechanics
                    var pss = root.GetComponentsInChildren<ParticleSystem>(true);
                    if (pss != null)
                    {
                        for (int i = 0; i < pss.Length; i++)
                        {
                            var ps = pss[i];
                            if (ps == null || ps.transform.position.z <= 1.0f) continue;
                            if (ps.GetComponentInParent<HealthManager>() != null || ps.GetComponentInParent<DamageHero>() != null) continue;
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            var em = ps.emission;
                            em.enabled = false;
                            disabledBgParticles++;
                        }
                    }
                }

                foreach (var c in colls)
                {
                    if (c == null) continue;
                    if (c.materials != null)
                        for (int i = 0; i < c.materials.Length; i++)
                            if (c.materials[i] != null) mats.Add(c.materials[i]);
                    if (c.materialInsts != null)
                        for (int i = 0; i < c.materialInsts.Length; i++)
                            if (c.materialInsts[i] != null) mats.Add(c.materialInsts[i]);
                }

                var queuedTex = new HashSet<Texture2D>();
                foreach (var mat in mats)
                {
                    if (mat == null || !mat.HasProperty("_MainTex")) continue;
                    if (!(mat.mainTexture is Texture2D t2d) || t2d == null) continue;
                    int tid = t2d.GetInstanceID();
                    if (_downscaledHkTexIds.Contains(tid) || IsHardwareAndroidCompressedFormat(t2d.format)) continue;

                    bool isBossTex = bossTexIds.Contains(tid);
                    int capDim = isBossTex ? HkMaxBossTextureDim : HkMaxArenaTextureDim;
                    bool use16Bit = !isBossTex;
                    bool needsResize = t2d.width > capDim || t2d.height > capDim;
                    bool needs16Bit = use16Bit && t2d.format != TextureFormat.RGBA4444 && ((long)t2d.width * t2d.height >= 256L * 256L);
                    if (!needsResize && !needs16Bit) continue;

                    if (!texToMats.TryGetValue(t2d, out var matList))
                    {
                        matList = new List<Material>(2);
                        texToMats[t2d] = matList;
                    }
                    matList.Add(mat);

                    if (queuedTex.Add(t2d))
                    {
                        jobs.Add(new HkTexDownscaleJob { Tex = t2d, CapDim = capDim, Use16Bit = use16Bit, IsBossTex = isBossTex });
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreMemGuard] CoDownscaleHkSceneTexturesBudgeted('{sceneName}') scan error: {ex.Message}");
                yield break;
            }

            long totalSavedBytes = 0;
            int arenaCount = 0;
            int bossCount = 0;
            const int maxPerFrame = 4;
            int processedThisFrame = 0;

            for (int j = 0; j < jobs.Count; j++)
            {
                if (token != _hkDownscaleToken || !scene.IsValid() || !scene.isLoaded) yield break;

                var job = jobs[j];
                if (job.Tex == null) continue;

                try
                {
                    var small = CreateDownscaledCopy(job.Tex, job.CapDim, job.Use16Bit, out long sb);
                    if (small != null)
                    {
                        totalSavedBytes += sb;
                        DownscaledTextureCount++;
                        if (job.IsBossTex) bossCount++; else arenaCount++;

                        if (texToMats.TryGetValue(job.Tex, out var matList))
                        {
                            for (int m = 0; m < matList.Count; m++)
                            {
                                if (matList[m] != null) matList[m].mainTexture = small;
                            }
                        }
                        foreach (var c in colls)
                        {
                            if (c == null) continue;
                            if (c.textures != null)
                            {
                                for (int i = 0; i < c.textures.Length; i++)
                                    if (ReferenceEquals(c.textures[i], job.Tex)) c.textures[i] = small;
                            }
                            if (c.textureInsts != null)
                            {
                                for (int i = 0; i < c.textureInsts.Length; i++)
                                    if (ReferenceEquals(c.textureInsts[i], job.Tex)) c.textureInsts[i] = small;
                            }
                        }
                        try { Resources.UnloadAsset(job.Tex); } catch { }
                    }
                }
                catch { }

                processedThisFrame++;
                if (processedThisFrame >= maxPerFrame)
                {
                    processedThisFrame = 0;
                    yield return null;
                }
            }

            if (arenaCount > 0 || bossCount > 0 || disabledBgParticles > 0)
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreMemGuard] Budgeted downscale in '{sceneName}': {arenaCount} arena/hub (<=512px RGBA4444) & {bossCount} boss (<=1024px RGBA32), disabled {disabledBgParticles} bg particles (~{totalSavedBytes / (1024 * 1024)} MB VRAM freed).");
            }
        }

        public static void RestoreHornetPhysicsAndGravitySafe(string reason, bool forceClearVelocity)
        {
            try
            {
                var hc = HeroController.instance;
                if (hc == null) return;
                var pd = PlayerData.instance;
                if (pd != null && pd.atBench) return;

                Rigidbody2D body = hc.Body;
                bool wasKinematic = body != null && body.bodyType != RigidbodyType2D.Dynamic;
                bool activeMoveState = hc.cState != null && (hc.cState.dashing || hc.cState.wallSliding || hc.cState.superDashing);
                if (!forceClearVelocity && !wasKinematic && activeMoveState) return;

                bool wasNoGravity = !hc.IsGravityApplied || (body != null && body.gravityScale <= Mathf.Epsilon);
                bool isFloating = hc.cState != null && hc.cState.floating;
                // Only clear cState.floating during explicit transitions/bench returns or when stuck kinematic (never cancel live Drifter's Cloak glide)
                bool wasFloating = isFloating && (forceClearVelocity || wasKinematic);
                if (!forceClearVelocity && isFloating && !wasKinematic)
                    wasNoGravity = false;

                if (!wasKinematic && !wasNoGravity && !wasFloating && !forceClearVelocity) return;

                if (wasKinematic)
                    body.bodyType = RigidbodyType2D.Dynamic;
                if (body != null && (forceClearVelocity || wasKinematic || wasNoGravity || wasFloating))
                    body.velocity = Vector2.zero;
                if (wasFloating)
                    hc.cState.floating = false;
                if (wasNoGravity)
                {
                    hc.AffectedByGravity(true);
                    if (body != null && body.gravityScale <= Mathf.Epsilon)
                        hc.ResetGravity();
                }

                if (wasKinematic || wasNoGravity || wasFloating)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Restored Hornet physics ({reason}): kinematic={wasKinematic}, noGravity={wasNoGravity}, floating={wasFloating}.");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] RestoreHornetPhysicsAndGravitySafe({reason}) error: {ex.Message}");
            }
        }

        private static void RestoreHkSceneHornetAudioSafe(string sceneName)
        {
            try
            {
                var gm = GameManager.instance;
                if (gm == null || gm.isPaused) return;

                var unpaused = gm.actorSnapshotUnpaused;
                bool patchedCsm = false;
                if (unpaused != null)
                {
                    if (!_csmActorSnapshotResolved)
                    {
                        _csmActorSnapshotResolved = true;
                        _csmActorSnapshotField = typeof(CustomSceneManager).GetField("actorSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    var csm = UnityEngine.Object.FindObjectOfType<CustomSceneManager>();
                    if (csm != null && _csmActorSnapshotField != null)
                    {
                        var cur = _csmActorSnapshotField.GetValue(csm) as UnityEngine.Audio.AudioMixerSnapshot;
                        if (cur == null)
                        {
                            _csmActorSnapshotField.SetValue(csm, unpaused);
                            patchedCsm = true;
                        }
                    }
                    unpaused.TransitionTo(0.25f);
                }

                int unmutedSources = 0;
                var hc = HeroController.instance;
                if (hc != null)
                {
                    var srcs = hc.GetComponentsInChildren<AudioSource>(true);
                    if (srcs != null)
                    {
                        for (int i = 0; i < srcs.Length; i++)
                        {
                            var a = srcs[i];
                            if (a != null && a.mute)
                            {
                                a.mute = false;
                                unmutedSources++;
                            }
                        }
                    }
                }

                if (patchedCsm || unmutedSources > 0)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreAudioFix] Restored Hornet voice/Actors mixer in '{sceneName}' (patchedNullActorSnapshot={patchedCsm}, unmutedHeroAudio={unmutedSources}).");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreAudioFix] RestoreHkSceneHornetAudioSafe('{sceneName}') error: {ex.Message}");
            }
        }

        private static IEnumerator CoRestoreHkSceneHornetAndAudio(string sceneName, int token)
        {
            yield return new WaitForSeconds(0.35f);
            if (token != _hkAudioRestoreToken) yield break;
            if (!string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.OrdinalIgnoreCase)) yield break;
            RestoreHkSceneHornetAudioSafe(sceneName);
            RestoreHornetPhysicsAndGravitySafe("hk-load-0.35s:" + sceneName, false);

            yield return new WaitForSeconds(0.85f);
            if (token != _hkAudioRestoreToken) yield break;
            if (!string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.OrdinalIgnoreCase)) yield break;
            RestoreHkSceneHornetAudioSafe(sceneName);
            RestoreHornetPhysicsAndGravitySafe("hk-load-1.2s:" + sceneName, false);

            // Unload finished HK boss scenes, bundles (Unload(true)), and unused textures once the new scene has fully loaded.
            if (SilksongMobileAPIPlugin.CfgUnloadFinishedHkBosses == null || SilksongMobileAPIPlugin.CfgUnloadFinishedHkBosses.Value)
            {
                if (SilksongMobileAPIPlugin.Instance != null)
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoUnloadFinishedHkBossesAfterLoad(sceneName, token));
            }

            // Fix white-screen softlock in GG_Boss_Door_Entrance when Conversation Control enters Greet 0..4
            // and waits forever for CONVO_FINISH (zero Update() polling; one-shot coroutine only for GG_Boss_Door_Entrance).
            if (string.Equals(sceneName, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase) &&
                (SilksongMobileAPIPlugin.CfgFixPantheonEntranceWhiteScreen == null || SilksongMobileAPIPlugin.CfgFixPantheonEntranceWhiteScreen.Value))
            {
                yield return new WaitForSeconds(1.8f);
                if (token != _hkAudioRestoreToken) yield break;
                if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase)) yield break;

                PlayMakerFSM convFsm = FindConversationControlInActiveScene();
                if (convFsm != null)
                {
                    string st = convFsm.ActiveStateName ?? "";
                    if (st.StartsWith("Greet", StringComparison.OrdinalIgnoreCase))
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] GG_Boss_Door_Entrance stalled in '{st}' at 3.0s -> sending CONVO_FINISH.");
                        try { convFsm.SendEvent("CONVO_FINISH"); } catch { }
                    }
                    else if (!string.Equals(st, "Talk Finish", StringComparison.OrdinalIgnoreCase))
                    {
                        // Give Pause/Atmos/Init/Text. a brief moment to reach Greet, then advance cleanly
                        yield return new WaitForSeconds(2.5f);
                        if (token != _hkAudioRestoreToken) yield break;
                        if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase)) yield break;
                        st = convFsm != null ? (convFsm.ActiveStateName ?? "") : "";
                        if (st.StartsWith("Greet", StringComparison.OrdinalIgnoreCase))
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] GG_Boss_Door_Entrance stalled in '{st}' at 5.5s -> sending CONVO_FINISH.");
                            try { convFsm.SendEvent("CONVO_FINISH"); } catch { }
                        }
                        else if (convFsm != null && convFsm.Fsm != null && !string.Equals(st, "Talk Finish", StringComparison.OrdinalIgnoreCase))
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] Advancing stuck Conversation Control from '{st}' -> 'Talk Finish'.");
                            try { convFsm.Fsm.SetState("Talk Finish"); } catch { }
                        }
                    }

                    // If still in GG_Boss_Door_Entrance 3.5s later, force Talk Finish and yield before direct fallback
                    yield return new WaitForSeconds(3.5f);
                    if (token != _hkAudioRestoreToken) yield break;
                    if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase)) yield break;

                    string finalState = convFsm != null ? (convFsm.ActiveStateName ?? "") : "";
                    if (convFsm != null && convFsm.Fsm != null && !string.Equals(finalState, "Talk Finish", StringComparison.OrdinalIgnoreCase))
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] Forcing Conversation Control from '{finalState}' -> 'Talk Finish'.");
                        try { convFsm.Fsm.SetState("Talk Finish"); } catch { }
                        yield return new WaitForSeconds(1.5f);
                        if (token != _hkAudioRestoreToken) yield break;
                        if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase)) yield break;
                    }
                }
                else
                {
                    yield return new WaitForSeconds(2.5f);
                    if (token != _hkAudioRestoreToken) yield break;
                    if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Boss_Door_Entrance", StringComparison.OrdinalIgnoreCase)) yield break;
                }

                // Final direct fallback if Talk Finish did not start a transition (never double-fires if IsInSceneTransition is true)
                try
                {
                    var gm = GameManager.instance;
                    if (gm != null && !gm.IsInSceneTransition)
                    {
                        string toScene = null;
                        if (convFsm != null && convFsm.FsmVariables != null)
                        {
                            var toVar = convFsm.FsmVariables.FindFsmString("To Scene");
                            if (toVar != null) toScene = toVar.Value;
                        }
                        if (string.IsNullOrEmpty(toScene) && StaticVariableList.Exists("firstBossScene"))
                        {
                            toScene = StaticVariableList.GetValue<string>("firstBossScene");
                        }
                        if (!string.IsNullOrEmpty(toScene))
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] Direct transition fallback from GG_Boss_Door_Entrance -> '{toScene}'.");
                            gm.BeginSceneTransition(new GameManager.SceneLoadInfo
                            {
                                SceneName = toScene,
                                EntryGateName = "door_dreamEnter",
                                EntryDelay = 0f,
                                PreventCameraFadeOut = true,
                                WaitForSceneTransitionCameraFade = false
                            });
                        }
                    }
                }
                catch (Exception exEnt)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonEntranceFix] Fallback error: {exEnt.Message}");
                }
            }

            // Fix GG_Grimm Phase 1 stall in Native Pantheons, Hall of Gods Statues, and Journal fights:
            // Grimm Scene/FSM stalls at "Grimm Appear 4" and Grimm Boss/Control stalls at "HP Scale".
            if (string.Equals(sceneName, "GG_Grimm", StringComparison.OrdinalIgnoreCase) &&
                (SilksongMobileAPIPlugin.CfgFixGrimmPhase1Stall == null || SilksongMobileAPIPlugin.CfgFixGrimmPhase1Stall.Value))
            {
                yield return new WaitForSeconds(1.5f);
                if (token != _hkAudioRestoreToken) yield break;
                if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Grimm", StringComparison.OrdinalIgnoreCase)) yield break;

                try
                {
                    var active = SceneManager.GetActiveScene();
                    if (active.IsValid() && active.isLoaded)
                    {
                        var roots = active.GetRootGameObjects();
                        if (roots != null)
                        {
                            bool advancedAppear = false;
                            for (int r = 0; r < roots.Length && !advancedAppear; r++)
                            {
                                if (roots[r] == null) continue;
                                var fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
                                if (fsms == null) continue;
                                for (int i = 0; i < fsms.Length; i++)
                                {
                                    var f = fsms[i];
                                    if (f != null && f.gameObject != null && f.gameObject.activeInHierarchy &&
                                        string.Equals(f.gameObject.name, "Grimm Scene", StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(f.ActiveStateName, "Grimm Appear 4", StringComparison.OrdinalIgnoreCase))
                                    {
                                        f.SendEvent("FINISHED");
                                        SilksongMobileAPIPlugin.LogDiagnosticWarn("[GrimmPhase1Fix] Advanced stuck Grimm Scene/FSM ('Grimm Appear 4' -> 'Battle Start').");
                                        advancedAppear = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception exG1)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[GrimmPhase1Fix] Grimm Scene unstick error: {exG1.Message}");
                }

                yield return new WaitForSeconds(0.35f);
                if (token != _hkAudioRestoreToken) yield break;
                if (!string.Equals(SceneManager.GetActiveScene().name, "GG_Grimm", StringComparison.OrdinalIgnoreCase)) yield break;

                try
                {
                    var active = SceneManager.GetActiveScene();
                    if (active.IsValid() && active.isLoaded)
                    {
                        var roots = active.GetRootGameObjects();
                        if (roots != null)
                        {
                            string tierEv = Mathf.Clamp((PlayerData.instance?.nailUpgrades ?? 0) + 1, 1, 5).ToString();
                            bool advancedHp = false;
                            for (int r = 0; r < roots.Length && !advancedHp; r++)
                            {
                                if (roots[r] == null) continue;
                                var fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
                                if (fsms == null) continue;
                                for (int i = 0; i < fsms.Length; i++)
                                {
                                    var f = fsms[i];
                                    if (f != null && f.gameObject != null && f.gameObject.activeInHierarchy &&
                                        string.Equals(f.gameObject.name, "Grimm Boss", StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(f.FsmName, "Control", StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(f.ActiveStateName, "HP Scale", StringComparison.OrdinalIgnoreCase))
                                    {
                                        f.SendEvent(tierEv);
                                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[GrimmPhase1Fix] Advanced stuck Grimm Boss/Control ('HP Scale' -> Set {tierEv}) to start Phase 1.");
                                        advancedHp = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception exG2)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[GrimmPhase1Fix] Grimm Boss unstick error: {exG2.Message}");
                }
            }
        }

        private static FieldInfo _hkScenesLoadedField;
        private static bool _hkScenesLoadedFieldResolved;

        private static Dictionary<string, AssetBundle> GetHkScenesLoadedBundleMap()
        {
            if (!_hkScenesLoadedFieldResolved)
            {
                try
                {
                    var hkScenesType = HarmonyLib.AccessTools.TypeByName("HkCore.HkScenes");
                    if (hkScenesType != null)
                    {
                        _hkScenesLoadedField = hkScenesType.GetField("loaded", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        _hkScenesLoadedFieldResolved = true;
                    }
                }
                catch { }
            }
            if (_hkScenesLoadedField == null) return null;
            try { return _hkScenesLoadedField.GetValue(null) as Dictionary<string, AssetBundle>; }
            catch { return null; }
        }

        private static IEnumerator CoUnloadFinishedHkBossesAfterLoad(string currentSceneName, int token)
        {
            // Wait briefly if a scene transition is still finishing so we only unload AFTER the new boss has completely loaded.
            float waitStart = Time.realtimeSinceStartup;
            while (GameManager.instance != null && GameManager.instance.IsInSceneTransition && Time.realtimeSinceStartup - waitStart < 5f)
            {
                yield return new WaitForSeconds(0.25f);
                if (token != _hkAudioRestoreToken) yield break;
            }
            if (token != _hkAudioRestoreToken) yield break;
            if (GameManager.instance != null && GameManager.instance.IsInSceneTransition) yield break;
            if (!string.Equals(SceneManager.GetActiveScene().name, currentSceneName, StringComparison.OrdinalIgnoreCase)) yield break;

            // Step 1: Unload any leftover finished GG_* Unity scenes that are not the current active scene.
            var leftoverScenes = new List<Scene>();
            try
            {
                int scCount = SceneManager.sceneCount;
                for (int i = 0; i < scCount; i++)
                {
                    Scene sc = SceneManager.GetSceneAt(i);
                    if (!sc.IsValid() || !sc.isLoaded) continue;
                    string sName = sc.name ?? "";
                    if (string.Equals(sName, currentSceneName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sName.StartsWith("GG_", StringComparison.OrdinalIgnoreCase))
                        leftoverScenes.Add(sc);
                }
            }
            catch { }

            int unloadedUnityScenes = 0;
            for (int i = 0; i < leftoverScenes.Count; i++)
            {
                if (token != _hkAudioRestoreToken) yield break;
                if (GameManager.instance != null && GameManager.instance.IsInSceneTransition) yield break;
                AsyncOperation op = null;
                try
                {
                    if (leftoverScenes[i].IsValid() && leftoverScenes[i].isLoaded)
                        op = SceneManager.UnloadSceneAsync(leftoverScenes[i]);
                }
                catch { }
                if (op != null)
                {
                    unloadedUnityScenes++;
                    while (!op.isDone) yield return null;
                    if (token != _hkAudioRestoreToken) yield break;
                }
            }
            if (token != _hkAudioRestoreToken) yield break;
            if (GameManager.instance != null && GameManager.instance.IsInSceneTransition) yield break;

            // Step 2: Build set of all currently loaded or loading scene names so we NEVER unload an active/incoming scene bundle.
            var activeSceneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(currentSceneName)) activeSceneNames.Add(currentSceneName);
            try
            {
                int scCount = SceneManager.sceneCount;
                for (int i = 0; i < scCount; i++)
                {
                    Scene sc = SceneManager.GetSceneAt(i);
                    if (sc.IsValid() && !string.IsNullOrEmpty(sc.name))
                        activeSceneNames.Add(sc.name);
                }
            }
            catch { }

            // Step 3: Unload finished per-scene AssetBundles from HkCore.HkScenes.loaded using Unload(true)
            // (never touches HkCore.HkScenes.common, shared, or shaders).
            int unloadedBundles = 0;
            var unloadedNames = new List<string>();
            try
            {
                var loadedMap = GetHkScenesLoadedBundleMap();
                if (loadedMap != null && loadedMap.Count > 0)
                {
                    var staleKeys = new List<string>();
                    foreach (var kv in loadedMap)
                    {
                        string key = kv.Key;
                        if (string.IsNullOrEmpty(key) || activeSceneNames.Contains(key)) continue;
                        if (key.IndexOf("common", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("shared", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("shader", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        staleKeys.Add(key);
                    }
                    for (int i = 0; i < staleKeys.Count; i++)
                    {
                        string k = staleKeys[i];
                        if (loadedMap.TryGetValue(k, out AssetBundle bundle))
                        {
                            loadedMap.Remove(k);
                            if (bundle != null)
                            {
                                try { bundle.Unload(true); } catch { }
                            }
                            unloadedBundles++;
                            unloadedNames.Add(k);
                        }
                    }
                }
            }
            catch (Exception exUnl)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkBossUnloader] Bundle unload warning: {exUnl.Message}");
            }

            // Step 4: Free unreferenced runtime downscaled textures & boss assets asynchronously.
            if (unloadedUnityScenes > 0 || unloadedBundles > 0)
            {
                AsyncOperation unloadOp = null;
                try { unloadOp = Resources.UnloadUnusedAssets(); } catch { }
                if (unloadOp != null)
                {
                    while (!unloadOp.isDone) yield return null;
                    if (token != _hkAudioRestoreToken) yield break;
                }
                try { GC.Collect(0, GCCollectionMode.Optimized); } catch { }
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkBossUnloader] Freed {unloadedBundles} finished HK bundle(s) [{string.Join(", ", unloadedNames)}] & {unloadedUnityScenes} leftover scene(s) after '{currentSceneName}' loaded (managedMB={GC.GetTotalMemory(false) / (1024 * 1024)}).");
            }
        }

        private static PlayMakerFSM FindConversationControlInActiveScene()
        {
            try
            {
                var active = SceneManager.GetActiveScene();
                if (!active.IsValid() || !active.isLoaded) return null;
                var roots = active.GetRootGameObjects();
                if (roots == null) return null;
                for (int r = 0; r < roots.Length; r++)
                {
                    if (roots[r] == null) continue;
                    var fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
                    if (fsms == null) continue;
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        if (fsms[i] != null && string.Equals(fsms[i].FsmName, "Conversation Control", StringComparison.OrdinalIgnoreCase))
                            return fsms[i];
                    }
                }
            }
            catch { }
            return null;
        }

        private static void EnsureHornetRoarObjectVariableSafe()
        {
            try
            {
                var hc = HeroController.instance;
                if (hc == null) return;
                var roarFsm = PlayMakerFSM.FindFsmOnGameObject(hc.gameObject, "Roar and Wound States");
                var vars = roarFsm?.FsmVariables;
                if (vars == null || vars.FindFsmGameObject("Roar Object") != null) return;

                var existing = vars.GameObjectVariables;
                int len = existing != null ? existing.Length : 0;
                var expanded = new HutongGames.PlayMaker.FsmGameObject[len + 1];
                if (len > 0) Array.Copy(existing, expanded, len);
                var roarObj = new HutongGames.PlayMaker.FsmGameObject("Roar Object");
                expanded[len] = roarObj;
                vars.GameObjectVariables = expanded;
                vars.AddVariableLookup(roarObj);
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreRoarFix] Added missing 'Roar Object' FsmGameObject to Hornet 'Roar and Wound States' FSM (fixes GG_Atrium Pantheon door/cage/bindings unlock crash).");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreRoarFix] EnsureHornetRoarObjectVariableSafe warning: {ex.Message}");
            }
        }

        private static void DisableCrashingHkEnviroRegionFsms(Scene scene)
        {
            try
            {
                if (!scene.IsValid() || !scene.isLoaded) return;
                var roots = scene.GetRootGameObjects();
                if (roots == null) return;
                int disabled = 0;
                for (int r = 0; r < roots.Length; r++)
                {
                    var root = roots[r];
                    if (root == null) continue;
                    var fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
                    if (fsms == null) continue;
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        var f = fsms[i];
                        if (f != null && string.Equals(f.FsmName, "Enviro Region", StringComparison.OrdinalIgnoreCase))
                        {
                            f.enabled = false;
                            disabled++;
                        }
                    }
                }
                if (disabled > 0)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreEnviroGuard] Disabled {disabled} HK1 'Enviro Region' FSM(s) in '{scene.name}' (prevents GG_Atrium footstep-region SIGABRT).");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreEnviroGuard] DisableCrashingHkEnviroRegionFsms('{scene.name}') error: {ex.Message}");
            }
        }

        public static bool HkEnviroRegionListener_Refresh_SafePrefix(EnviroRegionListener __instance)
        {
            try
            {
                if (!IsHkCoreSceneKey(SceneManager.GetActiveScene().name, out _))
                    return true;
                if (__instance != null)
                {
                    var regions = __instance.insideRegions;
                    if (regions != null)
                    {
                        for (int i = regions.Count - 1; i >= 0; i--)
                        {
                            if (regions[i] == null) regions.RemoveAt(i);
                        }
                    }
                    var pd = PlayerData.instance;
                    if (pd != null)
                    {
                        __instance.currentEnvironmentType = pd.environmentType;
                    }
                }
                return false;
            }
            catch { }
            return false;
        }

        public static bool HkSurfaceWaterRegion_SetSpawnedColors_SafePrefix(object __instance)
        {
            try
            {
                if (IsHkCoreSceneKey(SceneManager.GetActiveScene().name, out _))
                    return false;
            }
            catch { }
            return true;
        }

        public static void EnsureHkMemoryGuardInstalled()
        {
            if (_hkSceneMemHooked) return;
            _hkSceneMemHooked = true;
            try
            {
                var swrType = HarmonyLib.AccessTools.TypeByName("SurfaceWaterRegion");
                if (swrType != null)
                {
                    var setColorsMi = swrType.GetMethod("SetSpawnedGameObjectColorsTemp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var preMi = typeof(AssetHelperBridge).GetMethod(nameof(HkSurfaceWaterRegion_SetSpawnedColors_SafePrefix), BindingFlags.Static | BindingFlags.Public);
                    if (setColorsMi != null && preMi != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("HkCoreWaterCrashGuard", setColorsMi, preMi, null, null, HarmonyLib.Priority.First);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreWaterGuard] Installed Priority.First HK1 guard on SurfaceWaterRegion::SetSpawnedGameObjectColorsTemp.");
                    }
                }
                var erlType = typeof(EnviroRegionListener);
                var refreshMi = erlType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                var preRefreshMi = typeof(AssetHelperBridge).GetMethod(nameof(HkEnviroRegionListener_Refresh_SafePrefix), BindingFlags.Static | BindingFlags.Public);
                if (refreshMi != null && preRefreshMi != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("HkCoreEnviroCrashGuard", refreshMi, preRefreshMi, null, null, HarmonyLib.Priority.First);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreEnviroGuard] Installed Priority.First HK1 guard on EnviroRegionListener::Refresh(bool).");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreWaterGuard] Hook install warning: {ex.Message}");
            }
            SceneManager.sceneLoaded += (Scene loaded, LoadSceneMode mode) =>
            {
                try
                {
                    if (IsHkCoreSceneKey(loaded.name, out _))
                    {
                        EnsureHornetRoarObjectVariableSafe();
                        DisableCrashingHkEnviroRegionFsms(loaded);
                        DownscaleOversizedHkSceneTextures(loaded);
                        int audioToken = ++_hkAudioRestoreToken;
                        if (SilksongMobileAPIPlugin.Instance != null)
                            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoRestoreHkSceneHornetAndAudio(loaded.name, audioToken));
                    }
                    EnsureHkCoreToolsUnlockedInPlayerData();
                }
                catch { }
            };
            SceneManager.sceneUnloaded += (Scene unloaded) =>
            {
                try
                {
                    if (IsHkCoreSceneKey(unloaded.name, out _) && !IsHkCoreSceneKey(SceneManager.GetActiveScene().name, out _))
                    {
                        _hkDownscaleToken++;
                        _hkAudioRestoreToken++;
                    }
                }
                catch { }
            };
            SceneManager.activeSceneChanged += (Scene prev, Scene next) =>
            {
                try
                {
                    bool nextIsHk = IsHkCoreSceneKey(next.name, out _);
                    if (nextIsHk)
                    {
                        if (_savedMipmapLimit < 0)
                            _savedMipmapLimit = QualitySettings.globalTextureMipmapLimit;
                        if (QualitySettings.globalTextureMipmapLimit < 1)
                            QualitySettings.globalTextureMipmapLimit = 1;
                    }
                    else if (_savedMipmapLimit >= 0)
                    {
                        QualitySettings.globalTextureMipmapLimit = _savedMipmapLimit;
                        _savedMipmapLimit = -1;
                    }
                }
                catch { }
            };
        }

        private static PlayerData _hkToolsUnlockedForPd;

        public static void EnsureHkCoreToolsUnlockedInPlayerData()
        {
            try
            {
                var pd = PlayerData.instance;
                if (pd == null || HeroController.instance == null || ReferenceEquals(pd, _hkToolsUnlockedForPd)) return;
                EnsureHkCoreTypesResolved();
                var hkAsm = _hkScenesType?.Assembly;
                if (hkAsm == null) return;

                var toolNames = new List<string>(2);
                var htType = hkAsm.GetType("HkCore.HkHeroTools", false);
                if (htType != null)
                {
                    const BindingFlags sbf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    if (htType.GetProperty("ShadeCloak", sbf)?.GetValue(null) is ToolItem sc && !string.IsNullOrEmpty(sc.name))
                        toolNames.Add(sc.name);
                    if (htType.GetProperty("CrystalHeart", sbf)?.GetValue(null) is ToolItem ch && !string.IsNullOrEmpty(ch.name))
                        toolNames.Add(ch.name);
                }
                if (toolNames.Count == 0)
                {
                    toolNames.Add("HkCore Shade Cloak");
                    toolNames.Add("HkCore Crystal Heart");
                }

                int unlocked = 0;
                for (int i = 0; i < toolNames.Count; i++)
                {
                    string tn = toolNames[i];
                    var data = pd.GetToolData(tn);
                    if (!data.IsUnlocked || data.IsHidden)
                    {
                        data.IsUnlocked = true;
                        data.IsHidden = false;
                        data.HasBeenSeen = true;
                        pd.SetToolData(tn, data);
                        unlocked++;
                    }
                }
                _hkToolsUnlockedForPd = pd;
                if (unlocked > 0)
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreTools] Unlocked {unlocked} HK1 tool(s) ({string.Join(", ", toolNames)}) in PlayerData for Crest equipping.");

                // Clean up Team Cherry placeholder Yoshi sprite on Silksong's unused Shadow Recharge / shadowRingPrefab (keeps dark particles + sound!)
                try
                {
                    int cleaned = 0;
                    void CleanShadowRingObj(GameObject go)
                    {
                        if (go == null) return;
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r == null || r is ParticleSystemRenderer) continue;
                            r.enabled = false;
                            cleaned++;
                        }
                    }
                    var hc = HeroController.instance;
                    if (hc != null)
                    {
                        foreach (var tr in hc.GetComponentsInChildren<Transform>(true))
                        {
                            if (tr == null) continue;
                            string n = tr.name ?? "";
                            if (n.IndexOf("Shadow Recharge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("ShadowRing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("Shadow Ring", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                CleanShadowRingObj(tr.gameObject);
                            }
                        }
                        const BindingFlags hbf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        foreach (var fi in typeof(HeroController).GetFields(hbf))
                        {
                            if (fi.Name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                (fi.Name.IndexOf("Recharge", StringComparison.OrdinalIgnoreCase) >= 0 || fi.Name.IndexOf("Ring", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                if (fi.GetValue(hc) is GameObject pref) CleanShadowRingObj(pref);
                            }
                        }
                    }
                    // Also check HkCore.HkHeroFx cached shadowRingPrefab if HkCore loaded one
                    var fxType = hkAsm.GetType("HkCore.HkHeroFx", false);
                    if (fxType != null)
                    {
                        const BindingFlags sbf2 = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                        foreach (var fi in fxType.GetFields(sbf2))
                        {
                            if (fi.Name.IndexOf("shadowRing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fi.Name.IndexOf("Recharge", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (fi.GetValue(null) is GameObject pref) CleanShadowRingObj(pref);
                            }
                        }
                    }
                    if (cleaned > 0)
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreTools] Disabled {cleaned} placeholder Shadow Recharge sprite renderer(s) (keeps shadow particles & audio).");
                }
                catch { }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreTools] Unlock warning: {ex.Message}");
            }
        }

        public static void TrimMemoryForHkTransition(string targetScene)
        {
            try
            {
                EnsureHkMemoryGuardInstalled();
                if (_savedMipmapLimit < 0)
                    _savedMipmapLimit = QualitySettings.globalTextureMipmapLimit;
                if (QualitySettings.globalTextureMipmapLimit < 1)
                    QualitySettings.globalTextureMipmapLimit = 1;
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreMemGuard] Prepared mipmapLimit=1 before '{targetScene}' (managedMB={GC.GetTotalMemory(false) / (1024 * 1024)}).");
            }
            catch { }
        }

        private static bool _hkBossChallengeHookInstalled;
        private static FieldInfo _bcuiBossStatueFi;
        private static FieldInfo _bcuiOnCancelFi;
        private static FieldInfo _bcuiOnLevelSelectedFi;

        public static void EnsureHkCoreBossChallengeHookInstalled()
        {
            if (_hkBossChallengeHookInstalled) return;
            try
            {
                var loadBossMi = typeof(BossChallengeUI).GetMethod("LoadBoss", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
                var preMi = typeof(AssetHelperBridge).GetMethod(nameof(BossChallengeUI_LoadBoss_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (loadBossMi != null && preMi != null)
                {
                    _bcuiBossStatueFi = typeof(BossChallengeUI).GetField("bossStatue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _bcuiOnCancelFi = typeof(BossChallengeUI).GetField("OnCancel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _bcuiOnLevelSelectedFi = typeof(BossChallengeUI).GetField("OnLevelSelected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.HkCoreBossChallenge", loadBossMi, preMi, null, null, 0, "SilksongMobileAPI");
                    _hkBossChallengeHookInstalled = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreSceneBridge] Installed BossChallengeUI.LoadBoss(int, bool) bridge for HK1 boss statues.");
                    EnsureDirectHkBenchReturnHookInstalled();
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] BossChallengeUI hook failed: {ex.Message}");
            }
        }

        public static bool BossChallengeUI_LoadBoss_Prefix(BossChallengeUI __instance, int level, bool doHideAnim)
        {
            try
            {
                if (__instance == null) return true;
                var statue = _bcuiBossStatueFi?.GetValue(__instance) as BossStatue;
                if (statue == null) return true;
                BossScene bossScene = statue.UsingDreamVersion ? statue.dreamBossScene : statue.bossScene;
                if (bossScene == null) return true;
                string sceneName = bossScene.sceneName;
                if (level == 0) sceneName = bossScene.Tier1Scene;
                else if (level == 1) sceneName = bossScene.Tier2Scene;
                else if (level == 2) sceneName = bossScene.Tier3Scene;

                if (string.IsNullOrEmpty(sceneName) || !IsHkCoreSceneKey(sceneName, out _))
                    return true;

                EnsureHkCoreAddressablesSceneBridgeRegistered();
                TrimMemoryForHkTransition(sceneName);
                StaticVariableList.SetValue("bossSceneToLoad", sceneName, 0);
                _bcuiOnCancelFi?.SetValue(__instance, null);
                __instance.Hide(doHideAnim);

                var gm = GameManager.instance;
                if (gm != null && gm.playerData != null && statue.dreamReturnGate != null)
                    gm.playerData.bossReturnEntryGate = statue.dreamReturnGate.name;

                // Ensure statue fights never inherit a stale direct-journal session flag
                _directHkSessionActive = false;
                _activePantheonScenes = null;
                _activePantheonIndex = -1;
                EnsureDirectHkBenchReturnHookInstalled();

                int capturedLevel = level;
                string capturedKey = statue.UsingDreamVersion ? statue.dreamStatueStatePD : statue.statueStatePD;
                string capturedReturnGate = statue.dreamReturnGate != null ? statue.dreamReturnGate.name : "door_dreamReturn";
                string capturedSceneName = sceneName;
                int statueToken = ++_statueFightWatchdogToken;

                BossSceneController.SetupEvent = (BossSceneController self) =>
                {
                    if (self == null) return;
                    self.BossLevel = capturedLevel;
                    self.DreamReturnEvent = "DREAM RETURN";
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkStatueBridge] SetupEvent fired for '{capturedSceneName}' (level={capturedLevel}, key='{capturedKey}', returnGate='{capturedReturnGate}', bosses={self.bosses?.Length ?? 0}).");
                    self.OnBossesDead += () =>
                    {
                        try
                        {
                            var gmNow = GameManager.instance;
                            if (gmNow != null && !string.IsNullOrEmpty(capturedKey))
                            {
                                var comp = gmNow.GetPlayerDataVariable<BossStatue.Completion>(capturedKey);
                                if (capturedLevel == 0) comp.completedTier1 = true;
                                else if (capturedLevel == 1) comp.completedTier2 = true;
                                else if (capturedLevel == 2) comp.completedTier3 = true;
                                gmNow.SetPlayerDataVariable(capturedKey, comp);
                                if (gmNow.playerData != null)
                                {
                                    gmNow.playerData.currentBossStatueCompletionKey = capturedKey;
                                    gmNow.playerData.bossStatueTargetLevel = capturedLevel;
                                }
                            }
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkStatueBridge] OnBossesDead saved completion for '{capturedSceneName}' (key='{capturedKey}', level={capturedLevel}).");
                        }
                        catch (Exception exDead)
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkStatueBridge] OnBossesDead error: {exDead.Message}");
                        }
                    };
                    self.OnBossSceneComplete += () =>
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkStatueBridge] OnBossSceneComplete fired for '{capturedSceneName}' -> calling DoDreamReturn() + 3.5s fallback.");
                        try { self.DoDreamReturn(); } catch (Exception exDr) { SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkStatueBridge] DoDreamReturn error: {exDr.Message}"); }
                        if (SilksongMobileAPIPlugin.Instance != null)
                            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoEnsureStatueDreamReturnFallback(capturedSceneName, capturedReturnGate, statueToken));
                    };
                };

                if (SilksongMobileAPIPlugin.Instance != null)
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoWatchStatueBossEnd(capturedSceneName, capturedReturnGate, statueToken));

                var onSelected = _bcuiOnLevelSelectedFi?.GetValue(__instance) as Delegate;
                onSelected?.DynamicInvoke();
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Started HK1 boss challenge '{sceneName}' (level={level}, returnGate='{capturedReturnGate}') via BossChallengeUI bridge.");
                return false;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] BossChallengeUI_LoadBoss_Prefix error: {ex.Message}");
                return true;
            }
        }

        private static int _statueFightWatchdogToken;

        private static bool IsGodhomeHubScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return string.Equals(sceneName, "GG_Atrium", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "GG_Workshop", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sceneName, "GG_Atrium_Roof", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerator CoEnsureStatueDreamReturnFallback(string bossSceneName, string returnGate, int token)
        {
            yield return new WaitForSecondsRealtime(3.5f);
            if (_statueFightWatchdogToken != token || _directHkSessionActive) yield break;
            string cur = SceneManager.GetActiveScene().name;
            if (!string.Equals(cur, bossSceneName, StringComparison.OrdinalIgnoreCase)) yield break;
            SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkStatueBridge] Dream Return FSM stalled in '{bossSceneName}' after OnBossSceneComplete -> forcing transition to GG_Workshop (gate='{returnGate}').");
            var gm = GameManager.instance;
            if (gm != null)
            {
                gm.BeginSceneTransition(new GameManager.SceneLoadInfo
                {
                    SceneName = "GG_Workshop",
                    EntryGateName = string.IsNullOrEmpty(returnGate) ? "door_dreamReturn" : returnGate,
                    HeroLeaveDirection = GlobalEnums.GatePosition.door,
                    WaitForSceneTransitionCameraFade = true,
                    PreventCameraFadeOut = false,
                    AlwaysUnloadUnusedAssets = true
                });
            }
        }

        private static IEnumerator CoWatchStatueBossEnd(string bossSceneName, string returnGate, int token)
        {
            // Wait up to 20s for bossSceneName to become active
            float waitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - waitStart < 20f)
            {
                if (_statueFightWatchdogToken != token || _directHkSessionActive) yield break;
                if (string.Equals(SceneManager.GetActiveScene().name, bossSceneName, StringComparison.OrdinalIgnoreCase))
                    break;
                yield return new WaitForSecondsRealtime(0.4f);
            }
            if (!string.Equals(SceneManager.GetActiveScene().name, bossSceneName, StringComparison.OrdinalIgnoreCase))
                yield break;

            // Give boss scene 3s to spawn bosses
            yield return new WaitForSecondsRealtime(3.0f);
            bool sawAliveBoss = false;
            float zeroSince = -1f;
            var waitHalf = new WaitForSecondsRealtime(0.5f);

            while (_statueFightWatchdogToken == token && !_directHkSessionActive)
            {
                if (!string.Equals(SceneManager.GetActiveScene().name, bossSceneName, StringComparison.OrdinalIgnoreCase))
                    yield break;
                var hc = HeroController.SilentInstance;
                if (hc == null || hc.cState.dead) yield break;

                int aliveCount = 0;
                var bsc = BossSceneController.Instance;
                if (bsc != null && bsc.bosses != null && bsc.bosses.Length > 0)
                {
                    for (int i = 0; i < bsc.bosses.Length; i++)
                    {
                        var hm = bsc.bosses[i];
                        if (hm != null && !hm.isDead && hm.hp > 0) aliveCount++;
                    }
                }
                else
                {
                    var allHms = UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    for (int i = 0; i < allHms.Length; i++)
                    {
                        var hm = allHms[i];
                        if (hm != null && !hm.isDead && hm.hp > 0 && hm.gameObject.activeInHierarchy)
                            aliveCount++;
                    }
                }

                if (aliveCount > 0)
                {
                    sawAliveBoss = true;
                    zeroSince = -1f;
                }
                else if (sawAliveBoss)
                {
                    if (zeroSince < 0f) zeroSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - zeroSince >= 2.5f)
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkStatueBridge] All bosses dead in '{bossSceneName}' for 2.5s without scene ending (bsc={(bsc != null ? "present" : "null")}) -> triggering EndBossScene/GG_Workshop return.");
                        if (bsc != null)
                        {
                            try { bsc.EndBossScene(); } catch { }
                            yield return new WaitForSecondsRealtime(3.5f);
                        }
                        if (string.Equals(SceneManager.GetActiveScene().name, bossSceneName, StringComparison.OrdinalIgnoreCase) &&
                            _statueFightWatchdogToken == token && !_directHkSessionActive)
                        {
                            var gm = GameManager.instance;
                            if (gm != null)
                            {
                                gm.BeginSceneTransition(new GameManager.SceneLoadInfo
                                {
                                    SceneName = "GG_Workshop",
                                    EntryGateName = string.IsNullOrEmpty(returnGate) ? "door_dreamReturn" : returnGate,
                                    HeroLeaveDirection = GlobalEnums.GatePosition.door,
                                    WaitForSceneTransitionCameraFade = true,
                                    PreventCameraFadeOut = false,
                                    AlwaysUnloadUnusedAssets = true
                                });
                            }
                        }
                        yield break;
                    }
                }
                yield return waitHalf;
            }
        }

        private static bool _directHkSessionActive;
        private static bool _directHkBenchHookInstalled;
        private static string _savedBenchScene;
        private static string _savedBenchMarker;
        private static int _savedBenchType;
        private static string _directHkReturnScene;
        private static string _directHkReturnGate;

        // Active Pantheon queue (1 boss scene in RAM at a time)
        private static string _activePantheonName;
        private static string[] _activePantheonScenes;
        private static int _activePantheonIndex = -1;
        private static int _activePantheonLevel = 0;

        // Bindings bitmask: 1=Nail, 2=Shell, 4=Charms/Tools, 8=Soul/Silk
        public static bool HkBindNail;
        public static bool HkBindShell;
        public static bool HkBindTools;
        public static bool HkBindSilk;

        private static BossSequenceController.ChallengeBindings GetSelectedChallengeBindings()
        {
            var b = BossSequenceController.ChallengeBindings.None;
            if (HkBindNail) b |= BossSequenceController.ChallengeBindings.Nail;
            if (HkBindShell) b |= BossSequenceController.ChallengeBindings.Shell;
            if (HkBindTools) b |= BossSequenceController.ChallengeBindings.Charms;
            if (HkBindSilk) b |= BossSequenceController.ChallengeBindings.Soul;
            return b;
        }

        private static bool _hkBindingsApplied;
        private static int _savedBindMaxHealth;
        private static int _savedBindMaxHealthBase;
        private static int _savedBindSilkMax;
        private static int _savedBindNailUpgrades;

        private static void ApplyDirectHkBindingsIfAny()
        {
            try
            {
                if (_hkBindingsApplied) return;
                if (!HkBindNail && !HkBindShell && !HkBindTools && !HkBindSilk) return;
                var pd = PlayerData.instance;
                if (pd == null) return;

                // Apply bindings directly via Silksong PlayerData (exact PantheonOfPharloom pattern)
                // NEVER write BossSequenceController.currentData when currentSequence==null, or BoundMaxHealth() throws NRE -> SIGABRT!
                _savedBindMaxHealth = pd.maxHealth;
                _savedBindMaxHealthBase = pd.maxHealthBase;
                _savedBindSilkMax = pd.silkMax;
                _savedBindNailUpgrades = pd.nailUpgrades;
                _hkBindingsApplied = true;

                if (HkBindShell)
                {
                    pd.maxHealth = 4;
                    pd.maxHealthBase = 4;
                    pd.health = Mathf.Min(4, pd.health);
                    EventRegister.SendEvent("HEALTH UPDATE", null);
                }
                if (HkBindSilk)
                {
                    pd.silkMax = 9;
                    pd.silk = Mathf.Min(9, pd.silk);
                    EventRegister.SendEvent("REGENERATED SILK CHUNK", null);
                }
                if (HkBindNail)
                {
                    pd.nailUpgrades = 0;
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Applied native bindings (Nail={HkBindNail}, Shell={HkBindShell}, Tools={HkBindTools}, Silk={HkBindSilk}).");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] ApplyDirectHkBindings warning: {ex.Message}");
            }
        }

        private static void RestoreDirectHkBindings()
        {
            try
            {
                if (!_hkBindingsApplied) return;
                _hkBindingsApplied = false;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    if (_savedBindMaxHealth > 0)
                    {
                        pd.maxHealth = _savedBindMaxHealth;
                        pd.maxHealthBase = _savedBindMaxHealthBase > 0 ? _savedBindMaxHealthBase : _savedBindMaxHealth;
                        pd.health = pd.maxHealth;
                        EventRegister.SendEvent("HEALTH UPDATE", null);
                    }
                    if (_savedBindSilkMax > 0)
                    {
                        pd.silkMax = _savedBindSilkMax;
                        EventRegister.SendEvent("REGENERATED SILK CHUNK", null);
                    }
                    pd.nailUpgrades = _savedBindNailUpgrades;
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreSceneBridge] Restored PlayerData stats after HK1 bindings.");
            }
            catch { }
        }

        private static void CapturePlayerBenchIfOutsideHk()
        {
            var pd = PlayerData.instance;
            string curScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            if (!string.IsNullOrEmpty(curScene) && !IsHkCoreSceneKey(curScene, out _))
            {
                _directHkReturnScene = curScene;
                _directHkReturnGate = !string.IsNullOrEmpty(GameManager.instance?.entryGateName) ? GameManager.instance.entryGateName : "left1";
                if (pd != null && !string.IsNullOrEmpty(pd.respawnScene) && !IsHkCoreSceneKey(pd.respawnScene, out _))
                {
                    _savedBenchScene = pd.respawnScene;
                    _savedBenchMarker = pd.respawnMarkerName;
                    _savedBenchType = pd.respawnType;
                }
                else
                {
                    _savedBenchScene = curScene;
                    _savedBenchMarker = _directHkReturnGate;
                    _savedBenchType = 0;
                }
            }
        }

        private static void RestoreSavedBenchToPlayerData()
        {
            var pd = PlayerData.instance;
            if (pd != null && !string.IsNullOrEmpty(_savedBenchScene))
            {
                pd.respawnScene = _savedBenchScene;
                if (!string.IsNullOrEmpty(_savedBenchMarker))
                    pd.respawnMarkerName = _savedBenchMarker;
                pd.respawnType = _savedBenchType;
                pd.tempRespawnScene = "";
                pd.tempRespawnMarker = "";
            }
        }

        private static float _directHkBenchReturnUntilRealtime;

        public static void EnsureDirectHkBenchReturnHookInstalled()
        {
            if (_directHkBenchHookInstalled) return;
            try
            {
                var bstMi = typeof(GameManager).GetMethod("BeginSceneTransition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameManager.SceneLoadInfo) }, null);
                var preMi = typeof(AssetHelperBridge).GetMethod(nameof(DirectHk_BeginSceneTransition_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (bstMi != null && preMi != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.DirectHkBenchReturn", bstMi, preMi, null, null, 1000, "SilksongMobileAPI");
                }
                var rfrMi = typeof(GameManager).GetMethod("ReadyForRespawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                var rfrPreMi = typeof(AssetHelperBridge).GetMethod(nameof(DirectHk_ReadyForRespawn_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (rfrMi != null && rfrPreMi != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.DirectHkReadyForRespawn", rfrMi, rfrPreMi, null, null, 1000, "SilksongMobileAPI");
                }
                var ddrMi = typeof(BossSceneController).GetMethod("DoDreamReturn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                var ddrPreMi = typeof(AssetHelperBridge).GetMethod(nameof(DirectHk_DoDreamReturn_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (ddrMi != null && ddrPreMi != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.DirectHkDoDreamReturn", ddrMi, ddrPreMi, null, null, 1000, "SilksongMobileAPI");
                }
                // Issue 1 fix: Allow Godhome hub benches (GG_Atrium, GG_Workshop, GG_Atrium_Roof) to persist in PlayerData across save & quit
                var sbrPre1 = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeBench_SetBenchRespawnMarker_Prefix), BindingFlags.Static | BindingFlags.Public);
                var sbrPre2 = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeBench_SetBenchRespawnStrBool_Prefix), BindingFlags.Static | BindingFlags.Public);
                var sbrPre3 = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeBench_SetBenchRespawnStrIntBool_Prefix), BindingFlags.Static | BindingFlags.Public);
                var sbr1 = typeof(PlayerData).GetMethod("SetBenchRespawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(RespawnMarker), typeof(string), typeof(int) }, null);
                var sbr2 = typeof(PlayerData).GetMethod("SetBenchRespawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string), typeof(bool) }, null);
                var sbr3 = typeof(PlayerData).GetMethod("SetBenchRespawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string), typeof(int), typeof(bool) }, null);
                if (sbr1 != null && sbrPre1 != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeBenchRespawn", sbr1, sbrPre1, null, null, 1000, "SilksongMobileAPI");
                if (sbr2 != null && sbrPre2 != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeBenchRespawn", sbr2, sbrPre2, null, null, 1000, "SilksongMobileAPI");
                if (sbr3 != null && sbrPre3 != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeBenchRespawn", sbr3, sbrPre3, null, null, 1000, "SilksongMobileAPI");

                // HK1 benches in GG_Workshop/GG_Atrium call GameManager.SetDeathRespawnSimple (blocked by HkCore) and GameManager.SaveGame
                var sdrsMi = typeof(GameManager).GetMethod("SetDeathRespawnSimple", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                var sdrsPre = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeBench_SetDeathRespawnSimple_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (sdrsMi != null && sdrsPre != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeBenchRespawn", sdrsMi, sdrsPre, null, null, 1000, "SilksongMobileAPI");

                var sgMi = typeof(GameManager).GetMethod("SaveGame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(Action<bool>), typeof(bool), typeof(AutoSaveName) }, null);
                var sgPre = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeBench_SaveGame_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (sgMi != null && sgPre != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeBenchRespawn", sgMi, sgPre, null, null, 1000, "SilksongMobileAPI");

                // Optional Godhome inventory diagnostic breadcrumbs (only installed when EnableVerboseApiLogging=true)
                if (SilksongMobileAPIPlugin.CfgEnableVerboseApiLogging != null && SilksongMobileAPIPlugin.CfgEnableVerboseApiLogging.Value)
                {
                    var invOpenMi = typeof(GameManager).GetMethod("SetIsInventoryOpen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                    var invOpenPre = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeInvDiag_SetIsInventoryOpen_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (invOpenMi != null && invOpenPre != null)
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeInvDiag", invOpenMi, invOpenPre, null, null, 1000, "SilksongMobileAPI");

                    var getPaneMi = typeof(InventoryPaneList).GetMethod("GetPane", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(InventoryPaneList.PaneTypes) }, null);
                    var getPanePre = typeof(AssetHelperBridge).GetMethod(nameof(GodhomeInvDiag_GetPane_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (getPaneMi != null && getPanePre != null)
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.GodhomeInvDiag", getPaneMi, getPanePre, null, null, 1000, "SilksongMobileAPI");
                }

                _directHkBenchHookInstalled = true;
            }
            catch { }
        }

        private static void PersistGodhomeBenchIfAllowed(PlayerData pd, string sceneName, string markerName, int respawnType)
        {
            if (pd == null || _directHkSessionActive) return;
            if (SilksongMobileAPIPlugin.CfgAllowGodhomeBenchRespawn != null && !SilksongMobileAPIPlugin.CfgAllowGodhomeBenchRespawn.Value)
                return;
            if (!IsGodhomeHubScene(sceneName)) return;
            pd.respawnScene = sceneName;
            pd.respawnMarkerName = string.IsNullOrEmpty(markerName) ? "RestBench (1)" : markerName;
            pd.respawnType = respawnType;
            pd.tempRespawnScene = string.Empty;
            pd.tempRespawnMarker = string.Empty;
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[GodhomeBench] Persisted Godhome hub bench respawn -> scene='{pd.respawnScene}', marker='{pd.respawnMarkerName}', type={pd.respawnType}.");
        }

        public static bool GodhomeBench_SetBenchRespawnMarker_Prefix(PlayerData __instance, RespawnMarker spawnMarker, string sceneName, int spawnType)
        {
            try
            {
                string mName = spawnMarker != null ? spawnMarker.name : "RestBench (1)";
                PersistGodhomeBenchIfAllowed(__instance, sceneName, mName, spawnType);
            }
            catch { }
            return true;
        }

        public static bool GodhomeBench_SetBenchRespawnStrBool_Prefix(PlayerData __instance, string spawnMarker, string sceneName, bool facingRight)
        {
            try { PersistGodhomeBenchIfAllowed(__instance, sceneName, spawnMarker, 1); } catch { }
            return true;
        }

        public static bool GodhomeBench_SetBenchRespawnStrIntBool_Prefix(PlayerData __instance, string spawnMarker, string sceneName, int spawnType, bool facingRight)
        {
            try { PersistGodhomeBenchIfAllowed(__instance, sceneName, spawnMarker, spawnType); } catch { }
            return true;
        }

        public static bool GodhomeBench_SetDeathRespawnSimple_Prefix(GameManager __instance, string respawnMarkerName, int respawnType, bool respawnFacingRight)
        {
            try
            {
                if (__instance != null && __instance.playerData != null)
                {
                    string sc = !string.IsNullOrEmpty(__instance.sceneName) ? __instance.sceneName : SceneManager.GetActiveScene().name;
                    PersistGodhomeBenchIfAllowed(__instance.playerData, sc, respawnMarkerName, respawnType);
                }
            }
            catch { }
            return true;
        }

        public static bool GodhomeBench_SaveGame_Prefix(GameManager __instance, int saveSlot, Action<bool> ogCallback, bool withAutoSave, AutoSaveName autoSaveName)
        {
            try
            {
                var pd = __instance != null ? __instance.playerData : PlayerData.instance;
                if (pd != null && pd.atBench)
                {
                    string sc = SceneManager.GetActiveScene().name;
                    if (IsGodhomeHubScene(sc))
                    {
                        string marker = (IsGodhomeHubScene(pd.respawnScene) && !string.IsNullOrEmpty(pd.respawnMarkerName))
                            ? pd.respawnMarkerName
                            : "RestBench (1)";
                        PersistGodhomeBenchIfAllowed(pd, sc, marker, 1);
                    }
                }
            }
            catch { }
            return true;
        }

        public static bool GodhomeInvDiag_SetIsInventoryOpen_Prefix(GameManager __instance, bool value)
        {
            try
            {
                string sc = SceneManager.GetActiveScene().name;
                if (IsHkCoreSceneKey(sc, out _))
                {
                    var pd = __instance != null ? __instance.playerData : PlayerData.instance;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[InvDiag] SetIsInventoryOpen({value}) scene='{sc}' atBench={pd?.atBench} mapZone={pd?.mapZone} directHk={_directHkSessionActive}");
                    SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                }
            }
            catch { }
            return true;
        }

        public static bool GodhomeInvDiag_GetPane_Prefix(InventoryPaneList __instance, InventoryPaneList.PaneTypes paneTypes)
        {
            try
            {
                string sc = SceneManager.GetActiveScene().name;
                if (IsHkCoreSceneKey(sc, out _))
                {
                    int paneCount = __instance != null && __instance.panes != null ? __instance.panes.Length : -1;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[InvDiag] InventoryPaneList.GetPane({paneTypes}) scene='{sc}' paneCount={paneCount}");
                    SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                }
            }
            catch { }
            return true;
        }

        public static bool DirectHk_ReadyForRespawn_Prefix(GameManager __instance, bool isFirstLevelForPlayer)
        {
            try
            {
                var pd = __instance != null ? __instance.playerData : PlayerData.instance;

                // Issue 1: Loading a saved game whose bench is in a Godhome hub -> ensure HK Addressables scene bridge is active first!
                if (isFirstLevelForPlayer && pd != null && IsGodhomeHubScene(pd.respawnScene))
                {
                    EnsureHkCoreAddressablesSceneBridgeRegistered();
                    TrimMemoryForHkTransition(pd.respawnScene);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[GodhomeBench] ContinueGame into Godhome hub '{pd.respawnScene}' (marker='{pd.respawnMarkerName}').");
                    return true;
                }

                // Journal-launched direct HK fight -> return to recent Silksong bench (only when fought through journal!)
                if (_directHkSessionActive)
                {
                    RestoreSavedBenchToPlayerData();
                    RestoreDirectHkBindings();
                    _directHkSessionActive = false;
                    _activePantheonScenes = null;
                    _activePantheonIndex = -1;
                    _directHkBenchReturnUntilRealtime = Time.realtimeSinceStartup + 8f;
                    if (__instance != null) __instance.RespawningHero = true;
                    if (SilksongMobileAPIPlugin.Instance != null && !string.IsNullOrEmpty(_savedBenchScene))
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(EnsureBenchReturnFadeInCoroutine(_savedBenchScene));
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] DirectHk ReadyForRespawn -> recent bench '{_savedBenchScene}' (marker='{_savedBenchMarker}').");
                    return true;
                }

                // Issue 3: Dying in a Godhome Pantheon or Statue boss fight (NOT launched via Journal) -> respawn at Pantheon door / Statue (nonLethalRespawnScene), NOT Silksong bench!
                if (!isFirstLevelForPlayer && pd != null)
                {
                    string activeScene = SceneManager.GetActiveScene().name;
                    bool inHkNonHub = IsHkCoreSceneKey(activeScene, out _) && !IsGodhomeHubScene(activeScene);
                    if (inHkNonHub && IsGodhomeHubScene(pd.nonLethalRespawnScene))
                    {
                        string targetHub = pd.nonLethalRespawnScene;
                        string targetMarker = !string.IsNullOrEmpty(pd.nonLethalRespawnMarker) ? pd.nonLethalRespawnMarker : "Death Respawn Marker";
                        EnsureHkCoreAddressablesSceneBridgeRegistered();
                        __instance.RespawningHero = true;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[GodhomeRespawn] Death in '{activeScene}' (non-journal) -> routing ReadyForRespawn to '{targetHub}' marker='{targetMarker}' (bossReturnEntryGate='{pd.bossReturnEntryGate}').");
                        __instance.BeginSceneTransition(new GameManager.SceneLoadInfo
                        {
                            PreventCameraFadeOut = true,
                            WaitForSceneTransitionCameraFade = false,
                            EntryGateName = targetMarker,
                            SceneName = targetHub,
                            Visualization = GameManager.SceneLoadVisualizations.Default,
                            AlwaysUnloadUnusedAssets = true,
                            IsFirstLevelForPlayer = false
                        });
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[GodhomeRespawn] ReadyForRespawn_Prefix error: {ex.Message}");
            }
            return true;
        }

        public static bool DirectHk_BeginSceneTransition_Prefix(GameManager __instance, GameManager.SceneLoadInfo info)
        {
            try
            {
                // Pantheon low-RAM cleanup: force Unity to unload previous boss assets on every GG_* transition
                // so 15+ consecutive Pantheon bosses do not accumulate in RAM and trigger Android LMK OOM kills.
                if (info != null && (SilksongMobileAPIPlugin.CfgPantheonLowMemoryCleanup == null || SilksongMobileAPIPlugin.CfgPantheonLowMemoryCleanup.Value))
                {
                    string curScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                    string dstScene = info.SceneName ?? "";
                    if (curScene.StartsWith("GG_", StringComparison.OrdinalIgnoreCase) ||
                        dstScene.StartsWith("GG_", StringComparison.OrdinalIgnoreCase))
                    {
                        info.AlwaysUnloadUnusedAssets = true;
                    }
                }

                // Fix Pantheon completion infinite loading screen: BossDoorChallengeCompleteUI leaves GG_End_Sequence
                // with SceneName = "" (empty dreamReturnScene) or "GG_Unlock" (no gg_unlock.bundle in HkBossLoader).
                if (info != null && (SilksongMobileAPIPlugin.CfgFixPantheonEndSequenceReturn == null || SilksongMobileAPIPlugin.CfgFixPantheonEndSequenceReturn.Value))
                {
                    string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    bool leavingEndSeq = string.Equals(activeScene, "GG_End_Sequence", StringComparison.OrdinalIgnoreCase);
                    bool badTarget = string.IsNullOrEmpty(info.SceneName) || string.Equals(info.SceneName, "GG_Unlock", StringComparison.OrdinalIgnoreCase);
                    if ((leavingEndSeq && badTarget) || string.Equals(info.SceneName, "GG_Unlock", StringComparison.OrdinalIgnoreCase))
                    {
                        var pd = PlayerData.instance;
                        string targetHub = "GG_Atrium";
                        if (pd != null && (string.Equals(pd.nonLethalRespawnScene, "GG_Atrium", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(pd.nonLethalRespawnScene, "GG_Atrium_Roof", StringComparison.OrdinalIgnoreCase)))
                        {
                            targetHub = pd.nonLethalRespawnScene;
                        }
                        else
                        {
                            string gateHint = !string.IsNullOrEmpty(info.EntryGateName) ? info.EntryGateName : (pd?.bossReturnEntryGate ?? string.Empty);
                            if (gateHint.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                gateHint.IndexOf("Door_5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                gateHint.IndexOf("Tier5", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                targetHub = "GG_Atrium_Roof";
                            }
                        }

                        string origTarget = info.SceneName ?? "<empty>";
                        info.SceneName = targetHub;
                        info.SceneResourceLocation = null;
                        if (pd != null) pd.dreamReturnScene = targetHub;
                        if (string.IsNullOrEmpty(info.EntryGateName))
                        {
                            info.EntryGateName = (pd != null && !string.IsNullOrEmpty(pd.bossReturnEntryGate))
                                ? pd.bossReturnEntryGate
                                : "door_dreamEnter";
                        }
                        try
                        {
                            if (__instance != null)
                                __instance.SaveGame((Action<bool>)null);
                        }
                        catch { }
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PantheonEndFix] Redirected {activeScene} -> '{origTarget}' to '{targetHub}' (gate='{info.EntryGateName}') & saved completion.");
                    }
                }

                bool returning = _directHkSessionActive || Time.realtimeSinceStartup < _directHkBenchReturnUntilRealtime;
                if (!returning || info == null || string.IsNullOrEmpty(info.SceneName)) return true;
                // When dying or exiting a direct-launched HK1 boss/pantheon, HkCore/Godhome tries to send Hornet to GG_Atrium or GG_Workshop (which OOM-kills 6 GB phones).
                // Redirect straight back to the player's recent Silksong bench using native RespawningHero=true + empty EntryGateName (RestBench is a RespawnMarker, not a TransitionPoint!).
                if (string.Equals(info.SceneName, "GG_Atrium", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.SceneName, "GG_Workshop", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.SceneName, "GG_Atrium_Roof", StringComparison.OrdinalIgnoreCase))
                {
                    string targetBenchScene = !string.IsNullOrEmpty(_savedBenchScene) ? _savedBenchScene : _directHkReturnScene;
                    if (!string.IsNullOrEmpty(targetBenchScene))
                    {
                        RestoreSavedBenchToPlayerData();
                        RestoreDirectHkBindings();
                        _directHkSessionActive = false;
                        _activePantheonScenes = null;
                        _activePantheonIndex = -1;
                        _directHkBenchReturnUntilRealtime = Time.realtimeSinceStartup + 8f;
                        string rScene = targetBenchScene;
                        string rMarker = _savedBenchMarker;
                        try
                        {
                            if (__instance != null)
                            {
                                __instance.GetRespawnInfo(out string gmScene, out string gmMarker);
                                if (!string.IsNullOrEmpty(gmScene)) rScene = gmScene;
                                if (!string.IsNullOrEmpty(gmMarker)) rMarker = gmMarker;
                            }
                        }
                        catch { }
                        if (__instance != null) __instance.RespawningHero = true;
                        info.SceneName = rScene;
                        info.EntryGateName = rMarker ?? string.Empty;
                        info.HeroLeaveDirection = GlobalEnums.GatePosition.unknown;
                        info.WaitForSceneTransitionCameraFade = true;
                        info.PreventCameraFadeOut = false;
                        info.AlwaysUnloadUnusedAssets = true;
                        if (SilksongMobileAPIPlugin.Instance != null)
                            SilksongMobileAPIPlugin.Instance.StartCoroutine(EnsureBenchReturnFadeInCoroutine(targetBenchScene));
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Redirected death/exit from Godhome -> native bench respawn '{targetBenchScene}' (marker='{_savedBenchMarker}', RespawningHero=true).");
                    }
                }
            }
            catch { }
            return true;
        }

        public static bool IsDirectHkSessionActive => _directHkSessionActive;
        private static int _directHkFightToken;

        public static bool DirectHk_DoDreamReturn_Prefix(BossSceneController __instance)
        {
            try
            {
                if (!_directHkSessionActive) return true;
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreSceneBridge] Intercepted BossSceneController.DoDreamReturn() -> advancing Pantheon or returning to bench.");
                AdvanceDirectHkPantheonOrReturnToBench();
                return false;
            }
            catch { }
            return true;
        }

        public static void ReturnToSavedBenchFromDirectHk()
        {
            try
            {
                _directHkFightToken++;
                RestoreSavedBenchToPlayerData();
                RestoreDirectHkBindings();
                _directHkSessionActive = false;
                _activePantheonScenes = null;
                _activePantheonIndex = -1;
                _directHkBenchReturnUntilRealtime = Time.realtimeSinceStartup + 8f;
                RestoreHornetPhysicsAndGravitySafe("return-to-bench", true);
                var gm = GameManager.instance;
                if (gm != null)
                {
                    gm.RespawningHero = true;
                    gm.ReadyForRespawn(false);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HkCoreSceneBridge] ReturnToSavedBench ReadyForRespawn fallback: {ex.Message}");
            }
        }

        private static void AdvanceDirectHkPantheonOrReturnToBench()
        {
            try
            {
                if (_activePantheonScenes != null && _activePantheonIndex >= 0 && _activePantheonIndex + 1 < _activePantheonScenes.Length)
                {
                    _activePantheonIndex++;
                    if (_activePantheonIndex == _activePantheonScenes.Length / 2)
                    {
                        try
                        {
                            var hc = HeroController.instance;
                            if (hc != null) { hc.MaxHealth(); hc.AddSilk(9, false); }
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Pantheon '{_activePantheonName}' midpoint reached - restored Health & Silk!");
                        }
                        catch { }
                    }
                    string nextBoss = _activePantheonScenes[_activePantheonIndex];
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Pantheon '{_activePantheonName}' advancing to boss {_activePantheonIndex + 1}/{_activePantheonScenes.Length}: '{nextBoss}'");
                    LaunchSingleHkBossInternal(nextBoss, _activePantheonLevel, true);
                    return;
                }
                ReturnToSavedBenchFromDirectHk();
            }
            catch { ReturnToSavedBenchFromDirectHk(); }
        }

        private static float _lastPantheonSkipRealtime = -10f;

        public static string GetPantheonSkipButtonLabel()
        {
            try
            {
                if (_directHkSessionActive && _activePantheonScenes != null && _activePantheonScenes.Length > 0)
                {
                    int cur = Mathf.Clamp(_activePantheonIndex + 1, 1, _activePantheonScenes.Length);
                    return $"Skip Pantheon Boss ({cur}/{_activePantheonScenes.Length}) >>";
                }
                if (BossSequenceController.IsInSequence)
                {
                    return "Skip Pantheon Boss (Active) >>";
                }
            }
            catch { }
            return "Skip Pantheon Boss >>";
        }

        public static string SkipCurrentPantheonBoss(out bool didSkip)
        {
            didSkip = false;
            try
            {
                if (SilksongMobileAPIPlugin.CfgEnablePantheonBossSkip != null && !SilksongMobileAPIPlugin.CfgEnablePantheonBossSkip.Value)
                    return "Pantheon boss skip is disabled in config (BossFixes.EnablePantheonBossSkip).";

                float now = Time.realtimeSinceStartup;
                if (now - _lastPantheonSkipRealtime < 2.0f)
                    return "Skip already in progress...";

                // Path A: Journal / direct-launched Pantheon
                if (_directHkSessionActive && _activePantheonScenes != null && _activePantheonScenes.Length > 0)
                {
                    _lastPantheonSkipRealtime = now;
                    _directHkFightToken++;
                    int curIdx = _activePantheonIndex;
                    bool hasNext = curIdx >= 0 && curIdx + 1 < _activePantheonScenes.Length;
                    string curBoss = (curIdx >= 0 && curIdx < _activePantheonScenes.Length) ? _activePantheonScenes[curIdx] : "current";
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PantheonSkip] Direct Pantheon '{_activePantheonName}' skipping boss '{curBoss}' ({curIdx + 1}/{_activePantheonScenes.Length}).");
                    AdvanceDirectHkPantheonOrReturnToBench();
                    didSkip = true;
                    return hasNext
                        ? $"Skipped '{curBoss}' -> advancing to next Pantheon boss..."
                        : $"Skipped final Pantheon boss '{curBoss}' -> returning to bench...";
                }

                // Path B: Native Godhome Door Pantheon
                if (BossSequenceController.IsInSequence)
                {
                    var bsc = BossSceneController.Instance;
                    if (bsc == null)
                        return "Pantheon arena is still loading — try again in a moment.";

                    _lastPantheonSkipRealtime = now;
                    string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PantheonSkip] Native Pantheon skipping boss in '{activeScene}' via BossSceneController.EndBossScene().");
                    bsc.EndBossScene();
                    didSkip = true;
                    return $"Skipping Pantheon boss ('{activeScene}') -> moving to next boss...";
                }

                return "Not currently in a Pantheon fight (only works during a Pantheon run).";
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PantheonSkip] Failed to skip Pantheon boss: {ex.Message}");
                return $"Skip failed: {ex.Message}";
            }
        }

        private static IEnumerator WaitAndFinishDirectHkAfterBossesDead(int token, string sceneName)
        {
            yield return new WaitForSeconds(4.5f);
            if (_directHkSessionActive && _directHkFightToken == token)
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] BossesDead 4.5s safety timer fired in '{sceneName}' -> advancing/returning to bench.");
                AdvanceDirectHkPantheonOrReturnToBench();
            }
        }

        private static IEnumerator EnsureBenchReturnFadeInCoroutine(string benchScene)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 15f)
            {
                if (string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, benchScene, StringComparison.OrdinalIgnoreCase))
                    break;
                yield return new WaitForSeconds(0.25f);
            }
            yield return new WaitForSeconds(0.5f);
            try
            {
                var gm = GameManager.instance;
                if (gm != null) gm.FadeSceneIn();
                var hc = HeroController.instance;
                if (hc != null)
                {
                    hc.RegainControl();
                    hc.StartAnimationControl();
                }
                RestoreHornetPhysicsAndGravitySafe("bench-return-fade", false);
                var camCtrl = GameCameras.instance?.cameraController;
                if (camCtrl != null) camCtrl.PositionToHero(true);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Bench return fade-in & control restored in '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'.");
            }
            catch { }
        }

        private static IEnumerator LogDirectHkBossDiagnosticAfterLoad(int token, string sceneName)
        {
            float waitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - waitStart < 25f)
            {
                if (!_directHkSessionActive || _directHkFightToken != token) yield break;
                if (string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, sceneName, StringComparison.OrdinalIgnoreCase))
                    break;
                yield return new WaitForSeconds(0.25f);
            }

            // Fix #2: Release Hornet control on non-screaming HK1 bosses where Dream Entry relinquishes control and never gets ROAR LOCK END
            yield return new WaitForSeconds(1.2f);
            if (!_directHkSessionActive || _directHkFightToken != token) yield break;
            try
            {
                var hc = HeroController.instance;
                if (hc != null && hc.controlReqlinquished)
                {
                    hc.RegainControl();
                    hc.StartAnimationControl();
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Released Dream Entry control lock in '{sceneName}' (non-screaming boss fix).");
                }
                RestoreHornetPhysicsAndGravitySafe("direct-boss-diag:" + sceneName, false);
            }
            catch { }

            yield return new WaitForSeconds(1.5f);
            if (!_directHkSessionActive || _directHkFightToken != token) yield break;
            // Fix #3: In GG_Grimm, Grimm Scene/FSM stalls at 'Grimm Appear 4' [FINISHED->Battle Start] (confirmed in 2026-11-19_22-58-22/LogOutput.log).
            if (string.Equals(sceneName, "GG_Grimm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var allFsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
                    if (allFsms != null)
                    {
                        for (int i = 0; i < allFsms.Length; i++)
                        {
                            var f = allFsms[i];
                            if (f != null && f.gameObject.activeInHierarchy &&
                                string.Equals(f.gameObject.name, "Grimm Scene", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(f.ActiveStateName, "Grimm Appear 4", StringComparison.OrdinalIgnoreCase))
                            {
                                f.SendEvent("FINISHED");
                                SilksongMobileAPIPlugin.LogDiagnosticInfo("[HkCoreSceneBridge] Advanced stuck Grimm Scene/FSM ('Grimm Appear 4' -> 'Battle Start')!");
                                break;
                            }
                        }
                    }
                }
                catch { }
                yield return new WaitForSeconds(0.35f);
                // Fix #2: Grimm Boss/Control stalls at 'HP Scale' [1->Set 1..5->Set 5] (confirmed in 2026-11-20_08-04-16/LogOutput.log).
                try
                {
                    var allFsms2 = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
                    if (allFsms2 != null)
                    {
                        string tierEv = Mathf.Clamp((PlayerData.instance?.nailUpgrades ?? 0) + 1, 1, 5).ToString();
                        for (int i = 0; i < allFsms2.Length; i++)
                        {
                            var f = allFsms2[i];
                            if (f != null && f.gameObject.activeInHierarchy &&
                                string.Equals(f.gameObject.name, "Grimm Boss", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(f.FsmName, "Control", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(f.ActiveStateName, "HP Scale", StringComparison.OrdinalIgnoreCase))
                            {
                                f.SendEvent(tierEv);
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Advanced stuck Grimm Boss/Control ('HP Scale' -> Set {tierEv}) to start Phase 1 + battle audio!");
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            yield return new WaitForSeconds(0.5f);
            if (!_directHkSessionActive || _directHkFightToken != token) yield break;
            try
            {
                var bsc = BossSceneController.Instance;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[HkBossDiag] scene='{sceneName}' active='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' bsc={(bsc != null)}");
                if (bsc != null) sb.Append($" hasTransIn={bsc.HasTransitionedIn}");

                // Log HealthManager layer / NonBouncer / Invincible state for pogo diagnosis (no guessing!)
                var hms = UnityEngine.Object.FindObjectsOfType<HealthManager>();
                if (hms != null)
                {
                    for (int i = 0; i < hms.Length && i < 4; i++)
                    {
                        var hm = hms[i];
                        if (hm == null || !hm.gameObject.activeInHierarchy) continue;
                        var nb = hm.GetComponent<NonBouncer>();
                        sb.Append($" | HM:{hm.gameObject.name}(layer={hm.gameObject.layer}:{LayerMask.LayerToName(hm.gameObject.layer)},inv={hm.IsInvincible},nb={(nb != null ? nb.active.ToString() : "none")})");
                    }
                }

                // Log boss FSM states + outgoing transition events (for Grimm Scene / Grimm Boss diagnosis)
                var fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
                int logged = 0;
                if (fsms != null)
                {
                    for (int i = 0; i < fsms.Length && logged < 8; i++)
                    {
                        var f = fsms[i];
                        if (f == null || !f.gameObject.activeInHierarchy) continue;
                        string fn = f.FsmName ?? "";
                        string gn = f.gameObject.name ?? "";
                        bool isGrimmBoss = string.Equals(gn, "Grimm Boss", StringComparison.OrdinalIgnoreCase);
                        if (isGrimmBoss || (string.Equals(sceneName, "GG_Grimm", StringComparison.OrdinalIgnoreCase) ? false : (gn.IndexOf("Mantis", StringComparison.OrdinalIgnoreCase) >= 0 || gn.IndexOf("HK Prime", StringComparison.OrdinalIgnoreCase) >= 0 || fn == "Control" || fn.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0)))
                        {
                            string evs = "";
                            var st = f.Fsm?.ActiveState;
                            if (st?.Transitions != null && st.Transitions.Length > 0)
                            {
                                var eList = new List<string>(st.Transitions.Length);
                                for (int t = 0; t < st.Transitions.Length; t++)
                                    eList.Add(st.Transitions[t].EventName + "->" + st.Transitions[t].ToState);
                                evs = " [" + string.Join(",", eList) + "]";
                            }
                            sb.Append($" | {gn}/{fn}='{f.ActiveStateName}'{evs}");
                            logged++;
                        }
                    }
                }
                if (string.Equals(sceneName, "GG_Grimm", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
                        int playing = 0;
                        sb.Append(" | Audio:");
                        if (sources != null)
                        {
                            for (int i = 0; i < sources.Length && playing < 6; i++)
                            {
                                var a = sources[i];
                                if (a != null && a.isPlaying && a.clip != null)
                                {
                                    sb.Append($" {a.gameObject.name}({a.clip.name},v={a.volume:0.00})");
                                    playing++;
                                }
                            }
                        }
                        if (playing == 0) sb.Append(" none_playing");
                    }
                    catch { }
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo(sb.ToString());
            }
            catch { }
        }

        private static IEnumerator LaunchSingleHkBossCoroutine(string sceneName, int capturedLevel, int fightToken, bool isPantheonStep)
        {
            // Await async texture/bundle unload BEFORE allocating the next HK1 boss scene (prevents OOM kill on 6 GB phones like Pure Vessel after Grimm)
            TrimMemoryForHkTransition(sceneName);
            yield return Resources.UnloadUnusedAssets();
            GC.Collect();

            if (!_directHkSessionActive || _directHkFightToken != fightToken) yield break;
            var gm = GameManager.instance;
            if (gm == null) yield break;

            StaticVariableList.SetValue("bossSceneToLoad", sceneName, 0);
            try
            {
                var pd = PlayerData.instance;
                if (pd != null && !string.IsNullOrEmpty(_savedBenchScene))
                    pd.dreamReturnScene = _savedBenchScene;
            }
            catch { }

            BossSceneController.SetupEvent = (BossSceneController self) =>
            {
                if (self == null) return;
                self.BossLevel = capturedLevel;
                self.DreamReturnEvent = "DREAM RETURN";
                ApplyDirectHkBindingsIfAny();
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] BossSceneController.SetupEvent bound for '{sceneName}' (level={capturedLevel}).");
                self.OnBossesDead += () =>
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] OnBossesDead fired in '{sceneName}' (starting 4.5s return safety timer).");
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(WaitAndFinishDirectHkAfterBossesDead(fightToken, sceneName));
                };
                self.OnBossSceneComplete += () =>
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] OnBossSceneComplete fired in '{sceneName}'.");
                    if (_directHkSessionActive && _directHkFightToken == fightToken)
                        AdvanceDirectHkPantheonOrReturnToBench();
                };
            };

            if (SilksongMobileAPIPlugin.Instance != null)
                SilksongMobileAPIPlugin.Instance.StartCoroutine(LogDirectHkBossDiagnosticAfterLoad(fightToken, sceneName));

            RestoreHornetPhysicsAndGravitySafe("pre-boss-transition:" + sceneName, true);

            gm.BeginSceneTransition(new GameManager.SceneLoadInfo
            {
                SceneName = sceneName,
                EntryGateName = "door_dreamEnter",
                HeroLeaveDirection = GlobalEnums.GatePosition.door,
                EntryDelay = 0f,
                WaitForSceneTransitionCameraFade = true,
                PreventCameraFadeOut = false,
                Visualization = GameManager.SceneLoadVisualizations.Default,
                AlwaysUnloadUnusedAssets = false
            });
        }

        private static void LaunchSingleHkBossInternal(string sceneName, int level, bool isPantheonStep)
        {
            var gm = GameManager.instance;
            if (gm == null) return;

            EnsureHkCoreTypesResolved();
            EnsureHkCoreAddressablesSceneBridgeRegistered();
            EnsureDirectHkBenchReturnHookInstalled();

            int capturedLevel = Mathf.Clamp(level, 0, 2);
            int fightToken = ++_directHkFightToken;
            _directHkSessionActive = true;

            if (SilksongMobileAPIPlugin.Instance != null)
                SilksongMobileAPIPlugin.Instance.StartCoroutine(LaunchSingleHkBossCoroutine(sceneName, capturedLevel, fightToken, isPantheonStep));
        }

        public static string StartDirectHkBossFight(string sceneName, int level)
        {
            try
            {
                if (string.IsNullOrEmpty(sceneName)) return "No boss scene specified.";
                if (GameManager.instance == null) return "Load into a save file first.";
                CapturePlayerBenchIfOutsideHk();
                _activePantheonName = null;
                _activePantheonScenes = null;
                _activePantheonIndex = -1;
                LaunchSingleHkBossInternal(sceneName, level, false);
                string diffName = level == 0 ? "Attuned" : (level == 1 ? "Ascended" : "Radiant");
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Direct boss launch -> '{sceneName}' ({diffName}, bench return='{_savedBenchScene}').");
                return $"Loading {sceneName} ({diffName})...";
            }
            catch (Exception ex)
            {
                return $"Direct boss launch failed: {ex.Message}";
            }
        }

        public static string StartDirectHkPantheon(string pantheonName, string[] bossScenes, int level)
        {
            try
            {
                if (bossScenes == null || bossScenes.Length == 0) return "Empty pantheon.";
                if (GameManager.instance == null) return "Load into a save file first.";
                CapturePlayerBenchIfOutsideHk();
                _activePantheonName = pantheonName;
                _activePantheonScenes = bossScenes;
                _activePantheonIndex = 0;
                _activePantheonLevel = Mathf.Clamp(level, 0, 2);
                LaunchSingleHkBossInternal(bossScenes[0], _activePantheonLevel, true);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HkCoreSceneBridge] Started '{pantheonName}' ({bossScenes.Length} bosses, 1-at-a-time low-RAM, bench return='{_savedBenchScene}').");
                return $"Starting {pantheonName} (1/{bossScenes.Length})...";
            }
            catch (Exception ex)
            {
                return $"Pantheon launch failed: {ex.Message}";
            }
        }

        public static string TeleportToGodhomeScene(string targetScene)
        {
            try
            {
                if (string.IsNullOrEmpty(targetScene)) targetScene = "GG_Atrium";
                EnsureHkCoreTypesResolved();
                EnsureHkCoreAddressablesSceneBridgeRegistered();
                TrimMemoryForHkTransition(targetScene);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null || !(asm.GetName().Name ?? "").StartsWith("HkCore", StringComparison.OrdinalIgnoreCase)) continue;
                    var cpType = asm.GetType("HkCore.CorePlugin", false);
                    var instFi = cpType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var travelMi = cpType?.GetMethod("Travel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    var inst = instFi?.GetValue(null);
                    if (inst != null && travelMi != null)
                    {
                        travelMi.Invoke(inst, new object[] { targetScene });
                        return $"Teleporting Hornet to '{targetScene}' via HkCore...";
                    }
                }
                return "HkCore is not loaded yet (ensure HkCore and HkBossLoader are installed in BepInEx/plugins/).";
            }
            catch (Exception ex)
            {
                return $"Godhome teleport failed: {ex.Message}";
            }
        }

        public static string UnlockAllGodhomeStatuesAndDoors()
        {
            try
            {
                var gm = GameManager.instance;
                if (gm == null) return "Load into a save file first before unlocking Godhome.";
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null || !(asm.GetName().Name ?? "").StartsWith("HkBossLoader", StringComparison.OrdinalIgnoreCase)) continue;
                    var guType = asm.GetType("HkBossLoader.GodhomeUnlocks", false);
                    var unlockMi = guType?.GetMethod("Unlock", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (unlockMi != null)
                    {
                        unlockMi.Invoke(null, new object[] { gm });
                        HkCoreSyncOverlayToPlayerData();
                        return "Unlocked all Godhome Hall of Gods statues, Pantheons 1-5, and Land of Storms!";
                    }
                }
                return "HkBossLoader is not loaded yet (ensure HkBossLoader is installed in BepInEx/plugins/).";
            }
            catch (Exception ex)
            {
                return $"Godhome unlock failed: {ex.Message}";
            }
        }
    }
}
