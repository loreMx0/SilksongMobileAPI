using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilksongMobileAPI
{
    public static class PlayMakerHybridCLRBridge
    {
        private static bool _installed;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, FsmStateAction[]> _customActionsByState = new System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, FsmStateAction[]>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, object> _loadedStates = new System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, object>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, object> _lockedStates = new System.Runtime.CompilerServices.ConditionalWeakTable<FsmState, object>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Fsm, object> _preprocessedFsms = new System.Runtime.CompilerServices.ConditionalWeakTable<Fsm, object>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FsmStateAction, object> _sanitizedActionInstances = new System.Runtime.CompilerServices.ConditionalWeakTable<FsmStateAction, object>();
        private static readonly Dictionary<Type, bool> _isCustomTypeCache = new Dictionary<Type, bool>(128);
        private static readonly object _typeCacheLock = new object();
        private static FieldInfo _actionsLoadedField;
        private static FieldInfo _actionsField;
        private static MethodInfo _loadActionsMethod;
        private static MethodInfo _loadActionsPre;
        private static MethodInfo _loadActionsPost;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                _actionsLoadedField = typeof(FsmState).GetField("actionsLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _actionsField = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                _loadActionsMethod = typeof(FsmState).GetMethod("LoadActions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _loadActionsPre = typeof(PlayMakerHybridCLRBridge).GetMethod(nameof(FsmState_LoadActions_Prefix), BindingFlags.Static | BindingFlags.Public);
                _loadActionsPost = typeof(PlayMakerHybridCLRBridge).GetMethod(nameof(FsmState_LoadActions_Postfix), BindingFlags.Static | BindingFlags.Public);

                if (_loadActionsMethod != null && _loadActionsPre != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(
                        "SilksongMobileAPI.PlayMakerBridge",
                        _loadActionsMethod,
                        _loadActionsPre,
                        _loadActionsPost,
                        null,
                        HarmonyLib.Priority.First,
                        "SilksongMobileAPI.PlayMakerBridge");
                }

                SilksongMobileAPIPlugin.LogDiagnosticInfo("[PlayMakerHybridCLRBridge] Installed PlayMaker + FsmUtil Android IL2CPP bridge hooks (zero-overhead LoadActions guard).");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PlayMakerHybridCLRBridge] Hook install warning: {ex.Message}");
            }
        }

        public static void ReassertLoadActionsHook()
        {
            try
            {
                if (_loadActionsMethod == null)
                    _loadActionsMethod = typeof(FsmState).GetMethod("LoadActions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_loadActionsPre == null)
                    _loadActionsPre = typeof(PlayMakerHybridCLRBridge).GetMethod(nameof(FsmState_LoadActions_Prefix), BindingFlags.Static | BindingFlags.Public);
                if (_loadActionsPost == null)
                    _loadActionsPost = typeof(PlayMakerHybridCLRBridge).GetMethod(nameof(FsmState_LoadActions_Postfix), BindingFlags.Static | BindingFlags.Public);

                if (_loadActionsMethod != null && _loadActionsPre != null)
                {
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(
                        "SilksongMobileAPI.PlayMakerBridge",
                        _loadActionsMethod,
                        _loadActionsPre,
                        _loadActionsPost,
                        null,
                        HarmonyLib.Priority.First,
                        "SilksongMobileAPI.PlayMakerBridge");
                }
            }
            catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L83", __apiEx_1); }
        }

        public static void FsmUtil_InsertMethod_State(FsmState state, int index, Action method, bool everyFrame)
        {
            if (state == null || method == null) return;
            try { Silksong.FsmUtil.FsmUtil.InsertMethod(state, index, method, everyFrame); } catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L89", __apiEx_2); }
        }

        public static void FsmUtil_InsertMethod_StateReversed(FsmState state, Action method, int index, bool everyFrame)
        {
            if (state == null || method == null) return;
            try { Silksong.FsmUtil.FsmUtil.InsertMethod(state, index, method, everyFrame); } catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L95", __apiEx_3); }
        }

        public static void FsmUtil_InsertMethod_Fsm(PlayMakerFSM fsm, string stateName, int index, Action method, bool everyFrame)
        {
            if (fsm == null || method == null) return;
            try { Silksong.FsmUtil.FsmUtil.InsertMethod(fsm, stateName, index, method, everyFrame); } catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L101", __apiEx_4); }
        }

        public static void FsmUtil_AddMethod_State(FsmState state, Action method, bool everyFrame)
        {
            if (state == null || method == null) return;
            try { Silksong.FsmUtil.FsmUtil.AddMethod(state, method, everyFrame); } catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L107", __apiEx_5); }
        }

        public static void FsmUtil_InsertActionBefore(FsmStateAction targetAction, FsmStateAction newAction)
        {
            if (targetAction == null || newAction == null) return;
            try
            {
                Silksong.FsmUtil.FsmUtil.InsertActionBefore(targetAction, newAction);
            }
            catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L117", __apiEx_6); }
        }

        public static void FsmUtil_AddMethod_Fsm(PlayMakerFSM fsm, string stateName, Action method, bool everyFrame)
        {
            if (fsm == null || method == null) return;
            try { Silksong.FsmUtil.FsmUtil.AddMethod(fsm, stateName, method, everyFrame); } catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L123", __apiEx_7); }
        }

        public static FsmState GetFsmStateSafe(Fsm fsm, string stateName)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName)) return null;
            try
            {
                if (fsm.Owner is PlayMakerFSM ownerPfsm)
                {
                    if (!fsm.Initialized)
                    {
                        try { fsm.Init(ownerPfsm); } catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L135", __apiEx_8); }
                    }
                    try { EnsureFsmPreprocessedOnce(fsm); } catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L137", __apiEx_9); }
                }
                var st = fsm.GetState(stateName);
                if (st != null)
                {
                    if (_stateFsmBackingField != null && _stateFsmBackingField.GetValue(st) == null)
                    {
                        try { _stateFsmBackingField.SetValue(st, fsm); } catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L144", __apiEx_10); }
                    }
                    EnsureSingleStateLoadedAndLocked(st);
                }
                return st;
            }
            catch
            {
                return fsm.GetState(stateName);
            }
        }

        private static readonly Dictionary<Type, FieldInfo[]> _cachedCloneableFieldsByType = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] GetCloneableFieldsCached(Type t)
        {
            lock (_cachedCloneableFieldsByType)
            {
                if (_cachedCloneableFieldsByType.TryGetValue(t, out var cached))
                    return cached;
            }
            var list = new List<FieldInfo>(16);
            var cur = t;
            const BindingFlags FFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            while (cur != null && cur != typeof(object))
            {
                var fields = cur.GetFields(FFlags);
                for (int i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    if (!f.IsStatic && !f.IsInitOnly && !f.IsLiteral)
                        list.Add(f);
                }
                cur = cur.BaseType;
            }
            var arr = list.ToArray();
            lock (_cachedCloneableFieldsByType)
            {
                _cachedCloneableFieldsByType[t] = arr;
            }
            return arr;
        }

        public static FsmStateAction CloneActionInstanceSafe(FsmStateAction src, FsmState targetState)
        {
            if (src == null) return null;
            try
            {
                var t = src.GetType();
                var clone = Activator.CreateInstance(t) as FsmStateAction;
                if (clone == null) return src;
                var fields = GetCloneableFieldsCached(t);
                for (int i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    try { f.SetValue(clone, f.GetValue(src)); } catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L173", __apiEx_11); }
                }
                if (targetState != null)
                {
                    try { clone.Init(targetState); } catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L179", __apiEx_12); }
                }
                return clone;
            }
            catch
            {
                return src;
            }
        }

        public static FsmState CloneFsmStateSafe(FsmState sourceState)
        {
            if (sourceState == null) return null;
            EnsureSingleStateLoadedAndLocked(sourceState);
            var copy = new FsmState(sourceState);
            try
            {
                var srcActions = GetStateActionsSafe(sourceState);
                if (srcActions != null && srcActions.Length > 0)
                {
                    var clonedList = new List<FsmStateAction>(srcActions.Length);
                    for (int i = 0; i < srcActions.Length; i++)
                    {
                        var srcAct = srcActions[i];
                        if (srcAct == null) continue;
                        // Match PC PlayMaker FsmState(source) semantics: runtime FsmUtil delegate/lambda
                        // actions are not in serialized actionData and must not be copied by CopyState
                        // (fixes LostAndChained Idle -> Idle Choice -> Idle 1 1000-loop in Attack Control).
                        if (srcAct is Silksong.FsmUtil.Actions.LambdaAction) continue;
                        var actType = srcAct.GetType();
                        if (actType.IsGenericType && actType.GetGenericTypeDefinition() == typeof(Silksong.FsmUtil.Actions.DelegateAction<>))
                            continue;
                        clonedList.Add(CloneActionInstanceSafe(srcAct, copy));
                    }
                    SetStateActionsSafe(copy, clonedList.ToArray());
                }
                if (sourceState.Transitions != null && copy.Transitions != null && copy.Transitions.Length == sourceState.Transitions.Length)
                {
                    for (int i = 0; i < copy.Transitions.Length; i++)
                    {
                        if (copy.Transitions[i] != null && sourceState.Transitions[i] != null)
                        {
                            copy.Transitions[i].ToFsmState = sourceState.Transitions[i].ToFsmState;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L217", __apiEx_13); }
            return copy;
        }

        private static readonly FieldInfo _stateFsmBackingField = typeof(FsmState).GetField("fsm", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static Fsm GetStateFsmQuiet(FsmState state)
        {
            if (state == null) return null;
            try
            {
                if (_stateFsmBackingField != null)
                    return _stateFsmBackingField.GetValue(state) as Fsm;
            }
            catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L231", __apiEx_14); }
            return null;
        }

        private static object CreateDefaultPlayMakerWrapperField(Type ft)
        {
            if (ft == typeof(FsmFloat)) return new FsmFloat { UseVariable = true };
            if (ft == typeof(FsmInt)) return new FsmInt { UseVariable = true };
            if (ft == typeof(FsmBool)) return new FsmBool { UseVariable = true };
            if (ft == typeof(FsmString)) return new FsmString { UseVariable = true, Value = "" };
            if (ft == typeof(FsmVector2)) return new FsmVector2 { UseVariable = true };
            if (ft == typeof(FsmVector3)) return new FsmVector3 { UseVariable = true };
            if (ft == typeof(FsmColor)) return new FsmColor { UseVariable = true };
            if (ft == typeof(FsmRect)) return new FsmRect { UseVariable = true };
            if (ft == typeof(FsmQuaternion)) return new FsmQuaternion { UseVariable = true };
            if (ft == typeof(FsmGameObject)) return new FsmGameObject { UseVariable = true };
            if (ft == typeof(FsmObject)) return new FsmObject { UseVariable = true };
            if (ft == typeof(FsmMaterial)) return new FsmMaterial { UseVariable = true };
            if (ft == typeof(FsmTexture)) return new FsmTexture { UseVariable = true };
            if (ft == typeof(FsmEnum)) return new FsmEnum { UseVariable = true };
            if (ft == typeof(FsmArray)) return new FsmArray { UseVariable = true };
            if (ft == typeof(FsmOwnerDefault)) return new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
            if (ft == typeof(FsmEventTarget)) return new FsmEventTarget();
            return null;
        }

        private static void SanitizeSingleActionFieldsIfNeeded(FsmState state, FsmStateAction a)
        {
            if (a == null) return;
            try
            {
                // Fast-path explicit checks for PlayMaker actions constructed in C# without calling Reset()
                if (a is HutongGames.PlayMaker.Actions.SetVelocity2d sv2d)
                {
                    if (sv2d.gameObject == null) sv2d.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                    if (sv2d.vector == null) sv2d.vector = new FsmVector2 { UseVariable = true };
                    if (sv2d.x == null) sv2d.x = new FsmFloat { UseVariable = true };
                    if (sv2d.y == null) sv2d.y = new FsmFloat { UseVariable = true };
                    var ownerFsm = state?.Fsm ?? GetStateFsmQuiet(state);
                    if (ownerFsm != null) ownerFsm.HandleFixedUpdate = true;
                }
                else if (a is HutongGames.PlayMaker.Actions.DecelerateV2 dec2)
                {
                    if (dec2.gameObject == null) dec2.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                    if (dec2.deceleration == null) dec2.deceleration = new FsmFloat { UseVariable = true };
                    var ownerFsm = state?.Fsm ?? GetStateFsmQuiet(state);
                    if (ownerFsm != null) ownerFsm.HandleFixedUpdate = true;
                }
                else if (a is HutongGames.PlayMaker.Actions.AccelerateToXByScale accX)
                {
                    if (accX.gameObject == null) accX.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                    if (accX.accelerationFactor == null) accX.accelerationFactor = new FsmFloat { UseVariable = true };
                    if (accX.targetSpeed == null) accX.targetSpeed = new FsmFloat { UseVariable = true };
                    var ownerFsm = state?.Fsm ?? GetStateFsmQuiet(state);
                    if (ownerFsm != null) ownerFsm.HandleFixedUpdate = true;
                }
                else if (a is HutongGames.PlayMaker.Actions.SendEvent se)
                {
                    if (se.eventTarget == null) se.eventTarget = new FsmEventTarget();
                    if (se.delay == null) se.delay = new FsmFloat { Value = 0f };
                }
                else if (a is HutongGames.PlayMaker.Actions.SendEventByName sebn)
                {
                    if (sebn.eventTarget == null) sebn.eventTarget = new FsmEventTarget();
                    if (sebn.sendEvent == null) sebn.sendEvent = new FsmString { Value = "" };
                    if (sebn.delay == null) sebn.delay = new FsmFloat { Value = 0f };
                }
                else if (a is HutongGames.PlayMaker.Actions.FaceObjectV2 fo2)
                {
                    if (fo2.objectA == null) fo2.objectA = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                    if (fo2.objectB == null) fo2.objectB = new FsmGameObject { UseVariable = true };
                    if (fo2.spriteFacesRight == null) fo2.spriteFacesRight = new FsmBool { Value = false };
                    if (fo2.newAnimationClip == null) fo2.newAnimationClip = new FsmString { UseVariable = true, Value = "" };
                }

                bool alreadySanitized;
                lock (_sanitizedActionInstances)
                {
                    alreadySanitized = _sanitizedActionInstances.TryGetValue(a, out _);
                    if (!alreadySanitized)
                        _sanitizedActionInstances.Add(a, new object());
                }
                if (!alreadySanitized)
                {
                    var fields = GetCloneableFieldsCached(a.GetType());
                    for (int fIdx = 0; fIdx < fields.Length; fIdx++)
                    {
                        var f = fields[fIdx];
                        var ft = f.FieldType;
                        if (typeof(NamedVariable).IsAssignableFrom(ft) || ft == typeof(FsmOwnerDefault) || ft == typeof(FsmEventTarget))
                        {
                            if (f.GetValue(a) == null)
                            {
                                var defVal = CreateDefaultPlayMakerWrapperField(ft);
                                if (defVal != null)
                                    f.SetValue(a, defVal);
                            }
                        }
                    }
                    if (state != null && (state.Fsm != null || GetStateFsmQuiet(state) != null))
                    {
                        try { a.OnPreprocess(); } catch { }
                    }
                }
            }
            catch (System.Exception __apiEx_SanAct) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "SanitizeSingleActionFieldsIfNeeded", __apiEx_SanAct); }
        }

        private static void SanitizeStateActionsIfNeeded(FsmState state, FsmStateAction[] acts)
        {
            if (acts == null || acts.Length == 0) return;
            for (int i = 0; i < acts.Length; i++)
            {
                var a = acts[i];
                if (a == null) continue;
                if (state != null && (a.State != state || a.Fsm == null))
                {
                    try { a.Init(state); } catch { }
                }
                SanitizeSingleActionFieldsIfNeeded(state, a);
                if (a is HutongGames.PlayMaker.Actions.SendRandomEventV4 ||
                    a is HutongGames.PlayMaker.Actions.SendRandomEventV3 ||
                    a is HutongGames.PlayMaker.Actions.SendRandomEventV3ActiveBool ||
                    a is HutongGames.PlayMaker.Actions.SendRandomEventV2)
                {
                    SanitizeSendRandomEventV4IfNeeded(a);
                }
            }
        }

        public static void SetFsmStatesSafe(Fsm fsm, FsmState[] states)
        {
            if (fsm == null) return;
            try
            {
                fsm.States = states;
                if (states != null)
                {
                    for (int i = 0; i < states.Length; i++)
                    {
                        var st = states[i];
                        if (st == null) continue;
                        if (_stateFsmBackingField != null && _stateFsmBackingField.GetValue(st) == null)
                        {
                            try { _stateFsmBackingField.SetValue(st, fsm); } catch { }
                        }
                        var acts = _actionsField != null ? _actionsField.GetValue(st) as FsmStateAction[] : null;
                        if (acts != null && acts.Length > 0)
                        {
                            SanitizeStateActionsIfNeeded(st, acts);
                            lock (_customActionsByState)
                            {
                                _customActionsByState.Remove(st);
                                _customActionsByState.Add(st, acts);
                            }
                            LockStateSafe(st);
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_SetStates)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "SetFsmStatesSafe", __apiEx_SetStates);
                try { fsm.States = states; } catch { }
            }
        }

        public static FsmStateAction[] GetStateActionsSafe(FsmState state)
        {
            if (state == null) return Array.Empty<FsmStateAction>();
            var ownerFsm = GetStateFsmQuiet(state);
            if (ownerFsm != null && !ownerFsm.Initialized && ownerFsm.Owner is PlayMakerFSM ownerPfsm)
            {
                try { ownerFsm.Init(ownerPfsm); } catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L241", __apiEx_15); }
            }
            EnsureSingleStateLoadedAndLocked(state);
            var acts = (_actionsField != null ? _actionsField.GetValue(state) as FsmStateAction[] : null) ?? (ownerFsm != null ? state.Actions : null);
            if (acts != null && acts.Length > 0)
            {
                try
                {
                    SanitizeStateActionsIfNeeded(state, acts);
                    LockStateSafe(state);
                    lock (_customActionsByState)
                    {
                        _customActionsByState.Remove(state);
                        _customActionsByState.Add(state, acts);
                    }
                }
                catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L259", __apiEx_16); }
                return acts;
            }
            return acts ?? Array.Empty<FsmStateAction>();
        }

        public static void SetStateActionsSafe(FsmState state, FsmStateAction[] actions)
        {
            if (state == null) return;
            try
            {
                if (actions == null)
                {
                    lock (_lockedStates) { _lockedStates.Remove(state); }
                    lock (_loadedStates) { _loadedStates.Remove(state); }
                    lock (_customActionsByState) { _customActionsByState.Remove(state); }
                    _actionsLoadedField?.SetValue(state, false);
                    _actionsField?.SetValue(state, null);
                    try { state.Actions = null; } catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L277", __apiEx_17); }
                    return;
                }
                var safeActions = actions;
                state.Actions = safeActions;
                _actionsField?.SetValue(state, safeActions);
                SanitizeStateActionsIfNeeded(state, safeActions);
                lock (_customActionsByState)
                {
                    _customActionsByState.Remove(state);
                    _customActionsByState.Add(state, safeActions);
                }
                LockStateSafe(state);
            }
            catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L298", __apiEx_19); }
        }

        public static void CopyActionDataSafe(FsmState targetState, FsmState sourceState)
        {
            if (targetState == null || sourceState == null) return;
            try
            {
                EnsureSingleStateLoadedAndLocked(sourceState);
                lock (_lockedStates) { _lockedStates.Remove(targetState); }
                lock (_loadedStates) { _loadedStates.Remove(targetState); }
                lock (_customActionsByState) { _customActionsByState.Remove(targetState); }
                _actionsLoadedField?.SetValue(targetState, false);
                _actionsField?.SetValue(targetState, null);
                try { targetState.CopyActionData(sourceState); } catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L312", __apiEx_20); }
                try { targetState.LoadActions(); } catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L313", __apiEx_21); }
                var tActs = _actionsField != null ? _actionsField.GetValue(targetState) as FsmStateAction[] : null;
                var sActs = _actionsField != null ? _actionsField.GetValue(sourceState) as FsmStateAction[] : null;
                if ((tActs == null || tActs.Length == 0) && sActs != null && sActs.Length > 0)
                {
                    var cloned = new FsmStateAction[sActs.Length];
                    for (int i = 0; i < sActs.Length; i++)
                    {
                        cloned[i] = CloneActionInstanceSafe(sActs[i], targetState);
                    }
                    SetStateActionsSafe(targetState, cloned);
                }
            }
            catch (System.Exception __apiEx_22) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L326", __apiEx_22); }
        }

        public static void LockStateSafe(FsmState state)
        {
            if (state == null) return;
            try
            {
                lock (_lockedStates)
                {
                    if (!_lockedStates.TryGetValue(state, out _))
                        _lockedStates.Add(state, new object());
                }
                _actionsLoadedField?.SetValue(state, true);
            }
            catch (System.Exception __apiEx_23) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L341", __apiEx_23); }
        }

        public static void EnsureSingleStateLoadedAndLocked(FsmState state)
        {
            if (state == null) return;
            try
            {
                bool isLocked;
                lock (_lockedStates)
                {
                    isLocked = _lockedStates.TryGetValue(state, out _);
                }
                if (isLocked)
                {
                    var lockedActs = _actionsField != null ? _actionsField.GetValue(state) as FsmStateAction[] : null;
                    if (lockedActs != null && lockedActs.Length > 0)
                        SanitizeStateActionsIfNeeded(state, lockedActs);
                    if (state.Transitions == null) state.Transitions = Array.Empty<FsmTransition>();
                    return;
                }

                // CRITICAL: Read _actionsField directly so we never trigger FsmState.get_Actions -> LoadActions() before EnsureFsmPreprocessedOnce!
                var acts = _actionsField != null ? _actionsField.GetValue(state) as FsmStateAction[] : null;
                if (acts == null || acts.Length == 0)
                {
                    var quietFsm = GetStateFsmQuiet(state);
                    try
                    {
                        if (quietFsm != null)
                        {
                            if (!quietFsm.Initialized && quietFsm.Owner is PlayMakerFSM qOwnerPfsm)
                            {
                                try { quietFsm.Init(qOwnerPfsm); } catch (System.Exception __apiEx_24) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L371", __apiEx_24); }
                            }
                            EnsureFsmPreprocessedOnce(quietFsm);
                        }
                    }
                    catch (System.Exception __apiEx_25) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L376", __apiEx_25); }

                    bool shouldLoad = false;
                    lock (_loadedStates)
                    {
                        if (!_loadedStates.TryGetValue(state, out _))
                            shouldLoad = true;
                    }
                    if (shouldLoad && quietFsm != null && quietFsm.Initialized)
                    {
                        try { state.LoadActions(); } catch (System.Exception __apiEx_26) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L386", __apiEx_26); }
                        var loadedActs = _actionsField != null ? _actionsField.GetValue(state) as FsmStateAction[] : null;
                        if (loadedActs != null && loadedActs.Length > 0)
                        {
                            SanitizeStateActionsIfNeeded(state, loadedActs);
                            lock (_loadedStates)
                            {
                                if (!_loadedStates.TryGetValue(state, out _))
                                    _loadedStates.Add(state, new object());
                            }
                        }
                    }
                }
                else
                {
                    SanitizeStateActionsIfNeeded(state, acts);
                }
                if (state.Transitions == null) state.Transitions = Array.Empty<FsmTransition>();
            }
            catch (System.Exception __apiEx_27) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L400", __apiEx_27); }
        }

        public static void EnsurePlayMakerFsmPreprocessedAndLoaded(PlayMakerFSM pfsm)
        {
            if (pfsm == null) return;
            try
            {
                var fsm = pfsm.Fsm;
                if (fsm != null)
                {
                    lock (_preprocessedFsms)
                    {
                        if (_preprocessedFsms.TryGetValue(fsm, out _)) return;
                        _preprocessedFsms.Add(fsm, new object());
                    }
                }
                if (fsm == null || string.IsNullOrEmpty(fsm.ActiveStateName))
                {
                    try { pfsm.Preprocess(); } catch (System.Exception __apiEx_28) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L419", __apiEx_28); }
                }
            }
            catch (System.Exception __apiEx_29) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L422", __apiEx_29); }
        }

        public static void FinalizePlayMakerFsmStatesAfterModPatch(PlayMakerFSM pfsm)
        {
            if (pfsm == null) return;
            try
            {
                var fsm = pfsm.Fsm;
                var states = fsm?.States;
                if (states == null) return;
                for (int i = 0; i < states.Length; i++)
                {
                    var st = states[i];
                    if (st == null) continue;
                    // CRITICAL: Only inspect already-loaded `actions` backing field! Calling `st.Actions` here forces
                    // premature `FsmState.LoadActions()` on every untouched state during PlayMakerFSM.Awake().
                    var acts = _actionsField != null ? _actionsField.GetValue(st) as FsmStateAction[] : null;
                    if (acts != null && acts.Length > 0)
                    {
                        SanitizeStateActionsIfNeeded(st, acts);
                        st.Actions = acts;
                        lock (_customActionsByState)
                        {
                            _customActionsByState.Remove(st);
                            _customActionsByState.Add(st, acts);
                        }
                        LockStateSafe(st);
                    }
                }
            }
            catch (System.Exception __apiEx_31) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L459", __apiEx_31); }
        }

        private static bool HasCustomOrModAction(FsmStateAction[] actions)
        {
            if (actions == null || actions.Length == 0) return false;
            for (int i = 0; i < actions.Length; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                var t = a.GetType();
                bool isCustom;
                lock (_typeCacheLock)
                {
                    if (!_isCustomTypeCache.TryGetValue(t, out isCustom))
                    {
                        string asmName = t.Assembly?.GetName()?.Name ?? "";
                        isCustom = (asmName != "PlayMaker" && asmName != "Assembly-CSharp");
                        _isCustomTypeCache[t] = isCustom;
                    }
                }
                if (isCustom) return true;
            }
            return false;
        }

        [ThreadStatic]
        private static bool _inLoadActionsPrefix;

        public static bool FsmState_LoadActions_Prefix(FsmState __instance)
        {
            if (__instance == null || _inLoadActionsPrefix) return true;
            _inLoadActionsPrefix = true;
            try
            {
                // CRITICAL: NEVER access `__instance.Actions` property getter inside FsmState_LoadActions_Prefix!
                // When PlayMaker enters an unvisited state for the first time, `actionsLoaded` is false and `actions` is null;
                // reading `__instance.Actions` calls `if (!this.actionsLoaded) this.LoadActions()`, causing infinite recursion!
                var rawExisting = _actionsField != null ? _actionsField.GetValue(__instance) as FsmStateAction[] : null;

                bool isLocked;
                lock (_lockedStates)
                {
                    isLocked = _lockedStates.TryGetValue(__instance, out _);
                }
                if (isLocked)
                {
                    FsmStateAction[] savedLocked = null;
                    lock (_customActionsByState)
                    {
                        _customActionsByState.TryGetValue(__instance, out savedLocked);
                    }
                    if (savedLocked != null && (rawExisting == null || (rawExisting.Length == 0 && savedLocked.Length > 0)))
                    {
                        _actionsField?.SetValue(__instance, savedLocked);
                        _actionsLoadedField?.SetValue(__instance, true);
                    }
                    else if (rawExisting == null)
                    {
                        _actionsField?.SetValue(__instance, Array.Empty<FsmStateAction>());
                        _actionsLoadedField?.SetValue(__instance, true);
                    }
                    var curLocked = _actionsField != null ? _actionsField.GetValue(__instance) as FsmStateAction[] : null;
                    if (curLocked != null && curLocked.Length > 0)
                        SanitizeStateActionsIfNeeded(__instance, curLocked);
                    return false;
                }

                FsmStateAction[] savedCustom = null;
                lock (_customActionsByState)
                {
                    _customActionsByState.TryGetValue(__instance, out savedCustom);
                }
                if (savedCustom != null && savedCustom.Length > 0)
                {
                    var cur = (rawExisting != null && rawExisting.Length > 0) ? rawExisting : savedCustom;
                    _actionsField?.SetValue(__instance, cur);
                    LockStateSafe(__instance);
                    return false;
                }

                if (rawExisting != null && rawExisting.Length > 0)
                {
                    SanitizeStateActionsIfNeeded(__instance, rawExisting);
                    lock (_customActionsByState)
                    {
                        _customActionsByState.Remove(__instance);
                        _customActionsByState.Add(__instance, rawExisting);
                    }
                    LockStateSafe(__instance);
                    return false;
                }
            }
            catch (System.Exception __apiEx_32) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L548", __apiEx_32); }
            finally
            {
                _inLoadActionsPrefix = false;
            }
            return true;
        }

        public static void FsmState_LoadActions_Postfix(FsmState __instance)
        {
            if (__instance == null) return;
            try
            {
                FsmStateAction[] saved = null;
                lock (_customActionsByState)
                {
                    _customActionsByState.TryGetValue(__instance, out saved);
                }
                if (saved != null && saved.Length > 0)
                {
                    __instance.Actions = saved;
                    for (int i = 0; i < saved.Length; i++)
                    {
                        if (saved[i] != null && (saved[i].State != __instance || saved[i].Fsm == null))
                            saved[i].Init(__instance);
                    }
                }
            }
            catch (System.Exception __apiEx_33) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L576", __apiEx_33); }
        }

        public static void EnsureFsmPreprocessedOnce(Fsm fsm)
        {
            if (fsm == null) return;
            try
            {
                lock (_preprocessedFsms)
                {
                    if (_preprocessedFsms.TryGetValue(fsm, out _)) return;
                    _preprocessedFsms.Add(fsm, new object());
                }
                if (fsm.Owner is PlayMakerFSM pfsm && string.IsNullOrEmpty(fsm.ActiveStateName))
                {
                    try { pfsm.Preprocess(); } catch (System.Exception __apiEx_34) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L591", __apiEx_34); }
                }
            }
            catch (System.Exception __apiEx_35) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L594", __apiEx_35); }
        }

        public static void FinalizeSingleFsmStateAfterModPatch(FsmState st)
        {
            if (st == null) return;
            try
            {
                var acts = _actionsField != null ? _actionsField.GetValue(st) as FsmStateAction[] : null;
                if (acts != null && acts.Length > 0)
                {
                    bool hasCustom = false;
                    for (int j = 0; j < acts.Length; j++)
                    {
                        var a = acts[j];
                        if (a == null) continue;
                        if (a.State != st || a.Fsm == null)
                        {
                            try { a.Init(st); } catch (System.Exception __apiEx_36) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L612", __apiEx_36); }
                        }
                        SanitizeSingleActionFieldsIfNeeded(st, a);
                        SanitizeSendRandomEventV4IfNeeded(a);
                    }
                    if (HasCustomOrModAction(acts))
                    {
                        hasCustom = true;
                        lock (_customActionsByState)
                        {
                            _customActionsByState.Remove(st);
                            _customActionsByState.Add(st, acts);
                        }
                    }
                    if (hasCustom)
                        LockStateSafe(st);
                }
            }
            catch (System.Exception __apiEx_37) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L629", __apiEx_37); }
        }

        public static void SanitizeSendRandomEventV4IfNeeded(FsmStateAction action)
        {
            if (action == null) return;
            try
            {
                const BindingFlags FFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (action is HutongGames.PlayMaker.Actions.SendRandomEventV4 sre4)
                {
                    if (sre4.events == null) return;
                    int n = sre4.events.Length;

                    if (sre4.weights == null || sre4.weights.Length != n)
                    {
                        var nw = new FsmFloat[n];
                        for (int i = 0; i < n; i++)
                            nw[i] = (sre4.weights != null && i < sre4.weights.Length && sre4.weights[i] != null) ? sre4.weights[i] : new FsmFloat { Value = 1f };
                        sre4.weights = nw;
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                            if (sre4.weights[i] == null) sre4.weights[i] = new FsmFloat { Value = 1f };
                    }

                    if (sre4.eventMax == null || sre4.eventMax.Length != n)
                    {
                        var nem = new FsmInt[n];
                        for (int i = 0; i < n; i++)
                            nem[i] = (sre4.eventMax != null && i < sre4.eventMax.Length && sre4.eventMax[i] != null) ? sre4.eventMax[i] : new FsmInt { Value = 99 };
                        sre4.eventMax = nem;
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                            if (sre4.eventMax[i] == null) sre4.eventMax[i] = new FsmInt { Value = 99 };
                    }

                    if (sre4.missedMax == null || sre4.missedMax.Length != n)
                    {
                        var nmm = new FsmInt[n];
                        for (int i = 0; i < n; i++)
                            nmm[i] = (sre4.missedMax != null && i < sre4.missedMax.Length && sre4.missedMax[i] != null) ? sre4.missedMax[i] : new FsmInt { Value = 99 };
                        sre4.missedMax = nmm;
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                            if (sre4.missedMax[i] == null) sre4.missedMax[i] = new FsmInt { Value = 99 };
                    }

                    var trField = typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4).GetField("trackingInts", FFlags);
                    var trMissedField = typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4).GetField("trackingIntsMissed", FFlags);
                    var setupField = typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4).GetField("setupArrays", FFlags);

                    var tr = trField?.GetValue(sre4) as int[];
                    if (tr == null || tr.Length != n)
                    {
                        var ntr = new int[n];
                        if (tr != null)
                        {
                            int copyLen = Math.Min(tr.Length, n);
                            Array.Copy(tr, ntr, copyLen);
                        }
                        for (int i = 0; i < n; i++)
                        {
                            int maxV = sre4.eventMax[i] != null ? sre4.eventMax[i].Value : 99;
                            if (ntr[i] < 0 || (maxV > 0 && ntr[i] > maxV))
                                ntr[i] = 0;
                        }
                        trField?.SetValue(sre4, ntr);
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int maxV = sre4.eventMax[i] != null ? sre4.eventMax[i].Value : 99;
                            if (tr[i] < 0 || (maxV > 0 && tr[i] > maxV))
                                tr[i] = 0;
                        }
                    }

                    var trm = trMissedField?.GetValue(sre4) as int[];
                    if (trm == null || trm.Length != n)
                    {
                        var ntm = new int[n];
                        if (trm != null)
                        {
                            int copyLen = Math.Min(trm.Length, n);
                            Array.Copy(trm, ntm, copyLen);
                        }
                        for (int i = 0; i < n; i++)
                        {
                            if (ntm[i] < 0) ntm[i] = 0;
                        }
                        trMissedField?.SetValue(sre4, ntm);
                    }

                    setupField?.SetValue(sre4, true);

                    if (sre4.activeBool == null)
                    {
                        sre4.activeBool = new FsmBool { UseVariable = true, Value = true };
                    }
                    return;
                }

                var t = action.GetType();
                if (!t.Name.StartsWith("SendRandomEvent", StringComparison.Ordinal)) return;
                var events = t.GetField("events", FFlags)?.GetValue(action) as FsmEvent[];
                if (events == null) return;
                int len = events.Length;

                var weightsField = t.GetField("weights", FFlags);
                var weights = weightsField?.GetValue(action) as FsmFloat[];
                if (weights == null || weights.Length != len)
                {
                    var nw = new FsmFloat[len];
                    for (int i = 0; i < len; i++)
                        nw[i] = (weights != null && i < weights.Length && weights[i] != null) ? weights[i] : new FsmFloat { Value = 1f };
                    weightsField?.SetValue(action, nw);
                }

                var eventMaxField = t.GetField("eventMax", FFlags);
                if (eventMaxField != null && eventMaxField.FieldType == typeof(FsmInt[]))
                {
                    var eventMax = eventMaxField.GetValue(action) as FsmInt[];
                    if (eventMax == null || eventMax.Length != len)
                    {
                        var nem = new FsmInt[len];
                        for (int i = 0; i < len; i++)
                            nem[i] = (eventMax != null && i < eventMax.Length && eventMax[i] != null) ? eventMax[i] : new FsmInt { Value = 99 };
                        eventMaxField.SetValue(action, nem);
                    }
                }

                var missedMaxField = t.GetField("missedMax", FFlags);
                if (missedMaxField != null && missedMaxField.FieldType == typeof(FsmInt[]))
                {
                    var missedMax = missedMaxField.GetValue(action) as FsmInt[];
                    if (missedMax == null || missedMax.Length != len)
                    {
                        var nmm = new FsmInt[len];
                        for (int i = 0; i < len; i++)
                            nmm[i] = (missedMax != null && i < missedMax.Length && missedMax[i] != null) ? missedMax[i] : new FsmInt { Value = 99 };
                        missedMaxField.SetValue(action, nmm);
                    }
                }

                var trackingField = t.GetField("trackingInts", FFlags);
                if (trackingField != null)
                {
                    if (trackingField.FieldType == typeof(int[]))
                    {
                        var tr = trackingField.GetValue(action) as int[];
                        if (tr == null || tr.Length != len)
                        {
                            var ntr = new int[len];
                            if (tr != null) Array.Copy(tr, ntr, Math.Min(tr.Length, len));
                            trackingField.SetValue(action, ntr);
                        }
                    }
                    else if (trackingField.FieldType == typeof(FsmInt[]))
                    {
                        var tracking = trackingField.GetValue(action) as FsmInt[];
                        if (tracking == null || tracking.Length != len)
                        {
                            var ntr = new FsmInt[len];
                            for (int i = 0; i < len; i++)
                                ntr[i] = (tracking != null && i < tracking.Length && tracking[i] != null) ? tracking[i] : new FsmInt { Value = 0 };
                            trackingField.SetValue(action, ntr);
                        }
                    }
                }

                var trackingMissedField = t.GetField("trackingIntsMissed", FFlags);
                if (trackingMissedField != null)
                {
                    if (trackingMissedField.FieldType == typeof(int[]))
                    {
                        var trm = trackingMissedField.GetValue(action) as int[];
                        if (trm == null || trm.Length != len)
                        {
                            var ntm = new int[len];
                            if (trm != null) Array.Copy(trm, ntm, Math.Min(trm.Length, len));
                            trackingMissedField.SetValue(action, ntm);
                        }
                    }
                    else if (trackingMissedField.FieldType == typeof(FsmInt[]))
                    {
                        var trackingMissed = trackingMissedField.GetValue(action) as FsmInt[];
                        if (trackingMissed == null || trackingMissed.Length != len)
                        {
                            var ntm = new FsmInt[len];
                            for (int i = 0; i < len; i++)
                                ntm[i] = (trackingMissed != null && i < trackingMissed.Length && trackingMissed[i] != null) ? trackingMissed[i] : new FsmInt { Value = 0 };
                            trackingMissedField.SetValue(action, ntm);
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_38) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("PlayMakerHybridCLRBridge", "L724", __apiEx_38); }
        }

        public static void FsmState_OnEnter_Prefix(FsmState __instance)
        {
            // Kept for binary compatibility if referenced; no longer hooked natively to avoid per-state-transition lag.
        }
    }
}
