using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilksongMobileAPI
{
    public static class BepInExShims
    {
        public static UnityEngine.GradientColorKey CreateGradientColorKey(UnityEngine.Color col, float time)
        {
            UnityEngine.GradientColorKey key = default;
            key.color = col;
            key.time = time;
            return key;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Struct | AttributeTargets.Delegate, AllowMultiple = true, Inherited = false)]
    public class MobileHarmonyPatchMetaAttribute : HarmonyLib.HarmonyPatch
    {
        public string DeclaringTypeName;
        public string MethodName;
        public int MethodTypeInt;
        public string ArgumentTypeNamesJoined;

        public MobileHarmonyPatchMetaAttribute(string declaringTypeName, string methodName, int methodTypeInt, string argumentTypeNamesJoined)
        {
            DeclaringTypeName = declaringTypeName ?? "";
            MethodName = methodName ?? "";
            MethodTypeInt = methodTypeInt;
            ArgumentTypeNamesJoined = argumentTypeNamesJoined ?? "";
        }
    }
}

namespace BepInEx.Preloader.Core.Patching
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PatcherAutoPluginAttribute : Attribute
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public PatcherAutoPluginAttribute(string id = "", string name = "", string version = "")
        {
            Id = id; Name = name; Version = version;
        }
    }
}

namespace BepInEx
{
    [Flags]
    public enum DependencyFlags
    {
        None = 0,
        HardDependency = 1,
        SoftDependency = 2
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : Attribute
    {
        public string DependencyGUID { get; set; }
        public DependencyFlags Flags { get; set; }
        public Version MinimumVersion { get; set; }

        public BepInDependency(string DependencyGUID, DependencyFlags Flags = DependencyFlags.HardDependency)
        {
            this.DependencyGUID = DependencyGUID;
            this.Flags = Flags;
        }

        public BepInDependency(string DependencyGUID, string MinimumVersion)
        {
            this.DependencyGUID = DependencyGUID;
            this.Flags = DependencyFlags.HardDependency;
            if (!string.IsNullOrEmpty(MinimumVersion) && Version.TryParse(MinimumVersion, out var v))
                this.MinimumVersion = v;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInProcess : Attribute
    {
        public string ProcessName { get; set; }
        public BepInProcess(string ProcessName) { this.ProcessName = ProcessName; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInIncompatibility : Attribute
    {
        public string IncompatibilityGUID { get; set; }
        public BepInIncompatibility(string IncompatibilityGUID) { this.IncompatibilityGUID = IncompatibilityGUID; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class BepInAutoPluginAttribute : Attribute
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public BepInAutoPluginAttribute(string id = "", string name = "", string version = "")
        {
            Id = id; Name = name; Version = version;
        }
    }

    public interface IInputSystem
    {
        bool GetKey(KeyCode key);
        bool GetKeyDown(KeyCode key);
        bool GetKeyUp(KeyCode key);
        bool GetKey(string name);
        bool GetKeyDown(string name);
        bool GetKeyUp(string name);
        bool GetMouseButton(int button);
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        void ResetInputAxes();
        Vector3 mousePosition { get; }
        bool anyKey { get; }
        bool anyKeyDown { get; }
        IEnumerable<KeyCode> SupportedKeyCodes { get; }
    }

    public static class MobileVirtualInput
    {
        private static readonly HashSet<KeyCode> _downThisFrame = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> _upThisFrame = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> _heldKeys = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> _manualHeldKeys = new HashSet<KeyCode>();
        private static readonly Dictionary<KeyCode, int> _pulseDownFrames = new Dictionary<KeyCode, int>();
        private static readonly Dictionary<KeyCode, int> _pulseUpFrames = new Dictionary<KeyCode, int>();
        private static readonly Dictionary<KeyCode, float> _pulseHoldUntil = new Dictionary<KeyCode, float>();
        private static readonly List<KeyValuePair<KeyCode, float>> _delayedPulses = new List<KeyValuePair<KeyCode, float>>();
        private static readonly object _sync = new object();
        private static int _lastFrameUpdated = -1;
        private static bool _inNativeInputQuery;
        public static string LastPulsedKeySummary = "";

        private static KeyCode GetPairedNumericKey(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return (KeyCode)((int)KeyCode.Keypad0 + ((int)key - (int)KeyCode.Alpha0));
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return (KeyCode)((int)KeyCode.Alpha0 + ((int)key - (int)KeyCode.Keypad0));
            return KeyCode.None;
        }

        private static void ApplySinglePulseLocked(KeyCode key, float now, float holdSeconds)
        {
            if (key == KeyCode.None) return;
            _downThisFrame.Add(key);
            _heldKeys.Add(key);
            // Set to 1 so Input.GetKeyDown(key) is true for EXACTLY ONE full Update()->LateUpdate()->OnGUI() cycle!
            _pulseDownFrames[key] = 1;
            _pulseHoldUntil[key] = now + Mathf.Max(0.08f, holdSeconds);
            _pulseUpFrames.Remove(key);
            _upThisFrame.Remove(key);

            KeyCode paired = GetPairedNumericKey(key);
            if (paired != KeyCode.None)
            {
                _downThisFrame.Add(paired);
                _heldKeys.Add(paired);
                _pulseDownFrames[paired] = 1;
                _pulseHoldUntil[paired] = now + Mathf.Max(0.08f, holdSeconds);
                _pulseUpFrames.Remove(paired);
                _upThisFrame.Remove(paired);
            }
        }

        public static void PulseKey(KeyCode key, float holdSeconds = 0.16f)
        {
            if (key == KeyCode.None) return;
            EnsureFrameState();
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L156", __apiEx_1); }
            lock (_sync)
            {
                ApplySinglePulseLocked(key, now, holdSeconds);
                LastPulsedKeySummary = $"{key} (pulsed at {DateTime.Now:HH:mm:ss})";
            }
        }

        public static void PulseKeyWithDelayedRepeat(KeyCode key, float delaySeconds = 0.05f)
        {
            if (key == KeyCode.None) return;
            // Schedule a SINGLE pulse after the Mod Console closes (at +0.04s) so GetKeyDown fires
            // for exactly 1 frame when the game/mod Update() is active, never toggling twice!
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L175", __apiEx_2); }
            lock (_sync)
            {
                _delayedPulses.Add(new KeyValuePair<KeyCode, float>(key, now + Mathf.Clamp(delaySeconds, 0.03f, 0.08f)));
                LastPulsedKeySummary = $"{key} (queued at {DateTime.Now:HH:mm:ss})";
            }
        }

        public static void PulseShortcut(BepInEx.Configuration.KeyboardShortcut shortcut, bool withDelayedRepeat = false)
        {
            if (shortcut.MainKey == KeyCode.None) return;
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch { }
            lock (_sync)
            {
                foreach (var modKey in shortcut.Modifiers)
                {
                    if (modKey != KeyCode.None)
                    {
                        _heldKeys.Add(modKey);
                        _pulseHoldUntil[modKey] = now + 0.35f;
                    }
                }
            }
            if (withDelayedRepeat)
                PulseKeyWithDelayedRepeat(shortcut.MainKey, 0.05f);
            else
                PulseKey(shortcut.MainKey, 0.18f);
            lock (_sync)
            {
                LastPulsedKeySummary = $"{shortcut} (pulsed at {DateTime.Now:HH:mm:ss})";
            }
        }

        public static bool IsKeyManuallyHeld(KeyCode key)
        {
            lock (_sync) { return _manualHeldKeys.Contains(key); }
        }

        public static void SetKeyHeld(KeyCode key, bool held)
        {
            if (key == KeyCode.None) return;
            EnsureFrameState();
            lock (_sync)
            {
                KeyCode paired = GetPairedNumericKey(key);
                if (held)
                {
                    _manualHeldKeys.Add(key);
                    if (_heldKeys.Add(key))
                    {
                        _downThisFrame.Add(key);
                        _pulseDownFrames[key] = 1;
                    }
                    if (paired != KeyCode.None)
                    {
                        _manualHeldKeys.Add(paired);
                        if (_heldKeys.Add(paired))
                        {
                            _downThisFrame.Add(paired);
                            _pulseDownFrames[paired] = 1;
                        }
                    }
                }
                else
                {
                    _manualHeldKeys.Remove(key);
                    _pulseHoldUntil.Remove(key);
                    if (_heldKeys.Remove(key))
                    {
                        _upThisFrame.Add(key);
                        _pulseUpFrames[key] = 1;
                    }
                    if (paired != KeyCode.None)
                    {
                        _manualHeldKeys.Remove(paired);
                        _pulseHoldUntil.Remove(paired);
                        if (_heldKeys.Remove(paired))
                        {
                            _upThisFrame.Add(paired);
                            _pulseUpFrames[paired] = 1;
                        }
                    }
                }
                LastPulsedKeySummary = $"{key} {(held ? "[HELD]" : "[RELEASED]")} ({DateTime.Now:HH:mm:ss})";
            }
        }

        public static void EnsureFrameState()
        {
            int frame = -1;
            float now = 0f;
            try
            {
                frame = Time.frameCount;
                now = Time.realtimeSinceStartup;
            }
            catch { return; }

            if (frame == _lastFrameUpdated) return;

            lock (_sync)
            {
                _lastFrameUpdated = frame;
                _downThisFrame.Clear();
                _upThisFrame.Clear();

                for (int i = _delayedPulses.Count - 1; i >= 0; i--)
                {
                    var dp = _delayedPulses[i];
                    if (now >= dp.Value)
                    {
                        _delayedPulses.RemoveAt(i);
                        ApplySinglePulseLocked(dp.Key, now, 0.18f);
                    }
                }

                if (_pulseDownFrames.Count > 0)
                {
                    var keys = new List<KeyCode>(_pulseDownFrames.Keys);
                    foreach (var k in keys)
                    {
                        int rem = _pulseDownFrames[k];
                        if (rem > 0)
                        {
                            _downThisFrame.Add(k);
                            _heldKeys.Add(k);
                            if (rem - 1 <= 0)
                                _pulseDownFrames.Remove(k);
                            else
                                _pulseDownFrames[k] = rem - 1;
                        }
                        else
                        {
                            _pulseDownFrames.Remove(k);
                        }
                    }
                }

                if (_pulseHoldUntil.Count > 0)
                {
                    var keys = new List<KeyCode>(_pulseHoldUntil.Keys);
                    foreach (var k in keys)
                    {
                        if (now < _pulseHoldUntil[k])
                        {
                            _heldKeys.Add(k);
                        }
                        else
                        {
                            _pulseHoldUntil.Remove(k);
                            if (!_manualHeldKeys.Contains(k))
                            {
                                _heldKeys.Remove(k);
                                _upThisFrame.Add(k);
                                _pulseUpFrames[k] = 1;
                            }
                        }
                    }
                }

                if (_pulseUpFrames.Count > 0)
                {
                    var keys = new List<KeyCode>(_pulseUpFrames.Keys);
                    foreach (var k in keys)
                    {
                        int rem = _pulseUpFrames[k];
                        if (rem > 0)
                        {
                            _upThisFrame.Add(k);
                            if (rem - 1 <= 0)
                                _pulseUpFrames.Remove(k);
                            else
                                _pulseUpFrames[k] = rem - 1;
                        }
                        else
                        {
                            _pulseUpFrames.Remove(k);
                        }
                    }
                }
            }
        }

        public static bool HasActiveKey(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            EnsureFrameState();
            lock (_sync)
            {
                return _downThisFrame.Contains(key) || _heldKeys.Contains(key);
            }
        }

        public static bool HasActiveKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            EnsureFrameState();
            lock (_sync)
            {
                return _downThisFrame.Contains(key);
            }
        }

        public static bool HasActiveKeyUp(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            EnsureFrameState();
            lock (_sync)
            {
                return _upThisFrame.Contains(key);
            }
        }

        public static bool HasActiveKeyString(string name)
        {
            return TryParseKey(name, out var kc) && HasActiveKey(kc);
        }

        public static bool HasActiveKeyDownString(string name)
        {
            return TryParseKey(name, out var kc) && HasActiveKeyDown(kc);
        }

        public static bool HasActiveKeyUpString(string name)
        {
            return TryParseKey(name, out var kc) && HasActiveKeyUp(kc);
        }

        public static bool HasAnyKey()
        {
            EnsureFrameState();
            lock (_sync)
            {
                return _downThisFrame.Count > 0 || _heldKeys.Count > 0;
            }
        }

        public static bool HasAnyKeyDown()
        {
            EnsureFrameState();
            lock (_sync)
            {
                return _downThisFrame.Count > 0;
            }
        }

        private static readonly Dictionary<int, KeyCode> _inControlKeyMap = new Dictionary<int, KeyCode>();
        private static Type _inControlKeyType;
        private static bool _inControlKeyTypeLookedUp;

        public static KeyCode InControlKeyToUnityKeyCode(int inControlKey)
        {
            if (inControlKey <= 0) return KeyCode.None;
            lock (_inControlKeyMap)
            {
                if (_inControlKeyMap.TryGetValue(inControlKey, out var cached))
                    return cached;
            }
            KeyCode mapped = KeyCode.None;
            try
            {
                if (!_inControlKeyTypeLookedUp)
                {
                    _inControlKeyTypeLookedUp = true;
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (asm == null) continue;
                        var t = asm.GetType("InControl.Key", false);
                        if (t != null) { _inControlKeyType = t; break; }
                    }
                }
                if (_inControlKeyType != null)
                {
                    string name = Enum.GetName(_inControlKeyType, inControlKey);
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (name.StartsWith("Key", StringComparison.Ordinal) && name.Length == 4 && char.IsDigit(name[3]))
                            name = "Alpha" + name[3];
                        else if (name == "Control") name = "LeftControl";
                        else if (name == "Shift") name = "LeftShift";
                        else if (name == "Alt") name = "LeftAlt";
                        else if (name == "Command") name = "LeftCommand";
                        if (!Enum.TryParse<KeyCode>(name, true, out mapped))
                            mapped = KeyCode.None;
                    }
                }
            }
            catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L440", __apiEx_3); }
            lock (_inControlKeyMap)
            {
                _inControlKeyMap[inControlKey] = mapped;
            }
            return mapped;
        }

        public static bool HasActiveInControlKey(int inControlKey)
        {
            KeyCode kc = InControlKeyToUnityKeyCode(inControlKey);
            return kc != KeyCode.None && HasActiveKey(kc);
        }

        // ---- Hero input via reflection ----
        // InControl.dll is not in the build lib folder, so this code must not name any InControl
        // type (or a HeroActions-typed local) at compile time. Members are bound once by name.
        private static bool _heroInputBound;
        private static bool _heroInputBindFailed;
        private static FieldInfo _fiInputActions, _fiUp, _fiDown, _fiLeft, _fiRight, _fiMove, _fiDream;
        private static PropertyInfo _piIsPressed, _piWasPressed, _piWasReleased, _piVector;
        private static int _heroCacheFrame = -1;
        private static readonly Dictionary<int, bool> _heroCache = new Dictionary<int, bool>(32);

        private static void BindHeroInput()
        {
            _heroInputBound = true;
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _fiInputActions = typeof(InputHandler).GetField("inputActions", F);
                if (_fiInputActions == null) { _heroInputBindFailed = true; return; }
                Type haType = _fiInputActions.FieldType;
                _fiUp = haType.GetField("Up", F);
                _fiDown = haType.GetField("Down", F);
                _fiLeft = haType.GetField("Left", F);
                _fiRight = haType.GetField("Right", F);
                _fiMove = haType.GetField("MoveVector", F);
                _fiDream = haType.GetField("DreamNail", F);
                if (_fiUp == null || _fiDown == null || _fiLeft == null || _fiRight == null || _fiMove == null || _fiDream == null)
                { _heroInputBindFailed = true; return; }
                Type actType = _fiUp.FieldType;
                _piIsPressed = actType.GetProperty("IsPressed", F);
                _piWasPressed = actType.GetProperty("WasPressed", F);
                _piWasReleased = actType.GetProperty("WasReleased", F);
                _piVector = _fiMove.FieldType.GetProperty("Vector", F);
                if (_piIsPressed == null || _piWasPressed == null || _piWasReleased == null || _piVector == null)
                    _heroInputBindFailed = true;
            }
            catch (Exception e)
            {
                _heroInputBindFailed = true;
                UnityEngine.Debug.LogWarning("[SilksongMobileAPI] Hero input reflection bind failed: " + e.Message);
            }
            if (_heroInputBindFailed)
                UnityEngine.Debug.LogWarning("[SilksongMobileAPI] Hero input mapping disabled (member lookup failed).");
        }

        private static bool ReadBool(PropertyInfo p, object target)
        {
            object v = p.GetValue(target, null);
            return v is bool && (bool)v;
        }

        private static float ReadMoveAxis(object heroActions, bool yAxis)
        {
            object mv = _fiMove.GetValue(heroActions);
            if (mv == null) return 0f;
            object v = _piVector.GetValue(mv, null);
            if (v is Vector2)
            {
                Vector2 vec = (Vector2)v;
                return yAxis ? vec.y : vec.x;
            }
            return 0f;
        }

        // mode: 0 = GetKey (IsPressed), 1 = GetKeyDown (WasPressed), 2 = GetKeyUp (WasReleased)
        // Result is cached per (key, mode) for the current frame so repeated polls cost nothing.
        // Uses bound Func<bool> / Func<Vector2> delegates created once per InputHandler instance (zero reflection per frame, zero InControl.dll compile dependency).
        private static InputHandler _cachedHeroIh;
        private static object _cachedHeroIa, _cachedUpAct, _cachedDownAct, _cachedLeftAct, _cachedRightAct, _cachedMoveVecObj;
        private static Func<Vector2> _cachedMoveVecGetter;
        private static Func<bool> _upIsPressed, _upWasPressed, _upWasReleased;
        private static Func<bool> _downIsPressed, _downWasPressed, _downWasReleased;
        private static Func<bool> _leftIsPressed, _leftWasPressed, _leftWasReleased;
        private static Func<bool> _rightIsPressed, _rightWasPressed, _rightWasReleased;

        private static Func<bool> BindBoolGetterDelegate(PropertyInfo pi, object target)
        {
            if (pi == null || target == null) return null;
            try
            {
                var gm = pi.GetGetMethod(true);
                if (gm != null)
                    return Delegate.CreateDelegate(typeof(Func<bool>), target, gm, false) as Func<bool>;
            }
            catch { }
            return null;
        }

        private static bool CheckHeroInputActionKey(KeyCode key, int mode)
        {
            // Fast 1-cycle early exit for all non-movement keys (avoids Time.frameCount & reflection on F1..F12, Space, E, R, etc.)
            if (key != KeyCode.UpArrow && key != KeyCode.DownArrow && key != KeyCode.LeftArrow && key != KeyCode.RightArrow &&
                key != KeyCode.W && key != KeyCode.S && key != KeyCode.A && key != KeyCode.D)
            {
                return false;
            }
            try
            {
                if (_heroInputBindFailed) return false;
                int frame = UnityEngine.Time.frameCount;
                if (frame != _heroCacheFrame)
                {
                    _heroCache.Clear();
                    _heroCacheFrame = frame;
                }
                int cacheKey = ((int)key << 2) | mode;
                if (_heroCache.TryGetValue(cacheKey, out bool cached)) return cached;
                bool result = EvalHeroInputActionKey(key, mode);
                _heroCache[cacheKey] = result;
                return result;
            }
            catch { return false; }
        }

        private static bool EvalHeroInputActionKey(KeyCode key, int mode)
        {
            var ih = ManagerSingleton<InputHandler>.Instance;
            if (ih == null) return false;
            if (!_heroInputBound)
            {
                BindHeroInput();
                if (_heroInputBindFailed) return false;
            }
            if (!ReferenceEquals(ih, _cachedHeroIh) || _cachedHeroIa == null)
            {
                _cachedHeroIh = ih;
                _cachedHeroIa = _fiInputActions.GetValue(ih);
                if (_cachedHeroIa != null)
                {
                    _cachedUpAct = _fiUp.GetValue(_cachedHeroIa);
                    _cachedDownAct = _fiDown.GetValue(_cachedHeroIa);
                    _cachedLeftAct = _fiLeft.GetValue(_cachedHeroIa);
                    _cachedRightAct = _fiRight.GetValue(_cachedHeroIa);
                    _cachedMoveVecObj = _fiMove.GetValue(_cachedHeroIa);

                    _upIsPressed = BindBoolGetterDelegate(_piIsPressed, _cachedUpAct);
                    _upWasPressed = BindBoolGetterDelegate(_piWasPressed, _cachedUpAct);
                    _upWasReleased = BindBoolGetterDelegate(_piWasReleased, _cachedUpAct);

                    _downIsPressed = BindBoolGetterDelegate(_piIsPressed, _cachedDownAct);
                    _downWasPressed = BindBoolGetterDelegate(_piWasPressed, _cachedDownAct);
                    _downWasReleased = BindBoolGetterDelegate(_piWasReleased, _cachedDownAct);

                    _leftIsPressed = BindBoolGetterDelegate(_piIsPressed, _cachedLeftAct);
                    _leftWasPressed = BindBoolGetterDelegate(_piWasPressed, _cachedLeftAct);
                    _leftWasReleased = BindBoolGetterDelegate(_piWasReleased, _cachedLeftAct);

                    _rightIsPressed = BindBoolGetterDelegate(_piIsPressed, _cachedRightAct);
                    _rightWasPressed = BindBoolGetterDelegate(_piWasPressed, _cachedRightAct);
                    _rightWasReleased = BindBoolGetterDelegate(_piWasReleased, _cachedRightAct);

                    _cachedMoveVecGetter = null;
                    if (_cachedMoveVecObj != null && _piVector != null)
                    {
                        try
                        {
                            var vgm = _piVector.GetGetMethod(true);
                            if (vgm != null)
                                _cachedMoveVecGetter = Delegate.CreateDelegate(typeof(Func<Vector2>), _cachedMoveVecObj, vgm, false) as Func<Vector2>;
                        }
                        catch { }
                    }
                }
            }
            if (_cachedHeroIa == null) return false;

            Vector2 mv = _cachedMoveVecGetter != null ? _cachedMoveVecGetter() : Vector2.zero;
            if (key == KeyCode.UpArrow || key == KeyCode.W)
            {
                if (mode == 0 && mv.y > 0.35f) return true;
                if (mode == 0 && _upIsPressed != null) return _upIsPressed();
                if (mode == 1 && _upWasPressed != null) return _upWasPressed();
                if (mode == 2 && _upWasReleased != null) return _upWasReleased();
                return ReadBool(mode == 0 ? _piIsPressed : (mode == 1 ? _piWasPressed : _piWasReleased), _cachedUpAct);
            }
            if (key == KeyCode.DownArrow || key == KeyCode.S)
            {
                if (mode == 0 && mv.y < -0.35f) return true;
                if (mode == 0 && _downIsPressed != null) return _downIsPressed();
                if (mode == 1 && _downWasPressed != null) return _downWasPressed();
                if (mode == 2 && _downWasReleased != null) return _downWasReleased();
                return ReadBool(mode == 0 ? _piIsPressed : (mode == 1 ? _piWasPressed : _piWasReleased), _cachedDownAct);
            }
            if (key == KeyCode.LeftArrow || key == KeyCode.A)
            {
                if (mode == 0 && mv.x < -0.35f) return true;
                if (mode == 0 && _leftIsPressed != null) return _leftIsPressed();
                if (mode == 1 && _leftWasPressed != null) return _leftWasPressed();
                if (mode == 2 && _leftWasReleased != null) return _leftWasReleased();
                return ReadBool(mode == 0 ? _piIsPressed : (mode == 1 ? _piWasPressed : _piWasReleased), _cachedLeftAct);
            }
            if (key == KeyCode.RightArrow || key == KeyCode.D)
            {
                if (mode == 0 && mv.x > 0.35f) return true;
                if (mode == 0 && _rightIsPressed != null) return _rightIsPressed();
                if (mode == 1 && _rightWasPressed != null) return _rightWasPressed();
                if (mode == 2 && _rightWasReleased != null) return _rightWasReleased();
                return ReadBool(mode == 0 ? _piIsPressed : (mode == 1 ? _piWasPressed : _piWasReleased), _cachedRightAct);
            }
            return false;
        }

        // ======================================================================
        // Unity.InputSystem (ButtonControl / KeyControl / InputAction) Virtual Touch Bridge
        // ======================================================================
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBoxKeyCode> _buttonControlKeyCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBoxKeyCode>();
        private sealed class StrongBoxKeyCode { public KeyCode Key; public Func<bool> OrigIsPressed; public Func<bool> OrigWasPressed; public Func<bool> OrigWasReleased; }

        private static StrongBoxKeyCode ResolveButtonControlMeta(object ctrl)
        {
            if (ctrl == null) return null;
            if (_buttonControlKeyCache.TryGetValue(ctrl, out var meta)) return meta;
            meta = new StrongBoxKeyCode { Key = KeyCode.None };
            try
            {
                var t = ctrl.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var piName = t.GetProperty("name", F);
                string ctrlName = piName?.GetValue(ctrl, null) as string;
                if (!string.IsNullOrEmpty(ctrlName) && TryParseKey(ctrlName, out var parsedCtrlKey))
                {
                    meta.Key = parsedCtrlKey;
                }
                var piIs = t.GetProperty("isPressed", F);
                var piWasP = t.GetProperty("wasPressedThisFrame", F);
                var piWasR = t.GetProperty("wasReleasedThisFrame", F);
                meta.OrigIsPressed = BindBoolGetterDelegate(piIs, ctrl);
                meta.OrigWasPressed = BindBoolGetterDelegate(piWasP, ctrl);
                meta.OrigWasReleased = BindBoolGetterDelegate(piWasR, ctrl);
            }
            catch { }
            _buttonControlKeyCache.Add(ctrl, meta);
            return meta;
        }

        public static bool ButtonControl_get_isPressed(object ctrl)
        {
            var meta = ResolveButtonControlMeta(ctrl);
            if (meta != null && meta.Key != KeyCode.None && GetKey(meta.Key))
                return true;
            if (meta?.OrigIsPressed != null)
            {
                try { return meta.OrigIsPressed(); } catch { }
            }
            return false;
        }

        public static bool ButtonControl_get_wasPressedThisFrame(object ctrl)
        {
            var meta = ResolveButtonControlMeta(ctrl);
            if (meta != null && meta.Key != KeyCode.None && GetKeyDown(meta.Key))
                return true;
            if (meta?.OrigWasPressed != null)
            {
                try { return meta.OrigWasPressed(); } catch { }
            }
            return false;
        }

        public static bool ButtonControl_get_wasReleasedThisFrame(object ctrl)
        {
            var meta = ResolveButtonControlMeta(ctrl);
            if (meta != null && meta.Key != KeyCode.None && GetKeyUp(meta.Key))
                return true;
            if (meta?.OrigWasReleased != null)
            {
                try { return meta.OrigWasReleased(); } catch { }
            }
            return false;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBoxInputActionMeta> _inputActionMetaCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBoxInputActionMeta>();
        private sealed class StrongBoxInputActionMeta
        {
            public List<KeyCode> BoundKeys = new List<KeyCode>();
            public Func<bool> OrigIsPressed;
            public Func<bool> OrigWasPressed;
            public Func<bool> OrigWasReleased;
        }

        private static StrongBoxInputActionMeta ResolveInputActionMeta(object action)
        {
            if (action == null) return null;
            if (_inputActionMetaCache.TryGetValue(action, out var meta)) return meta;
            meta = new StrongBoxInputActionMeta();
            try
            {
                var t = action.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var miIs = t.GetMethod("IsPressed", F, null, Type.EmptyTypes, null);
                var miWasP = t.GetMethod("WasPressedThisFrame", F, null, Type.EmptyTypes, null);
                var miWasR = t.GetMethod("WasReleasedThisFrame", F, null, Type.EmptyTypes, null);
                if (miIs != null) meta.OrigIsPressed = Delegate.CreateDelegate(typeof(Func<bool>), action, miIs, false) as Func<bool>;
                if (miWasP != null) meta.OrigWasPressed = Delegate.CreateDelegate(typeof(Func<bool>), action, miWasP, false) as Func<bool>;
                if (miWasR != null) meta.OrigWasReleased = Delegate.CreateDelegate(typeof(Func<bool>), action, miWasR, false) as Func<bool>;

                // Extract any "<Keyboard>/..." bindings automatically
                var piBindings = t.GetProperty("bindings", F);
                if (piBindings != null && piBindings.GetValue(action, null) is IEnumerable bindingsEnum)
                {
                    foreach (var b in bindingsEnum)
                    {
                        if (b == null) continue;
                        var bt = b.GetType();
                        string path = (bt.GetProperty("effectivePath", F) ?? bt.GetProperty("path", F))?.GetValue(b, null) as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            int slash = path.LastIndexOf('/');
                            string keyPart = slash >= 0 ? path.Substring(slash + 1) : path;
                            if (TryParseKey(keyPart, out var kc) && kc != KeyCode.None && !meta.BoundKeys.Contains(kc))
                            {
                                meta.BoundKeys.Add(kc);
                            }
                        }
                    }
                }
            }
            catch { }
            _inputActionMetaCache.Add(action, meta);
            return meta;
        }

        public static bool InputAction_IsPressed(object action)
        {
            var meta = ResolveInputActionMeta(action);
            if (meta != null)
            {
                for (int i = 0; i < meta.BoundKeys.Count; i++)
                {
                    if (GetKey(meta.BoundKeys[i])) return true;
                }
                if (meta.OrigIsPressed != null)
                {
                    try { return meta.OrigIsPressed(); } catch { }
                }
            }
            return false;
        }

        public static bool InputAction_WasPressedThisFrame(object action)
        {
            var meta = ResolveInputActionMeta(action);
            if (meta != null)
            {
                for (int i = 0; i < meta.BoundKeys.Count; i++)
                {
                    if (GetKeyDown(meta.BoundKeys[i])) return true;
                }
                if (meta.OrigWasPressed != null)
                {
                    try { return meta.OrigWasPressed(); } catch { }
                }
            }
            return false;
        }

        public static bool InputAction_WasReleasedThisFrame(object action)
        {
            var meta = ResolveInputActionMeta(action);
            if (meta != null)
            {
                for (int i = 0; i < meta.BoundKeys.Count; i++)
                {
                    if (GetKeyUp(meta.BoundKeys[i])) return true;
                }
                if (meta.OrigWasReleased != null)
                {
                    try { return meta.OrigWasReleased(); } catch { }
                }
            }
            return false;
        }

        public static bool GetKey(KeyCode key)
        {
            if (HasActiveKey(key)) return true;
            if (CheckHeroInputActionKey(key, 0)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKey(key);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        public static bool GetKeyDown(KeyCode key)
        {
            if (HasActiveKeyDown(key)) return true;
            if (CheckHeroInputActionKey(key, 1)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKeyDown(key);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        public static bool GetKeyUp(KeyCode key)
        {
            if (HasActiveKeyUp(key)) return true;
            if (CheckHeroInputActionKey(key, 2)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKeyUp(key);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        public static bool GetKeyString(string name)
        {
            if (HasActiveKeyString(name)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKey(name);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        public static bool GetKeyDownString(string name)
        {
            if (HasActiveKeyDownString(name)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKeyDown(name);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        public static bool GetKeyUpString(string name)
        {
            if (HasActiveKeyUpString(name)) return true;
            if (_inNativeInputQuery) return false;
            try
            {
                _inNativeInputQuery = true;
                return Input.GetKeyUp(name);
            }
            catch { return false; }
            finally { _inNativeInputQuery = false; }
        }

        private static bool TryParseKey(string name, out KeyCode kc)
        {
            kc = KeyCode.None;
            if (string.IsNullOrEmpty(name)) return false;
            string trimmed = name.Trim();
            if (Enum.TryParse(trimmed, true, out kc)) return true;
            if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
                return Enum.TryParse("Alpha" + trimmed, true, out kc);
            return false;
        }

        public static bool GetMouseButton(int button)
        {
            if (button == 0)
            {
                try
                {
                    if (Input.touchCount > 0)
                        return true;
                }
                catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L683", __apiEx_4); }
            }
            try { return Input.GetMouseButton(button); } catch { return false; }
        }

        public static bool GetMouseButtonDown(int button)
        {
            if (button == 0)
            {
                try
                {
                    if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
                        return true;
                }
                catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L697", __apiEx_5); }
            }
            try { return Input.GetMouseButtonDown(button); } catch { return false; }
        }

        public static bool GetMouseButtonUp(int button)
        {
            if (button == 0)
            {
                try
                {
                    if (Input.touchCount > 0)
                    {
                        var ph = Input.GetTouch(0).phase;
                        if (ph == TouchPhase.Ended || ph == TouchPhase.Canceled)
                            return true;
                    }
                }
                catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L715", __apiEx_6); }
            }
            try { return Input.GetMouseButtonUp(button); } catch { return false; }
        }

        public static Vector3 get_mousePosition()
        {
            try
            {
                if (Input.touchCount > 0)
                {
                    Vector2 pos = Input.GetTouch(0).position;
                    return new Vector3(pos.x, pos.y, 0f);
                }
            }
            catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L730", __apiEx_7); }
            try { return Input.mousePosition; } catch { return Vector3.zero; }
        }
    }

    public class UnityInput : IInputSystem
    {
        private static UnityInput _current;
        public static IInputSystem Current => _current ?? (_current = new UnityInput());

        private static readonly KeyCode[] _allKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        public bool GetKey(KeyCode key) => MobileVirtualInput.GetKey(key);
        public bool GetKeyDown(KeyCode key) => MobileVirtualInput.GetKeyDown(key);
        public bool GetKeyUp(KeyCode key) => MobileVirtualInput.GetKeyUp(key);
        public bool GetKey(string name) => MobileVirtualInput.GetKeyString(name);
        public bool GetKeyDown(string name) => MobileVirtualInput.GetKeyDownString(name);
        public bool GetKeyUp(string name) => MobileVirtualInput.GetKeyUpString(name);
        public bool GetMouseButton(int button) => MobileVirtualInput.GetMouseButton(button);
        public bool GetMouseButtonDown(int button) => MobileVirtualInput.GetMouseButtonDown(button);
        public bool GetMouseButtonUp(int button) => MobileVirtualInput.GetMouseButtonUp(button);
        public void ResetInputAxes() { try { Input.ResetInputAxes(); } catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L751", __apiEx_8); } }
        public Vector3 mousePosition => MobileVirtualInput.get_mousePosition();
        public bool anyKey { get { if (MobileVirtualInput.HasAnyKey()) return true; try { return Input.anyKey; } catch { return false; } } }
        public bool anyKeyDown { get { if (MobileVirtualInput.HasAnyKeyDown()) return true; try { return Input.anyKeyDown; } catch { return false; } } }
        public IEnumerable<KeyCode> SupportedKeyCodes => _allKeys;
    }

    public sealed class ThreadingHelper
    {
        private static ThreadingHelper _instance;
        public static ThreadingHelper Instance => _instance ?? (_instance = new ThreadingHelper());
        public static object SynchronizingObject => Instance;

        public void StartSyncInvoke(Action action)
        {
            try { action?.Invoke(); } catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L766", __apiEx_9); }
        }

        public void StartAsyncInvoke(Func<Action> action)
        {
            try
            {
                var cb = action?.Invoke();
                cb?.Invoke();
            }
            catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L776", __apiEx_10); }
        }

        public IAsyncResult BeginInvoke(Delegate method, object[] args)
        {
            object res = null;
            try { res = method?.DynamicInvoke(args); } catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L782", __apiEx_11); }
            return new SimpleAsyncResult(res);
        }

        public object EndInvoke(IAsyncResult result) => (result as SimpleAsyncResult)?.AsyncState;
        public object Invoke(Delegate method, object[] args) { try { return method?.DynamicInvoke(args); } catch { return null; } }
        public bool InvokeRequired => false;

        private sealed class SimpleAsyncResult : IAsyncResult
        {
            public object AsyncState { get; }
            public System.Threading.WaitHandle AsyncWaitHandle => null;
            public bool CompletedSynchronously => true;
            public bool IsCompleted => true;
            public SimpleAsyncResult(object state) { AsyncState = state; }
        }
    }
}

namespace BepInEx.Configuration
{
    public struct KeyboardShortcut : IEquatable<KeyboardShortcut>
    {
        public static readonly KeyboardShortcut Empty = new KeyboardShortcut(KeyCode.None);
        private readonly KeyCode[] _allKeys;

        public KeyCode MainKey => (_allKeys != null && _allKeys.Length > 0) ? _allKeys[0] : KeyCode.None;
        public IEnumerable<KeyCode> Modifiers => (_allKeys != null && _allKeys.Length > 1) ? _allKeys.Skip(1) : Enumerable.Empty<KeyCode>();

        public KeyboardShortcut(KeyCode mainKey, params KeyCode[] modifiers)
        {
            if (mainKey == KeyCode.None)
            {
                _allKeys = Array.Empty<KeyCode>();
            }
            else if (modifiers == null || modifiers.Length == 0)
            {
                _allKeys = new KeyCode[] { mainKey };
            }
            else
            {
                var list = new List<KeyCode>(1 + modifiers.Length) { mainKey };
                foreach (var m in modifiers)
                    if (m != KeyCode.None && !list.Contains(m))
                        list.Add(m);
                _allKeys = list.ToArray();
            }
        }

        public bool ModifierKeyTest()
        {
            if (_allKeys != null)
            {
                for (int i = 1; i < _allKeys.Length; i++)
                    if (!MobileVirtualInput.GetKey(_allKeys[i])) return false;
            }
            return true;
        }

        public bool IsDown()
        {
            var main = MainKey;
            if (main == KeyCode.None) return false;
            if (!MobileVirtualInput.GetKeyDown(main)) return false;
            if (_allKeys != null)
            {
                for (int i = 1; i < _allKeys.Length; i++)
                    if (!MobileVirtualInput.GetKey(_allKeys[i])) return false;
            }
            return true;
        }

        public bool IsPressed()
        {
            var main = MainKey;
            if (main == KeyCode.None) return false;
            if (!MobileVirtualInput.GetKey(main)) return false;
            if (_allKeys != null)
            {
                for (int i = 1; i < _allKeys.Length; i++)
                    if (!MobileVirtualInput.GetKey(_allKeys[i])) return false;
            }
            return true;
        }

        public bool IsUp()
        {
            var main = MainKey;
            if (main == KeyCode.None) return false;
            return MobileVirtualInput.GetKeyUp(main);
        }

        public static KeyboardShortcut Deserialize(string str)
        {
            if (string.IsNullOrWhiteSpace(str) || str.Equals("None", StringComparison.OrdinalIgnoreCase))
                return Empty;
            try
            {
                var parts = str.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(p => Enum.TryParse<KeyCode>(p.Trim(), true, out var kc) ? kc : KeyCode.None)
                               .Where(kc => kc != KeyCode.None)
                               .ToArray();
                if (parts.Length == 0) return Empty;
                return new KeyboardShortcut(parts[0], parts.Skip(1).ToArray());
            }
            catch { return Empty; }
        }

        public string Serialize()
        {
            if (_allKeys == null || _allKeys.Length == 0) return "None";
            return string.Join(" + ", _allKeys.Select(k => k.ToString()));
        }

        public override string ToString() => Serialize();
        public bool Equals(KeyboardShortcut other) => Serialize() == other.Serialize();
        public override bool Equals(object obj) => obj is KeyboardShortcut ks && Equals(ks);
        public override int GetHashCode() => Serialize().GetHashCode();
        public static bool operator ==(KeyboardShortcut a, KeyboardShortcut b) => a.Equals(b);
        public static bool operator !=(KeyboardShortcut a, KeyboardShortcut b) => !a.Equals(b);
    }
}

namespace BepInEx.Bootstrap
{
    public static class Chainloader
    {
        private static Dictionary<string, PluginInfo> _pluginInfos;
        public static Dictionary<string, PluginInfo> PluginInfos
        {
            get
            {
                if (_pluginInfos == null)
                    _pluginInfos = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
                SilksongMobileAPI.HarmonyBridge.PopulateChainloaderPlugins(_pluginInfos);
                return _pluginInfos;
            }
        }

        private static GameObject _managerObject;
        public static GameObject ManagerObject
        {
            get
            {
                if (_managerObject == null)
                    _managerObject = SilksongMobileAPI.SilksongMobileAPIPlugin.Instance != null
                        ? SilksongMobileAPI.SilksongMobileAPIPlugin.Instance.gameObject
                        : new GameObject("BepInEx_Manager");
                return _managerObject;
            }
        }
    }
}

namespace BepInEx.Harmony
{
    public static class HarmonyWrapper
    {
        public static HarmonyLib.Harmony PatchAll(Type type) => SilksongMobileAPI.HarmonyBridge.CreateAndPatchAllType(type, null);
        public static HarmonyLib.Harmony PatchAll(Assembly assembly) => SilksongMobileAPI.HarmonyBridge.CreateAndPatchAllAssembly(assembly, null);
    }
}

namespace HarmonyLib
{
    public delegate ref F FieldRef<in T, F>(T instance = default);
    public delegate ref F FieldRef<F>();

    public static partial class AccessToolsShimExtensions
    {
    }

    public static partial class AccessToolsShimExtensions
    {
    }

    public struct Label : IEquatable<Label>
    {
        internal int id;
        public Label(int id) { this.id = id; }
        public bool Equals(Label other) => id == other.id;
        public override bool Equals(object obj) => obj is Label l && Equals(l);
        public override int GetHashCode() => id;
        public static bool operator ==(Label a, Label b) => a.id == b.id;
        public static bool operator !=(Label a, Label b) => a.id != b.id;
    }

    public struct OpCode : IEquatable<OpCode>
    {
        public short Value { get; }
        public string Name { get; }
        public OpCode(short value, string name = "")
        {
            Value = value;
            Name = name ?? "";
        }
        public bool Equals(OpCode other) => Value == other.Value;
        public override bool Equals(object obj) => obj is OpCode oc && Equals(oc);
        public override int GetHashCode() => Value;
        public override string ToString() => Name ?? Value.ToString();
        public static bool operator ==(OpCode a, OpCode b) => a.Value == b.Value;
        public static bool operator !=(OpCode a, OpCode b) => a.Value != b.Value;
    }

    public static class OpCodes
    {
        public static readonly OpCode Nop = new OpCode(0x00, "nop");
        public static readonly OpCode Ldarg_0 = new OpCode(0x02, "ldarg.0");
        public static readonly OpCode Ldarg_1 = new OpCode(0x03, "ldarg.1");
        public static readonly OpCode Ldarg_2 = new OpCode(0x04, "ldarg.2");
        public static readonly OpCode Ldarg_3 = new OpCode(0x05, "ldarg.3");
        public static readonly OpCode Ldloc_0 = new OpCode(0x06, "ldloc.0");
        public static readonly OpCode Ldloc_1 = new OpCode(0x07, "ldloc.1");
        public static readonly OpCode Ldloc_2 = new OpCode(0x08, "ldloc.2");
        public static readonly OpCode Ldloc_3 = new OpCode(0x09, "ldloc.3");
        public static readonly OpCode Stloc_0 = new OpCode(0x0A, "stloc.0");
        public static readonly OpCode Stloc_1 = new OpCode(0x0B, "stloc.1");
        public static readonly OpCode Stloc_2 = new OpCode(0x0C, "stloc.2");
        public static readonly OpCode Stloc_3 = new OpCode(0x0D, "stloc.3");
        public static readonly OpCode Ldarg_S = new OpCode(0x0E, "ldarg.s");
        public static readonly OpCode Ldloc_S = new OpCode(0x11, "ldloc.s");
        public static readonly OpCode Stloc_S = new OpCode(0x13, "stloc.s");
        public static readonly OpCode Ldnull = new OpCode(0x14, "ldnull");
        public static readonly OpCode Ldc_I4_M1 = new OpCode(0x15, "ldc.i4.m1");
        public static readonly OpCode Ldc_I4_0 = new OpCode(0x16, "ldc.i4.0");
        public static readonly OpCode Ldc_I4_1 = new OpCode(0x17, "ldc.i4.1");
        public static readonly OpCode Ldc_I4_2 = new OpCode(0x18, "ldc.i4.2");
        public static readonly OpCode Ldc_I4_3 = new OpCode(0x19, "ldc.i4.3");
        public static readonly OpCode Ldc_I4_4 = new OpCode(0x1A, "ldc.i4.4");
        public static readonly OpCode Ldc_I4_5 = new OpCode(0x1B, "ldc.i4.5");
        public static readonly OpCode Ldc_I4_6 = new OpCode(0x1C, "ldc.i4.6");
        public static readonly OpCode Ldc_I4_7 = new OpCode(0x1D, "ldc.i4.7");
        public static readonly OpCode Ldc_I4_8 = new OpCode(0x1E, "ldc.i4.8");
        public static readonly OpCode Ldc_I4_S = new OpCode(0x1F, "ldc.i4.s");
        public static readonly OpCode Ldc_I4 = new OpCode(0x20, "ldc.i4");
        public static readonly OpCode Ldc_R4 = new OpCode(0x22, "ldc.r4");
        public static readonly OpCode Dup = new OpCode(0x25, "dup");
        public static readonly OpCode Pop = new OpCode(0x26, "pop");
        public static readonly OpCode Call = new OpCode(0x28, "call");
        public static readonly OpCode Ret = new OpCode(0x2A, "ret");
        public static readonly OpCode Br_S = new OpCode(0x2B, "br.s");
        public static readonly OpCode Brfalse_S = new OpCode(0x2C, "brfalse.s");
        public static readonly OpCode Brtrue_S = new OpCode(0x2D, "brtrue.s");
        public static readonly OpCode Br = new OpCode(0x38, "br");
        public static readonly OpCode Brfalse = new OpCode(0x39, "brfalse");
        public static readonly OpCode Brtrue = new OpCode(0x3A, "brtrue");
        public static readonly OpCode Callvirt = new OpCode(0x6F, "callvirt");
        public static readonly OpCode Ldstr = new OpCode(0x72, "ldstr");
        public static readonly OpCode Newobj = new OpCode(0x73, "newobj");
        public static readonly OpCode Ldfld = new OpCode(0x7B, "ldfld");
        public static readonly OpCode Ldflda = new OpCode(0x7C, "ldflda");
        public static readonly OpCode Stfld = new OpCode(0x7D, "stfld");
        public static readonly OpCode Ldsfld = new OpCode(0x7E, "ldsfld");
        public static readonly OpCode Stsfld = new OpCode(0x80, "stsfld");
    }

    public class LocalBuilder
    {
        public int LocalIndex { get; }
        public Type LocalType { get; }
        public bool IsPinned { get; }
        public LocalBuilder(int index = 0, Type type = null, bool isPinned = false)
        {
            LocalIndex = index;
            LocalType = type ?? typeof(object);
            IsPinned = isPinned;
        }
    }

    public class ILGenerator
    {
        private int _nextLabel;
        private int _nextLocal;
        public Label DefineLabel() => new Label(++_nextLabel);
        public void MarkLabel(Label loc) { }
        public LocalBuilder DeclareLocal(Type localType) => new LocalBuilder(_nextLocal++, localType, false);
        public LocalBuilder DeclareLocal(Type localType, bool pinned) => new LocalBuilder(_nextLocal++, localType, pinned);
        public void Emit(OpCode opcode) { }
        public void Emit(OpCode opcode, byte arg) { }
        public void Emit(OpCode opcode, short arg) { }
        public void Emit(OpCode opcode, int arg) { }
        public void Emit(OpCode opcode, long arg) { }
        public void Emit(OpCode opcode, float arg) { }
        public void Emit(OpCode opcode, double arg) { }
        public void Emit(OpCode opcode, string str) { }
        public void Emit(OpCode opcode, Label label) { }
        public void Emit(OpCode opcode, LocalBuilder local) { }
        public void Emit(OpCode opcode, MethodInfo meth) { }
        public void Emit(OpCode opcode, ConstructorInfo con) { }
        public void Emit(OpCode opcode, FieldInfo field) { }
        public void Emit(OpCode opcode, Type cls) { }
    }

    public enum MethodType
    {
        Normal = 0,
        Getter = 1,
        Setter = 2,
        Constructor = 3,
        StaticConstructor = 4,
        Enumerator = 5,
        Async = 6
    }

    public enum ArgumentType
    {
        Normal = 0,
        Ref = 1,
        Out = 2,
        Pointer = 3
    }

    public static class Priority
    {
        public const int Last = 0;
        public const int VeryLow = 100;
        public const int Low = 200;
        public const int LowerThanNormal = 300;
        public const int Normal = 400;
        public const int HigherThanNormal = 500;
        public const int High = 600;
        public const int VeryHigh = 700;
        public const int First = 800;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyTranspiler : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyFinalizer : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyILManipulator : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyCleanup : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyWrapSafe : Attribute { }

    public enum HarmonyPatchType
    {
        All = 0,
        Prefix = 1,
        Postfix = 2,
        Transpiler = 3,
        Finalizer = 4,
        ReversePatch = 5,
        ILManipulator = 6
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyPrepare : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyTargetMethod : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
    public class HarmonyTargetMethods : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
    public class HarmonyArgument : Attribute
    {
        public string OriginalName { get; private set; }
        public int Index { get; private set; }
        public string NewName { get; private set; }

        public HarmonyArgument(string originalName) : this(originalName, null) { }
        public HarmonyArgument(int index) : this(index, null) { }
        public HarmonyArgument(string originalName, string newName)
        {
            OriginalName = originalName;
            Index = -1;
            NewName = newName;
        }
        public HarmonyArgument(int index, string name)
        {
            OriginalName = null;
            Index = index;
            NewName = name;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPriority : Attribute
    {
        public int info;
        public HarmonyPriority(int priority) { info = priority; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyBefore : Attribute
    {
        public string[] info;
        public HarmonyBefore(params string[] before) { info = before ?? Array.Empty<string>(); }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyAfter : Attribute
    {
        public string[] info;
        public HarmonyAfter(params string[] after) { info = after ?? Array.Empty<string>(); }
    }

    public enum HarmonyReversePatchType
    {
        Original = 0,
        Snapshot = 1
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class HarmonyReversePatch : Attribute
    {
        public HarmonyReversePatchType info;
        public HarmonyReversePatch(HarmonyReversePatchType type = HarmonyReversePatchType.Original) { info = type; }
    }

    public enum ExceptionBlockType
    {
        BeginExceptionBlock = 0,
        BeginCatchBlock = 1,
        BeginExceptFilterBlock = 2,
        BeginFaultBlock = 3,
        BeginFinallyBlock = 4,
        EndExceptionBlock = 5
    }

    public class ExceptionBlock
    {
        public ExceptionBlockType blockType;
        public Type catchType;
        public ExceptionBlock(ExceptionBlockType blockType, Type catchType = null)
        {
            this.blockType = blockType;
            this.catchType = catchType ?? typeof(object);
        }
    }

    public static class Transpilers
    {
        public static IEnumerable<CodeInstruction> MethodReplacer(this IEnumerable<CodeInstruction> instructions, MethodBase from, MethodBase to) => instructions;
        public static IEnumerable<CodeInstruction> Manipulator(this IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, bool> predicate, Action<CodeInstruction> action) => instructions;
        public static IEnumerable<CodeInstruction> DebugLogger(this IEnumerable<CodeInstruction> instructions, string text) => instructions;
    }

    public class CodeInstruction
    {
        public OpCode opcode;
        public object operand;
        public List<Label> labels = new List<Label>();
        public List<object> blocks = new List<object>();

        public CodeInstruction(OpCode opcode, object operand = null)
        {
            this.opcode = opcode;
            this.operand = operand;
        }

        public CodeInstruction(CodeInstruction instruction)
        {
            if (instruction != null)
            {
                opcode = instruction.opcode;
                operand = instruction.operand;
                if (instruction.labels != null) labels = new List<Label>(instruction.labels);
                if (instruction.blocks != null) blocks = new List<object>(instruction.blocks);
            }
        }

        public CodeInstruction Clone() => new CodeInstruction(this);
        public CodeInstruction Clone(OpCode newOpcode) { var c = Clone(); c.opcode = newOpcode; return c; }
        public CodeInstruction Clone(object newOperand) { var c = Clone(); c.operand = newOperand; return c; }
        public override string ToString() => $"{opcode} {operand}";
    }

    public class CodeMatch
    {
        public OpCode? opcode;
        public object operand;
        public string name;
        public CodeMatch() { }
        public CodeMatch(OpCode? opcode = null, object operand = null, string name = null)
        {
            this.opcode = opcode;
            this.operand = operand;
            this.name = name;
        }
        public CodeMatch(Func<CodeInstruction, bool> predicate, string name = null)
        {
            this.name = name;
        }
        public CodeMatch(CodeInstruction instruction, string name = null)
        {
            this.name = name;
        }
    }

    public class CodeMatcher
    {
        public int Pos => 0;
        public int Remaining => 0;
        public int Length => 0;
        public bool IsValid => false;
        public bool IsInvalid => true;
        private object _dummyOperand;
        public OpCode Opcode { get => default; set { } }
        public ref object Operand => ref _dummyOperand;
        public List<Label> Labels { get => new List<Label>(); set { } }
        public CodeInstruction Instruction => new CodeInstruction(default);

        public CodeMatcher() { }
        public CodeMatcher(IEnumerable<CodeInstruction> instructions, ILGenerator generator = null) { }

        public CodeMatcher Clone() => this;
        public CodeInstruction InstructionAt(int offset) => new CodeInstruction(default);
        public CodeMatcher Start() => this;
        public CodeMatcher End() => this;
        public CodeMatcher Advance(int offset = 1) => this;
        public CodeMatcher MatchStartForward(params CodeMatch[] matches) => this;
        public CodeMatcher MatchEndForward(params CodeMatch[] matches) => this;
        public CodeMatcher MatchStartBackwards(params CodeMatch[] matches) => this;
        public CodeMatcher MatchEndBackwards(params CodeMatch[] matches) => this;
        public CodeMatcher MatchForward(bool useEnd, params CodeMatch[] matches) => this;
        public CodeMatcher MatchBackwards(bool useEnd, params CodeMatch[] matches) => this;
        public CodeMatcher SearchForward(Func<CodeInstruction, bool> predicate) => this;
        public CodeMatcher SearchBackwards(Func<CodeInstruction, bool> predicate) => this;
        public CodeMatcher Insert(params CodeInstruction[] instructions) => this;
        public CodeMatcher Insert(IEnumerable<CodeInstruction> instructions) => this;
        public CodeMatcher InsertAndAdvance(params CodeInstruction[] instructions) => this;
        public CodeMatcher InsertAndAdvance(IEnumerable<CodeInstruction> instructions) => this;
        public CodeMatcher InsertBranch(OpCode opcode, int destination) => this;
        public CodeMatcher InsertBranchAndAdvance(OpCode opcode, int destination) => this;
        public CodeMatcher RemoveInstruction() => this;
        public CodeMatcher RemoveInstructions(int count) => this;
        public CodeMatcher RemoveInstructionsInRange(int start, int end) => this;
        public CodeMatcher RemoveInstructionsWithOffsets(int startOffset, int endOffset) => this;
        public CodeMatcher Set(OpCode opcode, object operand) => this;
        public CodeMatcher SetAndAdvance(OpCode opcode, object operand) => this;
        public CodeMatcher SetOpcodeAndAdvance(OpCode opcode) => this;
        public CodeMatcher SetOperandAndAdvance(object operand) => this;
        public CodeMatcher SetInstruction(CodeInstruction instruction) => this;
        public CodeMatcher SetInstructionAndAdvance(CodeInstruction instruction) => this;
        public CodeMatcher CreateLabel(out Label label) { label = default; return this; }
        public CodeMatcher CreateLabelAt(int position, out Label label) { label = default; return this; }
        public CodeMatcher AddLabels(IEnumerable<Label> labels) => this;
        public CodeMatcher AddLabelsAt(int position, IEnumerable<Label> labels) => this;
        public CodeMatcher ThrowIfInvalid(string explanation) => this;
        public CodeMatcher ThrowIfNotMatch(string explanation, params CodeMatch[] matches) => this;
        public CodeMatcher ThrowIfNotMatchForward(string explanation, params CodeMatch[] matches) => this;
        public CodeMatcher ThrowIfNotMatchBack(string explanation, params CodeMatch[] matches) => this;
        public CodeMatcher ThrowIfFalse(string explanation, Func<CodeMatcher, bool> statecheckfunc) => this;
        public CodeMatcher Repeat(Action<CodeMatcher> DoThis, Action<string> notFoundAction = null) => this;
        public object NamedMatch(string name) => new CodeInstruction(default);
        public IEnumerable<CodeInstruction> InstructionEnumeration() => Array.Empty<CodeInstruction>();
        public List<CodeInstruction> Instructions() => new List<CodeInstruction>();
        public List<CodeInstruction> InstructionsWithOffsets(int startOffset, int endOffset) => new List<CodeInstruction>();
    }

    public static class CodeInstructionExtensions
    {
        public static bool Calls(this CodeInstruction code, MethodInfo method)
        {
            if (code == null || method == null) return false;
            if (code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt) return false;
            return Equals(code.operand, method);
        }

        public static bool LoadsField(this CodeInstruction code, FieldInfo field, bool byAddress = false)
        {
            if (code == null || field == null) return false;
            return Equals(code.operand, field);
        }

        public static bool StoresField(this CodeInstruction code, FieldInfo field)
        {
            if (code == null || field == null) return false;
            return Equals(code.operand, field);
        }

        public static bool Is(this CodeInstruction code, OpCode opcode, object operand)
        {
            if (code == null) return false;
            return code.opcode == opcode && Equals(code.operand, operand);
        }

        public static CodeInstruction WithLabels(this CodeInstruction code, params Label[] labels)
        {
            if (code != null && labels != null) code.labels.AddRange(labels);
            return code;
        }

        public static CodeInstruction WithLabels(this CodeInstruction code, IEnumerable<Label> labels)
        {
            if (code != null && labels != null) code.labels.AddRange(labels);
            return code;
        }

        public static CodeInstruction MoveLabelsFrom(this CodeInstruction code, CodeInstruction other)
        {
            if (code != null && other != null && other.labels != null)
            {
                code.labels.AddRange(other.labels);
                other.labels.Clear();
            }
            return code;
        }
    }

    public class Patch : IComparable
    {
        public readonly int index;
        public readonly string owner;
        public readonly int priority;
        public readonly string[] before;
        public readonly string[] after;
        public readonly bool debug;
        public MethodInfo PatchMethod { get; set; }

        public Patch(MethodInfo patch, int index, string owner, int priority, string[] before, string[] after, bool debug)
        {
            PatchMethod = patch;
            this.index = index;
            this.owner = owner ?? "";
            this.priority = priority;
            this.before = before ?? Array.Empty<string>();
            this.after = after ?? Array.Empty<string>();
            this.debug = debug;
        }

        public MethodInfo GetMethod(MethodBase original) => PatchMethod;
        public int CompareTo(object obj) => 0;
    }

    public class Patches
    {
        public readonly ReadOnlyCollection<Patch> Prefixes;
        public readonly ReadOnlyCollection<Patch> Postfixes;
        public readonly ReadOnlyCollection<Patch> Transpilers;
        public readonly ReadOnlyCollection<Patch> Finalizers;
        public readonly ReadOnlyCollection<Patch> ILManipulators;

        public ReadOnlyCollection<string> Owners
        {
            get
            {
                var set = new HashSet<string>();
                if (Prefixes != null) foreach (var p in Prefixes) if (!string.IsNullOrEmpty(p.owner)) set.Add(p.owner);
                if (Postfixes != null) foreach (var p in Postfixes) if (!string.IsNullOrEmpty(p.owner)) set.Add(p.owner);
                if (Transpilers != null) foreach (var p in Transpilers) if (!string.IsNullOrEmpty(p.owner)) set.Add(p.owner);
                if (Finalizers != null) foreach (var p in Finalizers) if (!string.IsNullOrEmpty(p.owner)) set.Add(p.owner);
                return set.ToList().AsReadOnly();
            }
        }

        public Patches(Patch[] prefixes, Patch[] postfixes, Patch[] transpilers, Patch[] finalizers, Patch[] ilmanipulators = null)
        {
            Prefixes = new List<Patch>(prefixes ?? Array.Empty<Patch>()).AsReadOnly();
            Postfixes = new List<Patch>(postfixes ?? Array.Empty<Patch>()).AsReadOnly();
            Transpilers = new List<Patch>(transpilers ?? Array.Empty<Patch>()).AsReadOnly();
            Finalizers = new List<Patch>(finalizers ?? Array.Empty<Patch>()).AsReadOnly();
            ILManipulators = new List<Patch>(ilmanipulators ?? Array.Empty<Patch>()).AsReadOnly();
        }
    }

    public class PatchClassProcessor
    {
        private readonly Harmony _instance;
        private readonly Type _containerType;
        public string Category { get; set; }

        public PatchClassProcessor(Harmony instance, Type type)
        {
            _instance = instance;
            _containerType = type;
        }

        public PatchClassProcessor(Harmony instance, Type type, bool allowUnannotatedType)
        {
            _instance = instance;
            _containerType = type;
        }

        public List<MethodInfo> Patch()
        {
            if (_containerType != null)
            {
                SilksongMobileAPI.HarmonyBridge.PatchAllType(_instance, _containerType);
            }
            return new List<MethodInfo>();
        }
    }

    public class PatchProcessor
    {
        private readonly Harmony _instance;
        private readonly MethodBase _original;

        public PatchProcessor(Harmony instance, MethodBase original)
        {
            _instance = instance;
            _original = original;
        }

        public List<MethodInfo> Patch() => new List<MethodInfo>();

        public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, ILGenerator generator = null)
        {
            return new List<CodeInstruction>();
        }

        public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method)
        {
            return Enumerable.Empty<KeyValuePair<OpCode, object>>();
        }

        public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method, ILGenerator generator)
        {
            return Enumerable.Empty<KeyValuePair<OpCode, object>>();
        }

        public static Patches GetPatchInfo(MethodBase method) => SilksongMobileAPI.HarmonyBridge.GetPatchInfo(method);
    }

    public class Traverse<T>
    {
        private readonly Traverse _traverse;
        private readonly Func<T> _getter;
        private readonly Action<T> _setter;

        public Traverse() { }
        public Traverse(Traverse traverse)
        {
            _traverse = traverse;
        }
        public Traverse(Func<T> getter, Action<T> setter = null)
        {
            _getter = getter;
            _setter = setter;
        }

        public T Value
        {
            get
            {
                if (_getter != null) return _getter();
                if (_traverse != null) return _traverse.GetValue<T>();
                return default;
            }
            set
            {
                if (_setter != null) { _setter(value); return; }
                _traverse?.SetValue(value);
            }
        }
    }

    public class Traverse
    {
        private readonly object _root;
        private readonly Type _type;
        private readonly MemberInfo _member;
        private readonly object[] _params;

        private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        public Traverse(Type type) { _type = type; }
        public Traverse(object root) { _root = root; _type = root?.GetType(); }
        private Traverse(object root, Type type, MemberInfo member, object[] parameters = null)
        {
            _root = root;
            _type = type;
            _member = member;
            _params = parameters;
        }

        public static Traverse Create(Type type) => new Traverse(type);
        public static Traverse Create<T>() => new Traverse(typeof(T));
        public static Traverse Create(object root) => new Traverse(root);
        public static Traverse CreateWithType(string name) => new Traverse(AccessTools.TypeByName(name));

        public Traverse Type(string name)
        {
            if (_type == null || string.IsNullOrEmpty(name)) return new Traverse((object)null);
            var nested = _type.GetNestedType(name, AllFlags) ?? AccessTools.TypeByName(name);
            return new Traverse(_root, nested, null);
        }

        public Traverse Field(string name)
        {
            if (_type == null || string.IsNullOrEmpty(name)) return new Traverse((object)null);
            var f = FindField(_type, name);
            return new Traverse(_root, _type, f);
        }

        public Traverse<T> Field<T>(string name) => new Traverse<T>(Field(name));

        public Traverse Property(string name, object[] index = null)
        {
            if (_type == null || string.IsNullOrEmpty(name)) return new Traverse((object)null);
            var p = FindProperty(_type, name);
            return new Traverse(_root, _type, p, index);
        }

        public Traverse<T> Property<T>(string name, object[] index = null) => new Traverse<T>(Property(name, index));

        public Traverse Method(string name, params object[] arguments)
        {
            if (_type == null || string.IsNullOrEmpty(name)) return new Traverse((object)null);
            var m = FindMethod(_type, name, arguments?.Select(a => a?.GetType()).ToArray());
            return new Traverse(_root, _type, m, arguments);
        }

        public Traverse Method(string name, Type[] paramTypes, object[] arguments = null)
        {
            if (_type == null || string.IsNullOrEmpty(name)) return new Traverse((object)null);
            var m = FindMethod(_type, name, paramTypes);
            return new Traverse(_root, _type, m, arguments);
        }

        public bool FieldExists() => _member is FieldInfo;
        public bool PropertyExists() => _member is PropertyInfo;
        public bool MethodExists() => _member is MethodInfo;
        public bool TypeExists() => _type != null;

        public object GetValue()
        {
            try
            {
                if (_member is FieldInfo fi)
                    return fi.GetValue(fi.IsStatic ? null : _root);
                if (_member is PropertyInfo pi)
                    return pi.GetValue(pi.GetGetMethod(true)?.IsStatic == true ? null : _root, _params);
                if (_member is MethodInfo mi)
                    return mi.Invoke(mi.IsStatic ? null : _root, _params);
                return _root;
            }
            catch { return null; }
        }

        public T GetValue<T>()
        {
            var val = GetValue();
            if (val is T t) return t;
            try { if (val != null) return (T)Convert.ChangeType(val, typeof(T)); } catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L1600", __apiEx_12); }
            return default;
        }

        public object GetValue(params object[] arguments)
        {
            try
            {
                if (_member is MethodInfo mi)
                    return mi.Invoke(mi.IsStatic ? null : _root, arguments);
                return GetValue();
            }
            catch { return null; }
        }

        public T GetValue<T>(params object[] arguments)
        {
            var val = GetValue(arguments);
            if (val is T t) return t;
            return default;
        }

        public Traverse SetValue(object value)
        {
            try
            {
                if (_member is FieldInfo fi)
                    fi.SetValue(fi.IsStatic ? null : _root, value);
                else if (_member is PropertyInfo pi)
                    pi.SetValue(pi.GetSetMethod(true)?.IsStatic == true ? null : _root, value, _params);
            }
            catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L1631", __apiEx_13); }
            return this;
        }

        public Type GetValueType()
        {
            if (_member is FieldInfo fi) return fi.FieldType;
            if (_member is PropertyInfo pi) return pi.PropertyType;
            if (_member is MethodInfo mi) return mi.ReturnType;
            return _type;
        }

        public List<string> Fields()
        {
            var result = new List<string>();
            var t = _type ?? _root?.GetType();
            if (t == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (t != null && t != typeof(object))
            {
                try
                {
                    foreach (var f in t.GetFields(AllFlags | BindingFlags.DeclaredOnly))
                    {
                        if (f != null && !string.IsNullOrEmpty(f.Name) && seen.Add(f.Name))
                            result.Add(f.Name);
                    }
                }
                catch { }
                t = t.BaseType;
            }
            return result;
        }

        public List<string> Properties()
        {
            var result = new List<string>();
            var t = _type ?? _root?.GetType();
            if (t == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (t != null && t != typeof(object))
            {
                try
                {
                    foreach (var p in t.GetProperties(AllFlags | BindingFlags.DeclaredOnly))
                    {
                        if (p != null && !string.IsNullOrEmpty(p.Name) && seen.Add(p.Name))
                            result.Add(p.Name);
                    }
                }
                catch { }
                t = t.BaseType;
            }
            return result;
        }

        public List<string> Methods()
        {
            var result = new List<string>();
            var t = _type ?? _root?.GetType();
            if (t == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (t != null && t != typeof(object))
            {
                try
                {
                    foreach (var m in t.GetMethods(AllFlags | BindingFlags.DeclaredOnly))
                    {
                        if (m != null && !string.IsNullOrEmpty(m.Name) && seen.Add(m.Name))
                            result.Add(m.Name);
                    }
                }
                catch { }
                t = t.BaseType;
            }
            return result;
        }

        private static FieldInfo FindField(Type t, string name)
        {
            while (t != null && t != typeof(object))
            {
                var f = t.GetField(name, AllFlags) ?? t.GetField($"<{name}>k__BackingField", AllFlags);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type t, string name)
        {
            while (t != null && t != typeof(object))
            {
                var p = t.GetProperty(name, AllFlags);
                if (p != null) return p;
                t = t.BaseType;
            }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, Type[] paramTypes)
        {
            while (t != null && t != typeof(object))
            {
                var methods = t.GetMethods(AllFlags).Where(m => m.Name == name).ToArray();
                if (methods.Length == 1) return methods[0];
                if (methods.Length > 1 && paramTypes != null)
                {
                    foreach (var m in methods)
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == paramTypes.Length) return m;
                    }
                    return methods[0];
                }
                t = t.BaseType;
            }
            return null;
        }
    }
}

namespace Silksong.ModMenu
{
    public interface IModMenuCustomMenu
    {
        string ModMenuName();
        Silksong.ModMenu.Screens.AbstractMenuScreen BuildCustomMenu();
    }

    public static class ModMenuPlugin
    {
        public const string Id = "io.github.silksong-modding.modmenu";
        public static string Name => "ModMenu";
        public static string Version => "0.8.0";
    }

    public static class Registry
    {
        public delegate Silksong.ModMenu.Elements.SelectableElement MenuElementGenerator();
        public static void AddModMenu(string name, MenuElementGenerator generator) { }
        public static void AddModMenu(string name, Delegate generator) { }
    }
}

namespace Silksong.ModMenu.Screens
{
    public enum SelectOnShowBehaviour
    {
        AlwaysForget = 0,
        ForgetBackwards = 1,
        NeverForget = 2
    }

    public abstract class AbstractMenuScreen
    {
        public GameObject Container;
        public object MenuScreen;
        public UnityEngine.UI.Text TitleText;
        public GameObject ControlsPane;
        public Silksong.ModMenu.Elements.TextButton BackButton;
        public Silksong.ModMenu.Elements.VisibilityManager visibility = new Silksong.ModMenu.Elements.VisibilityManager(true);
        public bool AllowGoBack = true;
        public SelectOnShowBehaviour SelectOnShowBehaviour;
        public event Action OnShow;
        public event Action OnGoBack;
        public event Action OnHide;

        protected AbstractMenuScreen() { }
        protected AbstractMenuScreen(Silksong.ModMenu.Elements.LocalizedText title) { }
        public virtual void UpdateLayout() { }
        public virtual void AddChild(Silksong.ModMenu.Elements.IMenuEntity child) { }
        public virtual IEnumerable<Silksong.ModMenu.Elements.IMenuEntity> AllEntities() => Array.Empty<Silksong.ModMenu.Elements.IMenuEntity>();
        public virtual void SelectOnShow(MenuScreenNavigation.NavigationType navType) { }
        protected virtual UnityEngine.UI.Selectable GetDefaultSelectableInternal() => null;
        public virtual UnityEngine.UI.Selectable GetLastSelectableOrDefault() => null;
    }

    public class BasicMenuScreen : AbstractMenuScreen
    {
        public Silksong.ModMenu.Elements.INavigableMenuEntity Content { get; set; }
        public Vector2 Anchor;
        public BasicMenuScreen() { }
        public BasicMenuScreen(Silksong.ModMenu.Elements.LocalizedText title, Silksong.ModMenu.Elements.INavigableMenuEntity content = null) : base(title) { Content = content; }
        public void ClearContent() { }
    }

    public class SimpleMenuScreen : AbstractMenuScreen
    {
        public Silksong.ModMenu.Elements.VerticalGroup Content { get; } = new Silksong.ModMenu.Elements.VerticalGroup();
        public SimpleMenuScreen() { }
        public SimpleMenuScreen(string title) { }
        public SimpleMenuScreen(Silksong.ModMenu.Elements.LocalizedText title) : base(title) { }
        public void Add(Silksong.ModMenu.Elements.MenuElement element) { }
        public void AddRange(IEnumerable<Silksong.ModMenu.Elements.MenuElement> elements) { }
    }

    public class ScrollingMenuScreen : AbstractMenuScreen
    {
        public Silksong.ModMenu.Elements.ScrollingPane ScrollPane { get; } = new Silksong.ModMenu.Elements.ScrollingPane(null);
        public Silksong.ModMenu.Elements.VerticalGroup Content { get; } = new Silksong.ModMenu.Elements.VerticalGroup();
        public ScrollingMenuScreen() { }
        public ScrollingMenuScreen(Silksong.ModMenu.Elements.LocalizedText title) : base(title) { }
        public void Add(Silksong.ModMenu.Elements.MenuElement element) { }
        public void AddRange(IEnumerable<Silksong.ModMenu.Elements.MenuElement> elements) { }
    }

    public class PaginatedMenuScreen : AbstractMenuScreen
    {
        public int PageNumber { get; set; }
        public int PageCount { get; set; } = 1;
        public Vector2 Anchor;
        public PaginatedMenuScreen() { }
        public PaginatedMenuScreen(Silksong.ModMenu.Elements.LocalizedText title) : base(title) { }
        public void AddPage(Silksong.ModMenu.Elements.INavigableMenuEntity page) { }
        public void AddPages(IEnumerable<Silksong.ModMenu.Elements.INavigableMenuEntity> pages) { }
        public void InsertPage(int index, Silksong.ModMenu.Elements.INavigableMenuEntity page) { }
        public bool RemovePage(Silksong.ModMenu.Elements.INavigableMenuEntity page) => false;
        public void RemoveAt(int index) { }
    }

    public class PaginatedMenuScreenBuilder
    {
        public Vector2 Anchor;
        public float VerticalSpacing = 60f;
        public PaginatedMenuScreenBuilder(string title) { }
        public PaginatedMenuScreenBuilder(Silksong.ModMenu.Elements.LocalizedText title, int itemsPerPage = 8) { }
        public PaginatedMenuScreenBuilder(Silksong.ModMenu.Plugin.LocalizedText title, int itemsPerPage = 8) { }
        public void Add(Silksong.ModMenu.Elements.MenuElement element) { }
        public void AddRange(IEnumerable<Silksong.ModMenu.Elements.MenuElement> elements) { }
        public PaginatedMenuScreen Build() => new PaginatedMenuScreen();
    }

    public enum HistoryMode { None = 0, Push = 1, Add = 0, Replace = 1 }

    public static class MenuScreenNavigation
    {
        public enum NavigationType { Forwards = 0, Backwards = 1 }
        public static AbstractMenuScreen CurrentModMenuScreen { get; set; }
        public static void Show(AbstractMenuScreen screen, HistoryMode mode = default) { CurrentModMenuScreen = screen; }
        public static void GoBack(int count = 1) { }
        public static void GoBackTo(AbstractMenuScreen screen) { }
        public static void GoBackWhile(Func<AbstractMenuScreen, bool> predicate) { }
        public static void CloseAll() { }
    }
}

namespace Silksong.ModMenu.Elements
{
    public enum NavigationDirection { Up = 0, Left = 1, Right = 2, Down = 3 }
    public enum ElementState { DEFAULT = 0, TRUE = 1, FALSE = 2, INVALID = 3 }
    public enum FontSizes { Small = 0, Medium = 1, Large = 2 }

    public static class FontSizeConstants
    {
        public const int CHOICE_SMALL = 24;
        public const int CHOICE_MEDIUM = 28;
        public const int CHOICE_LARGE = 32;
        public const int DESCRIPTION_SMALL = 20;
        public const int DESCRIPTION_MEDIUM = 24;
        public const int DESCRIPTION_LARGE = 28;
        public const int LABEL_SMALL = 24;
        public const int LABEL_MEDIUM = 28;
        public const int LABEL_LARGE = 32;
        public const int SLIDER_SMALL = 24;
        public const int SLIDER_MEDIUM = 28;
        public const int SLIDER_LARGE = 32;
        public static int ChoiceSize(FontSizes sz) => 28;
        public static int DescriptionSize(FontSizes sz) => 24;
        public static int LabelSize(FontSizes sz) => 28;
        public static int SliderSize(FontSizes sz) => 28;
    }

    public static class SpacingConstants
    {
        public static float HSPACE_SMALL => 20f;
        public static float HSPACE_MEDIUM => 40f;
        public static float HSPACE_LARGE => 60f;
        public static float VSPACE_SMALL => 40f;
        public static float VSPACE_MEDIUM => 60f;
        public static float VSPACE_LARGE => 80f;
        public static readonly Vector2 TOP_CENTER_ANCHOR = new Vector2(0f, 300f);
    }

    public static class Colors
    {
        public static readonly Color DEFAULT = Color.white;
        public static readonly Color DEFAULT_LOCKED = Color.gray;
        public static readonly Color TRUE = Color.green;
        public static readonly Color TRUE_LOCKED = Color.gray;
        public static readonly Color FALSE = Color.red;
        public static readonly Color FALSE_LOCKED = Color.gray;
        public static readonly Color INVALID = Color.yellow;
        public static readonly Color INVALID_LOCKED = Color.gray;
        public static Color GetDefaultColor(ElementState state, bool locked = false) => Color.white;
        public static Color GetDefaultColor(MenuElement elem) => Color.white;
        public static Color GetDefaultColor(SelectableElement elem) => Color.white;
    }

    public class VisibilityManager
    {
        public bool VisibleSelf { get; set; } = true;
        public bool VisibleInHierarchy => VisibleSelf;
        public event Action<bool> OnVisibilityChanged;
        public VisibilityManager(bool visible = true) { VisibleSelf = visible; }
        public void SetParent(VisibilityManager parent) { }
        public void ClearParent() { }
    }

    public class MenuDisposable : IDisposable
    {
        public event Action OnDispose;
        public void Dispose() { try { OnDispose?.Invoke(); } catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L1869", __apiEx_14); } }
    }

    public interface IMenuEntity
    {
        VisibilityManager Visibility { get; }
        IEnumerable<MenuElement> AllElements();
        void UpdateLayout(Vector2 anchor);
        void SetGameObjectParent(GameObject parent);
        void ClearGameObjectParent();
    }

    public interface INavigable
    {
        void ClearNeighbors();
        void SetNeighbor(NavigationDirection dir, UnityEngine.UI.Selectable sel);
        void ClearNeighbor(NavigationDirection dir);
        bool GetSelectable(NavigationDirection dir, out UnityEngine.UI.Selectable sel);
    }

    public interface INavigableMenuEntity : IMenuEntity, INavigable
    {
        UnityEngine.UI.Selectable GetDefaultSelectable();
    }

    public static class IMenuEntityExtensions
    {
        public static bool get_Visible(this IMenuEntity entity) => entity?.Visibility?.VisibleInHierarchy ?? true;
        public static bool get_VisibleSelf(this IMenuEntity entity) => entity?.Visibility?.VisibleSelf ?? true;
        public static void set_VisibleSelf(this IMenuEntity entity, bool value) { if (entity?.Visibility != null) entity.Visibility.VisibleSelf = value; }
        public static bool get_VisibleInHierarchy(this IMenuEntity entity) => entity?.Visibility?.VisibleInHierarchy ?? true;
        public static void SetParents(this IMenuEntity child, IMenuEntity parent, GameObject go) { }
        public static void ClearParents(this IMenuEntity child) { }
    }

    public struct LocalizedText
    {
        public string Text { get; set; }
        public TeamCherry.Localization.LocalisedString Canonical { get; set; }
        public bool IsLocalized => false;
        public string Localized => Text ?? "";
        public LocalizedText(string text) { Text = text ?? ""; Canonical = default; }
        public LocalizedText(string sheet, string key) { Text = key ?? ""; Canonical = new TeamCherry.Localization.LocalisedString(sheet ?? "", key ?? ""); }
        public static implicit operator LocalizedText(string text) => new LocalizedText(text);
        public static implicit operator LocalizedText(TeamCherry.Localization.LocalisedString ls) => new LocalizedText(ls.Sheet, ls.Key);
        public static implicit operator Silksong.ModMenu.Plugin.LocalizedText(LocalizedText lt) => new Silksong.ModMenu.Plugin.LocalizedText(lt.Text);
        public static implicit operator LocalizedText(Silksong.ModMenu.Plugin.LocalizedText lt) => new LocalizedText(lt.Text);
        public static LocalizedText Raw(string text) => new LocalizedText(text);
        public static LocalizedText Key(string key) => new LocalizedText(key);
        public static LocalizedText Key(string sheet, string key) => new LocalizedText(sheet, key);
        public static LocalizedText Key(TeamCherry.Localization.LocalisedString ls) => new LocalizedText(ls.Sheet, ls.Key);
        public override string ToString() => Text ?? "";
    }

    public static class LocalizedTextExtensions
    {
        public static LocalizedText get_LocalizedText(this UnityEngine.UI.Text text) => new LocalizedText(text != null ? text.text : "");
        public static void set_LocalizedText(this UnityEngine.UI.Text text, LocalizedText value) { if (text != null) text.text = value.Text ?? ""; }
    }

    public class MenuElement : IMenuEntity
    {
        public VisibilityManager Visibility { get; } = new VisibilityManager(true);
        public GameObject Container;
        public RectTransform RectTransform;
        public ElementState State { get; set; }
        public bool ApplyDefaultColors = true;
        public bool Interactable { get; set; } = true;
        public bool Hidden { get; set; }
        public string Name { get; set; }
        public event Action<ElementState> OnStateChanged;
        public event Action<bool> OnVisibilityChanged;

        protected static GameObject _sharedModMenuDummyRoot;
        protected void EnsureContainerAndRect()
        {
            try
            {
                if (Container == null)
                {
                    if (_sharedModMenuDummyRoot == null)
                    {
                        _sharedModMenuDummyRoot = new GameObject("SilksongModMenu_ShimRoot");
                        _sharedModMenuDummyRoot.SetActive(false);
                        UnityEngine.Object.DontDestroyOnLoad(_sharedModMenuDummyRoot);
                    }
                    Container = new GameObject("ModMenuElement", typeof(RectTransform));
                    Container.transform.SetParent(_sharedModMenuDummyRoot.transform, false);
                }
                if (RectTransform == null && Container != null)
                {
                    RectTransform = Container.GetComponent<RectTransform>() ?? Container.AddComponent<RectTransform>();
                }
            }
            catch { }
        }

        public MenuElement() { EnsureContainerAndRect(); }
        public MenuElement(GameObject container) { Container = container; EnsureContainerAndRect(); }
        public virtual IEnumerable<MenuElement> AllElements() => new[] { this };
        public virtual void SetMainColor(Color color) { }
        public virtual void MaybeApplyDefaultColors() { }
        public virtual void UpdateLayout(Vector2 anchor) { }
        public virtual void SetFontSizes(FontSizes sizes) { }
        public virtual void SetGameObjectParent(GameObject parent) { }
        public virtual void ClearGameObjectParent() { }
    }

    public class SelectableElement : MenuElement, INavigableMenuEntity
    {
        public UnityEngine.UI.Selectable SelectableComponent;
        public bool IsSelected => false;
        public event Action<bool> OnInteractableChanged;
        public SelectableElement() { }
        public SelectableElement(GameObject container, UnityEngine.UI.Selectable sel) : base(container) { SelectableComponent = sel; }
        public void ForceSelect() { }
        public UnityEngine.UI.Selectable GetDefaultSelectable() => SelectableComponent;
        public void ClearNeighbors() { }
        public void ClearNeighbor(NavigationDirection dir) { }
        public void SetNeighbor(NavigationDirection dir, UnityEngine.UI.Selectable sel) { }
        public bool GetSelectable(NavigationDirection dir, out UnityEngine.UI.Selectable sel) { sel = SelectableComponent; return false; }
    }

    public class BaseSelectableValueElement : SelectableElement
    {
        public Silksong.ModMenu.Models.IBaseValueModel RawModel { get; set; }
        public BaseSelectableValueElement() { }
        public BaseSelectableValueElement(GameObject container, UnityEngine.UI.Selectable sel) : base(container, sel) { }
    }

    public class SelectableValueElement<T> : BaseSelectableValueElement
    {
        public Silksong.ModMenu.Models.IValueModel<T> Model;
        private T _val;
        public virtual T Value
        {
            get => Model != null ? Model.Value : _val;
            set
            {
                _val = value;
                if (Model != null) Model.Value = value;
                try { OnValueChanged?.Invoke(value); } catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L1986", __apiEx_15); }
            }
        }
        public event Action<T> OnValueChanged;
        public SelectableValueElement() { }
        public SelectableValueElement(GameObject container, UnityEngine.UI.Selectable sel, Silksong.ModMenu.Models.IValueModel<T> model) : base(container, sel) { Model = model; }
    }

    public class TextLabel : MenuElement
    {
        public UnityEngine.UI.Text Text;
        public TextLabel(LocalizedText text)
        {
            try
            {
                EnsureContainerAndRect();
                if (Container != null && Text == null)
                {
                    Text = Container.GetComponent<UnityEngine.UI.Text>() ?? Container.AddComponent<UnityEngine.UI.Text>();
                    Text.text = text.Text ?? "";
                }
            }
            catch { }
        }
    }

    public class TextButton : SelectableElement
    {
        public Action OnSubmit;
        public UnityEngine.UI.MenuButton MenuButton;
        public UnityEngine.UI.Text ButtonText;
        public UnityEngine.UI.Text DescriptionText;
        public TextButton(string label) { }
        public TextButton(LocalizedText label) { }
        public TextButton(LocalizedText label, LocalizedText description) { }
        public TextButton(Silksong.ModMenu.Screens.AbstractMenuScreen targetScreen) { }
        public TextButton(Silksong.ModMenu.Plugin.LocalizedText label) { }
        public TextButton(Silksong.ModMenu.Plugin.LocalizedText label, Silksong.ModMenu.Plugin.LocalizedText description) { }
        public static TextButton CreateGoToButton(LocalizedText label, Func<Silksong.ModMenu.Screens.AbstractMenuScreen> screenGetter) => new TextButton(label.ToString());
        public static TextButton CreateGoToButton(Silksong.ModMenu.Plugin.LocalizedText label, Func<Silksong.ModMenu.Screens.AbstractMenuScreen> screenGetter) => new TextButton(label.ToString());
    }

    public class ChoiceElement<T> : SelectableValueElement<T>
    {
        public Silksong.ModMenu.Models.IChoiceModel<T> ChoiceModel;
        public UnityEngine.UI.Text LabelText;
        public UnityEngine.UI.Text DescriptionText;
        public UnityEngine.UI.Text ChoiceText;
        public ChoiceElement(LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model, LocalizedText description) { ChoiceModel = model; Model = model; }
        public ChoiceElement(LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model) { ChoiceModel = model; Model = model; }
        public ChoiceElement(LocalizedText label, List<T> values, LocalizedText description) { }
        public ChoiceElement(LocalizedText label, List<T> values) { }
        public ChoiceElement(LocalizedText label, Silksong.ModMenu.Models.AbstractValueModel<T> model, LocalizedText description = default) { ChoiceModel = model; Model = model; }
        public ChoiceElement(Silksong.ModMenu.Plugin.LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model, Silksong.ModMenu.Plugin.LocalizedText description = default) { ChoiceModel = model; Model = model; }
        public ChoiceElement(Silksong.ModMenu.Plugin.LocalizedText label, Silksong.ModMenu.Models.AbstractValueModel<T> model, Silksong.ModMenu.Plugin.LocalizedText description = default) { ChoiceModel = model; Model = model; }
    }

    public class DynamicDescriptionChoiceElement<T> : ChoiceElement<T>
    {
        public UnityEngine.UI.Text RightText;
        public DynamicDescriptionChoiceElement(LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model, LocalizedText desc, LocalizedText rightDesc) : base(label, model, desc) { }
        public DynamicDescriptionChoiceElement(LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model, LocalizedText desc, Func<T, LocalizedText> rightDescFn) : base(label, model, desc) { }
    }

    public class SliderElement<T> : SelectableValueElement<T>
    {
        public UnityEngine.UI.Slider Slider;
        public Silksong.ModMenu.Models.SliderModel<T> SliderModel;
        public UnityEngine.UI.Text LabelText;
        public UnityEngine.UI.Text ValueText;
        public SliderElement(LocalizedText label, Silksong.ModMenu.Models.SliderModel<T> model) { SliderModel = model; Model = model; }
        public SliderElement(LocalizedText label, Silksong.ModMenu.Models.IChoiceModel<T> model, LocalizedText description = default) { Model = model; }
    }

    public class TextInput<T> : SelectableValueElement<T>
    {
        public Silksong.ModMenu.Models.ITextModel<T> TextModel;
        public UnityEngine.UI.Text LabelText;
        public UnityEngine.UI.Text DescriptionText;
        public UnityEngine.UI.InputField InputField;
        public event Action<string> OnTextValueChanged;
        public TextInput(LocalizedText label, Silksong.ModMenu.Models.ITextModel<T> model, LocalizedText description) { TextModel = model; Model = model; }
        public TextInput(LocalizedText label, Silksong.ModMenu.Models.ITextModel<T> model) { TextModel = model; Model = model; }
    }

    public class ColorInput : TextInput<Color>
    {
        public enum InputFormat { RGB = 0, RGBA = 1 }
        public float swatchSizeMultiplier = 1f;
        public InputFormat Format { get; set; }
        public GameObject Swatch;
        public UnityEngine.UI.Image SwatchFill;
        public UnityEngine.UI.Image SwatchOutline;
        public GameObject InvalidValueIndicator;
        public ColorInput(LocalizedText label) : base(label, null) { }
        public ColorInput(LocalizedText label, LocalizedText description) : base(label, null, description) { }
        public static char HexValidation(string text, int charIndex, char addedChar) => addedChar;
    }

    public class KeyBindElement : SelectableValueElement<KeyCode>
    {
        public UnityEngine.UI.Text LabelText;
        public UnityEngine.UI.Text KeyBindText;
        public UnityEngine.UI.Image KeyBindImage;
        public KeyBindElement(LocalizedText label, Silksong.ModMenu.Models.IValueModel<KeyCode> model) { Model = model; }
        public KeyBindElement(LocalizedText label) : this(label, new Silksong.ModMenu.Models.ValueModel<KeyCode>(KeyCode.None)) { }
    }

    public class KeyboardShortcutElement : SelectableValueElement<KeyboardShortcut>
    {
        public UnityEngine.UI.Text LabelText;
        public KeyboardShortcutElement(LocalizedText label, Silksong.ModMenu.Models.IValueModel<KeyboardShortcut> model) { Model = model; }
        public KeyboardShortcutElement(LocalizedText label) : this(label, new Silksong.ModMenu.Models.ValueModel<KeyboardShortcut>(KeyboardShortcut.Empty)) { }
    }

    public abstract class AbstractGroup : INavigableMenuEntity
    {
        public VisibilityManager Visibility { get; } = new VisibilityManager(true);
        public virtual IEnumerable<IMenuEntity> AllEntities() => Array.Empty<IMenuEntity>();
        public virtual IEnumerable<MenuElement> AllElements() => Array.Empty<MenuElement>();
        public virtual bool Contains(IMenuEntity entity) => false;
        public virtual void AddChild(IMenuEntity entity) { }
        public virtual void Clear() { }
        public virtual IEnumerable<INavigable> GetNavigables(NavigationDirection dir) => Array.Empty<INavigable>();
        public virtual void ClearNeighbor(NavigationDirection dir) { }
        public virtual void ClearNeighbors() { }
        public virtual UnityEngine.UI.Selectable GetDefaultSelectable() => null;
        public virtual bool GetSelectable(NavigationDirection dir, out UnityEngine.UI.Selectable sel) { sel = null; return false; }
        public virtual void SetGameObjectParent(GameObject parent) { }
        public virtual void ClearGameObjectParent() { }
        public virtual void SetNeighbor(NavigationDirection dir, UnityEngine.UI.Selectable sel) { }
        public virtual void UpdateLayout(Vector2 anchor) { }
    }

    public class VerticalGroup : AbstractGroup
    {
        public float VerticalSpacing = 60f;
        public bool HideInactiveElements = true;
        public void Add(IMenuEntity entity) { }
        public void AddRange(IEnumerable<IMenuEntity> entities) { }
        public void Insert(int index, IMenuEntity entity) { }
        public bool Remove(IMenuEntity entity) => false;
        public void RemoveAt(int index) { }
    }

    public class GridGroup : AbstractGroup
    {
        public int Columns = 2;
        public int Rows => 1;
        public float HorizontalSpacing = 40f;
        public float VerticalSpacing = 40f;
        public bool WrapHorizontal;
        public GridGroup(int columns = 2) { Columns = columns; }
        public bool TryGetCell(IMenuEntity entity, out int row, out int col) { row = 0; col = 0; return false; }
        public bool TryGetEntity(int row, int col, out IMenuEntity entity) { entity = null; return false; }
        public void Add(IMenuEntity entity) { }
        public void AddAt(int row, int col, IMenuEntity entity) { }
        public bool Remove(IMenuEntity entity) => false;
        public bool RemoveAt(int row, int col) => false;
    }

    public class FreeGroup : AbstractGroup
    {
        public int Count => 0;
        public bool TryGetOffset(IMenuEntity entity, out Vector2 offset) { offset = Vector2.zero; return false; }
        public void Add(IMenuEntity entity, Vector2 offset) { }
        public void Update(IMenuEntity entity, Vector2 offset) { }
        public bool Remove(IMenuEntity entity) => false;
    }

    public class ScrollingPane : INavigableMenuEntity
    {
        public enum ScrollAxes { Vertical = 0, Horizontal = 1, Both = 2 }
        public VisibilityManager Visibility { get; } = new VisibilityManager(true);
        public INavigableMenuEntity Content { get; set; }
        public UnityEngine.UI.ScrollRect scrollRect;
        public GameObject scrollPane;
        public GameObject contentPane;
        public Vector2 ViewportSize { get; set; }
        public ScrollAxes Axes { get; set; }
        public float SmoothScrollTime { get; set; } = 0.1f;
        public ScrollingPane(INavigableMenuEntity content) { Content = content; }
        public void ScrollTo(MenuElement element, bool immediate = false) { }
        public void ScrollTo(int index, bool immediate = false) { }
        public void QueueContentResize() { }
        public void UpdateLayout(Vector2 anchor) { }
        public void SetGameObjectParent(GameObject parent) { }
        public void ClearGameObjectParent() { }
        public UnityEngine.UI.Selectable GetDefaultSelectable() => null;
        public IEnumerable<MenuElement> AllElements() => Array.Empty<MenuElement>();
        public void ClearNeighbors() { }
        public void SetNeighbor(NavigationDirection dir, UnityEngine.UI.Selectable sel) { }
        public void ClearNeighbor(NavigationDirection dir) { }
        public bool GetSelectable(NavigationDirection dir, out UnityEngine.UI.Selectable sel) { sel = null; return false; }
    }
}

namespace Silksong.ModMenu.Models
{
    public delegate string IndexedToString<T>(int index, T val);

    public interface IDisplayable
    {
        string DisplayString();
    }

    public interface IBaseValueModel
    {
        event Action<object> OnRawValueChanged;
        object GetRawValue();
        void SetRawValue(object val);
        object RawValue { get; set; }
    }

    public interface IValueModel<T> : IBaseValueModel
    {
        event Action<T> OnValueChanged;
        T GetValue();
        void SetValue(T val);
        T Value { get; set; }
    }

    public interface IBaseChoiceModel : IBaseValueModel, IDisplayable
    {
        void MoveLeft();
        void MoveRight();
    }

    public interface IChoiceModel<T> : IValueModel<T>, IBaseChoiceModel
    {
    }

    public interface ITextModel<T> : IValueModel<T>, IDisplayable
    {
        bool IsTextValid { get; }
        event Action<string> OnTextValueChanged;
        string GetTextValue();
        void SetTextValue(string text);
        string TextValue { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ModMenuNameAttribute : Attribute
    {
        public string CustomName { get; }
        public ModMenuNameAttribute(string name) { CustomName = name; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ModMenuIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class LocalizedDescriptionAttribute : Attribute
    {
        public string SheetTitle { get; }
        public string Key { get; }
        public LocalizedDescriptionAttribute(string sheetTitle, string key) { SheetTitle = sheetTitle; Key = key; }
    }

    public abstract class AbstractValueModel<T> : IChoiceModel<T>, ITextModel<T>
    {
        protected T _value;
        protected AbstractValueModel() { }
        protected AbstractValueModel(T initial) { _value = initial; }
        public event Action<T> OnValueChanged;
        public event Action<object> OnRawValueChanged;
        public event Action<string> OnTextValueChanged;
        protected void InvokeOnValueChanged()
        {
            try { OnValueChanged?.Invoke(_value); } catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L2243", __apiEx_16); }
            try { OnRawValueChanged?.Invoke(_value); } catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L2244", __apiEx_17); }
        }
        public virtual T GetValue() => _value;
        public virtual void SetValue(T val) { _value = val; InvokeOnValueChanged(); }
        public virtual T Value
        {
            get => GetValue();
            set => SetValue(value);
        }
        public object GetRawValue() => _value;
        public void SetRawValue(object val) { if (val is T tv) SetValue(tv); }
        public object RawValue { get => GetRawValue(); set => SetRawValue(value); }
        public virtual string DisplayString() => _value != null ? _value.ToString() : "";
        public virtual void MoveLeft() { }
        public virtual void MoveRight() { }
        public virtual bool IsTextValid => true;
        public virtual string GetTextValue() => DisplayString();
        public virtual void SetTextValue(string text) { try { OnTextValueChanged?.Invoke(text); } catch (System.Exception __apiEx_18) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L2261", __apiEx_18); } }
        public string TextValue { get => GetTextValue(); set => SetTextValue(value); }
    }

    public class ValueModel<T> : AbstractValueModel<T>
    {
        public ValueModel() { }
        public ValueModel(T val) : base(val) { }
    }

    public class IntRangeChoiceModel : AbstractValueModel<int>
    {
        public int Minimum { get; set; }
        public int Maximum { get; set; }
        public bool Circular;
        public IndexedToString<int> DisplayFn;
        public Action<int, Silksong.ModMenu.Elements.ChoiceElement<int>> ValueDisplayInterceptor { get; set; }
        public IntRangeChoiceModel(int min, int max, int defaultValue = 0) : base(defaultValue) { Minimum = min; Maximum = max; }
        public void ResetParams(int min, int max, int val) { Minimum = min; Maximum = max; Value = val; }
    }

    public class FloatStepChoiceModel : AbstractValueModel<float>
    {
        public FloatStepChoiceModel(float min, float max, int steps) : base(min) { }
    }

    public class FloatRangeChoiceModel : AbstractValueModel<float>
    {
        public FloatRangeChoiceModel(float min, float max, float defaultValue = 0f) : base(defaultValue) { }
    }

    public class BoolChoiceModel : AbstractValueModel<bool>
    {
        public BoolChoiceModel(bool defaultValue = false) : base(defaultValue) { }
    }

    public class ListChoiceModel<T> : AbstractValueModel<T>
    {
        public IReadOnlyList<T> Values { get; private set; }
        public int Count => Values != null ? Values.Count : 0;
        public bool Circular;
        public int Index { get; set; }
        public IndexedToString<T> DisplayFn;
        public ListChoiceModel(List<T> items) : base(items != null && items.Count > 0 ? items[0] : default) { Values = items ?? new List<T>(); }
        public ListChoiceModel(IList<T> items, T defaultVal = default) : base(defaultVal) { Values = items != null ? new List<T>(items) : new List<T>(); }
        public void UpdateValues(List<T> items, int index = 0) { Values = items ?? new List<T>(); Index = index; }
        public void UpdateValues(IList<T> items, int index = 0) { Values = items != null ? new List<T>(items) : new List<T>(); Index = index; }
        public static string DefaultDisplayString(int idx, T val) => val != null ? val.ToString() : "";
    }

    public static class ChoiceModels
    {
        public static ListChoiceModel<bool> ForBool(string falseText, string trueText) => new ListChoiceModel<bool>(new List<bool> { false, true });
        public static ListChoiceModel<bool> ForBool() => new ListChoiceModel<bool>(new List<bool> { false, true });
        public static bool TryForEnum<T>(Type enumType, out ListChoiceModel<T> model) { model = new ListChoiceModel<T>(new List<T>()); return true; }
        public static bool TryForEnum<T>(out ListChoiceModel<T> model) { model = new ListChoiceModel<T>(new List<T>()); return true; }
        public static ListChoiceModel<T> ForEnum<T>() => new ListChoiceModel<T>(new List<T>());
        public static ListChoiceModel<T> ForNamedValues<T>(IEnumerable<ValueTuple<T, string>> items) => new ListChoiceModel<T>(new List<T>());
        public static ListChoiceModel<T> ForLocalizedValues<T>(IEnumerable<ValueTuple<T, Silksong.ModMenu.Elements.LocalizedText>> items) => new ListChoiceModel<T>(new List<T>());
        public static ListChoiceModel<T> ForValues<T>(List<T> items) => new ListChoiceModel<T>(items);
    }

    public abstract class SliderModel<T> : AbstractValueModel<T>
    {
        public int MinimumIndex;
        public int MaximumIndex;
        public int Index { get; set; }
        public IndexedToString<T> DisplayFn;
        protected SliderModel(int minIdx, int maxIdx) { MinimumIndex = minIdx; MaximumIndex = maxIdx; }
        public virtual T GetValue(int index) => Value;
        public virtual bool GetIndex(T val, out int index) { index = 0; return true; }
        public static string DefaultDisplayString(int idx, T val) => val != null ? val.ToString() : "";
    }

    public class IntSliderModel : SliderModel<int>
    {
        public IntSliderModel(int min, int max) : base(min, max) { Value = min; }
    }

    public class LinearFloatSliderModel : SliderModel<float>
    {
        public float MinimumValue;
        public float MaximumValue;
        public int Ticks;
        public LinearFloatSliderModel(float min, float max, int ticks = 100) : base(0, ticks) { MinimumValue = min; MaximumValue = max; Ticks = ticks; Value = min; }
        public static string DefaultDisplayString(int idx, float val) => val.ToString("F2");
    }

    public class ListSliderModel<T> : SliderModel<T>
    {
        public ListSliderModel(List<T> items) : base(0, items != null ? Math.Max(0, items.Count - 1) : 0) { }
    }

    public static class SliderModels
    {
        public static LinearFloatSliderModel ForFloats(float min, float max, int ticks = 100) => new LinearFloatSliderModel(min, max, ticks);
        public static IntSliderModel ForInts(int min, int max) => new IntSliderModel(min, max);
        public static ListSliderModel<T> ForNamedValues<T>(IEnumerable<ValueTuple<T, string>> items) => new ListSliderModel<T>(new List<T>());
        public static ListSliderModel<T> ForValues<T>(List<T> items) => new ListSliderModel<T>(items);
    }

    public class ParserTextModel<T> : AbstractValueModel<T>
    {
        public delegate bool Parse(string text, out T val);
        public delegate bool Unparse(T value, out string text);
        private readonly Parse _parse;
        private readonly Unparse _unparse;
        private string _lastText = "";
        private bool _isTextValid = true;
        public Func<T, bool> ConstraintFn = (T _) => true;
        public ParserTextModel() { }
        public ParserTextModel(Parse parseFn, Unparse unparseFn, T initial = default) : base(initial)
        {
            _parse = parseFn;
            _unparse = unparseFn;
            if (_unparse != null && _unparse(initial, out string text))
                _lastText = text ?? "";
            else
                _lastText = initial != null ? initial.ToString() : "";
        }
        public override bool IsTextValid => _isTextValid;
        public override string GetTextValue() => _lastText ?? base.GetTextValue();
        public override void SetValue(T val)
        {
            if (ConstraintFn != null && !ConstraintFn(val)) return;
            if (_unparse != null && _unparse(val, out string text))
                _lastText = text ?? "";
            else
                _lastText = val != null ? val.ToString() : "";
            _isTextValid = true;
            base.SetValue(val);
        }
        public override void SetTextValue(string text)
        {
            _lastText = text ?? "";
            if (_parse != null && _parse(_lastText, out T parsed) && (ConstraintFn == null || ConstraintFn(parsed)))
            {
                _isTextValid = true;
                base.SetValue(parsed);
            }
            else
            {
                _isTextValid = false;
            }
            base.SetTextValue(_lastText);
        }
    }

    public static class TextModels
    {
        public static ParserTextModel<string> ForStrings() => new ParserTextModel<string>();
        public static ParserTextModel<sbyte> ForSignedBytes() => new ParserTextModel<sbyte>();
        public static ParserTextModel<sbyte> ForSignedBytes(sbyte min, sbyte max) => new ParserTextModel<sbyte>();
        public static ParserTextModel<byte> ForBytes() => new ParserTextModel<byte>();
        public static ParserTextModel<byte> ForBytes(byte min, byte max) => new ParserTextModel<byte>();
        public static ParserTextModel<short> ForShorts() => new ParserTextModel<short>();
        public static ParserTextModel<short> ForShorts(short min, short max) => new ParserTextModel<short>();
        public static ParserTextModel<ushort> ForUnsignedShorts() => new ParserTextModel<ushort>();
        public static ParserTextModel<ushort> ForUnsignedShorts(ushort min, ushort max) => new ParserTextModel<ushort>();
        public static ParserTextModel<int> ForIntegers() => new ParserTextModel<int>();
        public static ParserTextModel<int> ForIntegers(int min, int max) => new ParserTextModel<int>();
        public static ParserTextModel<uint> ForUnsignedIntegers() => new ParserTextModel<uint>();
        public static ParserTextModel<uint> ForUnsignedIntegers(uint min, uint max) => new ParserTextModel<uint>();
        public static ParserTextModel<long> ForLongs() => new ParserTextModel<long>();
        public static ParserTextModel<long> ForLongs(long min, long max) => new ParserTextModel<long>();
        public static ParserTextModel<ulong> ForUnsignedLongs() => new ParserTextModel<ulong>();
        public static ParserTextModel<ulong> ForUnsignedLongs(ulong min, ulong max) => new ParserTextModel<ulong>();
        public static ParserTextModel<float> ForFloats() => new ParserTextModel<float>();
        public static ParserTextModel<float> ForFloats(float min, float max) => new ParserTextModel<float>();
        public static ParserTextModel<double> ForDoubles() => new ParserTextModel<double>();
        public static ParserTextModel<double> ForDoubles(double min, double max) => new ParserTextModel<double>();
        public static ParserTextModel<Color> ForHexColors() => new ParserTextModel<Color>();
    }
}

namespace Silksong.ModMenu.Plugin
{
    public interface IModMenuInterface
    {
        string ModMenuName();
    }

    public interface IModMenuCustomMenu : IModMenuInterface
    {
        Silksong.ModMenu.Screens.AbstractMenuScreen BuildCustomMenu();
    }

    public interface IModMenuCustomElement : IModMenuInterface
    {
        Silksong.ModMenu.Elements.SelectableElement BuildCustomElement();
    }

    public interface IModMenuNestedMenu : IModMenuInterface
    {
        int MinSubgroupSize();
    }

    public interface IModMenuToggle : IModMenuInterface
    {
        bool ModMenuGetEnabled();
        void ModMenuSetEnabled(bool enabled);
        Silksong.ModMenu.Elements.LocalizedText ModMenuDescription();
    }

    public struct LocalizedText
    {
        public string Text;
        public LocalizedText(string text) { Text = text ?? ""; }
        public static implicit operator LocalizedText(string text) => new LocalizedText(text);
        public static implicit operator Silksong.ModMenu.Elements.LocalizedText(LocalizedText lt) => new Silksong.ModMenu.Elements.LocalizedText(lt.Text);
        public static implicit operator LocalizedText(Silksong.ModMenu.Elements.LocalizedText lt) => new LocalizedText(lt.Text);
        public static LocalizedText Raw(string text) => new LocalizedText(text);
        public override string ToString() => Text ?? "";
    }

    public class ConfigEntrySubgroup
    {
        public List<Silksong.ModMenu.Elements.LocalizedText> Subgroups = new List<Silksong.ModMenu.Elements.LocalizedText>();
        public ConfigEntrySubgroup(IEnumerable<Silksong.ModMenu.Elements.LocalizedText> subgroups) { }
        public ConfigEntrySubgroup(Silksong.ModMenu.Elements.LocalizedText first, params Silksong.ModMenu.Elements.LocalizedText[] rest) { }
    }

    public class ConfigEntryFactory
    {
        public delegate bool MenuElementGenerator(ConfigEntryBase entry, out Silksong.ModMenu.Elements.MenuElement element);
        public List<MenuElementGenerator> Generators = new List<MenuElementGenerator>();
        public bool GenerateSubgroups = true;
        public int MinSubgroupSize = 3;

        public static void AddDefaultGenerator(MenuElementGenerator generator) { }

        public virtual bool GenerateMenuElement(ConfigEntryBase entry, out Silksong.ModMenu.Elements.MenuElement element)
        {
            if (Generators != null)
            {
                foreach (var g in Generators)
                {
                    if (g != null && g(entry, out element))
                        return true;
                }
            }
            element = new Silksong.ModMenu.Elements.SelectableElement();
            return true;
        }

        public bool GenerateEntryButton(Silksong.ModMenu.Elements.LocalizedText label, BaseUnityPlugin plugin, out Silksong.ModMenu.Elements.SelectableElement button)
        {
            button = new Silksong.ModMenu.Elements.TextButton(label);
            return true;
        }

        public bool GenerateEntryButton(Silksong.ModMenu.Elements.LocalizedText label, ConfigFile cfg, out Silksong.ModMenu.Screens.AbstractMenuScreen screen, out Silksong.ModMenu.Elements.SelectableElement button)
        {
            screen = new Silksong.ModMenu.Screens.PaginatedMenuScreen(label);
            button = new Silksong.ModMenu.Elements.TextButton(label);
            return true;
        }

        public static bool TryCreateMenuElement(ConfigEntryBase entry, out Silksong.ModMenu.Elements.SelectableElement element)
        {
            element = new Silksong.ModMenu.Elements.SelectableElement();
            return true;
        }
    }

    public static class MenuElementGenerators
    {
        public static ConfigEntryFactory.MenuElementGenerator CreateRightDescGenerator(bool allowAll = true) =>
            (ConfigEntryBase entry, out Silksong.ModMenu.Elements.MenuElement elem) => { elem = new Silksong.ModMenu.Elements.SelectableElement(); return true; };

        public static ConfigEntryFactory.MenuElementGenerator CreateIntSliderGenerator() =>
            (ConfigEntryBase entry, out Silksong.ModMenu.Elements.MenuElement elem) => { elem = new Silksong.ModMenu.Elements.SelectableElement(); return true; };
    }

    public static class ConfigEntryFactoryExtensions
    {
        public static Silksong.ModMenu.Elements.LocalizedText LabelName(this ConfigEntryBase entry) => new Silksong.ModMenu.Elements.LocalizedText(entry?.Definition?.Key ?? "");
        public static Silksong.ModMenu.Elements.LocalizedText DescriptionLine(this ConfigEntryBase entry) => new Silksong.ModMenu.Elements.LocalizedText(entry?.Description?.Description ?? "");
        public static void SynchronizeRawWith(this Silksong.ModMenu.Elements.BaseSelectableValueElement elem, ConfigEntryBase entry) { }
        public static void SynchronizeWith<T>(this Silksong.ModMenu.Elements.SelectableValueElement<T> elem, ConfigEntry<T> entry) { }
    }

    public class RGBColorValues : AcceptableValueBase
    {
        public RGBColorValues() : base(typeof(Color)) { }
        public override object Clamp(object value) => value;
        public override bool IsValid(object value) => true;
        public override string ToDescriptionString() => "# RGB Color";
    }
}

namespace MonoMod.RuntimeDetour
{
    public struct HookConfig
    {
        public bool ManualApply;
        public int Priority;
        public string ID;
    }

    public struct DetourConfig
    {
        public bool ManualApply;
        public int Priority;
        public string ID;
    }

    public interface IDetour : IDisposable
    {
        bool IsValid { get; }
        bool IsApplied { get; }
        void Apply();
        void Undo();
        void Free();
        MethodBase GenerateTrampoline(MethodBase signature = null);
        T GenerateTrampoline<T>() where T : Delegate;
    }

    public class Hook : IDetour
    {
        public bool IsValid => true;
        public bool IsApplied { get; private set; } = true;
        public Hook(MethodBase from, MethodInfo to)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to, null);
        }
        public Hook(MethodBase from, Delegate to)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to?.Method, to?.Target);
        }
        public Hook(MethodBase from, MethodInfo to, object target)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to, target);
        }
        public Hook(MethodBase from, MethodInfo to, ref HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to, null);
        }
        public Hook(MethodBase from, Delegate to, ref HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to?.Method, to?.Target);
        }
        public Hook(MethodBase from, MethodInfo to, HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to, null);
        }
        public Hook(MethodBase from, Delegate to, HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModHook(from, to?.Method, to?.Target);
        }
        public void Apply() { IsApplied = true; }
        public void Undo() { IsApplied = false; }
        public void Free() { IsApplied = false; }
        public void Dispose() { IsApplied = false; }
        public MethodBase GenerateTrampoline(MethodBase signature = null) => null;
        public T GenerateTrampoline<T>() where T : Delegate => null;
    }

    public class ILHook : IDetour
    {
        public bool IsValid => true;
        public bool IsApplied { get; private set; } = true;
        public ILHook(MethodBase from, MonoMod.Cil.ILContext.Manipulator manipulator)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModILHook(from, manipulator);
        }
        public ILHook(MethodBase from, MonoMod.Cil.ILContext.Manipulator manipulator, ref HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModILHook(from, manipulator);
        }
        public ILHook(MethodBase from, MonoMod.Cil.ILContext.Manipulator manipulator, HookConfig config)
        {
            SilksongMobileAPI.HarmonyBridge.RegisterMonoModILHook(from, manipulator);
        }
        public void Apply() { IsApplied = true; }
        public void Undo() { IsApplied = false; }
        public void Free() { IsApplied = false; }
        public void Dispose() { IsApplied = false; }
        public MethodBase GenerateTrampoline(MethodBase signature = null) => null;
        public T GenerateTrampoline<T>() where T : Delegate => null;
    }

    public class Detour : IDetour
    {
        public bool IsValid => true;
        public bool IsApplied { get; private set; } = true;
        public Detour(MethodBase from, MethodBase to) { }
        public Detour(MethodBase from, Delegate to) { }
        public void Apply() { IsApplied = true; }
        public void Undo() { IsApplied = false; }
        public void Free() { IsApplied = false; }
        public void Dispose() { IsApplied = false; }
        public MethodBase GenerateTrampoline(MethodBase signature = null) => null;
        public T GenerateTrampoline<T>() where T : Delegate => null;
    }
}

namespace MonoMod.Cil
{
    public enum MoveType
    {
        Before = 0,
        AfterLabels = 1,
        After = 2
    }

    public sealed class ILLabel
    {
        public object Target { get; set; }
    }

    public class ILContext
    {
        public delegate void Manipulator(ILContext il);
        public MethodBase Method { get; set; }
        public IList Instructions { get; } = new ArrayList();
        public ILContext(MethodBase method) { Method = method; }
        public void Invoke(Manipulator manip) { }
    }

    public class ILCursor
    {
        public ILContext Context { get; }
        public int Index { get; set; }
        public object Next { get; set; }
        public object Prev { get; set; }

        public ILCursor(ILContext context)
        {
            Context = context ?? new ILContext(null);
        }

        public bool TryGotoNext(params Func<object, bool>[] predicates) => false;
        public bool TryGotoNext(MoveType moveType, params Func<object, bool>[] predicates) => false;
        public bool TryGotoPrev(params Func<object, bool>[] predicates) => false;
        public bool TryGotoPrev(MoveType moveType, params Func<object, bool>[] predicates) => false;
        public ILCursor GotoNext(params Func<object, bool>[] predicates) => this;
        public ILCursor GotoNext(MoveType moveType, params Func<object, bool>[] predicates) => this;
        public int EmitDelegate<T>(T cb) where T : Delegate => 0;
        public ILCursor Emit(object opcode, object operand = null) => this;
        public ILCursor Emit(object opcode) => this;
        public ILCursor Remove() => this;
        public ILCursor RemoveRange(int num) => this;
        public ILLabel MarkLabel() => new ILLabel();
        public ILLabel DefineLabel() => new ILLabel();
    }

    public static class ILPatternMatchingExt
    {
        public static bool MatchLdarg(this object instr, int index) => false;
        public static bool MatchLdfld(this object instr, out FieldInfo field) { field = null; return false; }
        public static bool MatchLdfld(this object instr, Type type, string name) => false;
        public static bool MatchCallvirt(this object instr, out MethodBase method) { method = null; return false; }
        public static bool MatchCallvirt(this object instr, Type type, string name) => false;
        public static bool MatchCall(this object instr, out MethodBase method) { method = null; return false; }
        public static bool MatchCall(this object instr, Type type, string name) => false;
        public static bool MatchLdcI4(this object instr, int value) => false;
        public static bool MatchLdcI4(this object instr, out int value) { value = 0; return false; }
        public static bool MatchBle(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchBgt(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchBrtrue(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchBrfalse(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchLdloc(this object instr, int index) => false;
        public static bool MatchLdloc(this object instr, out int index) { index = 0; return false; }
        public static bool MatchStloc(this object instr, int index) => false;
        public static bool MatchStloc(this object instr, out int index) { index = 0; return false; }
        public static bool MatchLdstr(this object instr, string value) => false;
        public static bool MatchLdstr(this object instr, out string value) { value = null; return false; }
        public static bool MatchBeq(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchBneUn(this object instr, out ILLabel label) { label = null; return false; }
        public static bool MatchRet(this object instr) => false;
    }

    public static class RuntimeILReferenceBag
    {
        public static T Get<T>(int id) => default(T);

        public static class FastDelegateInvokers
        {
            public delegate TResult Func<in T1, in T2, out TResult>(T1 arg1, T2 arg2);
            public delegate TResult Func<in T1, in T2, in T3, out TResult>(T1 arg1, T2 arg2, T3 arg3);
            public delegate TResult Func<in T1, in T2, in T3, in T4, out TResult>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
            public delegate void Action<in T1>(T1 arg1);
            public delegate void Action<in T1, in T2>(T1 arg1, T2 arg2);
            public delegate void Action<in T1, in T2, in T3>(T1 arg1, T2 arg2, T3 arg3);

            public static TResult InvokeTypeVal1<T1, TResult>(T1 arg1, Func<T1, TResult> invoker)
                => invoker != null ? invoker(arg1) : default(TResult);

            public static TResult InvokeTypeVal2<T1, T2, TResult>(T1 arg1, T2 arg2, Func<T1, T2, TResult> invoker)
                => invoker != null ? invoker(arg1, arg2) : default(TResult);

            public static TResult InvokeTypeVal3<T1, T2, T3, TResult>(T1 arg1, T2 arg2, T3 arg3, Func<T1, T2, T3, TResult> invoker)
                => invoker != null ? invoker(arg1, arg2, arg3) : default(TResult);

            public static object GetDelegateInvoker<T>() => null;
        }
    }
}

// ============================================================================
// 8. Silksong.PrepatcherPlugin (PrepatcherPlugin.dll) Shims for Needleforge
// ============================================================================
namespace PrepatcherPlugin
{
    public class PrepatcherPlugin : BepInEx.BaseUnityPlugin
    {
        public const string Id = "io.github.silksong-modding.prepatcher";
    }

    public static class PlayerDataVariableEvents<T>
    {
        public delegate T PlayerDataVariableHandler(PlayerData pd, string fieldName, T current);

        public static event PlayerDataVariableHandler OnGetVariable
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents`1::add_OnGetVariable"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents`1::remove_OnGetVariable"); }
        }

        public static event PlayerDataVariableHandler OnSetVariable
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents`1::add_OnSetVariable"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents`1::remove_OnSetVariable"); }
        }
    }

    public static class PlayerDataVariableEvents
    {
        public static event Func<PlayerData, string, bool, bool> OnGetBool
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetBool"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetBool"); }
        }

        public static event Func<PlayerData, string, bool, bool> OnSetBool
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetBool"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetBool"); }
        }

        public static event Func<PlayerData, string, int, int> OnGetInt
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetInt"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetInt"); }
        }

        public static event Func<PlayerData, string, int, int> OnSetInt
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetInt"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetInt"); }
        }

        public static event Func<PlayerData, string, float, float> OnGetFloat
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetFloat"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetFloat"); }
        }

        public static event Func<PlayerData, string, float, float> OnSetFloat
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetFloat"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetFloat"); }
        }

        public static event Func<PlayerData, string, string, string> OnGetString
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetString"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetString"); }
        }

        public static event Func<PlayerData, string, string, string> OnSetString
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetString"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetString"); }
        }

        public static event Func<PlayerData, string, object, object> OnGetVariable
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetVariable"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetVariable"); }
        }

        public static event Func<PlayerData, string, object, object> OnSetVariable
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetVariable"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetVariable"); }
        }

        public static event Func<PlayerData, string, Vector3, Vector3> OnGetVector3
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnGetVector3"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnGetVector3"); }
        }

        public static event Func<PlayerData, string, Vector3, Vector3> OnSetVector3
        {
            add { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::add_OnSetVector3"); }
            remove { SilksongMobileAPI.HarmonyBridge.RegisterPrepatcherEvent(value, "PlayerDataVariableEvents::remove_OnSetVector3"); }
        }
    }

    public static class PlayerDataInternal
    {
        public static void IncrementInt(PlayerData pd, string name)
        {
            if (pd != null && !string.IsNullOrEmpty(name))
                pd.SetInt(name, pd.GetInt(name) + 1);
        }
        public static void DecrementInt(PlayerData pd, string name)
        {
            if (pd != null && !string.IsNullOrEmpty(name))
                pd.SetInt(name, pd.GetInt(name) - 1);
        }
        public static void IntAdd(PlayerData pd, string name, int amount)
        {
            if (pd != null && !string.IsNullOrEmpty(name))
                pd.SetInt(name, pd.GetInt(name) + amount);
        }
    }

    public static class PlayerDataAccess
    {
        public static bool blackThreadWorld
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(blackThreadWorld));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(blackThreadWorld));
        }
        public static bool atBench
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(atBench));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(atBench));
        }
        public static bool FleaGamesCanStart
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(FleaGamesCanStart));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(FleaGamesCanStart));
        }
        public static bool FleaGamesStarted
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(FleaGamesStarted));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(FleaGamesStarted));
        }
        public static string respawnMarkerName
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<string>(nameof(respawnMarkerName));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(respawnMarkerName));
        }
        public static GlobalEnums.PermadeathModes permadeathMode
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<GlobalEnums.PermadeathModes>(nameof(permadeathMode));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(permadeathMode));
        }
        public static bool silkFarmBattle1_complete
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(silkFarmBattle1_complete));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(silkFarmBattle1_complete));
        }
        public static bool silkFarmAbyssCoresCleared
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(silkFarmAbyssCoresCleared));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(silkFarmAbyssCoresCleared));
        }
        public static string respawnScene
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<string>(nameof(respawnScene));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(respawnScene));
        }
        public static int respawnType
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<int>(nameof(respawnType));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(respawnType));
        }
        public static GlobalEnums.MapZone mapZone
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<GlobalEnums.MapZone>(nameof(mapZone));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(mapZone));
        }
        public static bool defeatedLaceTower
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(defeatedLaceTower));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(defeatedLaceTower));
        }
        public static bool defeatedPhantom
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(defeatedPhantom));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(defeatedPhantom));
        }
        public static bool hasNeedolinMemoryPowerup
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(hasNeedolinMemoryPowerup));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(hasNeedolinMemoryPowerup));
        }
        public static bool defeatedLace1
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(defeatedLace1));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(defeatedLace1));
        }
        public static bool defeatedCoralKing
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(defeatedCoralKing));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(defeatedCoralKing));
        }
        public static bool encounteredLace1
        {
            get => SilksongMobileAPI.HarmonyBridge.GetPlayerDataField<bool>(nameof(encounteredLace1));
            set => SilksongMobileAPI.HarmonyBridge.SetPlayerDataField(value, nameof(encounteredLace1));
        }
    }
}

namespace UnityEngine
{
    public class AssetBundleUnloadOperation : AsyncOperation
    {
    }

    public sealed class SpriteMask : Renderer
    {
        public Sprite sprite { get; set; }
        public float alphaCutoff { get; set; } = 0.5f;
        public bool isCustomRangeActive { get; set; }
        public int frontSortingLayerID { get; set; }
        public int frontSortingOrder { get; set; }
        public int backSortingLayerID { get; set; }
        public int backSortingOrder { get; set; }
    }
}

namespace Silksong.UnityHelper.Extensions
{
    public static class UnityExtensions
    {
        public static T GetOrAddComponent<T>(this GameObject obj) where T : Component
        {
            if (obj == null) return null;
            var comp = obj.GetComponent<T>();
            return comp != null ? comp : obj.AddComponent<T>();
        }

        public static bool RemoveComponent<T>(this GameObject obj) where T : Component
        {
            if (obj == null) return false;
            var comp = obj.GetComponent<T>();
            if (comp == null) return false;
            UnityEngine.Object.Destroy(comp);
            return true;
        }

        public static GameObject FindChild(this GameObject parent, string path)
        {
            if (parent == null || string.IsNullOrEmpty(path)) return null;
            var tr = parent.transform.Find(path);
            if (tr != null) return tr.gameObject;
            string[] parts = path.Split('/');
            Transform cur = parent.transform;
            for (int i = 0; i < parts.Length && cur != null; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                Transform next = cur.Find(p);
                if (next == null)
                {
                    for (int c = 0; c < cur.childCount; c++)
                    {
                        var ch = cur.GetChild(c);
                        if (ch != null && string.Equals(ch.name, p, StringComparison.OrdinalIgnoreCase))
                        {
                            next = ch;
                            break;
                        }
                    }
                }
                cur = next;
            }
            return cur != null ? cur.gameObject : null;
        }

        public static GameObject FindGameObject(this UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(path)) return null;
            var roots = scene.GetRootGameObjects();
            if (roots == null) return null;
            string[] parts = path.Split('/');
            string rootName = parts[0];
            for (int i = 0; i < roots.Length; i++)
            {
                var r = roots[i];
                if (r == null) continue;
                if (string.Equals(r.name, rootName, StringComparison.Ordinal) ||
                    string.Equals(r.name, rootName, StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 1) return r;
                    string subPath = string.Join("/", parts, 1, parts.Length - 1);
                    var found = FindChild(r, subPath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public static void InvokeAfterSeconds(this MonoBehaviour mb, Action action, float seconds)
        {
            if (mb == null || action == null) return;
            mb.StartCoroutine(CoInvokeAfterSeconds(action, seconds));
        }

        public static void InvokeNextFrame(this MonoBehaviour mb, Action action)
        {
            if (mb == null || action == null) return;
            mb.StartCoroutine(CoInvokeNextFrame(action));
        }

        private static IEnumerator CoInvokeAfterSeconds(Action action, float seconds)
        {
            if (seconds > 0f) yield return new WaitForSeconds(seconds);
            else yield return null;
            try { action?.Invoke(); } catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3039", __apiEx_19); }
        }

        private static IEnumerator CoInvokeNextFrame(Action action)
        {
            yield return null;
            try { action?.Invoke(); } catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3045", __apiEx_20); }
        }
    }
}

namespace Silksong.UnityHelper.Util
{
    public static class SpriteUtil
    {
        public static Sprite LoadSpriteFromFile(string path, float ppu = 64f, Vector2? pivot = null, bool pointFilter = false)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                return CreateSpriteFromBytes(bytes, Path.GetFileNameWithoutExtension(path), ppu, pivot, pointFilter);
            }
            catch { return null; }
        }

        public static Sprite LoadEmbeddedSprite(Assembly asm, string resourceName, float ppu = 64f, Vector2? pivot = null, bool pointFilter = false)
        {
            try
            {
                if (asm == null || string.IsNullOrEmpty(resourceName)) return null;
                string match = asm.GetManifestResourceNames().FirstOrDefault(n =>
                    string.Equals(n, resourceName, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(match)) return null;
                using (var s = asm.GetManifestResourceStream(match))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return CreateSpriteFromBytes(ms.ToArray(), resourceName, ppu, pivot, pointFilter);
                    }
                }
            }
            catch { return null; }
        }

        public static Sprite LoadEmbeddedSprite(Assembly asm, string resourceName, float ppu)
            => LoadEmbeddedSprite(asm, resourceName, ppu, null, false);

        public static Sprite LoadSpriteFromFile(string fileName, float pixelsPerUnit)
            => LoadSpriteFromFile(fileName, pixelsPerUnit, null, false);

        public static Sprite LoadSpriteFromArray(byte[] buffer, float pixelsPerUnit = 64f, Vector2? pivot = null, bool makeUnreadable = false)
        {
            var tex = LoadTextureFromArray(buffer, makeUnreadable);
            if (tex == null) return null;
            Vector2 piv = pivot ?? (Vector2.one * 0.5f);
            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), piv, pixelsPerUnit > 0f ? pixelsPerUnit : 64f);
        }

        public static Sprite LoadSpriteFromArray(byte[] buffer, float pixelsPerUnit)
            => LoadSpriteFromArray(buffer, pixelsPerUnit, null, false);

        public static Texture2D LoadTextureFromArray(byte[] buffer, bool makeUnreadable = false)
        {
            if (buffer == null || buffer.Length == 0) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(tex, buffer, makeUnreadable);
            return tex;
        }

        public static Texture2D LoadTextureFromFile(string fileName, bool makeUnreadable = false)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName)) return null;
            var tex = LoadTextureFromArray(File.ReadAllBytes(fileName), makeUnreadable);
            if (tex != null) tex.name = Path.GetFileNameWithoutExtension(fileName);
            return tex;
        }

        public static Texture2D LoadEmbeddedTexture(Assembly asm, string path, bool makeUnreadable = false)
        {
            if (asm == null || string.IsNullOrEmpty(path)) return null;
            string match = asm.GetManifestResourceNames().FirstOrDefault(n =>
                string.Equals(n, path, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("." + path, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(path, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match)) return null;
            using (var s = asm.GetManifestResourceStream(match))
            {
                if (s == null) return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    var tex = LoadTextureFromArray(ms.ToArray(), makeUnreadable);
                    if (tex != null) tex.name = path;
                    return tex;
                }
            }
        }

        public static Texture2D PremultiplyAlpha(this Texture2D source)
        {
            if (source == null) return null;
            var pixels = source.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                pixels[i] = new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
            }
            source.SetPixels32(pixels);
            source.Apply();
            return source;
        }

        public static Sprite PremultiplyAlpha(this Sprite source)
        {
            if (source != null && source.texture != null) source.texture.PremultiplyAlpha();
            return source;
        }

        public static Texture2D GetReadableTexture(Texture2D tex)
        {
            if (tex == null || tex.isReadable) return tex;
            RenderTexture tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, tex.mipmapCount > 1);
            readable.ReadPixels(new Rect(0f, 0f, tmp.width, tmp.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return readable;
        }

        private static Sprite CreateSpriteFromBytes(byte[] bytes, string name, float ppu, Vector2? pivot, bool pointFilter)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes)) return null;
            tex.name = name ?? "ModSprite";
            if (pointFilter) tex.filterMode = FilterMode.Point;
            Vector2 piv = pivot ?? new Vector2(0.5f, 0.5f);
            var spr = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), piv, ppu > 0f ? ppu : 64f);
            spr.name = tex.name;
            return spr;
        }
    }

    public static class Tk2dUtil
    {
        public static tk2dSpriteAnimationClip CreateTk2dAnimationClip(
            string name,
            int fps,
            IEnumerable<Texture2D> frames,
            tk2dSpriteAnimationClip.WrapMode wrapMode = tk2dSpriteAnimationClip.WrapMode.Loop,
            int loopStart = 0,
            float pixelsPerUnit = 64f)
        {
            var list = frames != null ? frames.Where(x => x != null).ToArray() : Array.Empty<Texture2D>();
            if (list.Length == 0)
            {
                return new tk2dSpriteAnimationClip
                {
                    name = name ?? "CustomClip",
                    fps = fps > 0 ? fps : 12f,
                    wrapMode = wrapMode,
                    loopStart = loopStart,
                    frames = Array.Empty<tk2dSpriteAnimationFrame>()
                };
            }
            var names = list.Select((x, idx) => string.IsNullOrEmpty(x.name) ? ("frame_" + idx) : x.name).ToArray();
            var collection = CreateTk2dSpriteCollection(list, names, null, pixelsPerUnit);
            return new tk2dSpriteAnimationClip
            {
                name = name ?? "CustomClip",
                fps = fps > 0 ? fps : 12f,
                wrapMode = wrapMode,
                loopStart = loopStart,
                frames = collection != null ? collection.CreateFrames(names) : Array.Empty<tk2dSpriteAnimationFrame>()
            };
        }

        public static tk2dSpriteAnimationClip CreateTk2dAnimationClip(
            string name,
            int fps,
            IEnumerable<Sprite> frames,
            tk2dSpriteAnimationClip.WrapMode wrapMode = tk2dSpriteAnimationClip.WrapMode.Loop,
            int loopStart = 0,
            float pixelsPerUnit = 64f)
        {
            var list = frames != null ? frames.Where(x => x != null).ToArray() : Array.Empty<Sprite>();
            if (list.Length == 0)
            {
                return new tk2dSpriteAnimationClip
                {
                    name = name ?? "CustomClip",
                    fps = fps > 0 ? fps : 12f,
                    wrapMode = wrapMode,
                    loopStart = loopStart,
                    frames = Array.Empty<tk2dSpriteAnimationFrame>()
                };
            }
            var names = list.Select((x, idx) => string.IsNullOrEmpty(x.name) ? ("frame_" + idx) : x.name).ToArray();
            var collection = CreateTk2dSpriteCollection(list, names, null, pixelsPerUnit);
            return new tk2dSpriteAnimationClip
            {
                name = name ?? "CustomClip",
                fps = fps > 0 ? fps : 12f,
                wrapMode = wrapMode,
                loopStart = loopStart,
                frames = collection != null ? collection.CreateFrames(names) : Array.Empty<tk2dSpriteAnimationFrame>()
            };
        }

        public static tk2dSpriteCollectionData CreateTk2dSpriteCollection(
            IEnumerable<Texture2D> sprites,
            IEnumerable<string> spriteNames = null,
            IEnumerable<Vector2> spriteCenters = null,
            float pixelsPerUnit = 64f)
        {
            var texList = sprites != null ? sprites.Where(x => x != null).ToArray() : Array.Empty<Texture2D>();
            if (texList.Length == 0) return null;

            string[] names = spriteNames != null
                ? spriteNames.ToArray()
                : texList.Select((x, idx) => string.IsNullOrEmpty(x.name) ? ("sprite_" + idx) : x.name).ToArray();
            // Deduplicate names safely so PackTextures + CreateFromTexture never throws
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                string baseName = string.IsNullOrEmpty(names[i]) ? ("sprite_" + i) : names[i];
                string candidate = baseName;
                int dup = 1;
                while (!usedNames.Add(candidate)) candidate = baseName + "_" + (dup++);
                names[i] = candidate;
            }

            Vector2[] centers = spriteCenters != null
                ? spriteCenters.ToArray()
                : texList.Select(x => new Vector2(x.width * 0.5f, x.height * 0.5f)).ToArray();

            Texture2D[] readable = texList.Select(SpriteUtil.GetReadableTexture).ToArray();
            var atlas = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            Rect[] rects = atlas.PackTextures(readable, 2, 2048, false);
            atlas.Apply(false, true);
            for (int i = 0; i < rects.Length; i++)
            {
                Rect r = rects[i];
                r.x *= atlas.width;
                r.y *= atlas.height;
                r.width *= atlas.width;
                r.height *= atlas.height;
                r.y = atlas.height - r.yMax;
                rects[i] = r;
            }
            tk2dSpriteCollectionData coll = tk2dSpriteCollectionData.CreateFromTexture(
                atlas,
                tk2dSpriteCollectionSize.PixelsPerMeter(pixelsPerUnit > 0f ? pixelsPerUnit : 64f),
                names,
                rects,
                centers);
            if (coll != null) UnityEngine.Object.DontDestroyOnLoad(coll);
            return coll;
        }

        public static tk2dSpriteCollectionData CreateTk2dSpriteCollection(
            IEnumerable<Sprite> sprites,
            IEnumerable<string> spriteNames = null,
            IEnumerable<Vector2> spriteCenters = null,
            float pixelsPerUnit = 64f)
        {
            var spList = sprites != null ? sprites.Where(x => x != null && x.texture != null).ToArray() : Array.Empty<Sprite>();
            if (spList.Length == 0) return null;
            return CreateTk2dSpriteCollection(
                spList.Select(x => x.texture),
                spriteNames ?? spList.Select((x, idx) => string.IsNullOrEmpty(x.name) ? ("sprite_" + idx) : x.name),
                spriteCenters ?? spList.Select(x => x.pivot),
                pixelsPerUnit);
        }

        public static tk2dSpriteAnimationFrame CreateFrame(this tk2dSpriteCollectionData collection, string spriteName, bool triggerEvent = false)
        {
            return new tk2dSpriteAnimationFrame
            {
                spriteCollection = collection,
                spriteId = collection != null ? collection.GetSpriteIdByName(spriteName) : 0,
                triggerEvent = triggerEvent
            };
        }

        public static tk2dSpriteAnimationFrame[] CreateFrames(this tk2dSpriteCollectionData collection, params string[] spriteNames)
        {
            if (spriteNames == null || spriteNames.Length == 0) return Array.Empty<tk2dSpriteAnimationFrame>();
            return spriteNames.Select(x => collection.CreateFrame(x)).ToArray();
        }

        public static tk2dSpriteAnimationFrame[] CreateFrames(this tk2dSpriteCollectionData collection, IEnumerable<string> spriteNames)
        {
            if (spriteNames == null) return Array.Empty<tk2dSpriteAnimationFrame>();
            return spriteNames.Select(x => collection.CreateFrame(x)).ToArray();
        }
    }

    public static class WavUtil
    {
        public static AudioClip AudioClipFromEmbeddedResource(string clipName, Assembly asm, string resourceName)
        {
            try
            {
                if (asm == null || string.IsNullOrEmpty(resourceName))
                    return AudioClip.Create(clipName ?? "ModAudio", 4410, 1, 44100, false);
                string match = asm.GetManifestResourceNames().FirstOrDefault(n =>
                    string.Equals(n, resourceName, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match))
                {
                    using (var s = asm.GetManifestResourceStream(match))
                    {
                        if (s != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                var clip = SilksongMobileAPI.AssetHelperBridge.TryParseWavBytesToAudioClip(ms.ToArray(), clipName ?? resourceName);
                                if (clip != null) return clip;
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3155", __apiEx_21); }
            return AudioClip.Create(clipName ?? "ModAudio", 4410, 1, 44100, false);
        }
    }
}

// ============================================================================
// 9. Unity 6000.0.50+ UnityEngine.Networking.DownloadedTextureParams Shim (for Original Stitchwork 0.9.1)
// ============================================================================
namespace UnityEngine.Networking
{
    public struct DownloadedTextureParams
    {
        public bool readable;
        public bool mipmapChain;
        public bool linearColorSpace;
        public bool compress;

        public static DownloadedTextureParams Default => new DownloadedTextureParams
        {
            readable = false,
            mipmapChain = true,
            linearColorSpace = false,
            compress = false
        };
    }
}

// ============================================================================
// 10. MonoMod.Utils.Extensions & Steamworks.NET Mobile Compatibility Shims (for SSMP 0.3.1)
// ============================================================================
namespace MonoMod.Utils
{
    public static class Extensions
    {
        public static MethodInfo GetStateMachineTarget(this MethodInfo method)
        {
            if (method == null) return null;
            try
            {
                var attrs = method.GetCustomAttributes(true);
                if (attrs != null)
                {
                    for (int i = 0; i < attrs.Length; i++)
                    {
                        var a = attrs[i];
                        if (a == null) continue;
                        var stProp = a.GetType().GetProperty("StateMachineType", BindingFlags.Instance | BindingFlags.Public);
                        if (stProp != null && stProp.GetValue(a, null) is Type smType)
                        {
                            var moveNext = smType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (moveNext != null) return moveNext;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_22) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3211", __apiEx_22); }
            return method;
        }

        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> other)
        {
            if (list == null || other == null) return;
            if (list is List<T> concreteList)
            {
                concreteList.AddRange(other);
                return;
            }
            foreach (var item in other)
            {
                list.Add(item);
            }
        }

        public static void AddRange(this System.Collections.IDictionary dict, System.Collections.IDictionary other)
        {
            if (dict == null || other == null) return;
            foreach (System.Collections.DictionaryEntry entry in other)
            {
                dict[entry.Key] = entry.Value;
            }
        }

        public static void AddRange<TKey, TValue>(this IDictionary<TKey, TValue> dict, IDictionary<TKey, TValue> other)
        {
            if (dict == null || other == null) return;
            foreach (var kv in other)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        public static void AddRange<TKey, TValue>(this Dictionary<TKey, TValue> dict, Dictionary<TKey, TValue> other)
        {
            if (dict == null || other == null) return;
            foreach (var kv in other)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        public static void AddRange<TKey, TValue>(this IDictionary<TKey, TValue> dict, IEnumerable<KeyValuePair<TKey, TValue>> other)
        {
            if (dict == null || other == null) return;
            foreach (var kv in other)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        public static void InsertRange<T>(this IList<T> list, int index, IEnumerable<T> other)
        {
            if (list == null || other == null) return;
            if (list is List<T> concreteList)
            {
                concreteList.InsertRange(index, other);
                return;
            }
            foreach (var item in other)
            {
                list.Insert(index++, item);
            }
        }
    }
}

namespace System
{
    public readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false)
        {
            _value = fromEnd ? ~value : value;
        }
        public static Index Start => new Index(0);
        public static Index End => new Index(~0);
        public static Index FromStart(int value) => new Index(value, false);
        public static Index FromEnd(int value) => new Index(value, true);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length)
        {
            int offset = _value;
            if (IsFromEnd) offset += length + 1;
            return offset;
        }
        public override bool Equals(object value) => value is Index idx && _value == idx._value;
        public bool Equals(Index other) => _value == other._value;
        public override int GetHashCode() => _value;
        public static implicit operator Index(int value) => FromStart(value);
        public override string ToString() => IsFromEnd ? "^" + Value.ToString() : ((uint)Value).ToString();
    }

    public readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
        public override bool Equals(object value) => value is Range r && r.Start.Equals(Start) && r.End.Equals(End);
        public bool Equals(Range other) => other.Start.Equals(Start) && other.End.Equals(End);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => Start.ToString() + ".." + End.ToString();
        public ValueTuple<int, int> GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                return new ValueTuple<int, int>(0, 0);
            return new ValueTuple<int, int>(start, end - start);
        }
    }
}

namespace CustomFont
{
    public static class FontAssetBuilder
    {
        private static TMProOld.TMP_FontAsset _cachedFallbackFontAsset;

        private static TMProOld.TMP_FontAsset ResolveFallbackTmpFontAsset(UnityEngine.Font customFont)
        {
            try
            {
                if (_cachedFallbackFontAsset == null)
                {
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMProOld.TMP_FontAsset>();
                    if (all != null && all.Length > 0)
                        _cachedFallbackFontAsset = all[0];
                }
                if (_cachedFallbackFontAsset != null)
                {
                    var clone = UnityEngine.Object.Instantiate(_cachedFallbackFontAsset);
                    if (customFont != null)
                        clone.name = _cachedFallbackFontAsset.name + "_" + customFont.name;
                    return clone;
                }
            }
            catch (System.Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("FontAssetBuilder", "ResolveFallbackTmpFontAsset", ex);
            }
            return _cachedFallbackFontAsset;
        }

        public static TMProOld.TMP_FontAsset Build(UnityEngine.Font baseFont, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    var loadedFont = new UnityEngine.Font(path);
                    return ResolveFallbackTmpFontAsset(loadedFont ?? baseFont);
                }
            }
            catch (System.Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("FontAssetBuilder", "Build.Path", ex);
            }
            return ResolveFallbackTmpFontAsset(baseFont);
        }

        public static TMProOld.TMP_FontAsset Build(UnityEngine.Font baseFont, byte[] bytes)
        {
            return ResolveFallbackTmpFontAsset(baseFont);
        }
    }
}

namespace Steamworks
{
    [Serializable]
    public struct CSteamID : IEquatable<CSteamID>, IComparable<CSteamID>
    {
        public static readonly CSteamID Nil = new CSteamID(0UL);
        public ulong m_SteamID;

        public CSteamID(ulong ulSteamID)
        {
            m_SteamID = ulSteamID;
        }

        public bool IsValid() => m_SteamID != 0UL;
        public override string ToString() => m_SteamID.ToString();
        public override bool Equals(object other) => other is CSteamID id && m_SteamID == id.m_SteamID;
        public override int GetHashCode() => m_SteamID.GetHashCode();
        public bool Equals(CSteamID other) => m_SteamID == other.m_SteamID;
        public int CompareTo(CSteamID other) => m_SteamID.CompareTo(other.m_SteamID);
        public static bool operator ==(CSteamID x, CSteamID y) => x.m_SteamID == y.m_SteamID;
        public static bool operator !=(CSteamID x, CSteamID y) => x.m_SteamID != y.m_SteamID;
        public static explicit operator CSteamID(ulong value) => new CSteamID(value);
        public static explicit operator ulong(CSteamID that) => that.m_SteamID;
    }

    public struct SteamAPICall_t : IEquatable<SteamAPICall_t>, IComparable<SteamAPICall_t>
    {
        public static readonly SteamAPICall_t Invalid = new SteamAPICall_t(0UL);
        public ulong m_SteamAPICall;
        public SteamAPICall_t(ulong value) { m_SteamAPICall = value; }
        public bool Equals(SteamAPICall_t other) => m_SteamAPICall == other.m_SteamAPICall;
        public int CompareTo(SteamAPICall_t other) => m_SteamAPICall.CompareTo(other.m_SteamAPICall);
    }

    public enum EResult
    {
        k_EResultNone = 0,
        k_EResultOK = 1,
        k_EResultFail = 2
    }

    public enum ELobbyType
    {
        k_ELobbyTypePrivate = 0,
        k_ELobbyTypeFriendsOnly = 1,
        k_ELobbyTypePublic = 2,
        k_ELobbyTypeInvisible = 3
    }

    public enum EP2PSend
    {
        k_EP2PSendUnreliable = 0,
        k_EP2PSendUnreliableNoDelay = 1,
        k_EP2PSendReliable = 2,
        k_EP2PSendReliableWithBuffering = 3
    }

    public enum EP2PSessionError
    {
        k_EP2PSessionErrorNone = 0,
        k_EP2PSessionErrorNotRunningApp = 1,
        k_EP2PSessionErrorNoRightsToApp = 2,
        k_EP2PSessionErrorDestinationNotLoggedIn = 3,
        k_EP2PSessionErrorTimeout = 4,
        k_EP2PSessionErrorMax = 5
    }

    public enum ESteamInputGlyphSize
    {
        k_ESteamInputGlyphSize_Small = 0,
        k_ESteamInputGlyphSize_Medium = 1,
        k_ESteamInputGlyphSize_Large = 2,
        k_ESteamInputGlyphSize_Count = 3
    }

    public enum ESteamInputType
    {
        k_ESteamInputType_Unknown = 0,
        k_ESteamInputType_SteamController = 1,
        k_ESteamInputType_XBox360Controller = 2,
        k_ESteamInputType_XBoxOneController = 3,
        k_ESteamInputType_GenericGamepad = 4,
        k_ESteamInputType_PS4Controller = 5,
        k_ESteamInputType_AppleMFiController = 6,
        k_ESteamInputType_AndroidController = 7,
        k_ESteamInputType_SwitchJoyConPair = 8,
        k_ESteamInputType_SwitchJoyConSingle = 9,
        k_ESteamInputType_SwitchProController = 10,
        k_ESteamInputType_MobileTouch = 11,
        k_ESteamInputType_PS3Controller = 12,
        k_ESteamInputType_PS5Controller = 13,
        k_ESteamInputType_SteamDeckController = 14
    }

    public enum EInputActionOrigin
    {
        k_EInputActionOrigin_None = 0
    }

    public struct InputHandle_t
    {
        public ulong m_InputHandle;
        public InputHandle_t(ulong v) { m_InputHandle = v; }
    }

    public struct InputActionSetHandle_t
    {
        public ulong m_InputActionSetHandle;
        public InputActionSetHandle_t(ulong v) { m_InputActionSetHandle = v; }
    }

    public struct InputDigitalActionHandle_t
    {
        public ulong m_InputDigitalActionHandle;
        public InputDigitalActionHandle_t(ulong v) { m_InputDigitalActionHandle = v; }
    }

    public struct InputDigitalActionData_t
    {
        public byte bState;
        public byte bActive;
    }

    public struct SteamInputConfigurationLoaded_t
    {
        public uint m_unAppID;
        public InputHandle_t m_ulDeviceHandle;
        public ulong m_ulMappingCreator;
        public uint m_unMajorRevision;
        public uint m_unMinorRevision;
        public bool m_bUsesSteamInputAPI;
        public bool m_bUsesGamepadAPI;
    }

    public static class SteamInput
    {
        public static bool Init(bool bExplicitlyCallRunFrame = false) => false;
        public static void RunFrame(bool bReservedValue = true) { }
        public static int GetConnectedControllers(InputHandle_t[] handlesOut) => 0;
        public static InputActionSetHandle_t GetActionSetHandle(string pszActionSetName) => default;
        public static void ActivateActionSet(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle) { }
        public static InputDigitalActionHandle_t GetDigitalActionHandle(string pszActionName) => default;
        public static InputDigitalActionData_t GetDigitalActionData(InputHandle_t inputHandle, InputDigitalActionHandle_t digitalActionHandle) => default;
        public static int GetDigitalActionOrigins(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle, InputDigitalActionHandle_t digitalActionHandle, EInputActionOrigin[] originsOut) => 0;
        public static string GetGlyphPNGForActionOrigin(EInputActionOrigin eOrigin, ESteamInputGlyphSize eSize, uint unFlags) => "";
        public static string GetStringForActionOrigin(EInputActionOrigin eOrigin) => "";
        public static ESteamInputType GetInputTypeForHandle(InputHandle_t inputHandle) => ESteamInputType.k_ESteamInputType_Unknown;
    }

    public struct LobbyCreated_t
    {
        public EResult m_eResult;
        public ulong m_ulSteamIDLobby;
    }

    public struct LobbyEnter_t
    {
        public ulong m_ulSteamIDLobby;
        public uint m_rgfChatPermissions;
        public bool m_bLocked;
        public uint m_EChatRoomEnterResponse;
    }

    public struct LobbyMatchList_t
    {
        public uint m_nLobbiesMatching;
    }

    public struct GameLobbyJoinRequested_t
    {
        public CSteamID m_steamIDLobby;
        public CSteamID m_steamIDFriend;
    }

    public struct GameRichPresenceJoinRequested_t
    {
        public CSteamID m_steamIDFriend;
        public string m_rgchConnect;
    }

    public struct P2PSessionRequest_t
    {
        public CSteamID m_steamIDRemote;
    }

    public struct P2PSessionConnectFail_t
    {
        public CSteamID m_steamIDRemote;
        public byte m_eP2PSessionError;
    }

    public sealed class Callback<T> : IDisposable
    {
        public delegate void DispatchDelegate(T param);
        public static Callback<T> Create(DispatchDelegate func) => new Callback<T>();
        public void Dispose() { }
        public void Unregister() { }
    }

    public sealed class CallResult<T> : IDisposable
    {
        public delegate void APIDispatchDelegate(T param, bool bIOFailure);
        public static CallResult<T> Create(APIDispatchDelegate func = null) => new CallResult<T>();
        public void Set(SteamAPICall_t hAPICall, APIDispatchDelegate func = null) { }
        public bool IsActive() => false;
        public void Cancel() { }
        public void Dispose() { }
    }

    public static class Packsize
    {
        public static bool Test() => false;
    }

    public static class SteamAPI
    {
        public static bool Init() => false;
        public static void Shutdown() { }
        public static void RunCallbacks() { }
        public static bool IsSteamRunning() => false;
    }

    public static class SteamUser
    {
        public static CSteamID GetSteamID() => CSteamID.Nil;
    }

    public static class SteamFriends
    {
        public static string GetPersonaName() => "MobilePlayer";
        public static string GetFriendPersonaName(CSteamID steamIDFriend) => "Player";
        public static bool SetRichPresence(string pchKey, string pchValue) => false;
        public static void ClearRichPresence() { }
        public static void ActivateGameOverlayInviteDialog(CSteamID steamIDLobby) { }
    }

    public static class SteamMatchmaking
    {
        public static SteamAPICall_t CreateLobby(ELobbyType eLobbyType, int cMaxMembers) => SteamAPICall_t.Invalid;
        public static SteamAPICall_t JoinLobby(CSteamID steamIDLobby) => SteamAPICall_t.Invalid;
        public static void LeaveLobby(CSteamID steamIDLobby) { }
        public static CSteamID GetLobbyOwner(CSteamID steamIDLobby) => CSteamID.Nil;
        public static int GetNumLobbyMembers(CSteamID steamIDLobby) => 0;
        public static CSteamID GetLobbyMemberByIndex(CSteamID steamIDLobby, int iMember) => CSteamID.Nil;
        public static bool SetLobbyData(CSteamID steamIDLobby, string pchKey, string pchValue) => false;
        public static string GetLobbyData(CSteamID steamIDLobby, string pchKey) => "";
    }

    public static class SteamNetworking
    {
        public static bool SendP2PPacket(CSteamID steamIDRemote, byte[] pubData, uint cubData, EP2PSend eP2PSendType, int nChannel = 0) => false;
        public static bool IsP2PPacketAvailable(out uint pcubMsgSize, int nChannel = 0) { pcubMsgSize = 0; return false; }
        public static bool ReadP2PPacket(byte[] pubDest, uint cubDest, out uint pcubMsgSize, out CSteamID psteamIDRemote, int nChannel = 0)
        {
            pcubMsgSize = 0;
            psteamIDRemote = CSteamID.Nil;
            return false;
        }
        public static bool AcceptP2PSessionWithUser(CSteamID steamIDRemote) => false;
        public static bool CloseP2PSessionWithUser(CSteamID steamIDRemote) => false;
    }
}

namespace AssetHelperLib.BundleTools
{
    public class GameObjectInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string SceneName { get; set; } = string.Empty;
    }

    public class SceneBundleInfo
    {
        public string SceneName { get; set; } = string.Empty;
        public string BundleName { get; set; } = string.Empty;
        public System.Collections.Generic.List<GameObjectInfo> GameObjects { get; set; } = new System.Collections.Generic.List<GameObjectInfo>();
    }

    public class GameObjectLookup
    {
        public System.Collections.Generic.Dictionary<string, SceneBundleInfo> Scenes { get; set; } =
            new System.Collections.Generic.Dictionary<string, SceneBundleInfo>(System.StringComparer.OrdinalIgnoreCase);
    }

    public static class BundleUtils
    {
        private static GameObjectLookup _cachedLookup;

        public static GameObjectLookup LoadGameObjectLookup()
        {
            if (_cachedLookup != null) return _cachedLookup;
            _cachedLookup = new GameObjectLookup();
            try
            {
                foreach (var asm in global::SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    var bmType = asm.GetType("Silksong.AssetHelper.Internal.BundleMetadata", false);
                    if (bmType != null)
                    {
                        var f = bmType.GetField("_sceneBundleNameToSceneName", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (f?.GetValue(null) is System.Collections.IDictionary dict)
                        {
                            foreach (System.Collections.DictionaryEntry de in dict)
                            {
                                string bundleName = de.Key as string ?? "";
                                string sceneName = de.Value as string ?? "";
                                if (!string.IsNullOrEmpty(sceneName) && !_cachedLookup.Scenes.ContainsKey(sceneName))
                                {
                                    _cachedLookup.Scenes[sceneName] = new SceneBundleInfo
                                    {
                                        SceneName = sceneName,
                                        BundleName = bundleName
                                    };
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch (System.Exception __apiEx_23) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3521", __apiEx_23); }
            return _cachedLookup;
        }
    }
}

namespace System.Security
{
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class UnverifiableCodeAttribute : Attribute
    {
        public UnverifiableCodeAttribute() { }
    }
}

namespace System.Security.Permissions
{
    public enum SecurityAction
    {
        Demand = 2,
        Assert = 3,
        Deny = 4,
        PermitOnly = 5,
        LinkDemand = 6,
        InheritanceDemand = 7,
        RequestMinimum = 8,
        RequestOptional = 9,
        RequestRefuse = 10
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class SecurityPermissionAttribute : Attribute
    {
        public SecurityAction Action { get; set; }
        public bool SkipVerification { get; set; }
        public SecurityPermissionAttribute(SecurityAction action) { Action = action; }
    }
}

namespace BepInEx.Logging
{
    public class UnityLogListener : IDisposable
    {
        public void Dispose() { }
    }
}

namespace SilksongMobileAPI.IO
{
    public enum MatchType
    {
        Simple = 0,
        Win32 = 1
    }

    public enum MatchCasing
    {
        PlatformDefault = 0,
        CaseSensitive = 1,
        CaseInsensitive = 2
    }

    public class EnumerationOptions
    {
        public bool RecurseSubdirectories { get; set; }
        public bool IgnoreInaccessible { get; set; } = true;
        public int BufferSize { get; set; }
        public System.IO.FileAttributes AttributesToSkip { get; set; } = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System;
        public MatchType MatchType { get; set; }
        public MatchCasing MatchCasing { get; set; }
        public int MaxRecursionDepth { get; set; } = int.MaxValue;
        public bool ReturnSpecialDirectories { get; set; }

        public EnumerationOptions() { }
    }
}

namespace SilksongMobileAPI
{
    public static class EnumerationOptionsCompat
    {
        private static bool WildcardMatches(string fileName, string pattern, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
                return true;
            if (string.IsNullOrEmpty(fileName))
                return false;

            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return string.Equals(fileName, pattern, cmp);

            if (pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.IndexOf('*', 2) < 0 && pattern.IndexOf('?') < 0)
            {
                string ext = pattern.Substring(1);
                return fileName.EndsWith(ext, cmp);
            }

            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            var opts = ignoreCase
                ? System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                : System.Text.RegularExpressions.RegexOptions.CultureInvariant;
            return System.Text.RegularExpressions.Regex.IsMatch(fileName, regexPattern, opts);
        }

        public static string[] Directory_GetFiles(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path))
                return Array.Empty<string>();
            try
            {
                bool recurse = options != null && options.RecurseSubdirectories;
                bool ignoreCase = options == null || options.MatchCasing != SilksongMobileAPI.IO.MatchCasing.CaseSensitive;
                var searchOpt = recurse ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                if (!ignoreCase)
                    return System.IO.Directory.GetFiles(path, string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern, searchOpt);

                var all = System.IO.Directory.GetFiles(path, "*", searchOpt);
                if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*" || searchPattern == "*.*")
                    return all;

                var list = new System.Collections.Generic.List<string>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    string fn = System.IO.Path.GetFileName(all[i]);
                    if (WildcardMatches(fn, searchPattern, true))
                        list.Add(all[i]);
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static System.Collections.Generic.IEnumerable<string> Directory_EnumerateFiles(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            return Directory_GetFiles(path, searchPattern, options);
        }

        public static string[] Directory_GetDirectories(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path))
                return Array.Empty<string>();
            try
            {
                bool recurse = options != null && options.RecurseSubdirectories;
                bool ignoreCase = options == null || options.MatchCasing != SilksongMobileAPI.IO.MatchCasing.CaseSensitive;
                var searchOpt = recurse ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                if (!ignoreCase)
                    return System.IO.Directory.GetDirectories(path, string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern, searchOpt);

                var all = System.IO.Directory.GetDirectories(path, "*", searchOpt);
                if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*")
                    return all;

                var list = new System.Collections.Generic.List<string>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    string dn = System.IO.Path.GetFileName(all[i]);
                    if (WildcardMatches(dn, searchPattern, true))
                        list.Add(all[i]);
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static System.Collections.Generic.IEnumerable<string> Directory_EnumerateDirectories(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            return Directory_GetDirectories(path, searchPattern, options);
        }

        public static string[] Directory_GetFileSystemEntries(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            var files = Directory_GetFiles(path, searchPattern, options);
            var dirs = Directory_GetDirectories(path, searchPattern, options);
            var combined = new string[files.Length + dirs.Length];
            Array.Copy(dirs, 0, combined, 0, dirs.Length);
            Array.Copy(files, 0, combined, dirs.Length, files.Length);
            return combined;
        }

        public static System.Collections.Generic.IEnumerable<string> Directory_EnumerateFileSystemEntries(string path, string searchPattern, SilksongMobileAPI.IO.EnumerationOptions options)
        {
            return Directory_GetFileSystemEntries(path, searchPattern, options);
        }
    }
}

namespace MonoDetour.DetourTypes
{
    public enum ReturnFlow
    {
        None = 0,
        SkipOriginal = 1,
        HardReturn = 2
    }
}

namespace MonoDetour.Cil
{
    public class ILManipulationInfo
    {
        public delegate void Manipulator(ILManipulationInfo info);
    }
}

namespace MonoDetour.DetourTypes
{
    public interface IMonoDetourHookApplier
    {
        MonoDetour.MonoDetourHook Hook { get; set; }
    }

    public class PrefixDetour : IMonoDetourHookApplier
    {
        public MonoDetour.MonoDetourHook Hook { get; set; }
    }

    public class PostfixDetour : IMonoDetourHookApplier
    {
        public MonoDetour.MonoDetourHook Hook { get; set; }
    }

    public class ILHookDetour : IMonoDetourHookApplier
    {
        public MonoDetour.MonoDetourHook Hook { get; set; }
    }
}

namespace MonoDetour.Logging
{
    public interface IMonoDetourLogSource
    {
        int LogFilter { get; set; }
    }

    public static class MonoDetourLogger
    {
        public static int GlobalFilter { get; set; }
        public static int OverrideFilter { get; set; }
        public static bool LogToConsole { get; set; }
        public static void Log(int channel, string message)
        {
            if (!string.IsNullOrEmpty(message))
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo($"[MonoDetour] {message}");
        }
    }
}

namespace MonoDetour
{
    public interface IMonoDetourTargets { }

    public interface IMonoDetourHook : IDisposable
    {
        void Apply();
        void Undo();
    }

    public interface IReadOnlyMonoDetourHook
    {
        MethodBase Target { get; }
        MethodInfo Manipulator { get; }
        Delegate ManipulatorDelegate { get; }
        MonoDetourManager Owner { get; }
        MonoDetourConfig Config { get; }
        bool IsValid { get; }
        bool IsApplied { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class MonoDetourHookInitializeAttribute : Attribute
    {
        public MonoDetourHookInitializeAttribute() { }
    }

    public class MonoDetourConfig
    {
        public int Priority { get; private set; }
        public IEnumerable<string> Before { get; private set; }
        public IEnumerable<string> After { get; private set; }
        public string OverrideId { get; private set; }

        public MonoDetourConfig(int priority = 0, IEnumerable<string> before = null, IEnumerable<string> after = null, string overrideId = null)
        {
            Priority = priority;
            Before = before ?? Array.Empty<string>();
            After = after ?? Array.Empty<string>();
            OverrideId = overrideId;
        }

        public MonoDetourConfig WithPriority(int priority) { Priority = priority; return this; }
        public MonoDetourConfig WithBefore(IEnumerable<string> before) { Before = before; return this; }
        public MonoDetourConfig WithBefore(params string[] before) { Before = before; return this; }
        public MonoDetourConfig WithAfter(IEnumerable<string> after) { After = after; return this; }
        public MonoDetourConfig WithAfter(params string[] after) { After = after; return this; }
        public MonoDetourConfig AddBefore(IEnumerable<string> before) => this;
        public MonoDetourConfig AddBefore(params string[] before) => this;
        public MonoDetourConfig AddAfter(IEnumerable<string> after) => this;
        public MonoDetourConfig AddAfter(params string[] after) => this;
    }

    public class MonoDetourHook : IMonoDetourHook, IReadOnlyMonoDetourHook
    {
        public MethodBase Target { get; }
        public MethodInfo Manipulator { get; }
        public Delegate ManipulatorDelegate { get; }
        public Type ApplierType { get; }
        public MonoDetourManager Owner { get; }
        public MonoDetourConfig Config { get; }
        public bool IsValid => Target != null && Manipulator != null;
        public bool IsApplied { get; private set; }

        public MonoDetourHook(
            MethodBase target,
            MethodInfo manipulator,
            Delegate manipulatorDelegate,
            Type applierType,
            MonoDetourManager owner = null,
            MonoDetourConfig config = null,
            bool applyByDefault = true)
        {
            Target = target;
            Manipulator = manipulator ?? manipulatorDelegate?.Method;
            ManipulatorDelegate = manipulatorDelegate;
            ApplierType = applierType;
            Owner = owner;
            Config = config;
            if (applyByDefault) Apply();
        }

        public void Apply()
        {
            if (IsApplied || Target == null || Manipulator == null) return;
            IsApplied = true;
            try
            {
                string modName = Owner?.Id ?? Manipulator.DeclaringType?.Assembly?.GetName()?.Name ?? "MonoDetourMod";
                string applierName = ApplierType?.Name ?? "";
                int prio = Config != null ? Config.Priority : HarmonyLib.Priority.Normal;
                if (applierName.IndexOf("Postfix", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SilksongMobileAPI.RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(modName, Target, null, Manipulator, null, prio);
                }
                else if (applierName.IndexOf("Prefix", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SilksongMobileAPI.RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(modName, Target, Manipulator, null, null, prio);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("MonoDetourHook", "Apply", ex);
            }
        }

        public void Undo()
        {
            if (!IsApplied) return;
            IsApplied = false;
            try
            {
                if (Target != null && Manipulator != null)
                    SilksongMobileAPI.RuntimeHybridCLRAdapterBuilder.RemovePatchFromChain(Target, Manipulator, null);
            }
            catch { }
        }

        public void Dispose() => Undo();
    }

    public class MonoDetourManager : IDisposable
    {
        public string Id { get; }
        public int LogFilter { get; set; }
        private readonly List<MonoDetourHook> _hooks = new List<MonoDetourHook>();
        public IReadOnlyList<MonoDetourHook> Hooks => _hooks;
        public event Action<MonoDetourHook> OnHookThrew;

        public MonoDetourManager(string id = "com.silksong.monodetour.bridge")
        {
            Id = string.IsNullOrEmpty(id) ? "com.silksong.monodetour.bridge" : id;
        }

        public static void InvokeHookInitializers(Assembly assembly, bool reportUnloadableTypes = true)
        {
            if (assembly == null) return;
            try
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                if (types == null) return;
                foreach (var t in types)
                {
                    if (t != null) InvokeHookInitializers(t, reportUnloadableTypes);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("MonoDetourManager", "InvokeHookInitializers(Assembly)", ex);
            }
        }

        public static void InvokeHookInitializers(Type type, bool reportUnloadableTypes = true)
        {
            if (type == null) return;
            try
            {
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var m in type.GetMethods(F))
                {
                    if (m.GetParameters().Length != 0) continue;
                    bool hasInitAttr = false;
                    try
                    {
                        foreach (var ca in CustomAttributeData.GetCustomAttributes(m))
                        {
                            string caName = ca.AttributeType?.Name ?? "";
                            if (caName == "MonoDetourHookInitializeAttribute" || caName == "MonoDetourHookInitialize")
                            {
                                hasInitAttr = true;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (hasInitAttr)
                    {
                        try { m.Invoke(null, null); }
                        catch (Exception ex) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogModException(type.Assembly.GetName().Name, $"MonoDetourHookInitialize({type.FullName}.{m.Name})", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("MonoDetourManager", "InvokeHookInitializers(Type)", ex);
            }
        }

        public MonoDetourHook Hook<TApplier>(MethodBase target, Delegate manipulator, MonoDetourConfig config = null, bool applyByDefault = true)
        {
            var hook = new MonoDetourHook(target, manipulator?.Method, manipulator, typeof(TApplier), this, config, applyByDefault);
            _hooks.Add(hook);
            return hook;
        }

        public MonoDetourHook Hook<TApplier, TDelegate>(MethodBase target, TDelegate manipulator, MonoDetourConfig config = null, bool applyByDefault = true) where TDelegate : Delegate
        {
            var hook = new MonoDetourHook(target, manipulator?.Method, manipulator, typeof(TApplier), this, config, applyByDefault);
            _hooks.Add(hook);
            return hook;
        }

        public MonoDetourHook Hook(MethodBase target, Delegate manipulator, MonoDetourConfig config = null, bool applyByDefault = true)
        {
            var hook = new MonoDetourHook(target, manipulator?.Method, manipulator, typeof(MonoDetour.DetourTypes.PrefixDetour), this, config, applyByDefault);
            _hooks.Add(hook);
            return hook;
        }

        public MonoDetourHook ILHook(MethodBase target, Delegate manipulator, MonoDetourConfig config = null, bool applyByDefault = true)
        {
            return new MonoDetourHook(target, manipulator?.Method, manipulator, typeof(MonoDetour.DetourTypes.ILHookDetour), this, config, false);
        }

        public void HookAll()
        {
            ApplyHooks();
        }

        public void ApplyHooks()
        {
            for (int i = 0; i < _hooks.Count; i++) _hooks[i]?.Apply();
        }

        public void UndoHooks()
        {
            for (int i = 0; i < _hooks.Count; i++) _hooks[i]?.Undo();
        }

        public void DisposeHooks()
        {
            UndoHooks();
            _hooks.Clear();
        }

        public void Dispose() => DisposeHooks();
    }
}

namespace MonoDetour.HookGen
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public sealed class MonoDetourTargetsAttribute : Attribute
    {
        public Type TargetType { get; }
        public bool IncludeNestedTypes { get; set; }
        public string[] Members { get; set; }
        public string[] MemberNamePrefixes { get; set; }
        public string[] MemberNameSuffixes { get; set; }
        public bool GenerateControlFlowVariants { get; set; }

        public MonoDetourTargetsAttribute(Type targetType = null)
        {
            TargetType = targetType;
        }
    }

    public static class DefaultMonoDetourManager
    {
        public static MonoDetour.MonoDetourManager Instance { get; } = new MonoDetour.MonoDetourManager("DefaultMonoDetourManager");
        public static MonoDetour.MonoDetourManager New() => Instance;
    }
}

namespace Silksong.FsmUtil.Actions
{
    public class DelegateAction<T> : HutongGames.PlayMaker.FsmStateAction
    {
        public Action<T> Method;
        public T Arg;

        public override void Reset()
        {
            Method = null;
            base.Reset();
        }

        public override void OnEnter()
        {
            if (Method != null && Arg != null)
            {
                Method(Arg);
            }
            if (!(Arg is Action a) || a != new Action(base.Finish))
            {
                base.Finish();
            }
        }
    }

    public class LambdaAction : HutongGames.PlayMaker.FsmStateAction
    {
        public Action Method;
        public bool EveryFrame;

        public override void Reset()
        {
            Method = null;
            EveryFrame = false;
            base.Reset();
        }

        public override void OnEnter()
        {
            Method?.Invoke();
            if (!EveryFrame)
            {
                base.Finish();
            }
        }

        public override void OnUpdate()
        {
            if (EveryFrame)
            {
                Method?.Invoke();
            }
        }
    }

    public class InvokeMethod : HutongGames.PlayMaker.FsmStateAction
    {
        private readonly Action _action;

        public InvokeMethod(Action action)
        {
            _action = action;
        }

        public override void OnEnter()
        {
            _action?.Invoke();
            base.Finish();
        }
    }

    public class InvokeCoroutine : HutongGames.PlayMaker.FsmStateAction
    {
        private readonly Func<System.Collections.IEnumerator> _coroutine;
        private readonly bool _wait;

        public InvokeCoroutine(Func<System.Collections.IEnumerator> coroutine, bool wait)
        {
            _coroutine = coroutine;
            _wait = wait;
        }

        private System.Collections.IEnumerator InvokeRoutine()
        {
            if (_coroutine != null)
            {
                yield return _coroutine();
            }
            base.Finish();
        }

        public override void OnEnter()
        {
            if (_coroutine == null || base.Fsm?.Owner == null)
            {
                base.Finish();
                return;
            }
            if (_wait)
            {
                base.Fsm.Owner.StartCoroutine(InvokeRoutine());
                return;
            }
            base.Fsm.Owner.StartCoroutine(_coroutine());
            base.Finish();
        }
    }

    public class SimpleLogAction : HutongGames.PlayMaker.FsmStateAction
    {
        public HutongGames.PlayMaker.FsmString Text;

        public override void Reset()
        {
            Text = string.Empty;
            base.Reset();
        }

        public override void OnEnter()
        {
            if (Text?.Value != null)
            {
                Log("FSM Log: \"" + Text.Value + "\"");
            }
            base.Finish();
        }

        private new static void Log(string msg)
        {
            Silksong.FsmUtil.InternalLogger.Log("[Core]:[FsmUtil]:[LogAction] - " + msg);
        }
    }

    public class LogAction : SimpleLogAction
    {
        public override void OnEnter()
        {
            if (Text?.Value != null)
            {
                Log(Text.Value ?? "");
            }
            base.Finish();
        }

        private new void Log(string msg)
        {
            try
            {
                string path = base.Fsm?.GameObjectName ?? "";
                var tr = base.Fsm?.GameObject != null ? base.Fsm.GameObject.transform.parent : null;
                while (tr != null)
                {
                    path = tr.gameObject.name + "/" + path;
                    tr = tr.parent;
                }
                Silksong.FsmUtil.InternalLogger.Log(string.Concat("[", path, "]:[", base.Fsm?.Name ?? "", "]:[", base.State?.Name ?? "", "] - ", msg));
            }
            catch (System.Exception __apiEx_24) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L3929", __apiEx_24); }
        }
    }
}

namespace Silksong.FsmUtil
{
    using HutongGames.PlayMaker;
    using Silksong.FsmUtil.Actions;
    using UnityEngine;

    public static class InternalLogger
    {
        internal static BepInEx.Logging.ManualLogSource Logger;

        internal static void LogFine(string msg) => Logger?.LogDebug(msg);
        internal static void LogDebug(string msg) => Logger?.LogInfo(msg);
        internal static void Log(string msg) => Logger?.LogMessage(msg);
        internal static void LogWarn(string msg) => Logger?.LogWarning(msg);
        internal static void LogError(string msg) => Logger?.LogError(msg);
        internal static void LogFatal(string msg) => Logger?.LogFatal(msg);
    }

    public class Plugin
    {
        public const string Id = "org.silksong-modding.fsmutil";
        public static string Name => "Silksong.FsmUtil";
        public static string Version => "0.3.17";
    }

    public class FsmUtilPlugin
    {
        public const string Id = "org.silksong-modding.fsmutil";
        public const string Name = "Silksong.FsmUtil";
        public const string Version = "0.3.17";
        internal static BepInEx.Logging.ManualLogSource Log;
    }

    public static class FsmUtil
    {
        public static bool _patchInstalled = true;

        public static void InstallStateLockPatch()
        {
            _patchInstalled = true;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.Install();
        }

        public static bool ShouldBlockLoadActions(FsmState state)
        {
            return state != null && !SilksongMobileAPI.PlayMakerHybridCLRBridge.FsmState_LoadActions_Prefix(state);
        }

        public static void EnsureFsmLoaded(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.EnsurePlayMakerFsmPreprocessedAndLoaded(fsm);
        }

        public static void EnsureFsmLoaded(Fsm fsm)
        {
            if (fsm == null) return;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.EnsureFsmPreprocessedOnce(fsm);
        }

        public static void EnsureStateLoaded(FsmState state)
        {
            if (state == null) return;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.EnsureSingleStateLoadedAndLocked(state);
        }

        public static void LockState(FsmState state)
        {
            if (state == null) return;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.LockStateSafe(state);
        }

        public static FsmStateAction CloneActionInstance(FsmStateAction src, FsmState targetState = null)
        {
            return SilksongMobileAPI.PlayMakerHybridCLRBridge.CloneActionInstanceSafe(src, targetState);
        }

        public static PlayMakerFSM GetFsm(GameObject go, string fsmName)
        {
            if (go == null) return null;
            var fsms = go.GetComponents<PlayMakerFSM>();
            if (fsms == null) return null;
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == fsmName)
                    return fsms[i];
            }
            return null;
        }

        public static PlayMakerFSM GetFsm(Component component, string fsmName)
        {
            return component != null ? GetFsm(component.gameObject, fsmName) : null;
        }

        public static PlayMakerFSM GetFsmPreprocessed(GameObject go, string fsmName)
        {
            var pfsm = GetFsm(go, fsmName);
            if (pfsm != null)
            {
                EnsureFsmLoaded(pfsm);
            }
            return pfsm;
        }

        public static PlayMakerFSM GetFsmPreprocessed(Component component, string fsmName)
        {
            return component != null ? GetFsmPreprocessed(component.gameObject, fsmName) : null;
        }

        public static T GetItemFromArray<T>(T[] origArray, Func<T, bool> isItemCheck)
        {
            if (origArray == null || isItemCheck == null) return default;
            for (int i = 0; i < origArray.Length; i++)
            {
                if (isItemCheck(origArray[i]))
                    return origArray[i];
            }
            return default;
        }

        public static T[] GetItemsFromArray<T>(T[] origArray, Func<T, bool> isItemCheck)
        {
            if (origArray == null || isItemCheck == null) return Array.Empty<T>();
            int count = 0;
            for (int i = 0; i < origArray.Length; i++)
            {
                if (isItemCheck(origArray[i])) count++;
            }
            if (count == origArray.Length) return origArray;
            if (count == 0) return Array.Empty<T>();
            var res = new T[count];
            int idx = 0;
            for (int i = 0; i < origArray.Length; i++)
            {
                if (isItemCheck(origArray[i]))
                    res[idx++] = origArray[i];
            }
            return res;
        }

        public static FsmState GetState(PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null) return null;
            EnsureFsmLoaded(fsm);
            return GetState(fsm.Fsm, stateName);
        }

        public static FsmState GetState(Fsm fsm, string stateName)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName)) return null;
            var st = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetFsmStateSafe(fsm, stateName);
            if (st != null)
            {
                EnsureStateLoaded(st);
                return st;
            }
            var states = fsm.States;
            if (states == null) return null;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] != null && states[i].Name == stateName)
                {
                    EnsureStateLoaded(states[i]);
                    return states[i];
                }
            }
            return null;
        }

        public static FsmState FindState(PlayMakerFSM fsm, string stateName) => GetState(fsm, stateName);
        public static FsmState FindState(Fsm fsm, string stateName) => GetState(fsm, stateName);

        public static bool TryGetState(PlayMakerFSM fsm, string stateName, out FsmState state)
        {
            state = GetState(fsm, stateName);
            return state != null;
        }

        public static bool TryGetState(Fsm fsm, string stateName, out FsmState state)
        {
            state = GetState(fsm, stateName);
            return state != null;
        }

        private static readonly System.Collections.Generic.HashSet<string> _loggedDummyStateKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static FsmState CreateSafeDummyFsmState(Fsm fsm, string stateName)
        {
            var safeFsm = fsm ?? new Fsm();
            var dummy = new FsmState(safeFsm);
            dummy.Name = stateName ?? string.Empty;
            dummy.Actions = Array.Empty<FsmStateAction>();
            dummy.Transitions = Array.Empty<FsmTransition>();
            try { SilksongMobileAPI.PlayMakerHybridCLRBridge.LockStateSafe(dummy); } catch (System.Exception __apiEx_25) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L4128", __apiEx_25); }
            if (fsm != null && !string.IsNullOrEmpty(stateName))
            {
                try
                {
                    var orig = fsm.States ?? Array.Empty<FsmState>();
                    fsm.States = AddItemToArray(orig, dummy);
                }
                catch (System.Exception __apiEx_26) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("BepInExShims", "L4136", __apiEx_26); }
            }
            return dummy;
        }

        public static FsmState MustGetState(PlayMakerFSM fsm, string stateName)
        {
            var st = GetState(fsm, stateName);
            if (st == null)
            {
                string fsmLabel = fsm != null ? fsm.FsmName : "null";
                string logKey = fsmLabel + "::" + (stateName ?? "");
                bool shouldLog = false;
                lock (_loggedDummyStateKeys) { shouldLog = _loggedDummyStateKeys.Add(logKey); }
                if (shouldLog)
                {
                    SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo("[FsmUtil] MustGetState fallback: State '" + stateName + "' not found in PlayMakerFSM '" + fsmLabel + "'. Returning safe dummy FsmState.");
                }
                return CreateSafeDummyFsmState(fsm != null ? fsm.Fsm : null, stateName);
            }
            return st;
        }

        public static FsmState MustGetState(Fsm fsm, string stateName)
        {
            var st = GetState(fsm, stateName);
            if (st == null)
            {
                string fsmLabel = fsm != null ? fsm.Name : "null";
                string logKey = fsmLabel + "::" + (stateName ?? "");
                bool shouldLog = false;
                lock (_loggedDummyStateKeys) { shouldLog = _loggedDummyStateKeys.Add(logKey); }
                if (shouldLog)
                {
                    SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo("[FsmUtil] MustGetState fallback: State '" + stateName + "' not found in Fsm '" + fsmLabel + "'. Returning safe dummy FsmState.");
                }
                return CreateSafeDummyFsmState(fsm, stateName);
            }
            return st;
        }

        public static FsmTransition GetTransition(PlayMakerFSM fsm, string stateName, string eventName) => GetTransition(MustGetState(fsm, stateName), eventName);
        public static FsmTransition GetTransition(Fsm fsm, string stateName, string eventName) => GetTransition(MustGetState(fsm, stateName), eventName);

        public static FsmTransition GetTransition(FsmState state, string eventName)
        {
            if (state == null || state.Transitions == null) return null;
            var trs = state.Transitions;
            for (int i = 0; i < trs.Length; i++)
            {
                if (trs[i] != null && trs[i].EventName == eventName)
                    return trs[i];
            }
            return null;
        }

        public static FsmTransition FindTransition(PlayMakerFSM fsm, string stateName, string eventName)
        {
            var st = GetState(fsm, stateName);
            return st != null ? GetTransition(st, eventName) : null;
        }

        public static FsmTransition FindTransition(Fsm fsm, string stateName, string eventName)
        {
            var st = GetState(fsm, stateName);
            return st != null ? GetTransition(st, eventName) : null;
        }

        public static FsmTransition FindTransition(FsmState state, string eventName) => GetTransition(state, eventName);

        public static FsmTransition GetGlobalTransition(PlayMakerFSM fsm, string globalEventName) => fsm != null ? GetGlobalTransition(fsm.Fsm, globalEventName) : null;

        public static FsmTransition GetGlobalTransition(Fsm fsm, string globalEventName)
        {
            if (fsm == null || fsm.GlobalTransitions == null) return null;
            var trs = fsm.GlobalTransitions;
            for (int i = 0; i < trs.Length; i++)
            {
                if (trs[i] != null && trs[i].EventName == globalEventName)
                    return trs[i];
            }
            return null;
        }

        public static T GetAction<T>(PlayMakerFSM fsm, string stateName, int index) where T : FsmStateAction => GetAction<T>(MustGetState(fsm, stateName), index);
        public static T GetAction<T>(Fsm fsm, string stateName, int index) where T : FsmStateAction => GetAction<T>(MustGetState(fsm, stateName), index);

        public static T GetAction<T>(FsmState state, int index) where T : FsmStateAction
        {
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (acts == null || index < 0 || index >= acts.Length) return null;
            return acts[index] as T;
        }

        public static FsmStateAction GetAction(PlayMakerFSM fsm, string stateName, int index) => GetStateAction(fsm, stateName, index);
        public static FsmStateAction GetAction(Fsm fsm, string stateName, int index) => GetStateAction(fsm, stateName, index);
        public static FsmStateAction GetAction(FsmState state, int index) => GetStateAction(state, index);

        public static FsmStateAction GetStateAction(PlayMakerFSM fsm, string stateName, int index) => GetStateAction(MustGetState(fsm, stateName), index);
        public static FsmStateAction GetStateAction(Fsm fsm, string stateName, int index) => GetStateAction(MustGetState(fsm, stateName), index);

        public static FsmStateAction GetStateAction(FsmState state, int index)
        {
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (acts == null || index < 0 || index >= acts.Length) return null;
            return acts[index];
        }

        public static T[] GetActionsOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => GetActionsOfType<T>(MustGetState(fsm, stateName));
        public static T[] GetActionsOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => GetActionsOfType<T>(MustGetState(fsm, stateName));

        public static T[] GetActionsOfType<T>(FsmState state) where T : FsmStateAction
        {
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (acts == null || acts.Length == 0) return Array.Empty<T>();
            var list = new System.Collections.Generic.List<T>();
            for (int i = 0; i < acts.Length; i++)
            {
                if (acts[i] is T typed)
                    list.Add(typed);
            }
            return list.Count == 0 ? Array.Empty<T>() : list.ToArray();
        }

        public static T GetFirstActionOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => GetFirstActionOfType<T>(MustGetState(fsm, stateName));
        public static T GetFirstActionOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => GetFirstActionOfType<T>(MustGetState(fsm, stateName));

        public static T GetFirstActionOfType<T>(FsmState state) where T : FsmStateAction
        {
            int idx = IndexFirstActionOfType<T>(state);
            return idx == -1 ? null : GetAction<T>(state, idx);
        }

        public static T GetLastActionOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => GetLastActionOfType<T>(MustGetState(fsm, stateName));
        public static T GetLastActionOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => GetLastActionOfType<T>(MustGetState(fsm, stateName));

        public static T GetLastActionOfType<T>(FsmState state) where T : FsmStateAction
        {
            int idx = IndexLastActionOfType<T>(state);
            return idx == -1 ? null : GetAction<T>(state, idx);
        }

        public static int IndexFirstActionMatching(FsmState state, Func<FsmStateAction, bool> predicate)
        {
            if (state == null || predicate == null) return -1;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            for (int i = 0; i < acts.Length; i++)
            {
                if (predicate(acts[i])) return i;
            }
            return -1;
        }

        public static int IndexLastActionMatching(FsmState state, Func<FsmStateAction, bool> predicate)
        {
            if (state == null || predicate == null) return -1;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            for (int i = acts.Length - 1; i >= 0; i--)
            {
                if (predicate(acts[i])) return i;
            }
            return -1;
        }

        public static int IndexFirstActionOfType<T>(FsmState state) where T : FsmStateAction => IndexFirstActionMatching(state, a => a is T);
        public static int IndexLastActionOfType<T>(FsmState state) where T : FsmStateAction => IndexLastActionMatching(state, a => a is T);

        public static T[] AddItemToArray<T>(T[] origArray, T value)
        {
            if (origArray == null) return new T[] { value };
            var res = new T[origArray.Length + 1];
            origArray.CopyTo(res, 0);
            res[origArray.Length] = value;
            return res;
        }

        public static FsmState AddState(PlayMakerFSM fsm, string stateName) => AddState(fsm.Fsm, new FsmState(fsm.Fsm) { Name = stateName });
        public static FsmState AddState(Fsm fsm, string stateName) => AddState(fsm, new FsmState(fsm) { Name = stateName });
        public static FsmState AddState(PlayMakerFSM fsm, FsmState state) => AddState(fsm.Fsm, state);

        public static FsmState AddState(Fsm fsm, FsmState state)
        {
            if (fsm == null || state == null) return state;
            EnsureFsmLoaded(fsm);
            var orig = fsm.States ?? Array.Empty<FsmState>();
            var next = AddItemToArray(orig, state);
            fsm.States = next;
            EnsureStateLoaded(state);
            return state;
        }

        public static FsmState CopyState(PlayMakerFSM fsm, string fromState, string toState) => CopyState(fsm.Fsm, fromState, toState);

        public static FsmState CopyState(Fsm fsm, string fromState, string toState)
        {
            EnsureFsmLoaded(fsm);
            var src = GetState(fsm, fromState);
            var copy = SilksongMobileAPI.PlayMakerHybridCLRBridge.CloneFsmStateSafe(src) ?? new FsmState(src);
            copy.Name = toState;
            var trs = copy.Transitions;
            if (trs != null)
            {
                for (int i = 0; i < trs.Length; i++)
                {
                    if (trs[i] != null)
                        trs[i].ToFsmState = GetState(fsm, trs[i].ToState);
                }
            }
            AddState(fsm, copy);
            return copy;
        }

        public static FsmEvent AddTransition(PlayMakerFSM fsm, string stateName, string eventName, string toState) => AddTransition(MustGetState(fsm, stateName), eventName, toState);
        public static FsmEvent AddTransition(Fsm fsm, string stateName, string eventName, string toState) => AddTransition(MustGetState(fsm, stateName), eventName, toState);

        public static FsmEvent AddTransition(FsmState state, string eventName, string toState)
        {
            var ev = FsmEvent.GetFsmEvent(eventName);
            if (state == null) return ev;
            if (state.Fsm != null) EnsureFsmLoaded(state.Fsm);
            var tr = new FsmTransition
            {
                ToState = toState,
                ToFsmState = state.Fsm != null ? GetState(state.Fsm, toState) : null,
                FsmEvent = ev
            };
            state.Transitions = AddItemToArray(state.Transitions ?? Array.Empty<FsmTransition>(), tr);
            return ev;
        }

        public static void AddTransition(PlayMakerFSM fsm, string stateName, FsmEvent fsmEvent, string toState) => AddTransition(MustGetState(fsm, stateName), fsmEvent, toState);
        public static void AddTransition(Fsm fsm, string stateName, FsmEvent fsmEvent, string toState) => AddTransition(MustGetState(fsm, stateName), fsmEvent, toState);

        public static void AddTransition(FsmState state, FsmEvent fsmEvent, string toState)
        {
            if (state == null || fsmEvent == null) return;
            if (state.Fsm != null) EnsureFsmLoaded(state.Fsm);
            var tr = new FsmTransition
            {
                ToState = toState,
                ToFsmState = state.Fsm != null ? GetState(state.Fsm, toState) : null,
                FsmEvent = fsmEvent
            };
            state.Transitions = AddItemToArray(state.Transitions ?? Array.Empty<FsmTransition>(), tr);
        }

        public static FsmEvent AddGlobalTransition(PlayMakerFSM fsm, string globalEventName, string toState) => AddGlobalTransition(fsm.Fsm, globalEventName, toState);

        public static FsmEvent AddGlobalTransition(Fsm fsm, string globalEventName, string toState)
        {
            EnsureFsmLoaded(fsm);
            var ev = new FsmEvent(globalEventName) { IsGlobal = true };
            var tr = new FsmTransition
            {
                ToState = toState,
                ToFsmState = GetState(fsm, toState),
                FsmEvent = ev
            };
            fsm.GlobalTransitions = AddItemToArray(fsm.GlobalTransitions ?? Array.Empty<FsmTransition>(), tr);
            return ev;
        }

        public static void AddAction(PlayMakerFSM fsm, string stateName, FsmStateAction action) => AddAction(MustGetState(fsm, stateName), action);
        public static void AddAction(Fsm fsm, string stateName, FsmStateAction action) => AddAction(MustGetState(fsm, stateName), action);

        public static void AddAction(FsmState state, FsmStateAction action)
        {
            if (state == null || action == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            var next = AddItemToArray(acts, action);
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, next);
        }

        public static void AddActions(PlayMakerFSM fsm, string stateName, params FsmStateAction[] actions) => AddActions(MustGetState(fsm, stateName), actions);
        public static void AddActions(Fsm fsm, string stateName, params FsmStateAction[] actions) => AddActions(MustGetState(fsm, stateName), actions);

        public static void AddActions(FsmState state, params FsmStateAction[] actions)
        {
            if (state == null || actions == null || actions.Length == 0) return;
            for (int i = 0; i < actions.Length; i++)
            {
                AddAction(state, actions[i]);
            }
        }

        public static void AddMethod(PlayMakerFSM fsm, string stateName, Action<FsmStateAction> method) => AddMethod(MustGetState(fsm, stateName), method);
        public static void AddMethod(Fsm fsm, string stateName, Action<FsmStateAction> method) => AddMethod(MustGetState(fsm, stateName), method);

        public static void AddMethod(FsmState state, Action<FsmStateAction> method)
        {
            var act = new DelegateAction<FsmStateAction> { Method = method };
            act.Arg = act;
            AddAction(state, act);
        }

        public static void AddMethod(PlayMakerFSM fsm, string stateName, Action method) => AddMethod(MustGetState(fsm, stateName), method, false);
        public static void AddMethod(Fsm fsm, string stateName, Action method) => AddMethod(MustGetState(fsm, stateName), method, false);
        public static void AddMethod(FsmState state, Action method) => AddMethod(state, method, false);

        public static void AddMethod(PlayMakerFSM fsm, string stateName, Action method, bool everyFrame) => AddMethod(MustGetState(fsm, stateName), method, everyFrame);
        public static void AddMethod(Fsm fsm, string stateName, Action method, bool everyFrame) => AddMethod(MustGetState(fsm, stateName), method, everyFrame);

        public static void AddMethod(FsmState state, Action method, bool everyFrame)
        {
            var act = new LambdaAction { Method = method, EveryFrame = everyFrame };
            AddAction(state, act);
        }

        public static void AddLambdaMethod(PlayMakerFSM fsm, string stateName, Action<Action> method) => AddLambdaMethod(MustGetState(fsm, stateName), method);
        public static void AddLambdaMethod(Fsm fsm, string stateName, Action<Action> method) => AddLambdaMethod(MustGetState(fsm, stateName), method);

        public static void AddLambdaMethod(FsmState state, Action<Action> method)
        {
            var act = new DelegateAction<Action> { Method = method };
            act.Arg = act.Finish;
            AddAction(state, act);
        }

        public static void AddLambdaMethod(PlayMakerFSM fsm, string stateName, Action<FsmStateAction> method) => AddMethod(MustGetState(fsm, stateName), method);
        public static void AddLambdaMethod(Fsm fsm, string stateName, Action<FsmStateAction> method) => AddMethod(MustGetState(fsm, stateName), method);
        public static void AddLambdaMethod(FsmState state, Action<FsmStateAction> method) => AddMethod(state, method);

        public static void AddCoroutine(PlayMakerFSM fsm, string stateName, Func<System.Collections.IEnumerator> coroutine, bool wait = true) => AddCoroutine(MustGetState(fsm, stateName), coroutine, wait);
        public static void AddCoroutine(Fsm fsm, string stateName, Func<System.Collections.IEnumerator> coroutine, bool wait = true) => AddCoroutine(MustGetState(fsm, stateName), coroutine, wait);

        public static void AddCoroutine(FsmState state, Func<System.Collections.IEnumerator> coroutine, bool wait = true)
        {
            AddAction(state, new InvokeCoroutine(coroutine, wait));
        }

        public static T[] InsertItemIntoArray<T>(T[] origArray, T value, int index)
        {
            if (origArray == null) origArray = Array.Empty<T>();
            int len = origArray.Length;
            if (index < 0) index = 0;
            if (index > len) index = len;
            var res = new T[len + 1];
            for (int i = 0; i < index; i++) res[i] = origArray[i];
            res[index] = value;
            for (int i = index; i < len; i++) res[i + 1] = origArray[i];
            return res;
        }

        public static void InsertAction(PlayMakerFSM fsm, string stateName, FsmStateAction action, int index) => InsertAction(MustGetState(fsm, stateName), index, action);
        public static void InsertAction(PlayMakerFSM fsm, string stateName, int index, FsmStateAction action) => InsertAction(MustGetState(fsm, stateName), index, action);
        public static void InsertAction(Fsm fsm, string stateName, FsmStateAction action, int index) => InsertAction(MustGetState(fsm, stateName), index, action);
        public static void InsertAction(Fsm fsm, string stateName, int index, FsmStateAction action) => InsertAction(MustGetState(fsm, stateName), index, action);
        public static void InsertAction(FsmState state, FsmStateAction action, int index) => InsertAction(state, index, action);

        public static void InsertAction(FsmState state, int index, FsmStateAction action)
        {
            if (state == null || action == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            var next = InsertItemIntoArray(acts, action, index);
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, next);
        }

        public static void InsertActions(PlayMakerFSM fsm, string stateName, int index, params FsmStateAction[] actions) => InsertActions(MustGetState(fsm, stateName), index, actions);
        public static void InsertActions(Fsm fsm, string stateName, int index, params FsmStateAction[] actions) => InsertActions(MustGetState(fsm, stateName), index, actions);

        public static void InsertActions(FsmState state, int index, params FsmStateAction[] actions)
        {
            if (state == null || actions == null) return;
            for (int i = 0; i < actions.Length; i++)
            {
                InsertAction(state, actions[i], index);
                index++;
            }
        }

        public static void InsertMethod(PlayMakerFSM fsm, string stateName, Action<FsmStateAction> method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertMethod(PlayMakerFSM fsm, string stateName, int index, Action<FsmStateAction> method) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertMethod(Fsm fsm, string stateName, Action<FsmStateAction> method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertMethod(Fsm fsm, string stateName, int index, Action<FsmStateAction> method) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertMethod(FsmState state, Action<FsmStateAction> method, int index) => InsertMethod(state, index, method);

        public static void InsertMethod(FsmState state, int index, Action<FsmStateAction> method)
        {
            var act = new DelegateAction<FsmStateAction> { Method = method };
            act.Arg = act;
            InsertAction(state, act, index);
        }

        public static void InsertMethod(PlayMakerFSM fsm, string stateName, Action method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method, false);
        public static void InsertMethod(PlayMakerFSM fsm, string stateName, int index, Action method) => InsertMethod(MustGetState(fsm, stateName), index, method, false);
        public static void InsertMethod(Fsm fsm, string stateName, Action method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method, false);
        public static void InsertMethod(Fsm fsm, string stateName, int index, Action method) => InsertMethod(MustGetState(fsm, stateName), index, method, false);
        public static void InsertMethod(FsmState state, Action method, int index) => InsertMethod(state, index, method, false);
        public static void InsertMethod(FsmState state, int index, Action method) => InsertMethod(state, index, method, false);

        public static void InsertMethod(PlayMakerFSM fsm, string stateName, Action method, int index, bool everyFrame) => InsertMethod(MustGetState(fsm, stateName), index, method, everyFrame);
        public static void InsertMethod(PlayMakerFSM fsm, string stateName, int index, Action method, bool everyFrame) => InsertMethod(MustGetState(fsm, stateName), index, method, everyFrame);
        public static void InsertMethod(Fsm fsm, string stateName, Action method, int index, bool everyFrame) => InsertMethod(MustGetState(fsm, stateName), index, method, everyFrame);
        public static void InsertMethod(Fsm fsm, string stateName, int index, Action method, bool everyFrame) => InsertMethod(MustGetState(fsm, stateName), index, method, everyFrame);
        public static void InsertMethod(FsmState state, Action method, int index, bool everyFrame) => InsertMethod(state, index, method, everyFrame);

        public static void InsertMethod(FsmState state, int index, Action method, bool everyFrame)
        {
            var act = new LambdaAction { Method = method, EveryFrame = everyFrame };
            InsertAction(state, act, index);
        }

        public static void InsertLambdaMethod(PlayMakerFSM fsm, string stateName, Action<Action> method, int index) => InsertLambdaMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(PlayMakerFSM fsm, string stateName, int index, Action<Action> method) => InsertLambdaMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(Fsm fsm, string stateName, Action<Action> method, int index) => InsertLambdaMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(Fsm fsm, string stateName, int index, Action<Action> method) => InsertLambdaMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(FsmState state, Action<Action> method, int index) => InsertLambdaMethod(state, index, method);

        public static void InsertLambdaMethod(FsmState state, int index, Action<Action> method)
        {
            var act = new DelegateAction<Action> { Method = method };
            act.Arg = act.Finish;
            InsertAction(state, act, index);
        }

        public static void InsertLambdaMethod(PlayMakerFSM fsm, string stateName, Action<FsmStateAction> method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(PlayMakerFSM fsm, string stateName, int index, Action<FsmStateAction> method) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(Fsm fsm, string stateName, Action<FsmStateAction> method, int index) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(Fsm fsm, string stateName, int index, Action<FsmStateAction> method) => InsertMethod(MustGetState(fsm, stateName), index, method);
        public static void InsertLambdaMethod(FsmState state, Action<FsmStateAction> method, int index) => InsertMethod(state, index, method);
        public static void InsertLambdaMethod(FsmState state, int index, Action<FsmStateAction> method) => InsertMethod(state, index, method);

        public static void InsertCoroutine(PlayMakerFSM fsm, string stateName, int index, Func<System.Collections.IEnumerator> coroutine, bool wait = true) => InsertCoroutine(MustGetState(fsm, stateName), index, coroutine, wait);
        public static void InsertCoroutine(Fsm fsm, string stateName, int index, Func<System.Collections.IEnumerator> coroutine, bool wait = true) => InsertCoroutine(MustGetState(fsm, stateName), index, coroutine, wait);

        public static void InsertCoroutine(FsmState state, int index, Func<System.Collections.IEnumerator> coroutine, bool wait = true)
        {
            InsertAction(state, index, new InvokeCoroutine(coroutine, wait));
        }

        public static void InsertMethodBefore(FsmStateAction action, Action<FsmStateAction> method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertMethod(action.State, idx >= 0 ? idx : 0, method);
        }

        public static void InsertMethodAfter(FsmStateAction action, Action<FsmStateAction> method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertMethod(action.State, idx >= 0 ? idx + 1 : acts.Length, method);
        }

        public static void InsertMethodBefore(FsmStateAction action, Action method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertMethod(action.State, idx >= 0 ? idx : 0, method, false);
        }

        public static void InsertMethodAfter(FsmStateAction action, Action method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertMethod(action.State, idx >= 0 ? idx + 1 : acts.Length, method, false);
        }

        public static void InsertLambdaMethodBefore(FsmStateAction action, Action<Action> method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertLambdaMethod(action.State, idx >= 0 ? idx : 0, method);
        }

        public static void InsertLambdaMethodAfter(FsmStateAction action, Action<Action> method)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertLambdaMethod(action.State, idx >= 0 ? idx + 1 : acts.Length, method);
        }

        public static void InsertActionBefore(FsmStateAction action, FsmStateAction newAction)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertAction(action.State, idx >= 0 ? idx : 0, newAction);
        }

        public static void InsertActionAfter(FsmStateAction action, FsmStateAction newAction)
        {
            if (action?.State == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(action.State);
            int idx = Array.IndexOf(acts, action);
            InsertAction(action.State, idx >= 0 ? idx + 1 : acts.Length, newAction);
        }

        public static void ReplaceAction(PlayMakerFSM fsm, string stateName, FsmStateAction action, int index) => ReplaceAction(MustGetState(fsm, stateName), index, action);
        public static void ReplaceAction(PlayMakerFSM fsm, string stateName, int index, FsmStateAction action) => ReplaceAction(MustGetState(fsm, stateName), index, action);
        public static void ReplaceAction(Fsm fsm, string stateName, FsmStateAction action, int index) => ReplaceAction(MustGetState(fsm, stateName), index, action);
        public static void ReplaceAction(Fsm fsm, string stateName, int index, FsmStateAction action) => ReplaceAction(MustGetState(fsm, stateName), index, action);
        public static void ReplaceAction(FsmState state, FsmStateAction action, int index) => ReplaceAction(state, index, action);

        public static void ReplaceAction(FsmState state, int index, FsmStateAction action)
        {
            if (state == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (index < 0 || index >= acts.Length) return;
            acts[index] = action;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, acts);
        }

        public static void ReplaceAllActions(PlayMakerFSM fsm, string stateName, params FsmStateAction[] actions) => ReplaceAllActions(MustGetState(fsm, stateName), actions);
        public static void ReplaceAllActions(Fsm fsm, string stateName, params FsmStateAction[] actions) => ReplaceAllActions(MustGetState(fsm, stateName), actions);

        public static void ReplaceAllActions(FsmState state, params FsmStateAction[] actions)
        {
            if (state == null) return;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, actions ?? Array.Empty<FsmStateAction>());
        }

        public static void ReplaceActionsOfType<T>(FsmState state, Func<T, FsmStateAction> newActionGenerator) where T : FsmStateAction
        {
            if (state == null || newActionGenerator == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            bool changed = false;
            for (int i = 0; i < acts.Length; i++)
            {
                if (acts[i] is T typed)
                {
                    acts[i] = newActionGenerator(typed);
                    changed = true;
                }
            }
            if (changed)
            {
                SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, acts);
            }
        }

        public static void ReplaceFirstActionOfType<T>(FsmState state, FsmStateAction newAction) where T : FsmStateAction
        {
            int idx = IndexFirstActionOfType<T>(state);
            if (idx >= 0) ReplaceAction(state, idx, newAction);
        }

        public static void ReplaceLastActionOfType<T>(FsmState state, FsmStateAction newAction) where T : FsmStateAction
        {
            int idx = IndexLastActionOfType<T>(state);
            if (idx >= 0) ReplaceAction(state, idx, newAction);
        }

        public static bool ChangeTransition(PlayMakerFSM fsm, string stateName, string eventName, string toState) => ChangeTransition(MustGetState(fsm, stateName), eventName, toState);
        public static bool ChangeTransition(Fsm fsm, string stateName, string eventName, string toState) => ChangeTransition(MustGetState(fsm, stateName), eventName, toState);

        public static bool ChangeTransition(FsmState state, string eventName, string toState)
        {
            var tr = GetTransition(state, eventName);
            if (tr == null) return false;
            tr.ToState = toState;
            tr.ToFsmState = state?.Fsm != null ? GetState(state.Fsm, toState) : null;
            return true;
        }

        public static bool ChangeGlobalTransition(PlayMakerFSM fsm, string globalEventName, string toState) => ChangeGlobalTransition(fsm.Fsm, globalEventName, toState);

        public static bool ChangeGlobalTransition(Fsm fsm, string globalEventName, string toState)
        {
            var tr = GetGlobalTransition(fsm, globalEventName);
            if (tr == null) return false;
            tr.ToState = toState;
            tr.ToFsmState = GetState(fsm, toState);
            return true;
        }

        public static T[] RemoveItemsFromArray<T>(T[] origArray, Func<T, bool> shouldBeRemovedCallback)
        {
            if (origArray == null || shouldBeRemovedCallback == null) return origArray ?? Array.Empty<T>();
            int removeCount = 0;
            for (int i = 0; i < origArray.Length; i++)
            {
                if (shouldBeRemovedCallback(origArray[i])) removeCount++;
            }
            if (removeCount == 0) return origArray;
            var res = new T[origArray.Length - removeCount];
            int idx = 0;
            for (int i = 0; i < origArray.Length; i++)
            {
                if (!shouldBeRemovedCallback(origArray[i]))
                    res[idx++] = origArray[i];
            }
            return res;
        }

        public static void RemoveState(PlayMakerFSM fsm, string stateName) => RemoveState(fsm.Fsm, stateName);

        public static void RemoveState(Fsm fsm, string stateName)
        {
            if (fsm == null) return;
            EnsureFsmLoaded(fsm);
            fsm.States = RemoveItemsFromArray(fsm.States ?? Array.Empty<FsmState>(), s => s != null && s.Name == stateName);
        }

        public static void RemoveTransition(PlayMakerFSM fsm, string stateName, string eventName) => RemoveTransition(MustGetState(fsm, stateName), eventName);
        public static void RemoveTransition(Fsm fsm, string stateName, string eventName) => RemoveTransition(MustGetState(fsm, stateName), eventName);

        public static void RemoveTransition(FsmState state, string eventName)
        {
            if (state == null || state.Transitions == null) return;
            state.Transitions = RemoveItemsFromArray(state.Transitions, t => t != null && t.EventName == eventName);
        }

        public static void RemoveGlobalTransition(PlayMakerFSM fsm, string globalEventName) => RemoveGlobalTransition(fsm.Fsm, globalEventName);

        public static void RemoveGlobalTransition(Fsm fsm, string globalEventName)
        {
            if (fsm == null || fsm.GlobalTransitions == null) return;
            fsm.GlobalTransitions = RemoveItemsFromArray(fsm.GlobalTransitions, t => t != null && t.EventName == globalEventName);
        }

        public static void RemoveTransitionsTo(PlayMakerFSM fsm, string toState) => RemoveTransitionsTo(fsm.Fsm, toState);

        public static void RemoveTransitionsTo(Fsm fsm, string toState)
        {
            if (fsm == null) return;
            EnsureFsmLoaded(fsm);
            var states = fsm.States;
            if (states == null) return;
            for (int i = 0; i < states.Length; i++)
            {
                RemoveTransitionsTo(states[i], toState);
            }
        }

        public static void RemoveTransitionsTo(PlayMakerFSM fsm, string stateName, string toState) => RemoveTransitionsTo(MustGetState(fsm, stateName), toState);
        public static void RemoveTransitionsTo(Fsm fsm, string stateName, string toState) => RemoveTransitionsTo(MustGetState(fsm, stateName), toState);

        public static void RemoveTransitionsTo(FsmState state, string toState)
        {
            if (state == null || state.Transitions == null) return;
            state.Transitions = RemoveItemsFromArray(state.Transitions, t => t != null && t.ToState == toState);
        }

        public static void RemoveTransitions(PlayMakerFSM fsm, string stateName) => RemoveTransitions(MustGetState(fsm, stateName));
        public static void RemoveTransitions(Fsm fsm, string stateName) => RemoveTransitions(MustGetState(fsm, stateName));

        public static void RemoveTransitions(FsmState state)
        {
            if (state == null) return;
            state.Transitions = Array.Empty<FsmTransition>();
        }

        public static bool RemoveAction(PlayMakerFSM fsm, string stateName, int index) => RemoveAction(MustGetState(fsm, stateName), index);
        public static bool RemoveAction(Fsm fsm, string stateName, int index) => RemoveAction(MustGetState(fsm, stateName), index);

        public static bool RemoveAction(FsmState state, int index)
        {
            if (state == null) return false;
            var orig = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (index < 0 || index >= orig.Length) return false;
            var next = new FsmStateAction[orig.Length - 1];
            for (int i = 0; i < index; i++) next[i] = orig[i];
            for (int i = index; i < next.Length; i++) next[i] = orig[i + 1];
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, next);
            return true;
        }

        public static void RemoveActionsOfType<T>(PlayMakerFSM fsm) where T : FsmStateAction => RemoveActionsOfType<T>(fsm.Fsm);

        public static void RemoveActionsOfType<T>(Fsm fsm) where T : FsmStateAction
        {
            if (fsm == null) return;
            EnsureFsmLoaded(fsm);
            var states = fsm.States;
            if (states == null) return;
            for (int i = 0; i < states.Length; i++)
            {
                RemoveActionsOfType<T>(states[i]);
            }
        }

        public static void RemoveActionsOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => RemoveActionsOfType<T>(MustGetState(fsm, stateName));
        public static void RemoveActionsOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => RemoveActionsOfType<T>(MustGetState(fsm, stateName));

        public static void RemoveActionsOfType<T>(FsmState state) where T : FsmStateAction
        {
            if (state == null) return;
            var orig = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            var next = RemoveItemsFromArray(orig, a => a is T);
            if (!ReferenceEquals(orig, next))
            {
                SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, next);
            }
        }

        public static void RemoveFirstActionOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => RemoveFirstActionOfType<T>(MustGetState(fsm, stateName));
        public static void RemoveFirstActionOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => RemoveFirstActionOfType<T>(MustGetState(fsm, stateName));

        public static void RemoveFirstActionOfType<T>(FsmState state) where T : FsmStateAction
        {
            int idx = IndexFirstActionOfType<T>(state);
            if (idx != -1) RemoveAction(state, idx);
        }

        public static void RemoveFirstActionMatching(FsmState state, Func<FsmStateAction, bool> predicate)
        {
            int idx = IndexFirstActionMatching(state, predicate);
            if (idx != -1) RemoveAction(state, idx);
        }

        public static void RemoveLastActionOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => RemoveLastActionOfType<T>(MustGetState(fsm, stateName));
        public static void RemoveLastActionOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => RemoveLastActionOfType<T>(MustGetState(fsm, stateName));

        public static void RemoveLastActionOfType<T>(FsmState state) where T : FsmStateAction
        {
            int idx = IndexLastActionOfType<T>(state);
            if (idx != -1) RemoveAction(state, idx);
        }

        public static void RemoveLastActionMatching(FsmState state, Func<FsmStateAction, bool> predicate)
        {
            int idx = IndexLastActionMatching(state, predicate);
            if (idx != -1) RemoveAction(state, idx);
        }

        public static bool DisableAction(PlayMakerFSM fsm, string stateName, int index) => DisableAction(MustGetState(fsm, stateName), index);
        public static bool DisableAction(Fsm fsm, string stateName, int index) => DisableAction(MustGetState(fsm, stateName), index);

        public static bool DisableAction(FsmState state, int index)
        {
            if (state == null) return false;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            if (index < 0 || index >= acts.Length || acts[index] == null) return false;
            acts[index].Enabled = false;
            SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, acts);
            return true;
        }

        public static bool DisableActions(PlayMakerFSM fsm, string stateName, params int[] indices) => DisableActions(MustGetState(fsm, stateName), indices);
        public static bool DisableActions(Fsm fsm, string stateName, params int[] indices) => DisableActions(MustGetState(fsm, stateName), indices);

        public static bool DisableActions(FsmState state, params int[] indices)
        {
            if (state == null || indices == null) return false;
            bool ok = true;
            for (int i = 0; i < indices.Length; i++)
            {
                ok = ok && DisableAction(state, indices[i]);
            }
            return ok;
        }

        public static void DisableActionsOfType<T>(PlayMakerFSM fsm) where T : FsmStateAction => DisableActionsOfType<T>(fsm.Fsm);

        public static void DisableActionsOfType<T>(Fsm fsm) where T : FsmStateAction
        {
            if (fsm == null) return;
            EnsureFsmLoaded(fsm);
            var states = fsm.States;
            if (states == null) return;
            for (int i = 0; i < states.Length; i++)
            {
                DisableActionsOfType<T>(states[i]);
            }
        }

        public static void DisableActionsOfType<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction => DisableActionsOfType<T>(MustGetState(fsm, stateName));
        public static void DisableActionsOfType<T>(Fsm fsm, string stateName) where T : FsmStateAction => DisableActionsOfType<T>(MustGetState(fsm, stateName));

        public static void DisableActionsOfType<T>(FsmState state) where T : FsmStateAction
        {
            if (state == null) return;
            var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(state);
            bool changed = false;
            for (int i = 0; i < acts.Length; i++)
            {
                if (acts[i] is T)
                {
                    acts[i].Enabled = false;
                    changed = true;
                }
            }
            if (changed)
            {
                SilksongMobileAPI.PlayMakerHybridCLRBridge.SetStateActionsSafe(state, acts);
            }
        }

        public static T[] MakeNewVariableArray<T>(T[] orig, string name) where T : NamedVariable, new()
        {
            if (orig == null) orig = Array.Empty<T>();
            var res = new T[orig.Length + 1];
            orig.CopyTo(res, 0);
            var item = new T { Name = name };
            res[orig.Length] = item;
            return res;
        }

        public static FsmFloat AddFloatVariable(PlayMakerFSM fsm, string name) => AddFloatVariable(fsm.Fsm, name);
        public static FsmFloat AddFloatVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.FloatVariables, name);
            fsm.Variables.FloatVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmInt AddIntVariable(PlayMakerFSM fsm, string name) => AddIntVariable(fsm.Fsm, name);
        public static FsmInt AddIntVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.IntVariables, name);
            fsm.Variables.IntVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmBool AddBoolVariable(PlayMakerFSM fsm, string name) => AddBoolVariable(fsm.Fsm, name);
        public static FsmBool AddBoolVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.BoolVariables, name);
            fsm.Variables.BoolVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmString AddStringVariable(PlayMakerFSM fsm, string name) => AddStringVariable(fsm.Fsm, name);
        public static FsmString AddStringVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.StringVariables, name);
            fsm.Variables.StringVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmVector2 AddVector2Variable(PlayMakerFSM fsm, string name) => AddVector2Variable(fsm.Fsm, name);
        public static FsmVector2 AddVector2Variable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.Vector2Variables, name);
            fsm.Variables.Vector2Variables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmVector3 AddVector3Variable(PlayMakerFSM fsm, string name) => AddVector3Variable(fsm.Fsm, name);
        public static FsmVector3 AddVector3Variable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.Vector3Variables, name);
            fsm.Variables.Vector3Variables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmRect AddRectVariable(PlayMakerFSM fsm, string name) => AddRectVariable(fsm.Fsm, name);
        public static FsmRect AddRectVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.RectVariables, name);
            fsm.Variables.RectVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmQuaternion AddQuaternionVariable(PlayMakerFSM fsm, string name) => AddQuaternionVariable(fsm.Fsm, name);
        public static FsmQuaternion AddQuaternionVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.QuaternionVariables, name);
            fsm.Variables.QuaternionVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmColor AddColorVariable(PlayMakerFSM fsm, string name) => AddColorVariable(fsm.Fsm, name);
        public static FsmColor AddColorVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.ColorVariables, name);
            fsm.Variables.ColorVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmGameObject AddGameObjectVariable(PlayMakerFSM fsm, string name) => AddGameObjectVariable(fsm.Fsm, name);
        public static FsmGameObject AddGameObjectVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.GameObjectVariables, name);
            fsm.Variables.GameObjectVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmArray AddArrayVariable(PlayMakerFSM fsm, string name) => AddArrayVariable(fsm.Fsm, name);
        public static FsmArray AddArrayVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.ArrayVariables, name);
            fsm.Variables.ArrayVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmEnum AddEnumVariable(PlayMakerFSM fsm, string name) => AddEnumVariable(fsm.Fsm, name);
        public static FsmEnum AddEnumVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.EnumVariables, name);
            fsm.Variables.EnumVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmObject AddObjectVariable(PlayMakerFSM fsm, string name) => AddObjectVariable(fsm.Fsm, name);
        public static FsmObject AddObjectVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.ObjectVariables, name);
            fsm.Variables.ObjectVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmMaterial AddMaterialVariable(PlayMakerFSM fsm, string name) => AddMaterialVariable(fsm.Fsm, name);
        public static FsmMaterial AddMaterialVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.MaterialVariables, name);
            fsm.Variables.MaterialVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static FsmTexture AddTextureVariable(PlayMakerFSM fsm, string name) => AddTextureVariable(fsm.Fsm, name);
        public static FsmTexture AddTextureVariable(Fsm fsm, string name)
        {
            var next = MakeNewVariableArray(fsm.Variables.TextureVariables, name);
            fsm.Variables.TextureVariables = next;
            var v = next[next.Length - 1];
            fsm.Variables.AddVariableLookup(v);
            return v;
        }

        public static T FindInVariableArray<T>(T[] orig, string name) where T : NamedVariable
        {
            if (orig == null) return default;
            for (int i = 0; i < orig.Length; i++)
            {
                if (orig[i] != null && orig[i].Name == name)
                    return orig[i];
            }
            return default;
        }

        public static FsmFloat FindFloatVariable(PlayMakerFSM fsm, string name) => FindFloatVariable(fsm.Fsm, name);
        public static FsmFloat FindFloatVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.FloatVariables, name);

        public static FsmInt FindIntVariable(PlayMakerFSM fsm, string name) => FindIntVariable(fsm.Fsm, name);
        public static FsmInt FindIntVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.IntVariables, name);

        public static FsmBool FindBoolVariable(PlayMakerFSM fsm, string name) => FindBoolVariable(fsm.Fsm, name);
        public static FsmBool FindBoolVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.BoolVariables, name);

        public static FsmString FindStringVariable(PlayMakerFSM fsm, string name) => FindStringVariable(fsm.Fsm, name);
        public static FsmString FindStringVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.StringVariables, name);

        public static FsmVector2 FindVector2Variable(PlayMakerFSM fsm, string name) => FindVector2Variable(fsm.Fsm, name);
        public static FsmVector2 FindVector2Variable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.Vector2Variables, name);

        public static FsmVector3 FindVector3Variable(PlayMakerFSM fsm, string name) => FindVector3Variable(fsm.Fsm, name);
        public static FsmVector3 FindVector3Variable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.Vector3Variables, name);

        public static FsmRect FindRectVariable(PlayMakerFSM fsm, string name) => FindRectVariable(fsm.Fsm, name);
        public static FsmRect FindRectVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.RectVariables, name);

        public static FsmQuaternion FindQuaternionVariable(PlayMakerFSM fsm, string name) => FindQuaternionVariable(fsm.Fsm, name);
        public static FsmQuaternion FindQuaternionVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.QuaternionVariables, name);

        public static FsmColor FindColorVariable(PlayMakerFSM fsm, string name) => FindColorVariable(fsm.Fsm, name);
        public static FsmColor FindColorVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.ColorVariables, name);

        public static FsmGameObject FindGameObjectVariable(PlayMakerFSM fsm, string name) => FindGameObjectVariable(fsm.Fsm, name);
        public static FsmGameObject FindGameObjectVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.GameObjectVariables, name);

        public static FsmArray FindArrayVariable(PlayMakerFSM fsm, string name) => FindArrayVariable(fsm.Fsm, name);
        public static FsmArray FindArrayVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.ArrayVariables, name);

        public static FsmEnum FindEnumVariable(PlayMakerFSM fsm, string name) => FindEnumVariable(fsm.Fsm, name);
        public static FsmEnum FindEnumVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.EnumVariables, name);

        public static FsmObject FindObjectVariable(PlayMakerFSM fsm, string name) => FindObjectVariable(fsm.Fsm, name);
        public static FsmObject FindObjectVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.ObjectVariables, name);

        public static FsmMaterial FindMaterialVariable(PlayMakerFSM fsm, string name) => FindMaterialVariable(fsm.Fsm, name);
        public static FsmMaterial FindMaterialVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.MaterialVariables, name);

        public static FsmTexture FindTextureVariable(PlayMakerFSM fsm, string name) => FindTextureVariable(fsm.Fsm, name);
        public static FsmTexture FindTextureVariable(Fsm fsm, string name) => FindInVariableArray(fsm.Variables.TextureVariables, name);

        public static FsmFloat GetFloatVariable(PlayMakerFSM fsm, string name) => GetFloatVariable(fsm.Fsm, name);
        public static FsmFloat GetFloatVariable(Fsm fsm, string name) => FindFloatVariable(fsm, name) ?? AddFloatVariable(fsm, name);

        public static FsmInt GetIntVariable(PlayMakerFSM fsm, string name) => GetIntVariable(fsm.Fsm, name);
        public static FsmInt GetIntVariable(Fsm fsm, string name) => FindIntVariable(fsm, name) ?? AddIntVariable(fsm, name);

        public static FsmBool GetBoolVariable(PlayMakerFSM fsm, string name) => GetBoolVariable(fsm.Fsm, name);
        public static FsmBool GetBoolVariable(Fsm fsm, string name) => FindBoolVariable(fsm, name) ?? AddBoolVariable(fsm, name);

        public static FsmString GetStringVariable(PlayMakerFSM fsm, string name) => GetStringVariable(fsm.Fsm, name);
        public static FsmString GetStringVariable(Fsm fsm, string name) => FindStringVariable(fsm, name) ?? AddStringVariable(fsm, name);

        public static FsmVector2 GetVector2Variable(PlayMakerFSM fsm, string name) => GetVector2Variable(fsm.Fsm, name);
        public static FsmVector2 GetVector2Variable(Fsm fsm, string name) => FindVector2Variable(fsm, name) ?? AddVector2Variable(fsm, name);

        public static FsmVector3 GetVector3Variable(PlayMakerFSM fsm, string name) => GetVector3Variable(fsm.Fsm, name);
        public static FsmVector3 GetVector3Variable(Fsm fsm, string name) => FindVector3Variable(fsm, name) ?? AddVector3Variable(fsm, name);

        public static FsmRect GetRectVariable(PlayMakerFSM fsm, string name) => GetRectVariable(fsm.Fsm, name);
        public static FsmRect GetRectVariable(Fsm fsm, string name) => FindRectVariable(fsm, name) ?? AddRectVariable(fsm, name);

        public static FsmQuaternion GetQuaternionVariable(PlayMakerFSM fsm, string name) => GetQuaternionVariable(fsm.Fsm, name);
        public static FsmQuaternion GetQuaternionVariable(Fsm fsm, string name) => FindQuaternionVariable(fsm, name) ?? AddQuaternionVariable(fsm, name);

        public static FsmColor GetColorVariable(PlayMakerFSM fsm, string name) => GetColorVariable(fsm.Fsm, name);
        public static FsmColor GetColorVariable(Fsm fsm, string name) => FindColorVariable(fsm, name) ?? AddColorVariable(fsm, name);

        public static FsmGameObject GetGameObjectVariable(PlayMakerFSM fsm, string name) => GetGameObjectVariable(fsm.Fsm, name);
        public static FsmGameObject GetGameObjectVariable(Fsm fsm, string name) => FindGameObjectVariable(fsm, name) ?? AddGameObjectVariable(fsm, name);

        public static FsmArray GetArrayVariable(PlayMakerFSM fsm, string name) => GetArrayVariable(fsm.Fsm, name);
        public static FsmArray GetArrayVariable(Fsm fsm, string name) => FindArrayVariable(fsm, name) ?? AddArrayVariable(fsm, name);

        public static FsmEnum GetEnumVariable(PlayMakerFSM fsm, string name) => GetEnumVariable(fsm.Fsm, name);
        public static FsmEnum GetEnumVariable(Fsm fsm, string name) => FindEnumVariable(fsm, name) ?? AddEnumVariable(fsm, name);

        public static FsmObject GetObjectVariable(PlayMakerFSM fsm, string name) => GetObjectVariable(fsm.Fsm, name);
        public static FsmObject GetObjectVariable(Fsm fsm, string name) => FindObjectVariable(fsm, name) ?? AddObjectVariable(fsm, name);

        public static FsmMaterial GetMaterialVariable(PlayMakerFSM fsm, string name) => GetMaterialVariable(fsm.Fsm, name);
        public static FsmMaterial GetMaterialVariable(Fsm fsm, string name) => FindMaterialVariable(fsm, name) ?? AddMaterialVariable(fsm, name);

        public static FsmTexture GetTextureVariable(PlayMakerFSM fsm, string name) => GetTextureVariable(fsm.Fsm, name);
        public static FsmTexture GetTextureVariable(Fsm fsm, string name) => FindTextureVariable(fsm, name) ?? AddTextureVariable(fsm, name);

        public static void MakeLog(PlayMakerFSM fsm, bool additionalLogging = false) => MakeLog(fsm.Fsm, additionalLogging);

        public static void MakeLog(Fsm fsm, bool additionalLogging = false)
        {
            if (fsm == null) return;
            EnsureFsmLoaded(fsm);
            var states = fsm.States;
            if (states == null) return;
            for (int i = 0; i < states.Length; i++)
            {
                var st = states[i];
                if (st == null) continue;
                var acts = SilksongMobileAPI.PlayMakerHybridCLRBridge.GetStateActionsSafe(st);
                for (int j = acts.Length - 1; j >= 0; j--)
                {
                    InsertAction(fsm, st.Name, new LogAction { Text = $"{j} - {acts[j]?.GetType().FullName}" }, j);
                    if (additionalLogging)
                    {
                        InsertMethod(fsm, st.Name, () => Log(fsm, fsm.GameObjectName), j + 1, false);
                    }
                }
            }
        }

        public static void Log(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            Log(fsm.Fsm, fsm.gameObject != null ? fsm.gameObject.name : "");
        }

        public static void Log(Fsm fsm, string gameObjectName)
        {
            if (fsm == null) return;
            Log($"FSM \"{fsm.Name}\" on \"{gameObjectName}\"");
        }

        public static void Log(string goName, string fsmName, string part, string msg)
        {
            Log(string.Concat("[", goName, "]:[", fsmName, "]:[", part, "]"), msg);
        }

        public static void Log(string msg)
        {
            Log("[Core]:[FsmUtil]:[FsmUtil]", msg);
        }

        public static void Log(string pre, string post)
        {
            InternalLogger.Log(pre + " - " + post);
        }
    }
}

// ==============================================================================
// Ported from Hollow Knight Mobile API (Hollow Knight.zip):
// 1. AotContractResolver & ShouldSerializeContractResolver & JsonRuntimeFix
// 2. Vector2Converter & Vector3Converter (Newtonsoft.Json Unity struct safety)
// 3. AndroidCrashReporter (Persistent Crash.log + threaded Unity log & UnhandledException capture)
// 4. Modding.ReflectionHelper, Modding.CanvasUtil, Modding.KeybindUtil
// ==============================================================================
namespace Modding.Patches
{
    public class AotContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        public static readonly AotContractResolver Instance = new AotContractResolver();

        protected override Newtonsoft.Json.Serialization.IValueProvider CreateMemberValueProvider(System.Reflection.MemberInfo member)
        {
            return new Newtonsoft.Json.Serialization.ReflectionValueProvider(member);
        }
    }

    public class ShouldSerializeContractResolver : AotContractResolver
    {
        public static new readonly ShouldSerializeContractResolver Instance = new ShouldSerializeContractResolver();

        protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            Newtonsoft.Json.MemberSerialization memberSerialization)
        {
            Newtonsoft.Json.Serialization.JsonProperty prop = base.CreateProperty(member, memberSerialization);
            if (member?.DeclaringType?.Assembly?.FullName != null &&
                member.DeclaringType.Assembly.FullName.StartsWith("UnityEngine", System.StringComparison.Ordinal))
            {
                prop.Ignored = true;
            }
            return prop;
        }
    }

    public static class JsonRuntimeFix
    {
        private static bool _applied;

        public static void ApplyAotDefaults()
        {
            if (_applied) return;
            _applied = true;
            try
            {
                Newtonsoft.Json.JsonConvert.DefaultSettings = () =>
                {
                    var settings = new Newtonsoft.Json.JsonSerializerSettings
                    {
                        ContractResolver = ShouldSerializeContractResolver.Instance,
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None,
                        Formatting = Newtonsoft.Json.Formatting.Indented,
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                        MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore
                    };
                    settings.Converters.Add(new Modding.Converters.Vector2Converter());
                    settings.Converters.Add(new Modding.Converters.Vector3Converter());
                    return settings;
                };
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo(
                    "[JsonRuntimeFix] Applied Hollow Knight Mobile API Newtonsoft.Json AOT defaults (ReflectionValueProvider + Vector2/Vector3 converters).");
            }
            catch (System.Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("JsonRuntimeFix", "ApplyAotDefaults", ex);
            }
        }
    }
}

namespace Modding.Converters
{
    public abstract class JsonConverter<TClass> : Newtonsoft.Json.JsonConverter
    {
        public override bool CanConvert(System.Type objectType)
        {
            return typeof(TClass) == objectType;
        }

        public override object ReadJson(
            Newtonsoft.Json.JsonReader reader,
            System.Type objectType,
            object existingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (typeof(TClass) == objectType)
            {
                var token = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
                reader.Read();
                while (reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                {
                    string name = (string)reader.Value;
                    reader.Read();
                    token[name] = reader.Value;
                    reader.Read();
                }
                return ReadJson(token, existingValue);
            }
            return serializer.Deserialize(reader);
        }

        public override void WriteJson(
            Newtonsoft.Json.JsonWriter writer,
            object value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            writer.WriteStartObject();
            WriteJson(writer, (TClass)value);
            writer.WriteEndObject();
        }

        public abstract TClass ReadJson(System.Collections.Generic.Dictionary<string, object> token, object existingValue);
        public abstract void WriteJson(Newtonsoft.Json.JsonWriter writer, TClass value);
    }

    public class Vector2Converter : JsonConverter<UnityEngine.Vector2>
    {
        public override UnityEngine.Vector2 ReadJson(System.Collections.Generic.Dictionary<string, object> token, object existingValue)
        {
            float x = token.TryGetValue("x", out var xv) && xv != null ? System.Convert.ToSingle(xv) : 0f;
            float y = token.TryGetValue("y", out var yv) && yv != null ? System.Convert.ToSingle(yv) : 0f;
            return new UnityEngine.Vector2(x, y);
        }

        public override void WriteJson(Newtonsoft.Json.JsonWriter writer, UnityEngine.Vector2 value)
        {
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
        }
    }

    public class Vector3Converter : JsonConverter<UnityEngine.Vector3>
    {
        public override UnityEngine.Vector3 ReadJson(System.Collections.Generic.Dictionary<string, object> token, object existingValue)
        {
            float x = token.TryGetValue("x", out var xv) && xv != null ? System.Convert.ToSingle(xv) : 0f;
            float y = token.TryGetValue("y", out var yv) && yv != null ? System.Convert.ToSingle(yv) : 0f;
            float z = token.TryGetValue("z", out var zv) && zv != null ? System.Convert.ToSingle(zv) : 0f;
            return new UnityEngine.Vector3(x, y, z);
        }

        public override void WriteJson(Newtonsoft.Json.JsonWriter writer, UnityEngine.Vector3 value)
        {
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
            writer.WritePropertyName("z");
            writer.WriteValue(value.z);
        }
    }
}

namespace Modding
{
    public static class AndroidCrashReporter
    {
        private static int _installed;
        public static string CrashLogPath => SilksongMobileAPI.SilksongMobileAPIPlugin.DiagnosticLogPath;

        public static void Initialize()
        {
            if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0)
                return;

            try
            {
                // Clean up any old standalone Crash.log so only LogOutput.log and ModLog.log exist
                string persDir = UnityEngine.Application.persistentDataPath;
                if (!string.IsNullOrEmpty(persDir))
                {
                    string oldCrash = System.IO.Path.Combine(persDir, "Crash.log");
                    if (System.IO.File.Exists(oldCrash)) { try { System.IO.File.Delete(oldCrash); } catch { } }
                }
                if (!string.IsNullOrEmpty(BepInEx.Paths.BepInExRootPath))
                {
                    string oldCrash2 = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "Crash.log");
                    if (System.IO.File.Exists(oldCrash2)) { try { System.IO.File.Delete(oldCrash2); } catch { } }
                }

                // Do not emit direct CIL calls to Application.logMessageReceivedThreaded or
                // AppDomain.CurrentDomain.UnhandledException because Unity 6000 Android IL2CPP strips
                // unreferenced event accessors, causing HybridCLR HiTransform::Transform to throw
                // MissingMethodException when compiling Initialize().
                SilksongMobileAPI.SilksongMobileAPIPlugin.InstallCrashAndLogcatCapture();
                try
                {
                    var threadedEvt = typeof(UnityEngine.Application).GetEvent(
                        "logMessageReceivedThreaded",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var addMethod = threadedEvt?.GetAddMethod();
                    if (addMethod != null)
                    {
                        var cb = new UnityEngine.Application.LogCallback(HandleUnityLog);
                        addMethod.Invoke(null, new object[] { cb });
                    }
                }
                catch { }

                SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo(
                    "[AndroidCrashReporter] Installed IL2CPP-safe Unity log & crash capture hooks -> routed to ModLog.log");
            }
            catch (System.Exception ex)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("AndroidCrashReporter", "Initialize", ex);
            }
        }

        private static void HandleUnityLog(string message, string stackTrace, UnityEngine.LogType type)
        {
            if (type != UnityEngine.LogType.Error &&
                type != UnityEngine.LogType.Exception &&
                type != UnityEngine.LogType.Assert)
                return;
            if (string.IsNullOrEmpty(message)) return;
            if (message.IndexOf("Cannot set the parent of the GameObject", System.StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Sprite not found attached to tk2dSpriteAnimator", System.StringComparison.Ordinal) >= 0 ||
                message.IndexOf("get_fsm: Fsm not initialized", System.StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Could not parse crest id ", System.StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Could not load crest ", System.StringComparison.Ordinal) >= 0)
                return;

            SilksongMobileAPI.SilksongMobileAPIPlugin.RecordCrashOrThreadedError(
                type == UnityEngine.LogType.Exception ? "UNITY_EXCEPTION" : "UNITY_ERROR",
                message,
                stackTrace);
        }

        public static void Write(string line)
        {
            SilksongMobileAPI.SilksongMobileAPIPlugin.RecordCrashOrThreadedError("CRASH_REPORTER", line, null);
        }
    }

    public static class ReflectionHelper
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.FieldInfo>> Fields =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.FieldInfo>>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.FieldInfo, System.Delegate> FieldGetters =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.FieldInfo, System.Delegate>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.FieldInfo, System.Delegate> FieldSetters =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.FieldInfo, System.Delegate>();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.PropertyInfo>> Properties =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.PropertyInfo>>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.PropertyInfo, System.Delegate> PropertyGetters =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.PropertyInfo, System.Delegate>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.PropertyInfo, System.Delegate> PropertySetters =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.PropertyInfo, System.Delegate>();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.MethodInfo>> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.MethodInfo>>();

        private static bool _preloaded;

        private const System.Reflection.BindingFlags AllFlags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

        private static void CacheFields<T>()
        {
            System.Type t = typeof(T);
            var map = Fields.GetOrAdd(t, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.FieldInfo>(System.StringComparer.Ordinal));
            foreach (var f in t.GetFields(AllFlags))
            {
                if (f != null) map[f.Name] = f;
            }
        }

        public static void PreloadCommonTypes()
        {
            if (_preloaded) return;
            _preloaded = true;
            // Sequential preloading: System.Threading.Tasks.Parallel::Invoke is stripped in Silksong Android IL2CPP!
            try { CacheFields<PlayerData>(); } catch { }
            try { CacheFields<HeroController>(); } catch { }
            try { CacheFields<HeroControllerStates>(); } catch { }
            try { CacheFields<GameManager>(); } catch { }
            try { CacheFields<UIManager>(); } catch { }
            try { CacheFields<HealthManager>(); } catch { }
        }

        public static System.Reflection.FieldInfo GetFieldInfo(System.Type t, string name, bool instance = true)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            var map = Fields.GetOrAdd(t, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.FieldInfo>(System.StringComparer.Ordinal));
            return map.GetOrAdd(name, n => t.GetField(n, AllFlags));
        }

        public static System.Reflection.PropertyInfo GetPropertyInfo(System.Type t, string name, bool instance = true)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            var map = Properties.GetOrAdd(t, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.PropertyInfo>(System.StringComparer.Ordinal));
            return map.GetOrAdd(name, n => t.GetProperty(n, AllFlags));
        }

        public static System.Reflection.MethodInfo GetMethodInfo(System.Type t, string name, bool instance = true)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            var map = Methods.GetOrAdd(t, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.MethodInfo>(System.StringComparer.Ordinal));
            return map.GetOrAdd(name, n => t.GetMethod(n, AllFlags));
        }

        private static System.Delegate GetInstanceFieldGetter<TType, TField>(System.Reflection.FieldInfo fi)
        {
            return FieldGetters.GetOrAdd(fi, f => new System.Func<TType, TField>(obj => (TField)f.GetValue(obj)));
        }

        private static System.Delegate GetStaticFieldGetter<TField>(System.Reflection.FieldInfo fi)
        {
            return FieldGetters.GetOrAdd(fi, f => new System.Func<TField>(() => (TField)f.GetValue(null)));
        }

        private static System.Delegate GetInstanceFieldSetter<TType, TField>(System.Reflection.FieldInfo fi)
        {
            return FieldSetters.GetOrAdd(fi, f => new System.Action<TType, TField>((obj, val) => f.SetValue(obj, val)));
        }

        private static System.Delegate GetStaticFieldSetter<TField>(System.Reflection.FieldInfo fi)
        {
            return FieldSetters.GetOrAdd(fi, f => new System.Action<TField>(val => f.SetValue(null, val)));
        }

        public static TCast GetField<TObject, TField, TCast>(TObject obj, string name, TCast @default = default)
        {
            var fi = GetFieldInfo(typeof(TObject), name, true);
            return fi == null ? @default : (TCast)(object)((System.Func<TObject, TField>)GetInstanceFieldGetter<TObject, TField>(fi))(obj);
        }

        public static TField GetField<TObject, TField>(TObject obj, string name)
        {
            var fi = GetFieldInfo(typeof(TObject), name, true);
            return fi != null ? ((System.Func<TObject, TField>)GetInstanceFieldGetter<TObject, TField>(fi))(obj) : default;
        }

        public static TField GetField<TType, TField>(string name)
        {
            var fi = GetFieldInfo(typeof(TType), name, false);
            return fi != null ? ((System.Func<TField>)GetStaticFieldGetter<TField>(fi))() : default;
        }

        public static TField GetField<TField>(System.Type type, string name)
        {
            var fi = GetFieldInfo(type, name, false);
            return fi != null ? ((System.Func<TField>)GetStaticFieldGetter<TField>(fi))() : default;
        }

        public static void SetFieldSafe<TObject, TField>(TObject obj, string name, TField value)
        {
            var fi = GetFieldInfo(typeof(TObject), name, true);
            if (fi != null) ((System.Action<TObject, TField>)GetInstanceFieldSetter<TObject, TField>(fi))(obj, value);
        }

        public static void SetField<TObject, TField>(TObject obj, string name, TField value)
        {
            var fi = GetFieldInfo(typeof(TObject), name, true);
            if (fi != null) ((System.Action<TObject, TField>)GetInstanceFieldSetter<TObject, TField>(fi))(obj, value);
        }

        public static void SetField<TType, TField>(string name, TField value)
        {
            var fi = GetFieldInfo(typeof(TType), name, false);
            if (fi != null) ((System.Action<TField>)GetStaticFieldSetter<TField>(fi))(value);
        }

        public static void SetField<TField>(System.Type type, string name, TField value)
        {
            var fi = GetFieldInfo(type, name, false);
            if (fi != null) ((System.Action<TField>)GetStaticFieldSetter<TField>(fi))(value);
        }

        public static TProp GetProperty<TObject, TProp>(TObject obj, string name)
        {
            var pi = GetPropertyInfo(typeof(TObject), name, true);
            if (pi == null) return default;
            var del = (System.Func<TObject, TProp>)PropertyGetters.GetOrAdd(pi, p =>
            {
                try
                {
                    var getter = p.GetGetMethod(true);
                    if (getter != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Func<TObject, TProp>), null, getter, false);
                        if (d != null) return d;
                    }
                }
                catch { }
                return new System.Func<TObject, TProp>(o => (TProp)p.GetValue(o, null));
            });
            return del(obj);
        }

        public static void SetProperty<TObject, TProp>(TObject obj, string name, TProp value)
        {
            var pi = GetPropertyInfo(typeof(TObject), name, true);
            if (pi == null) return;
            var del = (System.Action<TObject, TProp>)PropertySetters.GetOrAdd(pi, p =>
            {
                try
                {
                    var setter = p.GetSetMethod(true);
                    if (setter != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Action<TObject, TProp>), null, setter, false);
                        if (d != null) return d;
                    }
                }
                catch { }
                return new System.Action<TObject, TProp>((o, v) => p.SetValue(o, v, null));
            });
            del(obj, value);
        }

        public static object CallMethod<TObject>(TObject obj, string name, params object[] args)
        {
            var mi = GetMethodInfo(typeof(TObject), name, true);
            return mi?.Invoke(obj, args);
        }
    }

    // Ported from Hollow Knight Mobile API (API.zip): HybridCLR stack sizing + PlayMaker2D FSM Preprocessing
    public static class HybridCLRRuntimeTuner
    {
        private static bool _tuned;

        public static void TuneInterpreterStacks()
        {
            if (_tuned) return;
            _tuned = true;
            try
            {
                System.Type runtimeApiType = null;
                foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    runtimeApiType = asm.GetType("HybridCLR.RuntimeApi", false);
                    if (runtimeApiType != null) break;
                }
                if (runtimeApiType != null)
                {
                    const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var setObjStack = runtimeApiType.GetMethod("SetInterpreterThreadObjectStackSize", S);
                    var setFrameStack = runtimeApiType.GetMethod("SetInterpreterThreadFrameStackSize", S);
                    setObjStack?.Invoke(null, new object[] { 1024 * 128 });
                    setFrameStack?.Invoke(null, new object[] { 1024 * 32 });
                    SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo(
                        "[HKMobileAPI.HybridCLRInitializer] Tuned HybridCLR interpreter thread stacks (ObjectStack=131072, FrameStack=32768).");
                }
            }
            catch { }
        }
    }

    public static class PlayMakerPrefabPreprocessor
    {
        private static readonly System.Collections.Generic.HashSet<int> _processedFsmIds = new System.Collections.Generic.HashSet<int>();

        public static int PreprocessGameObjectFsms(UnityEngine.GameObject root)
        {
            if (root == null) return 0;
            int count = 0;
            try
            {
                var fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
                if (fsms == null) return 0;
                for (int i = 0; i < fsms.Length; i++)
                {
                    var fsm = fsms[i];
                    if (fsm == null) continue;
                    int id = fsm.GetInstanceID();
                    if (!_processedFsmIds.Add(id)) continue;
                    try
                    {
                        fsm.Preprocess();
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        public static int PreprocessAllModPrefabFsms()
        {
            int count = 0;
            try
            {
                var fsms = UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
                if (fsms == null) return 0;
                for (int i = 0; i < fsms.Length; i++)
                {
                    var fsm = fsms[i];
                    if (fsm == null || fsm.gameObject == null) continue;
                    var sc = fsm.gameObject.scene;
                    if (sc.IsValid() && sc.isLoaded) continue; // Only preprocess unloaded/DontDestroyOnLoad prefab templates
                    int id = fsm.GetInstanceID();
                    if (!_processedFsmIds.Add(id)) continue;
                    try
                    {
                        fsm.Preprocess();
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class MaybeNullWhenAttribute : Attribute
    {
        public bool ReturnValue { get; }
        public MaybeNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
    }
}

namespace Unity.Mathematics
{
    public static class math
    {
        public static int abs(int x) => Math.Abs(x);
        public static float abs(float x) => Math.Abs(x);
        public static double abs(double x) => Math.Abs(x);
        public static int min(int a, int b) => Math.Min(a, b);
        public static float min(float a, float b) => Math.Min(a, b);
        public static int max(int a, int b) => Math.Max(a, b);
        public static float max(float a, float b) => Math.Max(a, b);
        public static float clamp(float x, float a, float b) => Mathf.Clamp(x, a, b);
        public static int clamp(int x, int a, int b) => Mathf.Clamp(x, a, b);
    }
}

namespace UnityStandardAssets.ImageEffects
{
    public class FastNoise : MonoBehaviour
    {
        public float intensityMultiplier = 0.25f;
        public float generalIntensity = 0.5f;
        public float blackIntensity = 1f;
        public float whiteIntensity = 1f;
        public float midGrey = 0.2f;
        public bool monochrome = false;
        public FilterMode filterMode = FilterMode.Bilinear;
        public bool CheckResources() => false;
    }
}

namespace TeamCherry.NestedFadeGroup
{
    public class NestedFadeGroupTimedFader : MonoBehaviour
    {
        public float fadeTime = 0.5f;
        public void FadeTo(float alpha, float duration) { }
    }
}

namespace HutongGames.PlayMaker.Actions
{
    public class FadeNestedFadeGroup : FsmStateAction
    {
        public FsmOwnerDefault target;
        public FsmFloat targetAlpha = 1f;
        public FsmFloat fadeTime = 0.25f;
        public FsmEvent finishEvent;
        public override void Reset()
        {
            target = null;
            targetAlpha = 1f;
            fadeTime = 0.25f;
            finishEvent = null;
        }
        public override void OnEnter()
        {
            if (finishEvent != null && Fsm != null)
                Fsm.Event(finishEvent);
            Finish();
        }
    }

    public class FadeNestedFadeGroupV3 : FsmStateAction
    {
        public FsmOwnerDefault Target;
        public FsmFloat TargetAlpha = 1f;
        public FsmFloat FadeTime = 0.25f;
        public FsmEvent FinishEvent;
        public override void Reset()
        {
            Target = null;
            TargetAlpha = 1f;
            FadeTime = 0.25f;
            FinishEvent = null;
        }
        public override void OnEnter()
        {
            if (FinishEvent != null && Fsm != null)
                Fsm.Event(FinishEvent);
            Finish();
        }
    }
}

namespace NAudio.Wave
{
    public interface IWaveProvider
    {
        int Read(byte[] buffer, int offset, int count);
    }

    public class MediaFoundationReader : IDisposable, IWaveProvider
    {
        public MediaFoundationReader(string file)
        {
            throw new NotSupportedException("Windows MediaFoundation is not used on Android; use .ogg/.wav/.mp3 tracks.");
        }
        public int Read(byte[] buffer, int offset, int count) => 0;
        public void Dispose() { }
    }

    public static class MediaFoundationEncoder
    {
        public static void EncodeToMp3(IWaveProvider inputProvider, string outputFile, int desiredBitRate = 192000)
        {
            throw new NotSupportedException("Windows MediaFoundation is not used on Android.");
        }
    }
}

namespace DanielSteginkUtils.Utilities
{
    public static class ClassIntegrations
    {
        public static T GetField<T>(object instance, string fieldName, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName)) return default;
            try
            {
                var f = HarmonyLib.AccessTools.Field(instance.GetType(), fieldName);
                if (f != null && f.GetValue(instance) is T val) return val;
            }
            catch { }
            return default;
        }

        public static void SetField<I>(I instance, string fieldName, object value, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName)) return;
            try
            {
                var t = instance is Type typeObj ? typeObj : instance.GetType();
                var f = HarmonyLib.AccessTools.Field(t, fieldName);
                if (f != null)
                    f.SetValue(instance is Type ? null : (object)instance, value);
            }
            catch { }
        }
    }
}

namespace DanielSteginkUtils.Utilities
{
    public static class NotchCosts
    {
        public static float NailDamagePerNotch() => 0.10f;
        public static float SpellDamagePerNotch() => 0.15f;
        public static float PassiveHealTime() => 30f;
        public static float PassiveSilkTime() => 8f;
    }
}

namespace DanielSteginkUtils.ExternalFiles
{
    public static class GetSprite
    {
        public static Sprite GetLocalSprite(string filePath, float pixelsPerUnit = 100f)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(filePath);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (UnityEngine.ImageConversion.LoadImage(tex, bytes))
                        return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
                }
            }
            catch { }
            var fallback = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            return Sprite.Create(fallback, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        }
    }

    public static class GetAudioClip
    {
        public static AudioClip GetAudioClipFromAssembly(System.Reflection.Assembly asm, string resourceName, bool isCompressed = false)
        {
            if (asm == null || string.IsNullOrEmpty(resourceName)) return null;
            try
            {
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s != null) return GetAudioClipFromStream(s, resourceName, isCompressed);
                }
            }
            catch { }
            return AudioClip.Create(resourceName ?? "dsu_asm_clip", 4410, 1, 44100, false);
        }

        public static AudioClip GetAudioClipFromStream(System.IO.Stream stream, string clipName, bool isCompressed = false)
        {
            if (stream == null) return null;
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    stream.CopyTo(ms);
                    var clip = SilksongMobileAPI.AssetHelperBridge.TryParseWavBytesToAudioClip(ms.ToArray(), clipName ?? "dsu_clip");
                    if (clip != null) return clip;
                }
            }
            catch { }
            return AudioClip.Create(clipName ?? "dsu_clip", 4410, 1, 44100, false);
        }
    }
}

namespace DanielSteginkUtils.Helpers
{
    public static class Needolin
    {
        private static AudioClip _defaultClip;
        public static PlayMakerFSM NeedolinFsm
        {
            get
            {
                try
                {
                    if (HeroController.instance != null)
                        return FSMUtility.LocateMyFSM(HeroController.instance.gameObject, "Silk Specials");
                }
                catch { }
                return null;
            }
        }
        public static AudioClip DefaultClip => _defaultClip;
        public static void SetNewDefaultClip(AudioClip clip) { _defaultClip = clip; }
        public static void SetNewAudioClip(AudioClip clip, PlayMakerFSM fsm = null, bool resetPitch = true) { if (clip != null) _defaultClip = clip; }
        public static void ResetAudioClip(PlayMakerFSM fsm = null, bool useDefault = true, bool resetPitch = true) { }
    }

    public static class DamageEnemy
    {
        public static void DealDamage(HealthManager enemy, int damage, AttackTypes attackType = AttackTypes.Generic, bool ignoreInvincible = false)
        {
            if (enemy == null || enemy.isDead || damage <= 0) return;
            try { enemy.ApplyExtraDamage(damage); } catch { }
        }
    }

    public static class GetEnemy
    {
        public static System.Collections.Generic.List<HealthManager> GetEnemies(float maxRange = 999f)
        {
            var list = new System.Collections.Generic.List<HealthManager>();
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<HealthManager>();
                var hc = HeroController.instance;
                for (int i = 0; i < all.Length; i++)
                {
                    var hm = all[i];
                    if (hm == null || hm.isDead || !hm.gameObject.activeInHierarchy) continue;
                    if (hc != null && maxRange < 900f && Vector2.Distance(hc.transform.position, hm.transform.position) > maxRange)
                        continue;
                    list.Add(hm);
                }
            }
            catch { }
            return list;
        }
    }

    public static class GetTools
    {
        public static System.Collections.Generic.List<ToolItem> GetEquippedTools()
        {
            var list = new System.Collections.Generic.List<ToolItem>();
            try
            {
                var all = ToolItemManager.GetAllTools();
                if (all != null)
                {
                    foreach (var t in all)
                    {
                        if (t != null && t.IsEquipped) list.Add(t);
                    }
                }
            }
            catch { }
            return list;
        }
    }
}

namespace DanielSteginkUtils.Utilities
{
    public static class Components
    {
        public static void RemoveComponent<T>(GameObject go) where T : Component
        {
            if (go == null) return;
            var c = go.GetComponent<T>();
            if (c != null) UnityEngine.Object.Destroy(c);
        }
    }
}

namespace BepInExUtils.Interfaces
{
    public interface IBepInUtils { }
}

namespace BepInExUtils.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BepInUtilsAttribute : Attribute
    {
        public string GUID { get; }
        public string Name { get; }
        public string Version { get; }
        public BepInUtilsAttribute(string guid, string name, string version) { GUID = guid; Name = name; Version = version; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class AccessExtensionsAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class AccessFieldAttribute<T> : Attribute
    {
        public AccessFieldAttribute(string name, string fieldName = "", T defaultValue = default, string typeName = "") { }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class AccessInstanceAttribute<T> : Attribute
    {
        public AccessInstanceAttribute(string name, string typeName = "") { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class AccessMethodAttribute : Attribute
    {
        public AccessMethodAttribute(string name, string methodName = "", string typeName = "") { }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ConfigBindAttribute<T> : Attribute
    {
        public ConfigBindAttribute(string key, string section, T defaultValue, string description, T minValue = default, T maxValue = default) { }
        public ConfigBindAttribute(string key, string section, T defaultValue, string description) { }
    }
}

namespace BepInExUtils.Logging
{
    public class Logger
    {
        private readonly string _name;
        public Logger(string name = "BepInExUtils") { _name = name ?? "BepInExUtils"; }
        public void Info(object msg) => SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticInfo($"[{_name}] {msg}");
        public void Debug(object msg) { }
        public void Warn(object msg) => SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticWarn($"[{_name}] {msg}");
        public void Error(object msg) => SilksongMobileAPI.SilksongMobileAPIPlugin.LogDiagnosticError($"[{_name}] {msg}");
    }
}

namespace BepInExUtils.Proxy
{
    public class ClassProxy
    {
        public object Native { get; protected set; }
        public ClassProxy(string typeName) { }
        public ClassProxy(object native, string typeName = "") { Native = native; }
    }
}

namespace BepInExUtils.Extensions
{
    public static class ObjectExtensions
    {
        public static object MethodInvoke(this object obj, string methodName, params object[] args)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            try
            {
                var t = obj as Type ?? obj.GetType();
                var mi = HarmonyLib.AccessTools.Method(t, methodName);
                return mi?.Invoke(obj is Type ? null : obj, args);
            }
            catch { return null; }
        }

        public static T GetFieldValue<T>(this object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return default;
            try
            {
                var t = obj as Type ?? obj.GetType();
                var f = HarmonyLib.AccessTools.Field(t, name);
                if (f != null)
                {
                    var val = f.GetValue(obj is Type ? null : obj);
                    if (val is T typed) return typed;
                }
                var p = HarmonyLib.AccessTools.Property(t, name);
                if (p != null)
                {
                    var val = p.GetValue(obj is Type ? null : obj, null);
                    if (val is T typed) return typed;
                }
            }
            catch { }
            return default;
        }

        public static void SetFieldValue<T>(this object obj, string name, T value)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return;
            try
            {
                var t = obj as Type ?? obj.GetType();
                var f = HarmonyLib.AccessTools.Field(t, name);
                if (f != null)
                {
                    f.SetValue(obj is Type ? null : obj, value);
                    return;
                }
                var p = HarmonyLib.AccessTools.Property(t, name);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(obj is Type ? null : obj, value, null);
                }
            }
            catch { }
        }
    }
}
namespace BepInExUtils.Extensions
{
    public static class GameObjectExtensions
    {
        public static string FullName(this GameObject go)
        {
            if (go == null) return "<null>";
            string path = go.name;
            Transform t = go.transform.parent;
            while (t != null) { path = t.name + "/" + path; t = t.parent; }
            return path;
        }
    }

    public static class MonoBehaviourExtensions
    {
        public static string FullName(this MonoBehaviour mb) => mb != null && mb.gameObject != null ? mb.gameObject.FullName() : "<null>";
    }

    public static class TransformExtensions
    {
        public static string FullName(this Transform tr) => tr != null && tr.gameObject != null ? tr.gameObject.FullName() : "<null>";
    }

    public static class ListExtensions
    {
        public static T GetValueOrDefault<T>(this System.Collections.Generic.IList<T> list, int index, T defaultValue = default)
        {
            if (list == null || index < 0 || index >= list.Count) return defaultValue;
            return list[index];
        }
    }

    public static class ObjectExtensionsMethodShim
    {
        public static object MethodInvoke(this object obj, string methodName, params object[] args)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            try
            {
                var t = obj as Type ?? obj.GetType();
                var mi = HarmonyLib.AccessTools.Method(t, methodName);
                return mi?.Invoke(obj is Type ? null : obj, args);
            }
            catch { return null; }
        }
    }
}

namespace HarmonyLib.Tools
{
    public static class HarmonyFileLog
    {
        public static bool Enabled { get; set; }
        public static string FileWriterPath { get; set; } = "";
    }
}

namespace BepInEx.Bootstrap
{
    public static class TypeLoader
    {
        public static object ReaderParameters;
    }
}

namespace GenericVariableExtension
{
    public static class VariableExtensionsGeneric
    {
        public static T GetVariable<T>(this HutongGames.PlayMaker.Fsm fsm, string name) where T : HutongGames.PlayMaker.NamedVariable
        {
            if (fsm == null || fsm.Variables == null || string.IsNullOrEmpty(name)) return null;
            try { return fsm.Variables.GetVariable(name) as T; } catch { return null; }
        }

        public static T GetVariable<T>(this PlayMakerFSM fsm, string name) where T : HutongGames.PlayMaker.NamedVariable
        {
            return fsm != null && fsm.Fsm != null ? GetVariable<T>(fsm.Fsm, name) : null;
        }
    }
}

namespace System.IO.Enumeration
{
    public struct FileSystemEntry
    {
        internal string _fullPath;
        internal bool _isDirectory;
        internal long _length;
        internal DateTimeOffset _lastWriteTimeUtc;

        public bool IsDirectory => _isDirectory;
        public long Length => _length;
        public DateTimeOffset LastWriteTimeUtc => _lastWriteTimeUtc;
        public string ToFullPath() => _fullPath ?? "";
    }

    public class FileSystemEnumerable<TResult> : System.Collections.Generic.IEnumerable<TResult>
    {
        public delegate bool FindPredicate(ref FileSystemEntry entry);
        public delegate TResult FindTransform(ref FileSystemEntry entry);

        private readonly string _directory;
        private readonly FindTransform _transform;
        private readonly System.IO.EnumerationOptions _options;

        public FindPredicate ShouldIncludePredicate { get; set; }
        public FindPredicate ShouldRecursePredicate { get; set; }

        public FileSystemEnumerable(string directory, FindTransform transform, System.IO.EnumerationOptions options = null)
        {
            _directory = directory;
            _transform = transform;
            _options = options;
        }

        public System.Collections.Generic.IEnumerator<TResult> GetEnumerator()
        {
            var results = new System.Collections.Generic.List<TResult>();
            if (!string.IsNullOrEmpty(_directory) && System.IO.Directory.Exists(_directory) && _transform != null)
            {
                try
                {
                    var opt = (_options != null && _options.RecurseSubdirectories)
                        ? System.IO.SearchOption.AllDirectories
                        : System.IO.SearchOption.TopDirectoryOnly;
                    foreach (var p in System.IO.Directory.GetFileSystemEntries(_directory, "*", opt))
                    {
                        bool isDir = System.IO.Directory.Exists(p);
                        long len = 0;
                        DateTimeOffset wt = DateTimeOffset.UtcNow;
                        try
                        {
                            if (isDir) wt = System.IO.Directory.GetLastWriteTimeUtc(p);
                            else
                            {
                                var fi = new System.IO.FileInfo(p);
                                len = fi.Length;
                                wt = fi.LastWriteTimeUtc;
                            }
                        }
                        catch { }
                        var entry = new FileSystemEntry { _fullPath = p, _isDirectory = isDir, _length = len, _lastWriteTimeUtc = wt };
                        if (ShouldIncludePredicate == null || ShouldIncludePredicate(ref entry))
                            results.Add(_transform(ref entry));
                    }
                }
                catch { }
            }
            return results.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Microsoft.CSharp.RuntimeBinder
{
    [Flags]
    public enum CSharpArgumentInfoFlags { None = 0, UseCompileTimeType = 1, Constant = 2, NamedArgument = 4, IsRef = 8, IsOut = 16, IsStaticType = 32 }
    [Flags]
    public enum CSharpBinderFlags { None = 0, CheckedContext = 1, InvokeSimpleName = 2, InvokeSpecialName = 4, BinaryOperationLogical = 8, ConvertExplicit = 16, ConvertArrayIndex = 32, ResultIndexed = 64, ValueFromCompoundAssignment = 128, ResultDiscarded = 256 }

    public sealed class CSharpArgumentInfo
    {
        public static CSharpArgumentInfo Create(CSharpArgumentInfoFlags flags, string name) => new CSharpArgumentInfo();
    }

    public static class Binder
    {
        public static System.Runtime.CompilerServices.CallSiteBinder GetMember(CSharpBinderFlags flags, string name, Type context, System.Collections.Generic.IEnumerable<CSharpArgumentInfo> argumentInfo) => null;
        public static System.Runtime.CompilerServices.CallSiteBinder SetMember(CSharpBinderFlags flags, string name, Type context, System.Collections.Generic.IEnumerable<CSharpArgumentInfo> argumentInfo) => null;
        public static System.Runtime.CompilerServices.CallSiteBinder InvokeMember(CSharpBinderFlags flags, string name, System.Collections.Generic.IEnumerable<Type> typeArguments, Type context, System.Collections.Generic.IEnumerable<CSharpArgumentInfo> argumentInfo) => null;
        public static System.Runtime.CompilerServices.CallSiteBinder Convert(CSharpBinderFlags flags, Type type, Type context) => null;
    }
}

namespace DebugMod.Hitbox
{
    public class HitboxRender : MonoBehaviour
    {
        public void UpdateHitbox(GameObject go) { }
    }
}

namespace CanvasUtil
{
    public struct RectData
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 AnchorPivot;
        public Vector2 AnchorPosition;
        public Vector2 RectSizeDelta;

        public RectData(Vector2 sizeDelta, Vector2 anchorPosition)
            : this(sizeDelta, anchorPosition, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)) { }

        public RectData(Vector2 sizeDelta, Vector2 anchorPosition, Vector2 min, Vector2 max, Vector2 pivot)
        {
            RectSizeDelta = sizeDelta;
            AnchorPosition = anchorPosition;
            AnchorMin = min;
            AnchorMax = max;
            AnchorPivot = pivot;
        }
    }

    public static class Fonts
    {
        public static Font GetFont(string fontName = null)
        {
            return SilksongMobileAPI.HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

    public static class CanvasUtil
    {
        public static GameObject CreateCanvas(RenderMode renderMode, Vector2 referencePixelsPerUnit, string name = "CanvasUtilCanvas")
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? "CanvasUtilCanvas" : name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var c = go.AddComponent<Canvas>();
            c.renderMode = renderMode;
            c.sortingOrder = 500;
            var scaler = go.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referencePixelsPerUnit.sqrMagnitude > 1f ? referencePixelsPerUnit : new Vector2(1920f, 1080f);
            go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            return go;
        }

        public static GameObject CreateTextPanel(GameObject parent, string text, int fontSize, TextAnchor textAnchor, RectData rectData, Font font = null)
        {
            var go = new GameObject("CanvasUtilText", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            if (parent != null) go.transform.SetParent(parent.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rectData.AnchorMin;
            rt.anchorMax = rectData.AnchorMax;
            rt.pivot = rectData.AnchorPivot;
            rt.anchoredPosition = rectData.AnchorPosition;
            rt.sizeDelta = rectData.RectSizeDelta;
            var t = go.GetComponent<UnityEngine.UI.Text>();
            t.text = text ?? "";
            t.fontSize = fontSize > 0 ? fontSize : 20;
            t.alignment = textAnchor;
            t.font = font ?? Fonts.GetFont();
            t.color = Color.white;
            return go;
        }
    }
}

namespace UnityEngine.UIElements
{
    public static class IEnumerableExtensions
    {
        public static int GetCount<T>(this System.Collections.Generic.IEnumerable<T> collection)
        {
            if (collection == null) return 0;
            if (collection is System.Collections.Generic.ICollection<T> col) return col.Count;
            int n = 0;
            foreach (var _ in collection) n++;
            return n;
        }
    }
}

namespace WishUtil
{
    public class CustomQuest
    {
        public string Name { get; set; }
        public CustomQuest() { }
        public CustomQuest(string name, object questType = null, object displayName = null, object description = null, params object[] extra)
        {
            Name = name;
        }
        public void Accept() { }
        public void Update() { }
    }

    public static class GetQuestType
    {
        public static object BuildCustomType(string name, Sprite icon = null, Color color = default) => null;
    }

    public static class QuestData
    {
        public static void AddQuest(CustomQuest quest) { }
    }
}

namespace System.Collections.Generic
{
    public static class CollectionExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
        {
            return GetValueOrDefault(dictionary, key, default(TValue));
        }

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        {
            if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
            return dictionary.TryGetValue(key, out TValue value) ? value : defaultValue;
        }

        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }

        public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value)
        {
            if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
            if (dictionary.TryGetValue(key, out value))
            {
                dictionary.Remove(key);
                return true;
            }
            value = default(TValue);
            return false;
        }
    }
}

namespace System
{
    public static class MathF
    {
        public const float PI = 3.14159274f;
        public const float E = 2.71828175f;
        public static float Abs(float x) => Math.Abs(x);
        public static float Min(float x, float y) => Math.Min(x, y);
        public static float Max(float x, float y) => Math.Max(x, y);
        public static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);
        public static float Floor(float x) => (float)Math.Floor(x);
        public static float Ceiling(float x) => (float)Math.Ceiling(x);
        public static float Round(float x) => (float)Math.Round(x);
        public static float Round(float x, int digits) => (float)Math.Round(x, digits);
        public static float Round(float x, MidpointRounding mode) => (float)Math.Round(x, mode);
        public static float Truncate(float x) => (float)Math.Truncate(x);
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Tan(float x) => (float)Math.Tan(x);
        public static float Asin(float x) => (float)Math.Asin(x);
        public static float Acos(float x) => (float)Math.Acos(x);
        public static float Atan(float x) => (float)Math.Atan(x);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Exp(float x) => (float)Math.Exp(x);
        public static float Log(float x) => (float)Math.Log(x);
        public static float Log(float x, float y) => (float)Math.Log(x, y);
        public static float Log10(float x) => (float)Math.Log10(x);
        public static int Sign(float x) => Math.Sign(x);
    }
}

namespace System
{
    public struct HashCode
    {
        private int _hash;
        public void Add<T>(T value)
        {
            unchecked { _hash = (_hash * 397) ^ (value != null ? value.GetHashCode() : 0); }
        }
        public int ToHashCode() => _hash;
        public static int Combine<T1>(T1 v1) => v1 != null ? v1.GetHashCode() : 0;
        public static int Combine<T1, T2>(T1 v1, T2 v2) { unchecked { return ((v1 != null ? v1.GetHashCode() : 0) * 397) ^ (v2 != null ? v2.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3>(T1 v1, T2 v2, T3 v3) { unchecked { return (Combine(v1, v2) * 397) ^ (v3 != null ? v3.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3, T4>(T1 v1, T2 v2, T3 v3, T4 v4) { unchecked { return (Combine(v1, v2, v3) * 397) ^ (v4 != null ? v4.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3, T4, T5>(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5) { unchecked { return (Combine(v1, v2, v3, v4) * 397) ^ (v5 != null ? v5.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3, T4, T5, T6>(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6) { unchecked { return (Combine(v1, v2, v3, v4, v5) * 397) ^ (v6 != null ? v6.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3, T4, T5, T6, T7>(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7) { unchecked { return (Combine(v1, v2, v3, v4, v5, v6) * 397) ^ (v7 != null ? v7.GetHashCode() : 0); } }
        public static int Combine<T1, T2, T3, T4, T5, T6, T7, T8>(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8) { unchecked { return (Combine(v1, v2, v3, v4, v5, v6, v7) * 397) ^ (v8 != null ? v8.GetHashCode() : 0); } }
    }
}

namespace HutongGames.PlayMaker.Actions
{
    public class SuperJumpRaycast : FsmStateAction
    {
        public FsmOwnerDefault FromGameObject;
        public FsmVector2 FromPosition;
        public FsmVector2 Direction;
        public Space Space;
        public FsmFloat Distance;
        public FsmBool StoreDidHit;
        public FsmGameObject StoreHitObject;
        public FsmVector2 StoreHitPoint;
        public FsmFloat StoreDistance;
        public FsmBool StoreHitSpikes;
        public FsmBool StoreIsTransitionGate;

        public override void Reset()
        {
            FromGameObject = null;
            FromPosition = new FsmVector2 { UseVariable = true };
            Direction = new FsmVector2 { Value = Vector2.up };
            Space = Space.World;
            Distance = 100f;
            StoreDidHit = null;
            StoreHitObject = null;
            StoreHitPoint = null;
            StoreDistance = null;
            StoreHitSpikes = null;
            StoreIsTransitionGate = null;
        }

        public override void OnEnter()
        {
            try
            {
                var go = Fsm?.GetOwnerDefaultTarget(FromGameObject);
                Vector2 origin = go != null ? (Vector2)go.transform.position : Vector2.zero;
                if (FromPosition != null && !FromPosition.IsNone) origin += FromPosition.Value;
                Vector2 dir = Direction != null && !Direction.IsNone ? Direction.Value : Vector2.up;
                float dist = Distance != null && !Distance.IsNone ? Distance.Value : 100f;
                var hit = Physics2D.Raycast(origin, dir, dist, 1 << 8);
                if (StoreDidHit != null) StoreDidHit.Value = hit.collider != null;
                if (StoreHitObject != null) StoreHitObject.Value = hit.collider != null ? hit.collider.gameObject : null;
                if (StoreHitPoint != null) StoreHitPoint.Value = hit.point;
                if (StoreDistance != null) StoreDistance.Value = hit.collider != null ? hit.distance : dist;
                if (StoreHitSpikes != null) StoreHitSpikes.Value = false;
                if (StoreIsTransitionGate != null) StoreIsTransitionGate.Value = hit.collider != null && hit.collider.GetComponent<TransitionPoint>() != null;
            }
            catch { }
            Finish();
        }
    }
}

namespace InControl
{
    public enum BindingSourceRejectionType
    {
        None = 0,
        DuplicateBindingOnAction = 1,
        DuplicateBindingOnActionSet = 2
    }

    public enum InputDeviceClass
    {
        Unknown = 0,
        Keyboard = 1,
        Mouse = 2,
        Controller = 3,
        Remote = 4,
        FlightStick = 5,
        TouchScreen = 6
    }
}

namespace TMPro.SpriteAssetUtilities
{
    public class TexturePacker_JsonArray
    {
        [Serializable] public struct SpriteFrame { public float x, y, w, h; }
        [Serializable] public struct SpriteSize { public float w, h; }
        [Serializable] public struct Frame { public string filename; public SpriteFrame frame; public bool rotated; public bool trimmed; public SpriteFrame spriteSourceSize; public SpriteSize sourceSize; public Vector2 pivot; }
        [Serializable] public struct Meta { public string app; public string version; public string image; public string format; public SpriteSize size; public float scale; public string smartupdate; }
        [Serializable] public class SpriteDataObject { public System.Collections.Generic.List<Frame> frames = new System.Collections.Generic.List<Frame>(); public Meta meta; }
    }
}

namespace SkongGamemodes
{
    public class GameModeData
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    public class GameModeManager : IDisposable
    {
        public static GameModeManager Instance { get; } = new GameModeManager();
        public void Init() { }
        public void Dispose() { }
    }

    public class GameModeManagerPlugin
    {
        public static GameModeManagerPlugin Instance { get; } = new GameModeManagerPlugin();
        public GameModeManager Manager => GameModeManager.Instance;
    }
}

