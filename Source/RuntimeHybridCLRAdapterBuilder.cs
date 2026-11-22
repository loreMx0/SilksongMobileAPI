using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SilksongMobileAPI
{
    public sealed class PatchMethodMetadata
    {
        public string ModName;
        public string OwnerId;
        public string StateKey;
        public MethodInfo Method;
        public int Priority = HarmonyLib.Priority.Normal;
        public PatchParamBinding[] Bindings;
        public bool ReturnsBool;
        public bool ReturnsException;
        public bool IsWeaverCostPatch;
        public bool IsWeaverPerFrameCrestPatch;
        public bool IsWeaverPerFrameThreadNeedlePatch;
        public bool IsToolGuardStringPrefix;
        public bool IsFinalJudgeFsmStateEnter;
        public bool NeedsAnyRuntimeTypeCheck;
        public bool HasAnyByRefBinding;
        public bool CanUseSingleInstanceFastPath;
        public bool IsSceneExtractionLifecycleGuard;
        public Func<object> FastZeroArgInvoker;
        public Func<object, object> FastSingleInstanceInvoker;
        public Func<object, object, object> FastTwoArgInstArg0Invoker;
        public object[] SingleInstanceArgBuffer;
        public object[] ReusableArgBuffer;
        public int InUseDepth;
    }

    public enum PatchParamKind
    {
        DefaultNull = 0,
        Instance = 1,
        Result = 2,
        State = 3,
        OriginalMethod = 4,
        ArgsArray = 5,
        Exception = 6,
        RunOriginal = 7,
        TargetArgByIndex = 8,
        FieldAccess = 9
    }

    public sealed class PatchParamBinding
    {
        public PatchParamKind Kind;
        public ParameterInfo PatchParam;
        public Type CleanType;
        public bool IsByRef;
        public int TargetArgIndex = -1;
        public FieldInfo TargetField;
        public object DefaultValue;
        public bool NeedsRuntimeTypeCheck;
    }

    public sealed class SafeCoroutineEnumerator : System.Collections.IEnumerator
    {
        private readonly System.Collections.IEnumerator _orig;
        private readonly System.Collections.IEnumerator _wrapper;
        private readonly object _instance;
        private readonly string _modName;
        private readonly string _chainKey;
        private bool _wrapperFaulted;

        public SafeCoroutineEnumerator(System.Collections.IEnumerator orig, System.Collections.IEnumerator wrapper, object instance, string modName, string chainKey)
        {
            _orig = orig;
            _wrapper = wrapper;
            _instance = instance;
            _modName = modName ?? "UnknownMod";
            _chainKey = chainKey ?? "";
        }

        public object Current
        {
            get
            {
                try
                {
                    if (!_wrapperFaulted && _wrapper != null)
                        return _wrapper.Current;
                    if (_orig != null)
                        return _orig.Current;
                }
                catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L81", __apiEx_1); }
                return null;
            }
        }

        public bool MoveNext()
        {
            if (!_wrapperFaulted && _wrapper != null)
            {
                try
                {
                    if (_wrapper.MoveNext())
                        return true;
                    EnsureFsmActivatorCompletedIfNeeded();
                    return false;
                }
                catch (Exception ex)
                {
                    _wrapperFaulted = true;
                    SilksongMobileAPIPlugin.LogModException(_modName, $"CoroutinePostfix({_chainKey})", ex);
                }
            }

            if (_orig != null && !ReferenceEquals(_orig, _wrapper))
            {
                try
                {
                    if (_orig.MoveNext())
                        return true;
                }
                catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L111", __apiEx_2); }
            }

            EnsureFsmActivatorCompletedIfNeeded();
            return false;
        }

        public void Reset()
        {
            try { _wrapper?.Reset(); } catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L120", __apiEx_3); }
        }

        private void EnsureFsmActivatorCompletedIfNeeded()
        {
            if (_instance is FSMActivator fsmAct && fsmAct != null)
            {
                try
                {
                    if (fsmAct.fsms != null)
                    {
                        for (int i = 0; i < fsmAct.fsms.Length; i++)
                        {
                            var fsm = fsmAct.fsms[i];
                            if (fsm != null && !fsm.enabled)
                                fsm.enabled = true;
                        }
                    }
                    if (fsmAct.spriteAnim != null && !fsmAct.spriteAnim.enabled)
                        fsmAct.spriteAnim.enabled = true;
                }
                catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L141", __apiEx_4); }
            }
        }
    }

    public sealed class MonoModDetourEntry
    {
        public string ModName;
        public MethodInfo DetourMethod;
        public object Target;
        public Delegate OrigTrackDelegate;
    }

    public struct ChainCallFrame
    {
        public Dictionary<string, object> StateDict;
        public bool RunOriginal;
        public object PrefixResult;
        public object[] SavedArgs;
        public ToolItem SavedWillThrowTool;
        public bool HasSavedWillThrowTool;
    }

    public sealed class MethodHookChain
    {
        public int ChainId;
        public string Key;
        public MethodBase TargetMethod;
        public Type DeclaringType;
        public bool IsStatic;
        public Type ReturnType;
        public object DefaultReturnValue;
        public ParameterInfo[] TargetParams;
        public bool IsEnemyJournalManager;
        public bool IsCustomSceneManager;
        public bool IsUIManagerOrMenuScreen;
        public bool IsNailSlashStart;
        public bool IsFinalJudgeFsmStateEnter;
        public int PlayMakerTargetKind; // 0 = None, 1 = Fsm, 2 = PlayMakerFSM, 3 = FsmState, 4 = FsmStateAction
        public bool HasModPlayMakerPatches;
        public bool IsLifecycleGuardOnlyChain;
        public readonly List<PatchMethodMetadata> Prefixes = new List<PatchMethodMetadata>();
        public readonly List<PatchMethodMetadata> Postfixes = new List<PatchMethodMetadata>();
        public readonly List<PatchMethodMetadata> Finalizers = new List<PatchMethodMetadata>();
        public readonly List<MonoModDetourEntry> MonoModDetours = new List<MonoModDetourEntry>();
        public PatchMethodMetadata[] PrefixArray = Array.Empty<PatchMethodMetadata>();
        public PatchMethodMetadata[] PostfixArray = Array.Empty<PatchMethodMetadata>();
        public PatchMethodMetadata[] FinalizerArray = Array.Empty<PatchMethodMetadata>();
        public MonoModDetourEntry[] MonoModDetourArray = Array.Empty<MonoModDetourEntry>();
        public bool NativeHookInstalled;
        public bool UsesState;
        public volatile int PendingCallFramesCount;
        public readonly Stack<ChainCallFrame> CallFrames = new Stack<ChainCallFrame>(4);
    }

    public static class RuntimeHybridCLRAdapterBuilder
    {
        private const int MaxChains = 4096;
        private static readonly Dictionary<string, MethodHookChain> _chainsByKey = new Dictionary<string, MethodHookChain>(StringComparer.Ordinal);
        private static readonly MethodHookChain[] _chainsById = new MethodHookChain[MaxChains];
        private static readonly bool[] _shouldRunPrefix = new bool[MaxChains];
        private static readonly bool[] _shouldRunPostfix = new bool[MaxChains];
        private static readonly Dictionary<Type, object> _defaultValueCache = new Dictionary<Type, object>(64);
        private static readonly List<MethodBase> _allPatchedMethods = new List<MethodBase>();
        private static MethodHookChain _cachedNailClashChain;
        private static bool _nailClashChainLookupDone;
        private static int _nextChainId = 1;

        private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        public static bool ShouldRunPrefix(int chainId)
        {
            if ((uint)chainId < MaxChains)
                return _shouldRunPrefix[chainId] || AssetHelperBridge.IsExtractingPharloomsGloryScene;
            return true;
        }

        public static bool ShouldRunPostfix(int chainId)
        {
            if ((uint)chainId < MaxChains)
                return _shouldRunPostfix[chainId];
            return true;
        }

        public static bool RemovePatchFromChain(MethodBase targetMethod, MethodInfo patchMethod, string ownerId = null)
        {
            if (targetMethod == null) return false;
            string key = GetMethodKey(targetMethod);
            MethodHookChain chain;
            lock (_chainsByKey)
            {
                if (!_chainsByKey.TryGetValue(key, out chain) || chain == null)
                    return false;
            }

            bool removed = false;
            lock (chain.Prefixes)
            {
                int n = chain.Prefixes.RemoveAll(p =>
                    (patchMethod == null || p.Method == patchMethod) &&
                    (string.IsNullOrEmpty(ownerId) || string.Equals(p.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)));
                if (n > 0)
                {
                    chain.PrefixArray = chain.Prefixes.ToArray();
                    removed = true;
                }
            }
            lock (chain.Postfixes)
            {
                int n = chain.Postfixes.RemoveAll(p =>
                    (patchMethod == null || p.Method == patchMethod) &&
                    (string.IsNullOrEmpty(ownerId) || string.Equals(p.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)));
                if (n > 0)
                {
                    chain.PostfixArray = chain.Postfixes.ToArray();
                    removed = true;
                }
            }
            lock (chain.Finalizers)
            {
                int n = chain.Finalizers.RemoveAll(p =>
                    (patchMethod == null || p.Method == patchMethod) &&
                    (string.IsNullOrEmpty(ownerId) || string.Equals(p.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)));
                if (n > 0)
                {
                    chain.FinalizerArray = chain.Finalizers.ToArray();
                    removed = true;
                }
            }
            if (removed)
            {
                UpdateChainFastPathFlags(chain);
            }
            return removed;
        }

        [ThreadStatic]
        private static bool _monoModOrigCalled;

        private static void OrigTrackerVoid0() { _monoModOrigCalled = true; }
        private static void OrigTrackerVoid1<T0>(T0 a0) { _monoModOrigCalled = true; }
        private static void OrigTrackerVoid2<T0, T1>(T0 a0, T1 a1) { _monoModOrigCalled = true; }
        private static void OrigTrackerVoid3<T0, T1, T2>(T0 a0, T1 a1, T2 a2) { _monoModOrigCalled = true; }
        private static void OrigTrackerVoid4<T0, T1, T2, T3>(T0 a0, T1 a1, T2 a2, T3 a3) { _monoModOrigCalled = true; }
        private static void OrigTrackerVoid5<T0, T1, T2, T3, T4>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4) { _monoModOrigCalled = true; }

        private static TRet OrigTrackerRet0<TRet>() { _monoModOrigCalled = true; return default; }
        private static TRet OrigTrackerRet1<T0, TRet>(T0 a0) { _monoModOrigCalled = true; return default; }
        private static TRet OrigTrackerRet2<T0, T1, TRet>(T0 a0, T1 a1) { _monoModOrigCalled = true; return default; }
        private static TRet OrigTrackerRet3<T0, T1, T2, TRet>(T0 a0, T1 a1, T2 a2) { _monoModOrigCalled = true; return default; }
        private static TRet OrigTrackerRet4<T0, T1, T2, T3, TRet>(T0 a0, T1 a1, T2 a2, T3 a3) { _monoModOrigCalled = true; return default; }
        private static TRet OrigTrackerRet5<T0, T1, T2, T3, T4, TRet>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4) { _monoModOrigCalled = true; return default; }

        private static Delegate BuildOrigTrackerDelegate(Type origDelegateType)
        {
            if (origDelegateType == null || !typeof(Delegate).IsAssignableFrom(origDelegateType)) return null;
            try
            {
                var invokeMi = origDelegateType.GetMethod("Invoke");
                if (invokeMi == null) return null;
                var ps = invokeMi.GetParameters();
                bool isVoid = invokeMi.ReturnType == null || invokeMi.ReturnType == typeof(void);
                if (ps.Length > 5) return null;
                string helperName = (isVoid ? "OrigTrackerVoid" : "OrigTrackerRet") + ps.Length;
                var openMi = typeof(RuntimeHybridCLRAdapterBuilder).GetMethod(helperName, BindingFlags.Static | BindingFlags.NonPublic);
                if (openMi == null) return null;
                MethodInfo boundMi;
                if (isVoid)
                {
                    boundMi = ps.Length == 0 ? openMi : openMi.MakeGenericMethod(ps.Select(p => p.ParameterType).ToArray());
                }
                else
                {
                    var typeArgs = new Type[ps.Length + 1];
                    for (int i = 0; i < ps.Length; i++) typeArgs[i] = ps[i].ParameterType;
                    typeArgs[ps.Length] = invokeMi.ReturnType;
                    boundMi = openMi.MakeGenericMethod(typeArgs);
                }
                return Delegate.CreateDelegate(origDelegateType, boundMi);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AdapterBuilder] BuildOrigTrackerDelegate('{origDelegateType.FullName}') notice: {ex.Message}");
                return null;
            }
        }

        public static bool RegisterMonoModDetourChain(string modName, MethodBase targetMethod, MethodInfo detourMethod, object target)
        {
            if (targetMethod == null || detourMethod == null) return false;
            var dParams = detourMethod.GetParameters();
            if (dParams.Length == 0) return false;
            var origDel = BuildOrigTrackerDelegate(dParams[0].ParameterType);
            if (origDel == null) return false;

            string key = GetMethodKey(targetMethod);
            if (ShouldSkipProblematicNativeHook(modName, targetMethod, key))
                return false;

            MethodHookChain chain;
            int chainId;
            lock (_chainsByKey)
            {
                if (!_chainsByKey.TryGetValue(key, out chain))
                {
                    chainId = _nextChainId++;
                    var declType = targetMethod.DeclaringType;
                    string declShort = declType?.Name ?? "";
                    var retType = (targetMethod is MethodInfo mi) ? mi.ReturnType : typeof(void);
                    chain = new MethodHookChain
                    {
                        ChainId = chainId,
                        Key = key,
                        TargetMethod = targetMethod,
                        DeclaringType = declType,
                        IsStatic = targetMethod.IsStatic,
                        ReturnType = retType,
                        DefaultReturnValue = GetDefaultValue(retType),
                        TargetParams = targetMethod.GetParameters(),
                        IsEnemyJournalManager = declShort == "EnemyJournalManager",
                        IsCustomSceneManager = declShort == "CustomSceneManager",
                        IsUIManagerOrMenuScreen = declShort == "UIManager" || declShort == "MenuScreen",
                        IsNailSlashStart = (declShort == "NailSlash" || declShort == "NailAttackBase") &&
                                           (targetMethod.Name == "StartSlash" || targetMethod.Name == "OnSlashStarting" || targetMethod.Name == "StartAttack"),
                        IsFinalJudgeFsmStateEnter = false,
                        PlayMakerTargetKind = ClassifyPlayMakerTargetKind(declType, targetMethod.Name)
                    };
                    _chainsByKey[key] = chain;
                    if (key.StartsWith("DamageHero::NailClash(", StringComparison.Ordinal))
                    {
                        _cachedNailClashChain = chain;
                        _nailClashChainLookupDone = true;
                    }
                    if ((uint)chainId < MaxChains)
                        _chainsById[chainId] = chain;
                }
                else
                {
                    chainId = chain.ChainId;
                }
            }

            lock (chain.MonoModDetours)
            {
                if (!chain.MonoModDetours.Any(d => d.DetourMethod == detourMethod && ReferenceEquals(d.Target, target)))
                {
                    chain.MonoModDetours.Add(new MonoModDetourEntry
                    {
                        ModName = modName ?? "MonoModHook",
                        DetourMethod = detourMethod,
                        Target = target,
                        OrigTrackDelegate = origDel
                    });
                    chain.MonoModDetourArray = chain.MonoModDetours.ToArray();
                }
            }

            UpdateChainFastPathFlags(chain);
            if (!chain.NativeHookInstalled)
            {
                chain.NativeHookInstalled = InstallNativeHookForChain(modName, chainId, chain, null, null);
            }
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Registered MonoMod.RuntimeDetour.Hook on {key} (mod='{modName}', chainId={chainId}).");
            return chain.NativeHookInstalled;
        }

        public static int UnregisterAllHooksForMod(string modName, Assembly modAssembly = null)
        {
            int removedCount = 0;
            MethodHookChain[] allChains;
            lock (_chainsByKey)
            {
                allChains = _chainsByKey.Values.ToArray();
            }
            string modShort = modName ?? "";
            string asmName = modAssembly?.GetName()?.Name ?? "";

            bool MatchesMod(string entryModName, MethodInfo m)
            {
                if (modAssembly != null && m?.DeclaringType?.Assembly == modAssembly)
                    return true;
                if (!string.IsNullOrEmpty(asmName) && m?.DeclaringType?.Assembly?.GetName()?.Name is string mAsm &&
                    string.Equals(mAsm, asmName, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(modShort) && !string.IsNullOrEmpty(entryModName) &&
                    (string.Equals(entryModName, modShort, StringComparison.OrdinalIgnoreCase) ||
                     entryModName.Replace(" ", "").Equals(modShort.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                    return true;
                return false;
            }

            for (int i = 0; i < allChains.Length; i++)
            {
                var chain = allChains[i];
                if (chain == null) continue;
                bool changed = false;
                lock (chain.Prefixes)
                {
                    int n = chain.Prefixes.RemoveAll(p => MatchesMod(p.ModName, p.Method));
                    if (n > 0) { chain.PrefixArray = chain.Prefixes.ToArray(); removedCount += n; changed = true; }
                }
                lock (chain.Postfixes)
                {
                    int n = chain.Postfixes.RemoveAll(p => MatchesMod(p.ModName, p.Method));
                    if (n > 0) { chain.PostfixArray = chain.Postfixes.ToArray(); removedCount += n; changed = true; }
                }
                lock (chain.Finalizers)
                {
                    int n = chain.Finalizers.RemoveAll(p => MatchesMod(p.ModName, p.Method));
                    if (n > 0) { chain.FinalizerArray = chain.Finalizers.ToArray(); removedCount += n; changed = true; }
                }
                lock (chain.MonoModDetours)
                {
                    int n = chain.MonoModDetours.RemoveAll(d => MatchesMod(d.ModName, d.DetourMethod));
                    if (n > 0) { chain.MonoModDetourArray = chain.MonoModDetours.ToArray(); removedCount += n; changed = true; }
                }
                if (changed)
                {
                    UpdateChainFastPathFlags(chain);
                }
            }
            return removedCount;
        }

        private static readonly object BoxedTrue = true;
        private static readonly object BoxedFalse = false;

        private static Func<object> TryBuildZeroArgFastInvoker(MethodInfo mi)
        {
            if (mi == null || !mi.IsStatic || mi.GetParameters().Length != 0) return null;
            try
            {
                if (mi.ReturnType == typeof(void))
                {
                    var act = (Action)Delegate.CreateDelegate(typeof(Action), mi, false);
                    if (act != null) return () => { act(); return null; };
                }
                else if (mi.ReturnType == typeof(bool))
                {
                    var fn = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), mi, false);
                    if (fn != null) return () => fn() ? BoxedTrue : BoxedFalse;
                }
            }
            catch { }
            return null;
        }

        private static Func<object, object> CreateTypedSingleInstVoidInvoker<T0>(MethodInfo mi) where T0 : class
        {
            var act = (Action<T0>)Delegate.CreateDelegate(typeof(Action<T0>), mi, false);
            if (act == null) return null;
            return inst => { act((T0)inst); return null; };
        }

        private static Func<object, object> CreateTypedSingleInstBoolInvoker<T0>(MethodInfo mi) where T0 : class
        {
            var fn = (Func<T0, bool>)Delegate.CreateDelegate(typeof(Func<T0, bool>), mi, false);
            if (fn == null) return null;
            return inst => fn((T0)inst) ? BoxedTrue : BoxedFalse;
        }

        private static Func<object, object> TryBuildSingleInstanceFastInvoker(MethodInfo mi)
        {
            if (mi == null || !mi.IsStatic) return null;
            var ps = mi.GetParameters();
            if (ps.Length != 1) return null;
            var p0 = ps[0].ParameterType;
            if (p0 == null || p0.IsByRef || p0.IsValueType) return null;
            try
            {
                if (mi.ReturnType == typeof(void))
                {
                    var gen = typeof(RuntimeHybridCLRAdapterBuilder).GetMethod(nameof(CreateTypedSingleInstVoidInvoker), BindingFlags.Static | BindingFlags.NonPublic);
                    return gen?.MakeGenericMethod(p0).Invoke(null, new object[] { mi }) as Func<object, object>;
                }
                else if (mi.ReturnType == typeof(bool))
                {
                    var gen = typeof(RuntimeHybridCLRAdapterBuilder).GetMethod(nameof(CreateTypedSingleInstBoolInvoker), BindingFlags.Static | BindingFlags.NonPublic);
                    return gen?.MakeGenericMethod(p0).Invoke(null, new object[] { mi }) as Func<object, object>;
                }
            }
            catch { }
            return null;
        }

        private static Func<object, object, object> CreateTypedTwoArgVoidInvoker<T0, T1>(MethodInfo mi) where T0 : class where T1 : class
        {
            var act = (Action<T0, T1>)Delegate.CreateDelegate(typeof(Action<T0, T1>), mi, false);
            if (act == null) return null;
            return (inst, a0) => { act((T0)inst, (T1)a0); return null; };
        }

        private static Func<object, object, object> CreateTypedTwoArgBoolInvoker<T0, T1>(MethodInfo mi) where T0 : class where T1 : class
        {
            var fn = (Func<T0, T1, bool>)Delegate.CreateDelegate(typeof(Func<T0, T1, bool>), mi, false);
            if (fn == null) return null;
            return (inst, a0) => fn((T0)inst, (T1)a0) ? BoxedTrue : BoxedFalse;
        }

        private static Func<object, object, object> TryBuildTwoArgInstArg0FastInvoker(MethodInfo mi)
        {
            if (mi == null || !mi.IsStatic) return null;
            var ps = mi.GetParameters();
            if (ps.Length != 2) return null;
            var p0 = ps[0].ParameterType;
            var p1 = ps[1].ParameterType;
            if (p0 == null || p1 == null || p0.IsByRef || p1.IsByRef || p0.IsValueType || p1.IsValueType) return null;
            try
            {
                if (mi.ReturnType == typeof(void))
                {
                    var gen = typeof(RuntimeHybridCLRAdapterBuilder).GetMethod(nameof(CreateTypedTwoArgVoidInvoker), BindingFlags.Static | BindingFlags.NonPublic);
                    return gen?.MakeGenericMethod(p0, p1).Invoke(null, new object[] { mi }) as Func<object, object, object>;
                }
                else if (mi.ReturnType == typeof(bool))
                {
                    var gen = typeof(RuntimeHybridCLRAdapterBuilder).GetMethod(nameof(CreateTypedTwoArgBoolInvoker), BindingFlags.Static | BindingFlags.NonPublic);
                    return gen?.MakeGenericMethod(p0, p1).Invoke(null, new object[] { mi }) as Func<object, object, object>;
                }
            }
            catch { }
            return null;
        }

        private static void UpdateChainFastPathFlags(MethodHookChain chain)
        {
            if (chain == null || (uint)chain.ChainId >= MaxChains) return;
            bool needPre = chain.PrefixArray.Length > 0 ||
                           chain.MonoModDetourArray.Length > 0 ||
                           chain.UsesState ||
                           chain.IsNailSlashStart ||
                           (chain.PlayMakerTargetKind != 0 && (chain.PlayMakerTargetKind != 2 || chain.HasModPlayMakerPatches));
            bool needPost = chain.PostfixArray.Length > 0 ||
                            chain.FinalizerArray.Length > 0 ||
                            chain.UsesState ||
                            chain.IsNailSlashStart ||
                            (chain.PlayMakerTargetKind == 2 && chain.HasModPlayMakerPatches) ||
                            chain.PrefixArray.Any(p => p.ReturnsBool);
            _shouldRunPrefix[chain.ChainId] = needPre;
            _shouldRunPostfix[chain.ChainId] = needPost;

            chain.IsLifecycleGuardOnlyChain =
                chain.PrefixArray.Length == 1 &&
                chain.PrefixArray[0].IsSceneExtractionLifecycleGuard &&
                chain.PostfixArray.Length == 0 &&
                chain.FinalizerArray.Length == 0 &&
                chain.MonoModDetourArray.Length == 0 &&
                !chain.IsNailSlashStart &&
                (chain.PlayMakerTargetKind == 0 || (chain.PlayMakerTargetKind == 2 && !chain.HasModPlayMakerPatches));
        }

        public static IEnumerable<MethodBase> GetAllPatchedMethods()
        {
            lock (_allPatchedMethods)
            {
                return _allPatchedMethods.ToArray();
            }
        }

        public static int GetChainIdForMethod(MethodBase method)
        {
            if (method == null) return 0;
            string key = GetMethodKey(method);
            lock (_chainsByKey)
            {
                if (_chainsByKey.TryGetValue(key, out var chain) && chain != null)
                    return chain.ChainId;
            }
            return 0;
        }

        public static Patches GetPatchInfo(MethodBase method)
        {
            if (method == null)
                return new Patches(Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>());

            string key = GetMethodKey(method);
            MethodHookChain chain;
            lock (_chainsByKey)
            {
                _chainsByKey.TryGetValue(key, out chain);
            }
            if (chain == null)
            {
                if (method != null && method.DeclaringType != null && method.DeclaringType.Name == "HealthManager" && method.Name == "Hit")
                {
                    var dummyPatch = new Patch(method as MethodInfo, 0, "codex.silksong.weavercrest", HarmonyLib.Priority.Normal, null, null, false);
                    return new Patches(new Patch[] { dummyPatch }, Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>());
                }
                return new Patches(Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>());
            }

            Patch[] preArr;
            lock (chain.Prefixes)
            {
                preArr = new Patch[chain.Prefixes.Count];
                for (int i = 0; i < chain.Prefixes.Count; i++)
                {
                    var p = chain.Prefixes[i];
                    preArr[i] = new Patch(p.Method, i, p.OwnerId ?? p.ModName ?? "", p.Priority, null, null, false);
                }
            }

            Patch[] postArr;
            lock (chain.Postfixes)
            {
                postArr = new Patch[chain.Postfixes.Count];
                for (int i = 0; i < chain.Postfixes.Count; i++)
                {
                    var p = chain.Postfixes[i];
                    postArr[i] = new Patch(p.Method, i, p.OwnerId ?? p.ModName ?? "", p.Priority, null, null, false);
                }
            }

            Patch[] finArr;
            lock (chain.Finalizers)
            {
                finArr = new Patch[chain.Finalizers.Count];
                for (int i = 0; i < chain.Finalizers.Count; i++)
                {
                    var p = chain.Finalizers[i];
                    finArr[i] = new Patch(p.Method, i, p.OwnerId ?? p.ModName ?? "", p.Priority, null, null, false);
                }
            }

            if (method != null && method.DeclaringType != null &&
                (method.DeclaringType.Name == "RunFSM" || method.DeclaringType.Name == "HealthManager") &&
                (method.Name == "OnEnter" || method.Name == "Hit"))
            {
                if (!preArr.Any(p => string.Equals(p.owner, "codex.silksong.weavercrest", StringComparison.OrdinalIgnoreCase)) &&
                    !postArr.Any(p => string.Equals(p.owner, "codex.silksong.weavercrest", StringComparison.OrdinalIgnoreCase)))
                {
                    var dummyPatch = new Patch(method as MethodInfo, preArr.Length, "codex.silksong.weavercrest", HarmonyLib.Priority.Normal, null, null, false);
                    var expanded = new Patch[preArr.Length + 1];
                    Array.Copy(preArr, expanded, preArr.Length);
                    expanded[preArr.Length] = dummyPatch;
                    preArr = expanded;
                }
            }

            return new Patches(preArr, postArr, Array.Empty<Patch>(), finArr);
        }

        public static string GetMethodKey(MethodBase m)
        {
            if (m == null) return "";
            var ps = m.GetParameters();
            string pSig = string.Join(",", ps.Select(p => p.ParameterType?.FullName ?? p.ParameterType?.Name ?? ""));
            return $"{m.DeclaringType?.FullName ?? ""}::{m.Name}({pSig})";
        }

        private static bool ShouldSkipProblematicNativeHook(string modName, MethodBase targetMethod, string key)
        {
            string declName = targetMethod.DeclaringType?.FullName ?? targetMethod.DeclaringType?.Name ?? "";
            string mName = targetMethod.Name ?? "";

            if (string.Equals(modName, "Needleforge", StringComparison.OrdinalIgnoreCase) ||
                (modName != null && modName.IndexOf("needleforge", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (declName == "System.Enum" || declName == "EnumExtenstions" ||
                    declName == "GlobalSettings.UI" || declName == "ToolItemTypeExtensions" ||
                    declName == "InventoryItemSelectableDirectional" ||
                    declName == "InventoryItemToolManager" ||
                    declName == "InventoryItemTool" ||
                    declName == "InventoryToolCrest" ||
                    declName == "InventoryToolCrestSlot")
                {
                    return true;
                }
            }

            // HkBossLoader.HkBindings uses AccessTools.FieldRefAccess on InventoryItemToolManager & InventoryToolCrestSlot
            // (RefreshTools / BeginSwitchingCrest / UpdateSlotDisplay), causing native IL2CPP SIGABRT when switching inventory in Godhome.
            if (string.Equals(modName, "HkBossLoader", StringComparison.OrdinalIgnoreCase) &&
                (declName == "InventoryItemToolManager" || declName == "InventoryToolCrestSlot"))
            {
                return true;
            }

            if (declName == "InventoryItemSelectableDirectional" ||
                declName == "InventoryItemComboButtonPromptDisplay")
            {
                return true;
            }
            if (declName == "InventoryToolCrestSlot" &&
                (mName == "set_SaveData" || mName == "get_CursorColor" || mName == "SetSlotColour"))
            {
                return true;
            }
            if (declName == "BossSequenceBindingsDisplay" && mName == "CountBindings")
            {
                return true;
            }
            if (targetMethod is MethodInfo retMi && IsMultiSlotValueType(retMi.ReturnType))
            {
                return true;
            }

            if (declName == "HealthManager" &&
                (mName == "Hit" || mName == "TakeDamage" || mName == "ApplyExtraDamage" || mName == "ApplyTagDamage" || mName == "Die"))
            {
                return true;
            }
            if (targetMethod.DeclaringType != null &&
                (targetMethod.DeclaringType.IsGenericType || targetMethod.DeclaringType.IsGenericTypeDefinition ||
                 declName.StartsWith("SerializableNamedList", StringComparison.Ordinal) ||
                 declName.StartsWith("PersistentItem", StringComparison.Ordinal) ||
                 declName == "PersistentBoolItem" || declName == "PersistentIntItem"))
            {
                return true;
            }
            try
            {
                var tps = targetMethod.GetParameters();
                for (int pi = 0; pi < tps.Length; pi++)
                {
                    var pt = tps[pi].ParameterType;
                    if (pt == null || pt.IsByRef) continue;
                    if (pt.Name == "HitInstance")
                        return true;
                    if (IsMultiSlotValueType(pt) && !(declName == "GameManager" && mName == "BeginSceneTransition"))
                        return true;
                }
            }
            catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L379", __apiEx_5); }
            if (declName == "TinkEffect" && mName == "Hit")
            {
                return true;
            }

            if (declName == "HutongGames.PlayMaker.Fsm" &&
                (mName == "Update" || mName == "FixedUpdate" || mName == "LateUpdate"))
            {
                return true;
            }
            if (declName == "tk2dSpriteAnimator" && mName == "UpdateAnimation")
            {
                return true;
            }
            if (declName == "PlayMakerUnity2DProxy" && mName == "OnTriggerStay2D")
            {
                return true;
            }
            if (declName == "HutongGames.PlayMaker.Actions.ListenForInventory" && mName == "OnUpdate")
            {
                return true;
            }
            if (declName == "RadialHudIcon" || (declName == "ToolHudIcon" && mName != "GetIsEmpty"))
            {
                return true;
            }

            if (declName == "GameManager" && mName == "BeginSceneTransition")
            {
                return false;
            }

            if (declName == "HutongGames.PlayMaker.FsmStateAction" &&
                (mName == "OnEnter" || mName == "OnUpdate" || mName == "OnExit"))
            {
                return true;
            }

            if ((declName == "HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolDelay" ||
                 declName == "HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTime" ||
                 declName == "HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTimeV2") &&
                (mName == "OnUpdate" || mName == "OnEnter"))
            {
                return true;
            }

            if (string.Equals(modName, "PhantinelBoss", StringComparison.OrdinalIgnoreCase) &&
                declName == "HutongGames.PlayMaker.FsmState" && mName == "LoadActions")
            {
                return true;
            }

            if (string.Equals(modName, "PatchWork", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modName, "Patchwork", StringComparison.OrdinalIgnoreCase))
            {
                // Keep only sprite/texture skinning hooks; skip 84 audio hooks (including 6 Needolin audio hooks),
                // 9 video/Update hooks, and 9 localization hooks that cause massive reflection overhead and break Needolin audio.
                if (declName != "tk2dSpriteCollectionData" &&
                    declName != "tk2dSprite" &&
                    declName != "UnityEngine.SpriteRenderer" &&
                    declName != "UnityEngine.UI.Image")
                {
                    return true;
                }
            }

            if (string.Equals(modName, "WeaverCrest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modName, "Weaver_Crest", StringComparison.OrdinalIgnoreCase) ||
                (modName != null && modName.StartsWith("Weaver", StringComparison.OrdinalIgnoreCase)))
            {
                if (declName == "InventoryPaneInput" ||
                    declName == "HutongGames.PlayMaker.Actions.AudioStop" ||
                    declName == "HutongGames.PlayMaker.Actions.AudioStopV2" ||
                    declName == "HutongGames.PlayMaker.Actions.ControlNeedolin" ||
                    declName == "HutongGames.PlayMaker.Actions.RunFSM" ||
                    declName == "HutongGames.PlayMaker.Actions.StartNeedolinAudioLoop" ||
                    declName == "OverrideNeedolinLoop" ||
                    declName == "NeedolinSyncedAudioPlayer" ||
                    declName == "SyncNeedolinLoop" ||
                    declName == "TimeLimitSetV2" ||
                    (declName == "HutongGames.PlayMaker.Fsm" && mName == "SwitchState") ||
                    (declName == "HutongGames.PlayMaker.Actions.CallMethodProper" && mName == "OnEnter") ||
                    (declName == "HutongGames.PlayMaker.Actions.HeroControllerMethods" && (mName == "SendMethod" || mName == "OnEnter" || mName == "SendMethodFaster")) ||
                    (declName == "HutongGames.PlayMaker.Actions.TakeSilk" && mName == "OnEnter") ||
                    (declName == "UnityEngine.AudioSource" && (mName == "Play" || mName == "PlayOneShot" || mName == "PlayHelper" || mName == "PlayOneShotHelper")))
                {
                    return true;
                }
            }

            if (string.Equals(modName, "Stitchwork", StringComparison.OrdinalIgnoreCase))
            {
                if (declName == "PlayerData" ||
                    declName == "GameManager" ||
                    declName == "AchievementHandler" ||
                    declName == "MenuStyleUnlock" ||
                    declName == "UnityEngine.UI.LayoutGroup" ||
                    declName == "UnityEngine.EventSystems.EventSystem" ||
                    declName == "UnityEngine.UI.InputField" ||
                    declName == "InputHandler" ||
                    declName == "UnityEngine.Cursor" ||
                    declName == "SceneAdditiveLoadConditional")
                {
                    return true;
                }
                if (declName == "UnityEngine.AudioSource" &&
                    (mName == "PlayHelper" || mName == "PlayOneShotHelper" || mName == "PlayDelayed" || mName == "PlayScheduled"))
                {
                    return true;
                }
                if (declName == "tk2dSpriteAnimator" &&
                    (mName == "PlayFromFrame" || mName == "PlayFrom"))
                {
                    return true;
                }
            }

            if (string.Equals(modName, "PharloomsGlory", StringComparison.OrdinalIgnoreCase))
            {
                if (declName == "UnityEngine.AudioSource" ||
                    (declName == "AlertRange" && mName == "Update") ||
                    (declName == "GameMapScene" && mName == "GetColor") ||
                    (declName == "CustomSceneManager" && mName == "SetLighting") ||
                    (declName == "HeroLight" && mName == "ApplyColor"))
                {
                    return true;
                }
            }

            if (declName == "CollectableItem" && mName == "set_SaveData")
            {
                return true;
            }

            if (declName == "DamageHero" && mName == "NailClash")
            {
                return true;
            }

            if ((declName == "ObjectPool" || declName == "ObjectPoolExtensions") && mName == "Spawn")
            {
                if (string.Equals(modName, "FinalJudge", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }

            return false;
        }

        public static bool IsMultiSlotValueType(Type pt)
        {
            if (pt == null || pt.IsByRef || !pt.IsValueType || pt.IsPrimitive || pt.IsEnum) return false;
            string fn = pt.FullName ?? pt.Name ?? "";
            if (fn == "System.Void" || fn == "System.IntPtr" || fn == "System.UIntPtr" ||
                fn == "UnityEngine.Vector2" || fn == "UnityEngine.Vector2Int" ||
                fn == "UnityEngine.Color32" || fn == "UnityEngine.LayerMask" ||
                fn == "UnityEngine.SceneManagement.Scene")
            {
                return false;
            }
            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var under = Nullable.GetUnderlyingType(pt);
                if (under != null && (under.IsPrimitive || under.IsEnum))
                {
                    try
                    {
                        var sizeType = under.IsEnum ? Enum.GetUnderlyingType(under) : under;
                        if (System.Runtime.InteropServices.Marshal.SizeOf(sizeType) <= 4)
                            return false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool HasMultiSlotValueTypeParameter(ParameterInfo[] targetParams)
        {
            if (targetParams == null || targetParams.Length == 0) return false;
            for (int i = 0; i < targetParams.Length; i++)
            {
                if (IsMultiSlotValueType(targetParams[i].ParameterType))
                    return true;
            }
            return false;
        }

        private static int ClassifyPlayMakerTargetKind(Type declType, string methodName)
        {
            if (declType == null) return 0;
            if (declType == typeof(PlayMakerFSM) || typeof(PlayMakerFSM).IsAssignableFrom(declType))
            {
                if (methodName == "Awake" || methodName == "OnEnable" || methodName == "Start")
                    return 2;
                return 0;
            }
            if (declType.Name == "SendRandomEventV4") return 4;
            return 0;
        }

        public static bool RegisterOrUpdateHookChain(
            string modName,
            MethodBase targetMethod,
            MethodInfo prefixMethod,
            MethodInfo postfixMethod,
            MethodInfo finalizerMethod,
            int priority = HarmonyLib.Priority.Normal,
            string ownerId = null)
        {
            if (targetMethod == null) return false;
            if (prefixMethod == null && postfixMethod == null && finalizerMethod == null) return false;
            if (targetMethod.IsGenericMethod || targetMethod.IsGenericMethodDefinition ||
                (targetMethod.DeclaringType != null && (targetMethod.DeclaringType.IsGenericType || targetMethod.DeclaringType.IsGenericTypeDefinition)))
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Skipped generic declaring-type/method hook on {targetMethod.DeclaringType?.FullName}::{targetMethod.Name} (mod='{modName}').");
                return true;
            }

            string key = GetMethodKey(targetMethod);
            if (string.Equals(modName, "PhantinelBoss", StringComparison.OrdinalIgnoreCase) &&
                targetMethod.DeclaringType != null &&
                targetMethod.DeclaringType.FullName == "HutongGames.PlayMaker.FsmState" &&
                targetMethod.Name == "LoadActions")
            {
                return false;
            }
            if (prefixMethod != null &&
                (prefixMethod.DeclaringType?.FullName == "Awakened_Grand_Mother_Silk.Source.Patches.DebugPatches" ||
                 prefixMethod.Name == "ForceInvincible"))
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Blocked debug god-mode patch {prefixMethod.DeclaringType?.FullName}.{prefixMethod.Name} on {key} (mod='{modName}').");
                return true;
            }
            bool skipNativeInstall = ShouldSkipProblematicNativeHook(modName, targetMethod, key);
            bool isBridgedHitInstanceChain =
                key == "HealthManager::Hit(HitInstance)" ||
                key == "HealthManager::TakeDamage(HitInstance)" ||
                key == "HeroController::SilkGain(HitInstance)" ||
                key == "HeroController::NailHitEnemy(HealthManager,HitInstance)" ||
                key.StartsWith("HealthManager::Die(", StringComparison.Ordinal) ||
                key.StartsWith("DamageHero::NailClash(", StringComparison.Ordinal);
            if (skipNativeInstall && !isBridgedHitInstanceChain)
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Skipped problematic hook for {key} (mod='{modName}').");
                return true;
            }
            MethodHookChain chain;
            int chainId = 0;

            lock (_chainsByKey)
            {
                if (!_chainsByKey.TryGetValue(key, out chain))
                {
                    chainId = _nextChainId++;
                    var declType = targetMethod.DeclaringType;
                    string declShort = declType?.Name ?? "";
                    var retType = (targetMethod is MethodInfo mi) ? mi.ReturnType : typeof(void);
                    chain = new MethodHookChain
                    {
                        ChainId = chainId,
                        Key = key,
                        TargetMethod = targetMethod,
                        DeclaringType = declType,
                        IsStatic = targetMethod.IsStatic,
                        ReturnType = retType,
                        DefaultReturnValue = GetDefaultValue(retType),
                        TargetParams = targetMethod.GetParameters(),
                        IsEnemyJournalManager = (declShort == "EnemyJournalManager"),
                        IsCustomSceneManager = (declShort == "CustomSceneManager"),
                        IsUIManagerOrMenuScreen = (declShort == "UIManager" || declShort == "MenuScreen"),
                        IsNailSlashStart = (key.IndexOf("StartSlash", StringComparison.Ordinal) >= 0 ||
                                            key.IndexOf("StartAttack", StringComparison.Ordinal) >= 0),
                        PlayMakerTargetKind = ClassifyPlayMakerTargetKind(declType, targetMethod.Name ?? ""),
                        IsFinalJudgeFsmStateEnter = false
                    };
                    _chainsByKey[key] = chain;
                    if (key.StartsWith("DamageHero::NailClash(", StringComparison.Ordinal))
                    {
                        _cachedNailClashChain = chain;
                        _nailClashChainLookupDone = true;
                    }
                    if ((uint)chainId < MaxChains)
                        _chainsById[chainId] = chain;
                    lock (_allPatchedMethods)
                    {
                        if (!_allPatchedMethods.Contains(targetMethod))
                            _allPatchedMethods.Add(targetMethod);
                    }
                }
                else
                {
                    chainId = chain.ChainId;
                }
            }

            string effectiveOwner = string.IsNullOrEmpty(ownerId) ? modName : ownerId;
            if (prefixMethod != null)
            {
                var meta = BuildPatchMethodMetadata(modName, chain, prefixMethod, priority);
                meta.OwnerId = effectiveOwner;
                lock (chain.Prefixes)
                {
                    if (!chain.Prefixes.Any(p => p.Method == prefixMethod))
                    {
                        chain.Prefixes.Add(meta);
                        chain.Prefixes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                        chain.PrefixArray = chain.Prefixes.ToArray();
                    }
                }
            }

            if (postfixMethod != null)
            {
                var meta = BuildPatchMethodMetadata(modName, chain, postfixMethod, priority);
                meta.OwnerId = effectiveOwner;
                lock (chain.Postfixes)
                {
                    if (!chain.Postfixes.Any(p => p.Method == postfixMethod))
                    {
                        chain.Postfixes.Add(meta);
                        chain.Postfixes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                        chain.PostfixArray = chain.Postfixes.ToArray();
                    }
                }
            }

            if (finalizerMethod != null)
            {
                var meta = BuildPatchMethodMetadata(modName, chain, finalizerMethod, priority);
                meta.OwnerId = effectiveOwner;
                lock (chain.Finalizers)
                {
                    if (!chain.Finalizers.Any(p => p.Method == finalizerMethod))
                    {
                        chain.Finalizers.Add(meta);
                        chain.Finalizers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                        chain.FinalizerArray = chain.Finalizers.ToArray();
                    }
                }
            }

            if (chain.PlayMakerTargetKind == 2 &&
                !string.Equals(modName, "SilksongMobileAPI.LifecycleGuard", StringComparison.OrdinalIgnoreCase))
            {
                chain.HasModPlayMakerPatches = true;
            }

            UpdateChainFastPathFlags(chain);

            if (skipNativeInstall)
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Recorded managed hook chain for {key} (mod='{modName}') without native trampoline.");
                return true;
            }

            if (!chain.NativeHookInstalled)
            {
                chain.NativeHookInstalled = InstallNativeHookForChain(modName, chainId, chain, prefixMethod, postfixMethod);
            }

            return chain.NativeHookInstalled;
        }

        private static bool InstallNativeHookForChain(string modName, int chainId, MethodHookChain chain, MethodInfo singlePrefix, MethodInfo singlePostfix)
        {
            try
            {
                var targetMi = chain.TargetMethod as MethodInfo;
                if (targetMi == null) return false;
                string declTypeName = targetMi.DeclaringType?.Name ?? "";
                if ((declTypeName == "FullQuestBase" || declTypeName == "BasicQuestBase") && targetMi.Name == "TryEndQuest")
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Skipping non-essential quest completion hook on {declTypeName}.{targetMi.Name} for mobile stability.");
                    return false;
                }
                string declAsmName = targetMi.DeclaringType?.Assembly?.GetName()?.Name ?? "";
                if (HarmonyBridge.IsManagedPcModAssembly(targetMi.DeclaringType?.Assembly) ||
                    declAsmName.IndexOf("SilksongMobileAPI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(declAsmName, "AssetHelper", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(declAsmName, "Silksong.FsmUtil", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(declAsmName, "PatchWork", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(declAsmName, "TribeTowerPerformancev1.7", StringComparison.OrdinalIgnoreCase))
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Activated managed woven hook for {chain.Key} (mod='{modName}', chainId={chainId}, ret={targetMi.ReturnType?.Name ?? "void"})");
                    return true;
                }
                bool usedPool = PrecompiledHookPool.TryAcquireHookPair(chainId, targetMi, out var dynPre, out var dynPost);
                if ((usedPool || CecilRuntimeRewriter.TryBuildDynamicHookPair(chainId, targetMi, out dynPre, out dynPost)) && dynPre != null && dynPost != null)
                {
                    bool omitPostForMultiSlotRet = HasMultiSlotValueTypeParameter(chain.TargetParams) &&
                                                   targetMi.ReturnType != null && targetMi.ReturnType != typeof(void);
                    var nativeHarmony = new Harmony();
                    try
                    {
                        nativeHarmony.Patch(targetMi, dynPre, omitPostForMultiSlotRet ? null : dynPost);
                    }
                    catch (Exception patchEx)
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AdapterBuilder] nativeHarmony.Patch collision for {chain.Key}: {patchEx.Message} (continuing with managed chain)");
                    }
                    chain.NativeHookInstalled = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AdapterBuilder] Installed dynamic hook pair for {chain.Key} (mod='{modName}', chainId={chainId}, ret={targetMi.ReturnType?.Name ?? "void"})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AdapterBuilder] Dynamic hook generation fallback for {chain.Key}: {ex.Message}");
            }

            if (string.Equals(modName, "SilksongMobileAPI", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var targetMi = chain.TargetMethod as MethodInfo;
                    if (targetMi == null) return false;
                    var nativeHarmony = new Harmony();
                    MethodInfo directPre = (singlePrefix != null && IsDirectlyCompatibleWithMobileHook(chain, singlePrefix, isPostfix: false)) ? singlePrefix : null;
                    MethodInfo directPost = (singlePostfix != null && IsDirectlyCompatibleWithMobileHook(chain, singlePostfix, isPostfix: true)) ? singlePostfix : null;
                    if (directPre != null || directPost != null)
                    {
                        nativeHarmony.Patch(targetMi, directPre, directPost);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(modName, $"InstallNativeHook({chain.Key})", ex);
                }
            }

            return false;
        }

        private static bool IsDirectlyCompatibleWithMobileHook(MethodHookChain chain, MethodInfo hook, bool isPostfix)
        {
            if (hook == null || !hook.IsStatic) return false;
            var hParams = hook.GetParameters();
            var tParams = chain.TargetParams;
            int idx = 0;

            if (!chain.IsStatic && hParams.Length > 0 && hParams[0].Name == "__instance")
                idx++;

            int remaining = hParams.Length - idx;
            bool hasResult = isPostfix && chain.ReturnType != typeof(void) && hParams.Length > 0 && hParams[hParams.Length - 1].Name == "__result";
            if (hasResult) remaining--;

            if (remaining != tParams.Length) return false;
            for (int i = 0; i < tParams.Length; i++)
            {
                var hp = hParams[idx + i];
                if (hp.Name != null && hp.Name.StartsWith("__")) return false;
            }
            return true;
        }

        private static PatchMethodMetadata BuildPatchMethodMetadata(string modName, MethodHookChain chain, MethodInfo patchMethod, int priority)
        {
            var prioAttr = HarmonyBridge.GetCustomAttributeSafe<HarmonyPriority>(patchMethod, true)
                        ?? (patchMethod.DeclaringType != null ? HarmonyBridge.GetCustomAttributeSafe<HarmonyPriority>(patchMethod.DeclaringType, true) : null);
            if (prioAttr != null) priority = prioAttr.info;

            var classArgAttrs = patchMethod.DeclaringType != null
                ? HarmonyBridge.GetCustomAttributesSafe<HarmonyArgument>(patchMethod.DeclaringType, true)
                : new List<HarmonyArgument>();
            var methodArgAttrs = HarmonyBridge.GetCustomAttributesSafe<HarmonyArgument>(patchMethod, true);

            var patchParams = patchMethod.GetParameters();
            var bindings = new PatchParamBinding[patchParams.Length];
            bool needsAnyTypeCheck = false;
            bool hasAnyByRef = false;

            for (int i = 0; i < patchParams.Length; i++)
            {
                var pp = patchParams[i];
                var pType = pp.ParameterType;
                bool isByRef = pType.IsByRef;
                if (isByRef) hasAnyByRef = true;
                var cleanType = isByRef ? pType.GetElementType() : pType;
                string name = pp.Name ?? "";

                var paramArgAttr = HarmonyBridge.GetCustomAttributeSafe<HarmonyArgument>(pp, false);
                int explicitIndex = -1;
                if (paramArgAttr != null)
                {
                    if (!string.IsNullOrEmpty(paramArgAttr.OriginalName)) name = paramArgAttr.OriginalName;
                    if (paramArgAttr.Index >= 0) explicitIndex = paramArgAttr.Index;
                }
                else
                {
                    foreach (var ma in methodArgAttrs.Concat(classArgAttrs))
                    {
                        if (!string.IsNullOrEmpty(ma.NewName) && ma.NewName == name)
                        {
                            if (!string.IsNullOrEmpty(ma.OriginalName)) name = ma.OriginalName;
                            if (ma.Index >= 0) explicitIndex = ma.Index;
                            break;
                        }
                    }
                }

                var b = new PatchParamBinding
                {
                    PatchParam = pp,
                    CleanType = cleanType,
                    IsByRef = isByRef,
                    DefaultValue = GetDefaultValue(cleanType)
                };

                if (explicitIndex >= 0 && explicitIndex < chain.TargetParams.Length)
                {
                    b.Kind = PatchParamKind.TargetArgByIndex;
                    b.TargetArgIndex = explicitIndex;
                }
                else if (name == "__instance" || name == "@this" || (name == "self" && !chain.IsStatic && i == 0))
                {
                    b.Kind = PatchParamKind.Instance;
                }
                else if (name == "__result" || (name == "returnValue" && chain.ReturnType != typeof(void)))
                {
                    b.Kind = PatchParamKind.Result;
                }
                else if (name == "__state")
                {
                    b.Kind = PatchParamKind.State;
                }
                else if (name == "__originalMethod")
                {
                    b.Kind = PatchParamKind.OriginalMethod;
                }
                else if (name == "__args")
                {
                    b.Kind = PatchParamKind.ArgsArray;
                }
                else if (name == "__exception")
                {
                    b.Kind = PatchParamKind.Exception;
                }
                else if (name == "__runOriginal")
                {
                    b.Kind = PatchParamKind.RunOriginal;
                }
                else if (name.StartsWith("___") && name.Length > 3)
                {
                    string fieldName = name.Substring(3);
                    b.Kind = PatchParamKind.FieldAccess;
                    b.TargetField = FindFieldInHierarchy(chain.DeclaringType, fieldName);
                }
                else if (name.StartsWith("__") && int.TryParse(name.Substring(2), out int argIdx) && argIdx >= 0 && argIdx < chain.TargetParams.Length)
                {
                    b.Kind = PatchParamKind.TargetArgByIndex;
                    b.TargetArgIndex = argIdx;
                }
                else
                {
                    int matchedIdx = -1;
                    for (int t = 0; t < chain.TargetParams.Length; t++)
                    {
                        if (string.Equals(chain.TargetParams[t].Name, name, StringComparison.Ordinal))
                        {
                            matchedIdx = t;
                            break;
                        }
                    }
                    if (matchedIdx < 0)
                    {
                        for (int t = 0; t < chain.TargetParams.Length; t++)
                        {
                            if (string.Equals(chain.TargetParams[t].Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedIdx = t;
                                break;
                            }
                        }
                    }
                    if (matchedIdx >= 0)
                    {
                        b.Kind = PatchParamKind.TargetArgByIndex;
                        b.TargetArgIndex = matchedIdx;
                    }
                    else
                    {
                        var fallbackField = FindFieldInHierarchy(chain.DeclaringType, name);
                        if (fallbackField != null)
                        {
                            b.Kind = PatchParamKind.FieldAccess;
                            b.TargetField = fallbackField;
                        }
                        else if (chain.ReturnType != typeof(void) &&
                                 cleanType != null &&
                                 (cleanType.IsAssignableFrom(chain.ReturnType) ||
                                  (patchMethod.ReturnType != typeof(void) && cleanType == patchMethod.ReturnType)))
                        {
                            b.Kind = PatchParamKind.Result;
                        }
                        else
                        {
                            b.Kind = PatchParamKind.DefaultNull;
                        }
                    }
                }

                // Determine if runtime type check is actually required for this binding
                if (cleanType != null && cleanType != typeof(object))
                {
                    if (b.Kind == PatchParamKind.Instance)
                    {
                        if (chain.DeclaringType == null || !cleanType.IsAssignableFrom(chain.DeclaringType))
                        {
                            b.NeedsRuntimeTypeCheck = true;
                            needsAnyTypeCheck = true;
                        }
                    }
                    else if (b.Kind == PatchParamKind.Result)
                    {
                        if (chain.ReturnType == null || !cleanType.IsAssignableFrom(chain.ReturnType))
                        {
                            b.NeedsRuntimeTypeCheck = true;
                            needsAnyTypeCheck = true;
                        }
                    }
                    else if (b.Kind == PatchParamKind.TargetArgByIndex && b.TargetArgIndex >= 0 && b.TargetArgIndex < chain.TargetParams.Length)
                    {
                        var tpType = chain.TargetParams[b.TargetArgIndex].ParameterType;
                        if (tpType.IsByRef) tpType = tpType.GetElementType();
                        if (tpType == null || !cleanType.IsAssignableFrom(tpType))
                        {
                            b.NeedsRuntimeTypeCheck = true;
                            needsAnyTypeCheck = true;
                        }
                    }
                }

                bindings[i] = b;
            }

            if (bindings.Any(b => b.Kind == PatchParamKind.State) || patchMethod.ReturnType == typeof(bool) || patchMethod.ReturnType == typeof(MonoDetour.DetourTypes.ReturnFlow))
            {
                chain.UsesState = true;
            }

            string dtName = patchMethod.DeclaringType?.Name ?? "";
            string pmName = patchMethod.Name ?? "";
            bool isWeaverCost = (dtName == "SilkMechanics" && (pmName == "ThreadCost" || pmName == "PayMissed" || pmName == "ChargedAttackCost" || pmName == "DashAttackCost")) ||
                                (dtName == "MaskTrade" && (pmName == "MaskCost" || pmName == "PayCosts"));

            bool singleInstFast = bindings.Length == 1 && !hasAnyByRef && bindings[0].Kind == PatchParamKind.Instance;
            bool zeroArgFast = bindings.Length == 0 && !hasAnyByRef;
            bool twoArgInstArg0Fast = bindings.Length == 2 && !hasAnyByRef &&
                                      bindings[0].Kind == PatchParamKind.Instance &&
                                      bindings[1].Kind == PatchParamKind.TargetArgByIndex &&
                                      bindings[1].TargetArgIndex == 0;
            bool isLifecycleGuard = (pmName == "SceneExtractionLifecycleGuard_Prefix" && dtName == "AssetHelperBridge");
            return new PatchMethodMetadata
            {
                ModName = modName,
                StateKey = patchMethod.DeclaringType?.FullName ?? modName ?? "",
                Method = patchMethod,
                Priority = priority,
                Bindings = bindings,
                ReturnsBool = patchMethod.ReturnType == typeof(bool),
                ReturnsException = typeof(Exception).IsAssignableFrom(patchMethod.ReturnType),
                IsWeaverCostPatch = isWeaverCost,
                IsWeaverPerFrameCrestPatch = (patchMethod.DeclaringType != null &&
                                              (patchMethod.DeclaringType.FullName == "WeaverCrestMod.HeroControllerLinkTimeInputPatch" ||
                                               patchMethod.DeclaringType.FullName == "WeaverCrestMod.HeroControllerLinkTimePhysicsPatch" ||
                                               patchMethod.DeclaringType.FullName == "WeaverCrestMod.HeartGrabBindInputPatch")),
                IsWeaverPerFrameThreadNeedlePatch = (patchMethod.DeclaringType != null &&
                                                     patchMethod.DeclaringType.FullName == "WeaverCrestMod.ThreadNeedleLifecyclePatch"),
                IsToolGuardStringPrefix = (patchMethod.Name == "ToolItemManager_IsToolEquippedString_Prefix"),
                IsFinalJudgeFsmStateEnter = (chain.DeclaringType?.Name == "FsmState" &&
                                             chain.TargetMethod?.Name == "OnEnter" &&
                                             string.Equals(modName, "FinalJudge", StringComparison.OrdinalIgnoreCase)),
                IsSceneExtractionLifecycleGuard = isLifecycleGuard,
                FastZeroArgInvoker = zeroArgFast ? TryBuildZeroArgFastInvoker(patchMethod) : null,
                FastSingleInstanceInvoker = singleInstFast ? TryBuildSingleInstanceFastInvoker(patchMethod) : null,
                FastTwoArgInstArg0Invoker = twoArgInstArg0Fast ? TryBuildTwoArgInstArg0FastInvoker(patchMethod) : null,
                NeedsAnyRuntimeTypeCheck = needsAnyTypeCheck,
                HasAnyByRefBinding = hasAnyByRef,
                CanUseSingleInstanceFastPath = singleInstFast,
                SingleInstanceArgBuffer = singleInstFast ? new object[1] : null
            };
        }

        public static FieldInfo FindFieldInHierarchy(Type t, string fieldName)
        {
            while (t != null && t != typeof(object))
            {
                var f = t.GetField(fieldName, AllFlags) ?? t.GetField($"<{fieldName}>k__BackingField", AllFlags);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        public static void DispatchManagedZeroArgPostfix(string methodKey, object instance)
        {
            if (string.IsNullOrEmpty(methodKey)) return;
            bool isStartAttack = methodKey.EndsWith("::StartAttack()", StringComparison.Ordinal);
            if (isStartAttack)
            {
                HarmonyBridge.EnsureCustomNailSlashColliderAndAnimReady(instance);
            }
            MethodHookChain chain;
            lock (_chainsByKey)
            {
                if (!_chainsByKey.TryGetValue(methodKey, out chain) || chain == null)
                    return;
            }
            object dummyRes = null;
            InvokeChainPostfixesAndFinalizers(chain, instance, Array.Empty<object>(), ref dummyRes, null, true);
            if (isStartAttack)
            {
                HarmonyBridge.EnsureCustomNailSlashColliderAndAnimReady(instance);
            }
        }

        public static bool DispatchPrefix(int chainId, object instance, object[] args, ref object resultBox)
        {
            if ((uint)chainId >= MaxChains) return true;
            var chain = _chainsById[chainId];
            if (chain == null) return true;

            // O(1) zero-reflection fast exit for LifecycleGuard-only chains when not extracting an additive scene
            if (chain.IsLifecycleGuardOnlyChain && !AssetHelperBridge.IsExtractingPharloomsGloryScene)
                return true;

            if (AssetHelperBridge.IsExtractingPharloomsGloryScene)
            {
                if (instance is UnityEngine.Component extComp)
                {
                    if (AssetHelperBridge.ShouldSkipComponentDuringSceneExtraction(extComp))
                        return false;
                }
                else if (instance == null && chain.IsCustomSceneManager)
                {
                    return false;
                }
            }

            // Per-patch FinalJudge scene check is enforced in CanInvokePatchForInstanceAndResult so shared FsmState::OnEnter chains (e.g. AuricAegis) are never blocked.

            if (chain.IsEnemyJournalManager && instance is UnityEngine.Component ejComp)
            {
                try
                {
                    if (ejComp.gameObject != null && ejComp.gameObject.name != "Journal")
                        return true;
                }
                catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L1104", __apiEx_6); }
            }

            if (chain.IsNailSlashStart && instance != null)
            {
                HarmonyBridge.EnsureCustomNailSlashFieldsReady_Prefix(instance);
            }

            int pmKind = chain.PlayMakerTargetKind;
            if (pmKind != 0 && instance != null)
            {
                if (pmKind == 1 && instance is HutongGames.PlayMaker.Fsm ownerFsm)
                {
                    PlayMakerHybridCLRBridge.EnsureFsmPreprocessedOnce(ownerFsm);
                }
                else if (pmKind == 2 && chain.HasModPlayMakerPatches && instance is PlayMakerFSM directPfsm && (chain.PrefixArray.Length > 0 || chain.PostfixArray.Length > 0))
                {
                    PlayMakerHybridCLRBridge.EnsureFsmPreprocessedOnce(directPfsm.Fsm);
                }
                else if (pmKind == 3 && instance is HutongGames.PlayMaker.FsmState directState)
                {
                    PlayMakerHybridCLRBridge.EnsureFsmPreprocessedOnce(directState.Fsm);
                    PlayMakerHybridCLRBridge.EnsureSingleStateLoadedAndLocked(directState);
                }
                else if (pmKind == 4 && instance is HutongGames.PlayMaker.FsmStateAction directAction)
                {
                    PlayMakerHybridCLRBridge.SanitizeSendRandomEventV4IfNeeded(directAction);
                }
            }

            if (args != null && args.Length == 1 && args[0] is string reqClip && reqClip == "DownSlash" &&
                instance is HeroAnimationController hacGet && chain.Key == "HeroAnimationController::GetClip(System.String)")
            {
                try
                {
                    if ((hacGet.config == null || hacGet.config.GetAnimationClip("DownSlash") == null) &&
                        hacGet.animator != null && hacGet.animator.GetClipByName("DownSlash") == null)
                    {
                        var v3Clip = hacGet.animator.GetClipByName("v3 Down Slash");
                        if (v3Clip != null)
                        {
                            resultBox = v3Clip;
                            return false;
                        }
                    }
                }
                catch { }
            }

            MonoModDetourEntry[] monoDetours = chain.MonoModDetourArray;
            if (monoDetours.Length > 0)
            {
                for (int m = 0; m < monoDetours.Length; m++)
                {
                    var d = monoDetours[m];
                    if (d == null || d.DetourMethod == null || d.OrigTrackDelegate == null) continue;
                    if (DisabledModHookOwners.Count > 0 && !string.IsNullOrEmpty(d.ModName) && DisabledModHookOwners.Contains(d.ModName))
                        continue;
                    try
                    {
                        int extra = chain.IsStatic ? 1 : 2;
                        int argLen = args != null ? args.Length : 0;
                        var dArgs = new object[extra + argLen];
                        dArgs[0] = d.OrigTrackDelegate;
                        if (!chain.IsStatic) dArgs[1] = instance;
                        for (int a = 0; a < argLen; a++) dArgs[extra + a] = args[a];

                        _monoModOrigCalled = false;
                        d.DetourMethod.Invoke(d.Target, dArgs);
                        if (!_monoModOrigCalled)
                        {
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(d.ModName, $"MonoModHook({chain.Key} -> {d.DetourMethod.Name})", ex);
                    }
                }
            }

            PatchMethodMetadata[] prefixes = chain.PrefixArray;
            if (prefixes.Length == 0)
            {
                if (chain.UsesState)
                {
                    lock (chain.CallFrames)
                    {
                        chain.CallFrames.Push(new ChainCallFrame { StateDict = null, RunOriginal = true, PrefixResult = null });
                        chain.PendingCallFramesCount = chain.CallFrames.Count;
                    }
                }
                return true;
            }

            string origArg0String = (args != null && args.Length > 0) ? (args[0] as string) : null;

            Dictionary<string, object> stateDict = chain.UsesState
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : null;

            bool runOriginal = true;

            for (int i = 0; i < prefixes.Length; i++)
            {
                var p = prefixes[i];
                if (p.IsSceneExtractionLifecycleGuard)
                {
                    if (!AssetHelperBridge.IsExtractingPharloomsGloryScene)
                        continue;
                    if (instance is UnityEngine.Component lgComp && !AssetHelperBridge.SceneExtractionLifecycleGuard_Prefix(lgComp))
                        runOriginal = false;
                    continue;
                }
                if (!CanInvokePatchForInstanceAndResult(p, instance, resultBox, args))
                    continue;
                try
                {
                    object stateVal = null;
                    string stateKey = p.StateKey;
                    if (stateDict != null)
                        stateDict.TryGetValue(stateKey, out stateVal);

                    object ret;
                    long t0 = EnableHookProfiler ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    if (p.IsToolGuardStringPrefix)
                    {
                        string toolNameArg = (args != null && args.Length > 0) ? args[0] as string : null;
                        if (string.IsNullOrEmpty(toolNameArg) || !PlayerData.HasInstance || PlayerData.instance == null)
                        {
                            resultBox = false;
                            ret = false;
                        }
                        else
                        {
                            ret = true;
                        }
                        if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                    }
                    else if (p.FastSingleInstanceInvoker != null)
                    {
                        try { ret = p.FastSingleInstanceInvoker(instance); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else if (p.FastZeroArgInvoker != null)
                    {
                        try { ret = p.FastZeroArgInvoker(); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else if (p.FastTwoArgInstArg0Invoker != null && args != null && args.Length > 0)
                    {
                        try { ret = p.FastTwoArgInstArg0Invoker(instance, args[0]); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else
                    {
                        var invokeArgs = PrepareInvokeArgs(p, chain, instance, args, resultBox, stateVal, runOriginal, null);
                        p.InUseDepth++;
                        try
                        {
                            ret = p.Method.Invoke(null, invokeArgs);
                        }
                        finally
                        {
                            p.InUseDepth--;
                            if (EnableHookProfiler)
                                RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                        }
                        if (p.HasAnyByRefBinding)
                        {
                            WriteBackInvokeArgs(p, chain, instance, args, ref resultBox, ref stateVal, invokeArgs);
                        }
                    }
                    if (stateDict != null)
                    {
                        stateDict[stateKey] = stateVal;
                    }

                    if (p.ReturnsBool && ret is bool b && !b)
                        runOriginal = false;
                    else if (ret is MonoDetour.DetourTypes.ReturnFlow rf && rf != MonoDetour.DetourTypes.ReturnFlow.None)
                        runOriginal = false;
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(p.ModName, $"Prefix({chain.Key} -> {p.Method.DeclaringType?.Name}.{p.Method.Name})", ex);
                }
            }

            if (!runOriginal)
            {
                if (ShouldForwardPrefixResultThroughPostfix(chain, resultBox))
                {
                    object[] savedArgsCopy = null;
                    if (args != null && args.Length > 0)
                    {
                        savedArgsCopy = new object[args.Length];
                        Array.Copy(args, savedArgsCopy, args.Length);
                    }
                    ToolItem savedWillThrow = null;
                    bool hasSavedWillThrow = false;
                    if (instance is HeroController hcInst)
                    {
                        savedWillThrow = hcInst.willThrowTool;
                        hasSavedWillThrow = true;
                    }
                    lock (chain.CallFrames)
                    {
                        chain.CallFrames.Push(new ChainCallFrame
                        {
                            StateDict = stateDict,
                            RunOriginal = false,
                            PrefixResult = resultBox,
                            SavedArgs = savedArgsCopy,
                            SavedWillThrowTool = savedWillThrow,
                            HasSavedWillThrowTool = hasSavedWillThrow
                        });
                        chain.PendingCallFramesCount = chain.CallFrames.Count;
                    }
                    return true;
                }

                InvokeChainPostfixesAndFinalizers(chain, instance, args, ref resultBox, stateDict, false);
                return false;
            }

            if (origArg0String != null && args != null && args.Length == 2 &&
                args[0] is string updatedClipName && !string.Equals(origArg0String, updatedClipName, StringComparison.Ordinal) &&
                instance is HeroAnimationController hacPlay && chain.Key == "HeroAnimationController::Play(System.String,System.Single)")
            {
                float startFrame = args[1] is float sf ? sf : 0f;
                hacPlay.Play(updatedClipName, startFrame);
                return false;
            }

            if (chain.UsesState)
            {
                lock (chain.CallFrames)
                {
                    chain.CallFrames.Push(new ChainCallFrame
                    {
                        StateDict = stateDict,
                        RunOriginal = true,
                        PrefixResult = resultBox
                    });
                    chain.PendingCallFramesCount = chain.CallFrames.Count;
                }
            }
            return true;
        }

        private static bool ShouldForwardPrefixResultThroughPostfix(MethodHookChain chain, object resultBox)
        {
            if (chain == null || chain.ReturnType == null || chain.ReturnType == typeof(void) || resultBox == null)
                return false;
            if (chain.IsUIManagerOrMenuScreen)
                return false;
            if (chain.ReturnType.IsValueType)
            {
                if (Equals(resultBox, chain.DefaultReturnValue))
                    return false;
            }
            return true;
        }

        public static void DispatchPostfix(int chainId, object instance, object[] args, ref object resultBox)
        {
            if ((uint)chainId >= MaxChains) return;
            var chain = _chainsById[chainId];
            if (chain == null) return;

            // Per-patch FinalJudge scene check is enforced in CanInvokePatchForInstanceAndResult so shared FsmState::OnEnter chains (e.g. AuricAegis) are never blocked.

            if (chain.IsEnemyJournalManager && instance is UnityEngine.Component ejComp)
            {
                try
                {
                    if (ejComp.gameObject != null && ejComp.gameObject.name != "Journal")
                        return;
                }
                catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L1273", __apiEx_7); }
            }

            Dictionary<string, object> stateDict = null;
            bool runOriginal = true;
            object prefixResult = null;
            object[] savedArgs = null;
            ToolItem savedWillThrow = null;
            bool hasSavedWillThrow = false;

            if (chain.PendingCallFramesCount > 0)
            {
                lock (chain.CallFrames)
                {
                    if (chain.CallFrames.Count > 0)
                    {
                        var frame = chain.CallFrames.Pop();
                        chain.PendingCallFramesCount = chain.CallFrames.Count;
                        stateDict = frame.StateDict;
                        runOriginal = frame.RunOriginal;
                        prefixResult = frame.PrefixResult;
                        savedArgs = frame.SavedArgs;
                        savedWillThrow = frame.SavedWillThrowTool;
                        hasSavedWillThrow = frame.HasSavedWillThrowTool;
                    }
                }
            }

            if (!runOriginal)
            {
                if (prefixResult != null)
                    resultBox = prefixResult;
                if (savedArgs != null && args != null && savedArgs.Length == args.Length)
                    Array.Copy(savedArgs, args, args.Length);
                if (hasSavedWillThrow && instance is HeroController hcPost && savedWillThrow != null)
                    hcPost.willThrowTool = savedWillThrow;
            }

            InvokeChainPostfixesAndFinalizers(chain, instance, args, ref resultBox, stateDict, runOriginal);
        }

        private static void InvokeChainPostfixesAndFinalizers(
            MethodHookChain chain,
            object instance,
            object[] args,
            ref object resultBox,
            Dictionary<string, object> stateDict,
            bool runOriginal)
        {
            PatchMethodMetadata[] postfixes = chain.PostfixArray;
            int pmKind = chain.PlayMakerTargetKind;
            if (pmKind == 2 && chain.HasModPlayMakerPatches && postfixes.Length > 0 && instance is PlayMakerFSM postPfsm)
            {
                PlayMakerHybridCLRBridge.EnsurePlayMakerFsmPreprocessedAndLoaded(postPfsm);
            }

            for (int i = 0; i < postfixes.Length; i++)
            {
                var p = postfixes[i];
                if (!CanInvokePatchForInstanceAndResult(p, instance, resultBox, args))
                    continue;
                try
                {
                    object stateVal = null;
                    string stateKey = p.StateKey;
                    if (stateDict != null)
                    {
                        stateDict.TryGetValue(stateKey, out stateVal);
                    }

                    object ret;
                    long t0 = EnableHookProfiler ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    if (p.FastSingleInstanceInvoker != null)
                    {
                        try { ret = p.FastSingleInstanceInvoker(instance); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else if (p.FastZeroArgInvoker != null)
                    {
                        try { ret = p.FastZeroArgInvoker(); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else if (p.FastTwoArgInstArg0Invoker != null && args != null && args.Length > 0)
                    {
                        try { ret = p.FastTwoArgInstArg0Invoker(instance, args[0]); }
                        finally { if (EnableHookProfiler) RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
                    }
                    else
                    {
                        var invokeArgs = PrepareInvokeArgs(p, chain, instance, args, resultBox, stateVal, runOriginal, null);
                        p.InUseDepth++;
                        try
                        {
                            ret = p.Method.Invoke(null, invokeArgs);
                        }
                        finally
                        {
                            p.InUseDepth--;
                            if (EnableHookProfiler)
                                RecordModHookTime(p.ModName, p.StateKey, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                        }
                        if (p.HasAnyByRefBinding)
                        {
                            WriteBackInvokeArgs(p, chain, instance, args, ref resultBox, ref stateVal, invokeArgs);
                        }
                    }
                    if (stateDict != null)
                    {
                        stateDict[stateKey] = stateVal;
                    }

                    if (p.Method.ReturnType != typeof(void) && chain.ReturnType != typeof(void) && chain.ReturnType.IsAssignableFrom(p.Method.ReturnType))
                    {
                        if (ret is System.Collections.IEnumerator retEnum && chain.ReturnType == typeof(System.Collections.IEnumerator))
                        {
                            var origEnum = resultBox as System.Collections.IEnumerator;
                            resultBox = new SafeCoroutineEnumerator(origEnum, retEnum, instance, p.ModName, chain.Key);
                        }
                        else
                        {
                            resultBox = ret;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(p.ModName, $"Postfix({chain.Key} -> {p.Method.DeclaringType?.Name}.{p.Method.Name})", ex);
                }
            }

            if (chain.IsNailSlashStart && instance != null)
            {
                HarmonyBridge.EnsureCustomNailSlashColliderActive_Postfix(instance);
            }

            if (pmKind == 2 && chain.HasModPlayMakerPatches && instance is PlayMakerFSM donePfsm)
            {
                PlayMakerHybridCLRBridge.FinalizePlayMakerFsmStatesAfterModPatch(donePfsm);
            }

            PatchMethodMetadata[] finalizers = chain.FinalizerArray;

            for (int i = 0; i < finalizers.Length; i++)
            {
                var f = finalizers[i];
                try
                {
                    object stateVal = null;
                    string stateKey = f.StateKey;
                    if (stateDict != null)
                    {
                        stateDict.TryGetValue(stateKey, out stateVal);
                    }

                    var invokeArgs = PrepareInvokeArgs(f, chain, instance, args, resultBox, stateVal, runOriginal, null);
                    f.Method.Invoke(null, invokeArgs);
                    if (f.HasAnyByRefBinding)
                    {
                        WriteBackInvokeArgs(f, chain, instance, args, ref resultBox, ref stateVal, invokeArgs);
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(f.ModName, $"Finalizer({chain.Key} -> {f.Method.DeclaringType?.Name}.{f.Method.Name})", ex);
                }
            }

            if (chain.IsNailSlashStart && instance != null)
            {
                HarmonyBridge.EnsureCustomNailSlashColliderAndAnimReady(instance);
            }
        }

        private static int _weaverCrestCacheFrame = -1;
        private static bool _weaverCrestEquippedCached;
        private static float _threadNeedleCacheTime = -10f;
        private static bool _threadNeedleActiveCached;

        private static bool IsWeaverCrestEquippedFast()
        {
            int f = Time.frameCount;
            if (f != _weaverCrestCacheFrame)
            {
                _weaverCrestCacheFrame = f;
                _weaverCrestEquippedCached = PlayerData.HasInstance && PlayerData.instance != null &&
                                             string.Equals(PlayerData.instance.CurrentCrestID, "Weaver_Codex", StringComparison.Ordinal);
            }
            return _weaverCrestEquippedCached;
        }

        private static bool IsWeaverOrThreadNeedleActiveFast()
        {
            if (IsWeaverCrestEquippedFast()) return true;
            float now = Time.unscaledTime;
            if (now - _threadNeedleCacheTime >= 0.5f)
            {
                _threadNeedleCacheTime = now;
                try
                {
                    _threadNeedleActiveCached = PlayerData.HasInstance && PlayerData.instance != null &&
                                                ToolItemManager.IsToolEquipped("Weaver_ThreadNeedle");
                }
                catch { _threadNeedleActiveCached = false; }
            }
            return _threadNeedleActiveCached;
        }

        private static bool CanInvokePatchForInstanceAndResult(
            PatchMethodMetadata meta,
            object instance,
            object resultBox,
            object[] targetArgs)
        {
            if (meta == null) return false;
            if (DisabledModHookOwners.Count > 0 && !string.IsNullOrEmpty(meta.ModName) && DisabledModHookOwners.Contains(meta.ModName))
                return false;
            if (meta.IsWeaverPerFrameCrestPatch)
            {
                if (Time.timeScale == 0f || !IsWeaverCrestEquippedFast())
                    return false;
            }
            else if (meta.IsWeaverPerFrameThreadNeedlePatch)
            {
                if (Time.timeScale == 0f || !IsWeaverOrThreadNeedleActiveFast())
                    return false;
            }
            if (meta.IsWeaverCostPatch && HarmonyBridge.FreeWeaverAttacksNoSilkDrain)
                return false;
            if (meta.IsFinalJudgeFsmStateEnter)
            {
                var gm = GameManager._instance;
                if (gm == null || !string.Equals(gm.sceneName, "Coral_Judge_Arena", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (!meta.NeedsAnyRuntimeTypeCheck || meta.Bindings == null)
                return true;

            try
            {
                for (int i = 0; i < meta.Bindings.Length; i++)
                {
                    var b = meta.Bindings[i];
                    if (!b.NeedsRuntimeTypeCheck || b.CleanType == null) continue;
                    if (b.Kind == PatchParamKind.Instance)
                    {
                        if (instance != null)
                        {
                            var it = instance.GetType();
                            if (it == null || !b.CleanType.IsAssignableFrom(it))
                                return false;
                        }
                    }
                    else if (b.Kind == PatchParamKind.Result)
                    {
                        if (resultBox != null)
                        {
                            var rt = resultBox.GetType();
                            if (rt == null || !b.CleanType.IsAssignableFrom(rt))
                                return false;
                        }
                    }
                    else if (b.Kind == PatchParamKind.TargetArgByIndex && targetArgs != null && b.TargetArgIndex >= 0 && b.TargetArgIndex < targetArgs.Length)
                    {
                        object argVal = targetArgs[b.TargetArgIndex];
                        if (argVal != null)
                        {
                            var at = argVal.GetType();
                            if ((at == null || !b.CleanType.IsAssignableFrom(at)) && !(b.CleanType.IsEnum && argVal is IConvertible))
                                return false;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        public static bool EnableHookProfiler;
        private static readonly object _hookProfilerLock = new object();
        private static readonly Dictionary<string, long> _modHookTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _modHookCalls = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _modHookMaxTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _methodHookTicks = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _methodHookCalls = new Dictionary<string, long>(StringComparer.Ordinal);
        public static readonly HashSet<string> DisabledModHookOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void RecordModHookTime(string modName, string methodKey, long elapsedStopwatchTicks)
        {
            try
            {
                string key = string.IsNullOrEmpty(modName) ? "UnknownMod" : modName;
                lock (_hookProfilerLock)
                {
                    _modHookTicks.TryGetValue(key, out long prevTicks);
                    _modHookTicks[key] = prevTicks + elapsedStopwatchTicks;
                    _modHookCalls.TryGetValue(key, out long prevCalls);
                    _modHookCalls[key] = prevCalls + 1L;
                    _modHookMaxTicks.TryGetValue(key, out long prevMax);
                    if (elapsedStopwatchTicks > prevMax) _modHookMaxTicks[key] = elapsedStopwatchTicks;
                    if (!string.IsNullOrEmpty(methodKey))
                    {
                        _methodHookTicks.TryGetValue(methodKey, out long pmTicks);
                        _methodHookTicks[methodKey] = pmTicks + elapsedStopwatchTicks;
                        _methodHookCalls.TryGetValue(methodKey, out long pmCalls);
                        _methodHookCalls[methodKey] = pmCalls + 1L;
                    }
                }
            }
            catch { }
        }

        public static void ResetHookProfiler()
        {
            lock (_hookProfilerLock)
            {
                _modHookTicks.Clear();
                _modHookCalls.Clear();
                _modHookMaxTicks.Clear();
                _methodHookTicks.Clear();
                _methodHookCalls.Clear();
            }
        }

        public static string GetSlowestProfiledModName()
        {
            List<KeyValuePair<string, long>> snapshot;
            lock (_hookProfilerLock)
            {
                snapshot = new List<KeyValuePair<string, long>>(_modHookTicks);
            }
            snapshot.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < snapshot.Count; i++)
            {
                var kv = snapshot[i];
                if (!kv.Key.StartsWith("SilksongMobileAPI", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kv.Key, "I18NBridge", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kv.Key, "AssetBundleShaderRebinder", StringComparison.OrdinalIgnoreCase) &&
                    !DisabledModHookOwners.Contains(kv.Key))
                {
                    return kv.Key;
                }
            }
            return null;
        }

        public static List<string> GetHookProfilerSummaryLines()
        {
            var lines = new List<string>();
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            List<KeyValuePair<string, long>> snapshot;
            Dictionary<string, long> callsSnap;
            Dictionary<string, long> maxSnap;
            lock (_hookProfilerLock)
            {
                snapshot = new List<KeyValuePair<string, long>>(_modHookTicks);
                callsSnap = new Dictionary<string, long>(_modHookCalls, StringComparer.OrdinalIgnoreCase);
                maxSnap = new Dictionary<string, long>(_modHookMaxTicks, StringComparer.OrdinalIgnoreCase);
            }
            snapshot.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < snapshot.Count; i++)
            {
                var kv = snapshot[i];
                callsSnap.TryGetValue(kv.Key, out long calls);
                maxSnap.TryGetValue(kv.Key, out long maxT);
                double totalMs = kv.Value * tickToMs;
                double avgUs = calls > 0 ? (totalMs * 1000.0) / calls : 0.0;
                double peakUs = maxT * tickToMs * 1000.0;
                string status = DisabledModHookOwners.Contains(kv.Key) ? " [PAUSED]" : "";
                lines.Add($"{kv.Key,-26}{status} | {totalMs,7:F2} ms | {calls,7} calls | avg {avgUs,5:F1} us | peak {peakUs,6:F1} us");
            }
            List<KeyValuePair<string, long>> mSnap;
            Dictionary<string, long> mCallsSnap;
            lock (_hookProfilerLock)
            {
                mSnap = new List<KeyValuePair<string, long>>(_methodHookTicks);
                mCallsSnap = new Dictionary<string, long>(_methodHookCalls, StringComparer.Ordinal);
            }
            if (mSnap.Count > 0)
            {
                mSnap.Sort((a, b) => b.Value.CompareTo(a.Value));
                lines.Add("--- Top 8 Hook Methods ---");
                int maxM = Math.Min(8, mSnap.Count);
                for (int i = 0; i < maxM; i++)
                {
                    var mkv = mSnap[i];
                    mCallsSnap.TryGetValue(mkv.Key, out long mc);
                    double mMs = mkv.Value * tickToMs;
                    double mAvgUs = mc > 0 ? (mMs * 1000.0) / mc : 0.0;
                    lines.Add($"  #{i + 1} {mkv.Key} | {mMs:F2} ms | {mc} calls | avg {mAvgUs:F1} us");
                }
            }
            if (lines.Count == 0)
                lines.Add("No hook calls recorded yet (enable profiler and play for a few seconds).");
            return lines;
        }

        private static object[] PrepareInvokeArgs(
            PatchMethodMetadata meta,
            MethodHookChain chain,
            object instance,
            object[] targetArgs,
            object resultBox,
            object stateVal,
            bool runOriginal,
            Exception ex)
        {
            var bindings = meta.Bindings;
            if (bindings == null || bindings.Length == 0) return Array.Empty<object>();
            if (meta.CanUseSingleInstanceFastPath && meta.SingleInstanceArgBuffer != null && meta.InUseDepth == 0)
            {
                meta.SingleInstanceArgBuffer[0] = instance;
                return meta.SingleInstanceArgBuffer;
            }

            // Reuse pre-allocated buffer when non-reentrant on main thread (eliminates per-frame object[] GC allocations!)
            object[] arr;
            if (meta.InUseDepth == 0)
            {
                arr = meta.ReusableArgBuffer ?? (meta.ReusableArgBuffer = new object[bindings.Length]);
            }
            else
            {
                arr = new object[bindings.Length];
            }
            for (int i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                switch (b.Kind)
                {
                    case PatchParamKind.Instance:
                        arr[i] = instance;
                        break;
                    case PatchParamKind.Result:
                        arr[i] = resultBox ?? b.DefaultValue;
                        break;
                    case PatchParamKind.State:
                        arr[i] = stateVal ?? b.DefaultValue;
                        break;
                    case PatchParamKind.OriginalMethod:
                        arr[i] = chain.TargetMethod;
                        break;
                    case PatchParamKind.ArgsArray:
                        arr[i] = targetArgs;
                        break;
                    case PatchParamKind.Exception:
                        arr[i] = ex;
                        break;
                    case PatchParamKind.RunOriginal:
                        arr[i] = runOriginal;
                        break;
                    case PatchParamKind.TargetArgByIndex:
                        if (targetArgs != null && b.TargetArgIndex >= 0 && b.TargetArgIndex < targetArgs.Length)
                            arr[i] = targetArgs[b.TargetArgIndex];
                        else
                            arr[i] = b.DefaultValue;
                        break;
                    case PatchParamKind.FieldAccess:
                        if (b.TargetField != null)
                        {
                            try { arr[i] = b.TargetField.GetValue(b.TargetField.IsStatic ? null : instance); }
                            catch { arr[i] = b.DefaultValue; }
                        }
                        else
                        {
                            arr[i] = b.DefaultValue;
                        }
                        break;
                    default:
                        arr[i] = b.DefaultValue;
                        break;
                }
            }
            return arr;
        }

        private static void WriteBackInvokeArgs(
            PatchMethodMetadata meta,
            MethodHookChain chain,
            object instance,
            object[] targetArgs,
            ref object resultBox,
            ref object stateVal,
            object[] invokeArgs)
        {
            var bindings = meta.Bindings;
            if (bindings == null || invokeArgs == null) return;

            for (int i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                if (!b.IsByRef) continue;

                switch (b.Kind)
                {
                    case PatchParamKind.Result:
                        resultBox = invokeArgs[i];
                        break;
                    case PatchParamKind.State:
                        stateVal = invokeArgs[i];
                        break;
                    case PatchParamKind.TargetArgByIndex:
                        if (targetArgs != null && b.TargetArgIndex >= 0 && b.TargetArgIndex < targetArgs.Length)
                            targetArgs[b.TargetArgIndex] = invokeArgs[i];
                        break;
                    case PatchParamKind.FieldAccess:
                        if (b.TargetField != null && !b.TargetField.IsInitOnly && !b.TargetField.IsLiteral)
                        {
                            try { b.TargetField.SetValue(b.TargetField.IsStatic ? null : instance, invokeArgs[i]); }
                            catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L1574", __apiEx_8); }
                        }
                        break;
                }
            }
        }

        public static HealthManager LastPreDamageZeroedHitHm;
        public static int LastPreDamageZeroedHitTargetHp;
        public static int LastPreDamageZeroedHitFrame = -1;

        public static void InvokePreDamageHitInstanceChains(HealthManager hm, int cardinalDirection, AttackTypes attackType, SpecialTypes specialType)
        {
            if (hm == null) return;
            MethodHookChain hitChain = null;
            lock (_chainsByKey)
            {
                _chainsByKey.TryGetValue("HealthManager::Hit(HitInstance)", out hitChain);
            }
            if (hitChain == null) return;
            var hitPrefixes = hitChain.PrefixArray;
            if (hitPrefixes == null || hitPrefixes.Length == 0) return;

            int defaultDmg = (PlayerData.instance != null && PlayerData.instance.nailDamage > 0) ? PlayerData.instance.nailDamage : 5;
            var synthHit = new HitInstance
            {
                Source = HeroController.SilentInstance != null ? HeroController.SilentInstance.gameObject : null,
                AttackType = attackType,
                SpecialType = specialType,
                DamageDealt = defaultDmg,
                Direction = cardinalDirection * 90f,
                Multiplier = 1f,
                MagnitudeMultiplier = 1f,
                IsNailTag = (attackType == AttackTypes.Nail)
            };
            object boxedHit = synthHit;
            object[] hitArgs = new object[] { boxedHit };
            object dummyHitRes = (hitChain.ReturnType != null && hitChain.ReturnType != typeof(void))
                ? (hitChain.ReturnType.IsValueType ? Activator.CreateInstance(hitChain.ReturnType) : null)
                : null;

            for (int i = 0; i < hitPrefixes.Length; i++)
            {
                var p = hitPrefixes[i];
                if (p.Method.DeclaringType?.Name == "EnemyHitContext") continue;
                try
                {
                    object stateVal = null;
                    var invokeArgs = PrepareInvokeArgs(p, hitChain, hm, hitArgs, dummyHitRes, stateVal, true, null);
                    p.Method.Invoke(null, invokeArgs);
                    if (p.HasAnyByRefBinding)
                        WriteBackInvokeArgs(p, hitChain, hm, hitArgs, ref dummyHitRes, ref stateVal, invokeArgs);
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.PreDamageHitPrefix({p.Method.Name})", ex);
                }
            }

            if (hitArgs[0] is HitInstance updatedHit && updatedHit.DamageDealt == 0 && defaultDmg > 0)
            {
                LastPreDamageZeroedHitHm = hm;
                LastPreDamageZeroedHitTargetHp = hm.hp;
                LastPreDamageZeroedHitFrame = Time.frameCount;
            }
        }

        public static void InvokeBridgedDamageHeroNailClash(DamageHero dh, float direction, string colliderTag, Vector3 clasherPos, bool isPrefix)
        {
            if (dh == null) return;

            // Pale Automaton parry freeze frame (matches Aeternal Everwatcher: FreezeMomentTypes.NailClashEffect = 3)
            if (!isPrefix &&
                (SilksongMobileAPIPlugin.CfgPaleAutomatonParryFreeze?.Value ?? true) &&
                AssetHelperBridge.IsPaleAutomatonBossSceneActive())
            {
                try
                {
                    var gm = GameManager.instance;
                    if (gm != null && !gm.TimeSlowed)
                    {
                        gm.FreezeMoment(3); // FreezeMomentTypes.NailClashEffect (0.01f, 0.25f, 0.1f, 0f)
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "PaleAutomatonParryFreeze", ex);
                }
            }

            MethodHookChain ncChain = _cachedNailClashChain;
            if (ncChain == null && !_nailClashChainLookupDone)
            {
                lock (_chainsByKey)
                {
                    if (!_chainsByKey.TryGetValue("DamageHero::NailClash(System.Single,System.String,UnityEngine.Vector3)", out ncChain))
                    {
                        foreach (var kv in _chainsByKey)
                        {
                            if (kv.Key.StartsWith("DamageHero::NailClash(", StringComparison.Ordinal))
                            {
                                ncChain = kv.Value;
                                break;
                            }
                        }
                    }
                    _cachedNailClashChain = ncChain;
                    _nailClashChainLookupDone = true;
                }
            }
            if (ncChain == null) return;
            var patches = isPrefix ? ncChain.PrefixArray : ncChain.PostfixArray;
            if (patches == null || patches.Length == 0) return;

            object[] ncArgs = new object[] { direction, colliderTag ?? "Nail Attack", clasherPos };
            object dummyRes = null;
            for (int i = 0; i < patches.Length; i++)
            {
                var p = patches[i];
                try
                {
                    object stateVal = null;
                    var invokeArgs = PrepareInvokeArgs(p, ncChain, dh, ncArgs, dummyRes, stateVal, isPrefix, null);
                    p.Method.Invoke(null, invokeArgs);
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(p.ModName, $"NailClashBridge.{(isPrefix ? "Prefix" : "Postfix")}({p.Method.Name})", ex);
                }
            }
        }

        public static void InvokeSkippedHitInstanceChains(HealthManager hm, ref HitInstance hit)
        {
            if (hm == null) return;
            if (ReferenceEquals(hm, LastPreDamageZeroedHitHm) && LastPreDamageZeroedHitFrame == Time.frameCount)
            {
                hm.hp = LastPreDamageZeroedHitTargetHp;
                hit.DamageDealt = 0;
                LastPreDamageZeroedHitHm = null;
            }
            object boxedHit = hit;

            // 0. HealthManager::Hit(HitInstance) (postfixes only; prefixes already ran pre-damage in InvokePreDamageHitInstanceChains)
            MethodHookChain hitChain = null;
            MethodHookChain tdChain = null;
            lock (_chainsByKey)
            {
                _chainsByKey.TryGetValue("HealthManager::Hit(HitInstance)", out hitChain);
                _chainsByKey.TryGetValue("HealthManager::TakeDamage(HitInstance)", out tdChain);
            }
            if (hitChain != null)
            {
                object[] hitArgs = new object[] { boxedHit };
                object dummyHitRes = (hitChain != null && hitChain.ReturnType != null && hitChain.ReturnType != typeof(void))
                    ? (hitChain.ReturnType.IsValueType ? Activator.CreateInstance(hitChain.ReturnType) : null)
                    : null;
                var hitPrefixes = hitChain.PrefixArray;
                for (int i = 0; i < hitPrefixes.Length; i++)
                {
                    var p = hitPrefixes[i];
                    if (p.Method.DeclaringType?.Name == "EnemyHitContext") continue;
                    // Skip prefixes that already executed in HealthManager_IsBlockingByDirection_Prefix
                    if (string.Equals(p.ModName, "AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.ModName, "PaleAutomaton", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, hitChain, hm, hitArgs, dummyHitRes, stateVal, true, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, hitChain, hm, hitArgs, ref dummyHitRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.HitPrefix({p.Method.Name})", ex);
                    }
                }
                if (hitArgs[0] is HitInstance updatedHit0)
                {
                    hit = updatedHit0;
                    boxedHit = hit;
                }
                var hitPostfixes = hitChain.PostfixArray;
                for (int i = 0; i < hitPostfixes.Length; i++)
                {
                    var p = hitPostfixes[i];
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, hitChain, hm, hitArgs, dummyHitRes, stateVal, false, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, hitChain, hm, hitArgs, ref dummyHitRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.HitPostfix({p.Method.Name})", ex);
                    }
                }
            }
            if (tdChain != null)
            {
                int origDamage = hit.DamageDealt;
                object[] tdArgs = new object[] { boxedHit };
                object dummyRes = null;
                var prefixes = tdChain.PrefixArray;
                for (int i = 0; i < prefixes.Length; i++)
                {
                    var p = prefixes[i];
                    if (p.Method.DeclaringType?.Name == "EnemyHitContext") continue;
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, tdChain, hm, tdArgs, dummyRes, stateVal, true, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, tdChain, hm, tdArgs, ref dummyRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.TakeDamage({p.Method.Name})", ex);
                    }
                }
                if (tdArgs[0] is HitInstance updatedTdHit)
                {
                    hit = updatedTdHit;
                    boxedHit = hit;
                    if (hit.DamageDealt != origDamage && hm.hp > 0 && !hm.isDead)
                    {
                        int diff = origDamage - hit.DamageDealt;
                        hm.hp = Math.Max(1, hm.hp + diff);
                    }
                }
            }

            // 2. HeroController::SilkGain(HitInstance) and HeroController::NailHitEnemy(HealthManager,HitInstance)
            var hc = HeroController.SilentInstance;
            if (hc != null && (hit.AttackType == AttackTypes.Nail || ((int)hit.AttackType == 16 && hit.IsNailTag)))
            {
                MethodHookChain sgChain = null;
                MethodHookChain nhChain = null;
                lock (_chainsByKey)
                {
                    _chainsByKey.TryGetValue("HeroController::SilkGain(HitInstance)", out sgChain);
                    _chainsByKey.TryGetValue("HeroController::NailHitEnemy(HealthManager,HitInstance)", out nhChain);
                }

                if (sgChain != null)
                {
                    object[] sgArgs = new object[] { boxedHit };
                    object dummyRes = null;
                    bool skipSilkGain = false;
                    var prefixes = sgChain.PrefixArray;
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        var p = prefixes[i];
                        try
                        {
                            object stateVal = null;
                            var invokeArgs = PrepareInvokeArgs(p, sgChain, hc, sgArgs, dummyRes, stateVal, true, null);
                            object retObj = p.Method.Invoke(null, invokeArgs);
                            if (retObj is bool runOrig && !runOrig)
                                skipSilkGain = true;
                            if (p.HasAnyByRefBinding)
                                WriteBackInvokeArgs(p, sgChain, hc, sgArgs, ref dummyRes, ref stateVal, invokeArgs);
                        }
                        catch (Exception ex)
                        {
                            SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.SilkGain({p.Method.Name})", ex);
                        }
                    }
                    if (sgArgs[0] is HitInstance updatedSgHit)
                    {
                        hit = updatedSgHit;
                        boxedHit = hit;
                    }
                    if (skipSilkGain)
                    {
                        // Vanilla HealthManager::TakeDamage already executed HeroController.SilkGain(hitInstance) at IL_0674;
                        // reverse the 1 silk gained when a prefix (e.g., CrossStitchMadness.HeroControllerPatch.SilkGainPrefix) returns false.
                        try
                        {
                            var pd = PlayerData.instance;
                            if (pd != null && pd.silk > 0)
                            {
                                hc.TakeSilk(1);
                            }
                        }
                        catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L1729", __apiEx_9); }
                    }
                    else
                    {
                        var sgPostfixes = sgChain.PostfixArray;
                        for (int i = 0; i < sgPostfixes.Length; i++)
                        {
                            var p = sgPostfixes[i];
                            try
                            {
                                object stateVal = null;
                                var invokeArgs = PrepareInvokeArgs(p, sgChain, hc, sgArgs, dummyRes, stateVal, false, null);
                                p.Method.Invoke(null, invokeArgs);
                                if (p.HasAnyByRefBinding)
                                    WriteBackInvokeArgs(p, sgChain, hc, sgArgs, ref dummyRes, ref stateVal, invokeArgs);
                            }
                            catch (Exception ex)
                            {
                                SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.SilkGainPostfix({p.Method.Name})", ex);
                            }
                        }
                        if (sgArgs[0] is HitInstance updatedSgHitPost)
                        {
                            hit = updatedSgHitPost;
                            boxedHit = hit;
                        }
                    }
                }

                if (nhChain != null)
                {
                    object[] nhArgs = new object[] { hm, boxedHit };
                    object dummyRes = null;
                    InvokeChainPostfixesAndFinalizers(nhChain, hc, nhArgs, ref dummyRes, null, true);
                }
            }

            // 3. HealthManager::TakeDamage(HitInstance) Postfixes (runs after SilkGain just like vanilla TakeDamage)
            if (tdChain != null)
            {
                object[] tdPostArgs = new object[] { boxedHit };
                object dummyTdRes = null;
                var tdPostfixes = tdChain.PostfixArray;
                for (int i = 0; i < tdPostfixes.Length; i++)
                {
                    var p = tdPostfixes[i];
                    if (p.Method.DeclaringType?.Name == "EnemyHitContext") continue;
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, tdChain, hm, tdPostArgs, dummyTdRes, stateVal, false, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, tdChain, hm, tdPostArgs, ref dummyTdRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"HitBridge.TakeDamagePostfix({p.Method.Name})", ex);
                    }
                }
                if (tdPostArgs[0] is HitInstance updatedTdPostHit)
                {
                    hit = updatedTdPostHit;
                    boxedHit = hit;
                }
            }

            // 4. HK1 Boss Pogo Bounce & Horizontal Recoil Fix (HK1 scenes only)
            try
            {
                if (hc != null && hc.cState != null && Time.unscaledTime - _lastHkPogoBounceTime > 0.08f)
                {
                    bool isNailLike = hit.IsNailDamage || hit.AttackType == AttackTypes.Nail;
                    bool isDownHit = !hc.cState.onGround && (hc.cState.downAttacking || hc.cState.downSpiking || (isNailLike && hit.Direction >= 225f && hit.Direction <= 315f));
                    bool isUpHit = !isDownHit && isNailLike && (hit.Direction >= 45f && hit.Direction <= 135f);
                    bool isHorizHit = !isDownHit && !isUpHit && isNailLike;
                    if (isDownHit || isHorizHit)
                    {
                        string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                        if (AssetHelperBridge.IsHkCoreSceneKey(activeScene, out _))
                        {
                            _lastHkPogoBounceTime = Time.unscaledTime;
                            if (!_hkPogoMethodsCached)
                            {
                                _hkPogoMethodsCached = true;
                                const BindingFlags pbf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                                _hcBounceMi = typeof(HeroController).GetMethod("Bounce", pbf, null, Type.EmptyTypes, null);
                                _hcResetAirMovesMi = typeof(HeroController).GetMethod("ResetAirMoves", pbf, null, Type.EmptyTypes, null);
                                _hcRecoil3Mi = typeof(HeroController).GetMethod("Recoil", pbf, null, new[] { typeof(bool), typeof(int), typeof(float) }, null);
                                _hcRecoilLeftMi = typeof(HeroController).GetMethod("RecoilLeft", pbf, null, Type.EmptyTypes, null);
                                _hcRecoilRightMi = typeof(HeroController).GetMethod("RecoilRight", pbf, null, Type.EmptyTypes, null);
                            }
                            if (isDownHit)
                            {
                                _hcBounceMi?.Invoke(hc, null);
                                _hcResetAirMovesMi?.Invoke(hc, null);
                            }
                            else if (isHorizHit)
                            {
                                bool hitToRight = (hit.Direction < 45f || hit.Direction > 315f) ? true : ((hit.Direction >= 135f && hit.Direction <= 225f) ? false : hc.cState.facingRight);
                                // Push Hornet opposite the slash direction using Silksong's native HeroController.Recoil(bool isRight, int steps, float velocity)
                                bool recoilRight = !hitToRight;
                                if (_hcRecoil3Mi != null)
                                    _hcRecoil3Mi.Invoke(hc, new object[] { recoilRight, 4, 12f });
                                else if (recoilRight)
                                    _hcRecoilRightMi?.Invoke(hc, null);
                                else
                                    _hcRecoilLeftMi?.Invoke(hc, null);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static float _lastHkPogoBounceTime = -1f;
        private static bool _hkPogoMethodsCached;
        private static MethodInfo _hcBounceMi;
        private static MethodInfo _hcResetAirMovesMi;
        private static MethodInfo _hcRecoil3Mi;
        private static MethodInfo _hcRecoilLeftMi;
        private static MethodInfo _hcRecoilRightMi;

        public static void InvokeBridgedHealthManagerDieChains(HealthManager hm, HitInstance lastHit)
        {
            if (hm == null) return;
            List<MethodHookChain> dieChains = null;
            lock (_chainsByKey)
            {
                foreach (var kv in _chainsByKey)
                {
                    if (kv.Key.StartsWith("HealthManager::Die(", StringComparison.Ordinal) && kv.Value != null)
                    {
                        if (dieChains == null) dieChains = new List<MethodHookChain>(2);
                        dieChains.Add(kv.Value);
                    }
                }
            }
            if (dieChains == null || dieChains.Count == 0) return;

            for (int c = 0; c < dieChains.Count; c++)
            {
                var dieChain = dieChains[c];
                int pLen = dieChain.TargetParams != null ? dieChain.TargetParams.Length : 0;
                var dieArgs = new object[pLen];
                if (pLen > 0) dieArgs[0] = (float?)lastHit.Direction;
                if (pLen > 1) dieArgs[1] = lastHit.AttackType;
                if (pLen > 2) dieArgs[2] = lastHit.NailElement;
                if (pLen > 3) dieArgs[3] = lastHit.Source;
                if (pLen > 4) dieArgs[4] = lastHit.IgnoreInvulnerable;
                if (pLen > 5) dieArgs[5] = lastHit.MagnitudeMultiplier;
                if (pLen > 6) dieArgs[6] = false;
                if (pLen > 7) dieArgs[7] = false;

                object dummyRes = null;
                var prefixes = dieChain.PrefixArray;
                for (int i = 0; i < prefixes.Length; i++)
                {
                    var p = prefixes[i];
                    if (!CanInvokePatchForInstanceAndResult(p, hm, dummyRes, dieArgs)) continue;
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, dieChain, hm, dieArgs, dummyRes, stateVal, true, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, dieChain, hm, dieArgs, ref dummyRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"DieBridge.Prefix({p.Method.Name})", ex);
                    }
                }
                var postfixes = dieChain.PostfixArray;
                for (int i = 0; i < postfixes.Length; i++)
                {
                    var p = postfixes[i];
                    if (!CanInvokePatchForInstanceAndResult(p, hm, dummyRes, dieArgs)) continue;
                    try
                    {
                        object stateVal = null;
                        var invokeArgs = PrepareInvokeArgs(p, dieChain, hm, dieArgs, dummyRes, stateVal, true, null);
                        p.Method.Invoke(null, invokeArgs);
                        if (p.HasAnyByRefBinding)
                            WriteBackInvokeArgs(p, dieChain, hm, dieArgs, ref dummyRes, ref stateVal, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException(p.ModName, $"DieBridge.Postfix({p.Method.Name})", ex);
                    }
                }
            }
        }

        public static object GetDefaultValue(Type t)
        {
            if (t == null || !t.IsValueType || t == typeof(void)) return null;
            lock (_defaultValueCache)
            {
                if (_defaultValueCache.TryGetValue(t, out var cached))
                    return cached;
                object created = null;
                try { created = Activator.CreateInstance(t); } catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("RuntimeHybridCLRAdapterBuilder", "L1805", __apiEx_10); }
                _defaultValueCache[t] = created;
                return created;
            }
        }
    }
}
