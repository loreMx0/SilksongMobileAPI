using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilksongMobileAPI
{
    public sealed class ExtractedHarmonyPatchInfo
    {
        public string DeclaringTypeName;
        public string MethodName;
        public MethodType? PatchMethodType;
        public string[] ArgumentTypeNames;
    }

    public static class HarmonyBridge
    {
        private static readonly Dictionary<string, string> _harmonyIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PluginInfo> _pluginInfosByType = new Dictionary<string, PluginInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _assemblyLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, byte[]>> _embeddedResourcesByAssembly = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<ExtractedHarmonyPatchInfo>> _extractedPatchInfos = new Dictionary<string, List<ExtractedHarmonyPatchInfo>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, string>> _i18nOverridesBySheet = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _i18nFlatKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "FIRST_WEAVER_SUPER", "First Sinner and Widow" },
            { "SPINNER_SUPER", "First Sinner and Widow" },
            { "FIRST_WEAVER_MAIN", "Revolt and Devotion" },
            { "SPINNER_MAIN", "Revolt and Devotion" },
            { "StartMultiplayerBtn", "Start Multiplayer" },
            { "MainMenu/StartMultiplayerBtn", "Start Multiplayer" },
            { "MainMenu.StartMultiplayerBtn", "Start Multiplayer" },
            { "!!MainMenu/StartMultiplayerBtn!!", "Start Multiplayer" },
            { "!_StartMultiplayerBtn", "Start Multiplayer" }
        };
        private static bool _i18nHooksInstalled;
        private static readonly List<Delegate> _prepatcherGetHandlers = new List<Delegate>();
        private static readonly List<Delegate> _prepatcherSetHandlers = new List<Delegate>();
        private static bool _prepatcherHooksInstalled;

        private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        // ======================================================================
        // AccessTools.FieldRefAccess<T, F> Shims
        // ======================================================================
        private sealed class FieldRefBox<T, F>
        {
            private readonly FieldInfo _field;
            private F _cached;

            public FieldRefBox(FieldInfo field)
            {
                _field = field;
            }

            public ref F Invoke(T instance)
            {
                try
                {
                    if (_field != null && (instance != null || _field.IsStatic))
                    {
                        object val = _field.GetValue(_field.IsStatic ? null : instance);
                        _cached = val is F typed ? typed : default;
                    }
                }
                catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L76", __apiEx_1); }
                return ref _cached;
            }
        }

        public static FieldRef<T, F> FieldRefAccess<T, F>(string fieldName)
        {
            var field = RuntimeHybridCLRAdapterBuilder.FindFieldInHierarchy(typeof(T), fieldName);
            return FieldRefAccessByField<T, F>(field);
        }

        public static FieldRef<T, F> FieldRefAccessByField<T, F>(FieldInfo field)
        {
            if (field != null)
            {
                try
                {
                    if (CecilRuntimeRewriter.TryBuildFieldRefDelegate<T, F>(field, out var del) && del != null)
                        return del;
                }
                catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L96", __apiEx_2); }
            }
            var box = new FieldRefBox<T, F>(field);
            return new FieldRef<T, F>(box.Invoke);
        }

        public static ref F FieldRefAccessInstance<T, F>(T instance, string fieldName)
        {
            var del = FieldRefAccess<T, F>(fieldName);
            return ref del(instance);
        }

        public static FieldRef<object, F> FieldRefAccessByFieldObject<F>(FieldInfo field)
        {
            var box = new FieldRefBox<object, F>(field);
            return new FieldRef<object, F>(box.Invoke);
        }

        public static FieldRef<object, F> FieldRefAccessByTypeAndName<F>(Type type, string fieldName)
        {
            var field = RuntimeHybridCLRAdapterBuilder.FindFieldInHierarchy(type, fieldName);
            return FieldRefAccessByFieldObject<F>(field);
        }

        public static string GetAssemblyMetadataKey(object attr)
        {
            if (attr == null) return "";
            try
            {
                var t = attr.GetType();
                var f = t.GetField("<Key>k__BackingField", AllFlags) ?? t.GetField("m_key", AllFlags) ?? t.GetField("key", AllFlags);
                if (f != null) return (f.GetValue(attr) as string) ?? "";
                var p = t.GetProperty("Key", AllFlags);
                if (p != null) return (p.GetValue(attr, null) as string) ?? "";
            }
            catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L131", __apiEx_3); }
            return "";
        }

        public static string GetAssemblyMetadataValue(object attr)
        {
            if (attr == null) return "";
            try
            {
                var t = attr.GetType();
                var f = t.GetField("<Value>k__BackingField", AllFlags) ?? t.GetField("m_value", AllFlags) ?? t.GetField("value", AllFlags);
                if (f != null) return (f.GetValue(attr) as string) ?? "";
                var p = t.GetProperty("Value", AllFlags);
                if (p != null) return (p.GetValue(attr, null) as string) ?? "";
            }
            catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L146", __apiEx_4); }
            return "";
        }

        private sealed class StaticFieldRefBox<F>
        {
            private readonly FieldInfo _field;
            private F _cached;

            public StaticFieldRefBox(FieldInfo field)
            {
                _field = field;
            }

            public ref F Invoke()
            {
                try
                {
                    if (_field != null && _field.IsStatic)
                    {
                        object val = _field.GetValue(null);
                        _cached = val is F typed ? typed : default;
                    }
                }
                catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L170", __apiEx_5); }
                return ref _cached;
            }
        }

        public static FieldRef<F> StaticFieldRefAccessByField<F>(FieldInfo field)
        {
            if (field != null)
            {
                try
                {
                    if (CecilRuntimeRewriter.TryBuildStaticFieldRefDelegate<F>(field, out var del) && del != null)
                        return del;
                }
                catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L184", __apiEx_6); }
            }
            var box = new StaticFieldRefBox<F>(field);
            return new FieldRef<F>(box.Invoke);
        }

        public static FieldRef<F> StaticFieldRefAccessByTypeAndName<F>(Type type, string fieldName)
        {
            var field = RuntimeHybridCLRAdapterBuilder.FindFieldInHierarchy(type, fieldName);
            return StaticFieldRefAccessByField<F>(field);
        }

        public static FieldRef<F> StaticFieldRefAccessByName<F>(string typeColonFieldname)
        {
            if (!string.IsNullOrEmpty(typeColonFieldname))
            {
                int idx = typeColonFieldname.LastIndexOf(':');
                if (idx > 0)
                {
                    string tName = typeColonFieldname.Substring(0, idx).TrimEnd(':');
                    string fName = typeColonFieldname.Substring(idx + 1);
                    var t = ResolveTypeRobust(tName);
                    if (t != null) return StaticFieldRefAccessByTypeAndName<F>(t, fName);
                }
            }
            return StaticFieldRefAccessByField<F>(null);
        }

        public static FieldRef<F> StaticFieldRefAccessTyped<T, F>(string fieldName)
        {
            return StaticFieldRefAccessByTypeAndName<F>(typeof(T), fieldName);
        }

        public static FieldRef<F> StaticFieldRefAccessTypedByField<T, F>(FieldInfo field)
        {
            return StaticFieldRefAccessByField<F>(field);
        }

        public static ref F StaticFieldRefAccessValue<T, F>(string fieldName)
        {
            var del = StaticFieldRefAccessByTypeAndName<F>(typeof(T), fieldName);
            return ref del();
        }

        public static ref F StaticFieldRefAccessValueByField<T, F>(FieldInfo field)
        {
            var del = StaticFieldRefAccessByField<F>(field);
            return ref del();
        }

        private static bool _tomlConvertersRegistered;
        public static void EnsureTomlConvertersRegistered()
        {
            if (_tomlConvertersRegistered) return;
            _tomlConvertersRegistered = true;
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }
            catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L243", __apiEx_7); }
            try
            {
                if (!TomlTypeConverter.CanConvert(typeof(KeyboardShortcut)))
                {
                    TomlTypeConverter.AddConverter(
                        typeof(KeyboardShortcut),
                        new TypeConverter
                        {
                            ConvertToString = (obj, type) => obj is KeyboardShortcut ks ? ks.Serialize() : "None",
                            ConvertToObject = (str, type) => KeyboardShortcut.Deserialize(str)
                        });
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Registered TomlTypeConverter for BepInEx.Configuration.KeyboardShortcut.");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HarmonyBridge] TomlTypeConverter registration warning: {ex.Message}");
            }
        }

        // ======================================================================
        // IL2CPP-Safe CustomAttribute Helpers (NEVER calls stripped CustomAttributeExtensions!)
        // ======================================================================
        public static T GetCustomAttributeSafe<T>(ICustomAttributeProvider provider, bool inherit = true) where T : Attribute
        {
            if (provider == null) return null;
            try
            {
                var arr = provider.GetCustomAttributes(typeof(T), inherit);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] is T typed) return typed;
                    }
                }
            }
            catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L281", __apiEx_8); }
            try
            {
                var all = provider.GetCustomAttributes(inherit);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] is T typed) return typed;
                    }
                }
            }
            catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L293", __apiEx_9); }
            return null;
        }

        public static List<T> GetCustomAttributesSafe<T>(ICustomAttributeProvider provider, bool inherit = true) where T : Attribute
        {
            var list = new List<T>();
            if (provider == null) return list;
            try
            {
                var arr = provider.GetCustomAttributes(typeof(T), inherit);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] is T typed) list.Add(typed);
                    }
                    return list;
                }
            }
            catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L313", __apiEx_10); }
            try
            {
                var all = provider.GetCustomAttributes(inherit);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] is T typed) list.Add(typed);
                    }
                }
            }
            catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L325", __apiEx_11); }
            return list;
        }

        public static Attribute GetCustomAttributeByTypeSafe(ICustomAttributeProvider provider, Type attributeType, bool inherit = true)
        {
            if (provider == null || attributeType == null) return null;
            try
            {
                var arr = provider.GetCustomAttributes(attributeType, inherit);
                if (arr != null && arr.Length > 0 && arr[0] is Attribute a) return a;
            }
            catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L337", __apiEx_12); }
            try
            {
                var all = provider.GetCustomAttributes(inherit);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && attributeType.IsAssignableFrom(all[i].GetType()) && all[i] is Attribute a)
                            return a;
                    }
                }
            }
            catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L350", __apiEx_13); }
            return null;
        }

        public static IEnumerable<Attribute> GetCustomAttributesByTypeSafe(ICustomAttributeProvider provider, Type attributeType, bool inherit = true)
        {
            var list = new List<Attribute>();
            if (provider == null || attributeType == null) return list;
            try
            {
                var arr = provider.GetCustomAttributes(attributeType, inherit);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i] is Attribute a) list.Add(a);
                    return list;
                }
            }
            catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L368", __apiEx_14); }
            try
            {
                var all = provider.GetCustomAttributes(inherit);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && attributeType.IsAssignableFrom(all[i].GetType()) && all[i] is Attribute a)
                            list.Add(a);
                }
            }
            catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L379", __apiEx_15); }
            return list;
        }

        public static bool IsDefinedSafe(ICustomAttributeProvider provider, Type attributeType, bool inherit = true)
        {
            if (provider == null || attributeType == null) return false;
            try { return provider.IsDefined(attributeType, inherit); } catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L386", __apiEx_16); }
            return GetCustomAttributeByTypeSafe(provider, attributeType, inherit) != null;
        }

        // ======================================================================
        // v2.9.4 Thunderstore Mod Bundle Compatibility Bridges
        // ======================================================================
        public static string ToTitleCaseSafe(System.Globalization.TextInfo textInfo, string str)
        {
            if (string.IsNullOrEmpty(str)) return str ?? "";
            try
            {
                char[] chars = str.ToCharArray();
                bool newWord = true;
                for (int i = 0; i < chars.Length; i++)
                {
                    char c = chars[i];
                    if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '/' || c == '.')
                    {
                        newWord = true;
                    }
                    else if (newWord)
                    {
                        chars[i] = char.ToUpperInvariant(c);
                        newWord = false;
                    }
                    else
                    {
                        chars[i] = char.ToLowerInvariant(c);
                    }
                }
                return new string(chars);
            }
            catch
            {
                return str;
            }
        }

        public static HashSet<T> EnumerableToHashSet<T>(IEnumerable<T> source)
        {
            return source != null ? new HashSet<T>(source) : new HashSet<T>();
        }

        public static HashSet<T> EnumerableToHashSetComparer<T>(IEnumerable<T> source, IEqualityComparer<T> comparer)
        {
            return source != null ? new HashSet<T>(source, comparer) : new HashSet<T>(comparer);
        }

        public static IEnumerable<T> EnumerableTakeLast<T>(IEnumerable<T> source, int count)
        {
            if (source == null || count <= 0) return Array.Empty<T>();
            var list = source as IList<T> ?? source.ToList();
            int total = list.Count;
            if (count >= total) return list;
            var res = new List<T>(count);
            for (int i = total - count; i < total; i++)
                res.Add(list[i]);
            return res;
        }

        public static T[] GetSubArraySafe<T>(T[] array, System.Range range)
        {
            if (array == null) return Array.Empty<T>();
            var offsetAndLen = range.GetOffsetAndLength(array.Length);
            int start = offsetAndLen.Item1;
            int len = offsetAndLen.Item2;
            if (len <= 0) return Array.Empty<T>();
            T[] dest = new T[len];
            Array.Copy(array, start, dest, 0, len);
            return dest;
        }

        public static DateTime GetDateTimeUnixEpoch()
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        public static string PathGetRelativePath(string relativeTo, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (string.IsNullOrEmpty(relativeTo)) return path;
            try
            {
                string fullRel = Path.GetFullPath(relativeTo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(fullRel, StringComparison.OrdinalIgnoreCase))
                    return fullPath.Substring(fullRel.Length);
                Uri fromUri = new Uri(fullRel);
                Uri toUri = new Uri(fullPath);
                return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        public static string GetManagedPathSafe()
        {
            try
            {
                string root = Paths.BepInExRootPath;
                if (!string.IsNullOrEmpty(root))
                    return Path.Combine(root, "core");
            }
            catch { }
            return Application.persistentDataPath ?? "";
        }

        public static string GetBepInExAssemblyDirectorySafe()
        {
            try
            {
                string root = Paths.BepInExRootPath;
                if (!string.IsNullOrEmpty(root))
                    return Path.Combine(root, "core");
            }
            catch { }
            return Application.persistentDataPath ?? "";
        }

        public static string GetApplicationDataPathSafe()
        {
            try
            {
                string root = Paths.BepInExRootPath;
                if (!string.IsNullOrEmpty(root))
                {
                    string parent = Path.GetDirectoryName(root);
                    return !string.IsNullOrEmpty(parent) ? parent : root;
                }
            }
            catch { }
            return Application.persistentDataPath ?? "";
        }

        public static HarmonyLib.PatchProcessor CreateProcessor(Harmony instance, MethodBase original)
        {
            return new HarmonyLib.PatchProcessor(instance, original);
        }

        public static TDelegate AccessToolsMethodDelegate<TDelegate>(MethodInfo method, object instance = null, bool virtualCall = true) where TDelegate : class
        {
            if (method == null) return null;
            try
            {
                if (instance == null)
                    return Delegate.CreateDelegate(typeof(TDelegate), method) as TDelegate;
                return Delegate.CreateDelegate(typeof(TDelegate), instance, method) as TDelegate;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "AccessToolsMethodDelegate", ex);
                return null;
            }
        }

        public static System.Diagnostics.Process ProcessStartSafe(string fileName)
        {
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ProcessStartSafe] Suppressed desktop Process.Start('{fileName}') on Android.");
            return null;
        }

        public static System.Diagnostics.Process ProcessStartWithArgsSafe(string fileName, string arguments)
        {
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ProcessStartSafe] Suppressed desktop Process.Start('{fileName}', '{arguments}') on Android.");
            return null;
        }

        public static System.Diagnostics.Process ProcessStartInfoSafe(System.Diagnostics.ProcessStartInfo startInfo)
        {
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ProcessStartSafe] Suppressed desktop Process.Start(ProcessStartInfo='{startInfo?.FileName}') on Android.");
            return null;
        }

        public static void SetFileSystemWatcherEnableRaisingEventsSafe(System.IO.FileSystemWatcher watcher, bool value)
        {
            if (watcher == null) return;
            try
            {
                watcher.EnableRaisingEvents = value;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[FileSystemWatcherSafe] Ignored EnableRaisingEvents({value}) on Android: {ex.Message}");
            }
        }

        // ======================================================================
        // v2.9.5 Batch 02 & Batch 03 Compatibility Bridges
        // ======================================================================
        public static byte MathClampByte(byte value, byte min, byte max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static int MathClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static long MathClampLong(long value, long min, long max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static float MathClampFloat(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double MathClampDouble(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static object InControl_GetKeyboardProviderSafe() => null;
        public static object InControl_GetMouseProviderSafe() => null;

        public static bool InControl_MouseGetButtonIsPressed(object provider, int control)
        {
            return BepInEx.MobileVirtualInput.GetMouseButton(0);
        }

        public static bool InControl_MouseGetButtonWasPressed(object provider, int control)
        {
            return BepInEx.MobileVirtualInput.GetMouseButtonDown(0);
        }

        public static float InControl_MouseGetDeltaScroll(object provider) => 0f;
        public static float InControl_MouseGetDeltaX(object provider) => 0f;
        public static float InControl_MouseGetDeltaY(object provider) => 0f;

        public static Vector2 InControl_MouseGetPosition(object provider)
        {
            Vector3 mp = BepInEx.MobileVirtualInput.get_mousePosition();
            return new Vector2(mp.x, mp.y);
        }

        public static AudioClip LoadWavOrAudioFromFileSync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                return AssetHelperBridge.TryParseWavBytesToAudioClip(bytes, Path.GetFileNameWithoutExtension(filePath) ?? "ModAudio");
            }
            catch { }
            return null;
        }

        // ======================================================================
        // I18N / TeamCherry.Localization JSON Bridge + Custom NailSlash Hitbox Fix
        // ======================================================================
        public static bool FreeWeaverAttacksNoSilkDrain = false;
        public static bool SuppressVerboseCombatLogs = true;

        public static void LogModInfoFiltered(BepInEx.Logging.ManualLogSource logger, object data)
        {
            if (logger == null) return;
            if (SuppressVerboseCombatLogs)
            {
                try
                {
                    if (GameManager._instance != null && !GameManager._instance.IsMenuScene())
                    {
                        return;
                    }
                }
                catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L408", __apiEx_17); }
            }
            logger.LogInfo(data);
        }

        public static void LogModDebugFiltered(BepInEx.Logging.ManualLogSource logger, object data)
        {
            if (logger == null || SuppressVerboseCombatLogs) return;
            logger.LogDebug(data);
        }

        public static void LogModWarnFiltered(BepInEx.Logging.ManualLogSource logger, object data)
        {
            if (logger == null) return;
            string s = data?.ToString() ?? "";
            if (s.IndexOf("Lace_Cross_Slash_Fast", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("no extracted Silkshot wavs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("HUD_health_evolve_transform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Packsize test failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Scaled HP for ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger.LogInfo(data);
                return;
            }
            if (SuppressVerboseCombatLogs)
            {
                try
                {
                    if (GameManager._instance != null && !GameManager._instance.IsMenuScene())
                    {
                        return;
                    }
                }
                catch (System.Exception __apiEx_18) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L441", __apiEx_18); }
            }
            logger.LogWarning(data);
        }

        public static void LogModErrorFiltered(BepInEx.Logging.ManualLogSource logger, object data)
        {
            if (logger == null) return;
            string s = data?.ToString() ?? "";
            if (s.IndexOf("Deep Docks prefab bundle location was not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Weaver mask hook MISSING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger.LogInfo(data);
                return;
            }
            logger.LogError(data);
        }

        private static Matrix4x4 _savedPcModGuiMatrix = Matrix4x4.identity;
        private static int _pcModGuiMatrixDepth;

        public static void BeginPcModOnGuiScale()
        {
            try
            {
                if (_pcModGuiMatrixDepth == 0)
                {
                    _savedPcModGuiMatrix = GUI.matrix;
                }
                _pcModGuiMatrixDepth++;
                float sw = Mathf.Max(1f, (float)Screen.width);
                float sh = Mathf.Max(1f, (float)Screen.height);
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sw / 1920f, sh / 1080f, 1f));
            }
            catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L475", __apiEx_19); }
        }

        public static void EndPcModOnGuiScale()
        {
            try
            {
                if (_pcModGuiMatrixDepth > 0)
                {
                    _pcModGuiMatrixDepth--;
                    if (_pcModGuiMatrixDepth == 0)
                    {
                        GUI.matrix = _savedPcModGuiMatrix;
                    }
                }
            }
            catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L491", __apiEx_20); }
        }
        private static FieldInfo _nailSlashAnimTriggerCounterField;
        private static FieldInfo _nailSlashIsStartingSlashField;
        private static FieldInfo _nailSlashHasPolyField;
        private static FieldInfo _nailAttackIsDamagerActiveField;
        private static FieldInfo _nailAttackCanDamageEnemiesField;
        private static FieldInfo _nailAttackDamagersField;
        private static FieldInfo _nailAttackHcField;
        private static FieldInfo _nailAttackSlashSpriteField;
        private static FieldInfo _nailSlashAudioField;
        private static FieldInfo _nailSlashOriginalPitchField;
        private static FieldInfo _heroExtraDamagersField;
        private static MethodInfo _nailSlashSetCollidersActiveMethod;
        private static bool _nailSlashFieldsResolved;
        private static readonly HashSet<int> _initializedNailSlashIds = new HashSet<int>();

        public static void EnsureCustomNailSlashColliderAndAnimReady(object target)
        {
            if (target == null) return;
            try
            {
                if (!_nailSlashFieldsResolved)
                {
                    _nailSlashFieldsResolved = true;
                    _nailSlashAnimTriggerCounterField = typeof(NailSlash).GetField("animTriggerCounter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailSlashIsStartingSlashField = typeof(NailSlash).GetField("<IsStartingSlash>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailSlashHasPolyField = typeof(NailSlash).GetField("hasPoly", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailAttackIsDamagerActiveField = typeof(NailAttackBase).GetField("<IsDamagerActive>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailAttackCanDamageEnemiesField = typeof(NailAttackBase).GetField("<CanDamageEnemies>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailAttackDamagersField = typeof(NailAttackBase).GetField("damagers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailAttackHcField = typeof(NailAttackBase).GetField("hc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailAttackSlashSpriteField = typeof(NailAttackBase).GetField("slashSprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailSlashAudioField = typeof(NailSlash).GetField("audio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailSlashOriginalPitchField = typeof(NailSlash).GetField("originalPitch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _heroExtraDamagersField = typeof(HeroExtraNailSlash).GetField("damagers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _nailSlashSetCollidersActiveMethod = typeof(NailSlash).GetMethod("SetCollidersActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                }

                NailSlash ns = target as NailSlash;
                NailAttackBase nab = target as NailAttackBase;
                GameObject attackGo = (target as Component)?.gameObject;

                if (ns == null && nab == null && attackGo == null)
                {
                    var t = target.GetType();
                    var pAttack = t.GetProperty("Attack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? t.GetProperty("NailSlash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pAttack != null)
                    {
                        try
                        {
                            var val = pAttack.GetValue(target, null);
                            if (val is NailSlash vns) ns = vns;
                            if (val is NailAttackBase vnab) nab = vnab;
                            if (val is Component vc) attackGo = vc.gameObject;
                        }
                        catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L548", __apiEx_21); }
                    }
                    var pGo = t.GetProperty("GameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pGo != null && attackGo == null)
                    {
                        try { attackGo = pGo.GetValue(target, null) as GameObject; } catch (System.Exception __apiEx_22) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L553", __apiEx_22); }
                    }
                    if (ns == null && attackGo != null)
                    {
                        ns = attackGo.GetComponent<NailSlash>() ?? attackGo.GetComponentInChildren<NailSlash>(true);
                        if (nab == null) nab = attackGo.GetComponent<NailAttackBase>() ?? attackGo.GetComponentInChildren<NailAttackBase>(true);
                    }
                }

                if (attackGo != null)
                {
                    var extraOnGo = attackGo.GetComponent<HeroExtraNailSlash>();
                    if (extraOnGo != null && _heroExtraDamagersField != null)
                    {
                        var curArr = _heroExtraDamagersField.GetValue(extraOnGo) as DamageEnemies[];
                        if (curArr == null)
                        {
                            _heroExtraDamagersField.SetValue(extraOnGo, attackGo.GetComponentsInChildren<DamageEnemies>(true) ?? Array.Empty<DamageEnemies>());
                        }
                    }
                    if (ns == null) ns = attackGo.GetComponent<NailSlash>() ?? attackGo.GetComponentInChildren<NailSlash>(true);
                    if (nab == null) nab = attackGo.GetComponent<NailAttackBase>() ?? attackGo.GetComponentInChildren<NailAttackBase>(true);
                }

                if (ns != null)
                {
                    int nsId = ns.GetInstanceID();
                    if (_initializedNailSlashIds.Contains(nsId) && ns.anim != null && ns.poly != null)
                        return;
                    if (attackGo == null) attackGo = ns.gameObject;
                    var parentTr = ns.transform.parent;
                    if (parentTr != null)
                    {
                        var extraParent = parentTr.GetComponent<HeroExtraNailSlash>();
                        if (extraParent != null && _heroExtraDamagersField != null)
                        {
                            var curArr = _heroExtraDamagersField.GetValue(extraParent) as DamageEnemies[];
                            if (curArr == null)
                            {
                                _heroExtraDamagersField.SetValue(extraParent, parentTr.GetComponentsInChildren<DamageEnemies>(true) ?? Array.Empty<DamageEnemies>());
                            }
                        }
                    }

                    // Ensure NailAttackBase & NailSlash private Awake fields are non-null even if instantiated under an inactive parent (e.g. Wanderer Swipe)
                    if (_nailAttackHcField != null && _nailAttackHcField.GetValue(ns) == null)
                    {
                        var hc = ns.GetComponentInParent<HeroController>(true) ?? HeroController.SilentInstance;
                        if (hc != null) _nailAttackHcField.SetValue(ns, hc);
                    }
                    if (_nailAttackSlashSpriteField != null && _nailAttackSlashSpriteField.GetValue(ns) == null)
                    {
                        var sp = ns.GetComponent<tk2dSprite>() ?? ns.GetComponentInChildren<tk2dSprite>(true);
                        if (sp != null) _nailAttackSlashSpriteField.SetValue(ns, sp);
                    }
                    if (_nailSlashAudioField != null && _nailSlashAudioField.GetValue(ns) == null)
                    {
                        var aud = ns.GetComponent<AudioSource>() ?? ns.GetComponentInChildren<AudioSource>(true);
                        if (aud != null)
                        {
                            _nailSlashAudioField.SetValue(ns, aud);
                            _nailSlashOriginalPitchField?.SetValue(ns, aud.pitch);
                        }
                    }
                    if (ns.anim == null)
                    {
                        ns.anim = ns.GetComponent<tk2dSpriteAnimator>() ?? ns.GetComponentInChildren<tk2dSpriteAnimator>(true);
                    }
                    if (ns.mesh == null)
                    {
                        ns.mesh = ns.GetComponent<MeshRenderer>() ?? ns.GetComponentInChildren<MeshRenderer>(true);
                    }
                    if (ns.poly == null)
                    {
                        ns.poly = ns.GetComponent<PolygonCollider2D>() ?? ns.GetComponentInChildren<PolygonCollider2D>(true);
                    }
                    if (ns.poly != null)
                    {
                        _nailSlashHasPolyField?.SetValue(ns, true);
                    }
                    if (_nailAttackDamagersField != null)
                    {
                        var dmgList = _nailAttackDamagersField.GetValue(ns) as List<DamageEnemies>;
                        if (dmgList == null || dmgList.Count == 0)
                        {
                            var newList = new List<DamageEnemies>();
                            var de = ns.EnemyDamager ?? ns.GetComponent<DamageEnemies>() ?? ns.GetComponentInChildren<DamageEnemies>(true);
                            if (de != null) newList.Add(de);
                            _nailAttackDamagersField.SetValue(ns, newList);
                        }
                    }
                    // Clean null entries in damagers (Needleforge assigns enemyDamager after NailAttackBase.Awake, leaving { null })
                    if (_nailAttackDamagersField != null)
                    {
                        var dmgList = _nailAttackDamagersField.GetValue(ns) as List<DamageEnemies>;
                        var de = ns.EnemyDamager ?? ns.GetComponent<DamageEnemies>() ?? ns.GetComponentInChildren<DamageEnemies>(true);
                        if (dmgList == null)
                        {
                            dmgList = new List<DamageEnemies>();
                            if (de != null) dmgList.Add(de);
                            _nailAttackDamagersField.SetValue(ns, dmgList);
                        }
                        else
                        {
                            dmgList.RemoveAll(d => d == null);
                            if (de != null && !dmgList.Contains(de))
                                dmgList.Add(de);
                        }
                    }
                    if (ns.anim != null && ns.poly != null)
                    {
                        _initializedNailSlashIds.Add(nsId);
                    }
                }
                else if (nab != null)
                {
                    if (attackGo == null) attackGo = nab.gameObject;
                    if (_nailAttackDamagersField != null)
                    {
                        var dmgList = _nailAttackDamagersField.GetValue(nab) as List<DamageEnemies>;
                        var de = nab.EnemyDamager ?? nab.GetComponent<DamageEnemies>() ?? nab.GetComponentInChildren<DamageEnemies>(true);
                        if (dmgList == null)
                        {
                            dmgList = new List<DamageEnemies>();
                            if (de != null) dmgList.Add(de);
                            _nailAttackDamagersField.SetValue(nab, dmgList);
                        }
                        else
                        {
                            dmgList.RemoveAll(d => d == null);
                            if (de != null && !dmgList.Contains(de))
                                dmgList.Add(de);
                        }
                    }
                    _nailAttackIsDamagerActiveField?.SetValue(nab, true);
                    _nailAttackCanDamageEnemiesField?.SetValue(nab, true);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[SlashFix] EnsureCustomNailSlashColliderAndAnimReady warning: {ex.Message}");
            }
        }

        public static void EnsureCustomNailSlashFieldsReady_Prefix(object target)
        {
            // Only populate null component references BEFORE StartSlash() so StartSlash() runs cleanly without pre-modifying clip.frames!
            EnsureCustomNailSlashColliderAndAnimReady(target);
        }

        public static void EnsureCustomNailSlashColliderActive_Postfix(object target)
        {
            if (target == null) return;
            try
            {
                EnsureCustomNailSlashColliderAndAnimReady(target);
                NailSlash ns = target as NailSlash;
                NailAttackBase nab = target as NailAttackBase;
                GameObject attackGo = null;
                if (ns == null && target is Component c && c != null && c.gameObject != null)
                {
                    attackGo = c.gameObject;
                    ns = attackGo.GetComponent<NailSlash>() ?? attackGo.GetComponentInChildren<NailSlash>(true);
                    if (nab == null) nab = attackGo.GetComponent<NailAttackBase>() ?? attackGo.GetComponentInChildren<NailAttackBase>(true);
                }
                if (ns != null && attackGo == null) attackGo = ns.gameObject;
                if (nab != null && attackGo == null) attackGo = nab.gameObject;

                if (ns != null)
                {
                    var clip = ns.anim != null ? (ns.anim.CurrentClip ?? (!string.IsNullOrEmpty(ns.animName) ? ns.anim.GetClipByName(ns.animName) : null)) : null;
                    int trigCount = 0;
                    bool frame0Trigger = false;
                    if (clip != null && clip.frames != null && clip.frames.Length > 0)
                    {
                        frame0Trigger = clip.frames[0] != null && clip.frames[0].triggerEvent && clip.frames[0].eventInfo != "Bounce";
                        for (int i = 0; i < clip.frames.Length; i++)
                        {
                            var fr = clip.frames[i];
                            if (fr != null && fr.triggerEvent && fr.eventInfo != "Bounce")
                                trigCount++;
                        }
                    }

                    bool isWeaverOrCustom = frame0Trigger || trigCount > 2 ||
                                            (ns.animName != null && ns.animName.IndexOf("Weaver", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                            (ns.gameObject.name.IndexOf("Weaver", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                            (ns.transform.parent != null && ns.transform.parent.name.IndexOf("Weaver", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isWeaverOrCustom && clip != null && clip.frames != null && clip.frames.Length > 1 && trigCount > 2)
                    {
                        // Normalize custom clips that set triggerEvent=true on every frame so only the final frame fires trigger #2 (EndDamage)
                        for (int i = 0; i < clip.frames.Length - 1; i++)
                        {
                            var fr = clip.frames[i];
                            if (fr != null && fr.eventInfo != "Bounce")
                                fr.triggerEvent = false;
                        }
                        var lastFr = clip.frames[clip.frames.Length - 1];
                        if (lastFr != null)
                            lastFr.triggerEvent = true;
                    }

                    var damager = ns.EnemyDamager ?? ns.GetComponent<DamageEnemies>() ?? ns.GetComponentInChildren<DamageEnemies>(true);
                    if (isWeaverOrCustom || (ns.poly != null && !ns.poly.enabled && frame0Trigger))
                    {
                        int attackLayer = LayerMask.NameToLayer("Attack");
                        if (attackLayer >= 0 && attackGo != null)
                            attackGo.layer = attackLayer;

                        _nailSlashHasPolyField?.SetValue(ns, true);
                        _nailSlashAnimTriggerCounterField?.SetValue(ns, 1);
                        _nailSlashIsStartingSlashField?.SetValue(ns, false);
                        _nailAttackIsDamagerActiveField?.SetValue(ns, true);
                        _nailAttackCanDamageEnemiesField?.SetValue(ns, true);
                        try { _nailSlashSetCollidersActiveMethod?.Invoke(ns, new object[] { true }); } catch (System.Exception __apiEx_23) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L768", __apiEx_23); }

                        if (damager != null)
                        {
                            if (attackLayer >= 0) damager.gameObject.layer = attackLayer;
                            var rb = damager.gameObject.GetComponent<Rigidbody2D>();
                            if (rb == null)
                            {
                                rb = damager.gameObject.AddComponent<Rigidbody2D>();
                                rb.bodyType = RigidbodyType2D.Kinematic;
                                rb.gravityScale = 0f;
                                rb.useFullKinematicContacts = true;
                            }
                            rb.simulated = true;
                            try { damager.EndDamage(); } catch (System.Exception __apiEx_24) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L782", __apiEx_24); }
                            try { damager.StartDamage(); } catch (System.Exception __apiEx_25) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L783", __apiEx_25); }
                        }

                        if (ns.poly != null)
                        {
                            ns.poly.isTrigger = true;
                            if (!ns.poly.enabled)
                                ns.poly.enabled = true;
                        }
                    }
                    else if (damager != null)
                    {
                        // Even for standard 2-trigger slashes, clear any stale damagedColliders at swing start so every swing registers hits!
                        try { damager.EndDamage(); } catch (System.Exception __apiEx_26) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L796", __apiEx_26); }
                        try { damager.StartDamage(); } catch (System.Exception __apiEx_27) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L797", __apiEx_27); }
                    }
                }
            }
            catch (System.Exception __apiEx_28) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L801", __apiEx_28); }
        }

        private static FieldInfo _hcConfigCanPlayNeedolinField;
        private static bool _hcConfigFieldChecked;

        public static void EnsureHeroConfigCanPlayNeedolin(HeroController hc)
        {
            if (hc == null) return;
            try
            {
                if (PlayerData.HasInstance && PlayerData.instance != null)
                {
                    PlayerData.instance.hasNeedolin = true;
                    PlayerData.instance.hasNeedolinMemoryPowerup = true;
                    PlayerData.instance.hasSilkSpecial = true;
                    PlayerData.instance.disableSilkAbilities = false;
                }
                if (!_hcConfigFieldChecked)
                {
                    _hcConfigFieldChecked = true;
                    _hcConfigCanPlayNeedolinField = typeof(HeroControllerConfig).GetField("canPlayNeedolin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                var cfg = hc.Config;
                if (cfg != null && !cfg.ForceBareInventory && !cfg.CanPlayNeedolin)
                {
                    _hcConfigCanPlayNeedolinField?.SetValue(cfg, true);
                }
                if (hc.configs != null && _hcConfigCanPlayNeedolinField != null)
                {
                    for (int i = 0; i < hc.configs.Length; i++)
                    {
                        var cg = hc.configs[i];
                        if (cg != null && cg.Config != null && !cg.Config.ForceBareInventory && !cg.Config.CanPlayNeedolin)
                        {
                            _hcConfigCanPlayNeedolinField.SetValue(cg.Config, true);
                        }
                    }
                }
                if (hc.specialConfigs != null && _hcConfigCanPlayNeedolinField != null)
                {
                    for (int i = 0; i < hc.specialConfigs.Length; i++)
                    {
                        var cg = hc.specialConfigs[i];
                        if (cg != null && cg.Config != null && !cg.Config.ForceBareInventory && !cg.Config.CanPlayNeedolin)
                        {
                            _hcConfigCanPlayNeedolinField.SetValue(cg.Config, true);
                        }
                    }
                }
                var ih = ManagerSingleton<InputHandler>.Instance;
                if (ih != null && ih.ForceDreamNailRePress)
                {
                    ih.ForceDreamNailRePress = false;
                }
                // CRITICAL: Do NOT iterate hc.GetComponentsInChildren<PlayMakerFSM>() or access st.Actions here!
                // EnsureHeroConfigCanPlayNeedolin is called from HeroController.Awake() -> SetConfigGroup() BEFORE
                // PlayMakerFSM.Awake() / Fsm.Init() has initialized Hornet's "Silk Specials" FSM; touching st.Actions
                // prematurely runs FsmState.LoadActions() while st.Fsm is null, breaking CallMethodProper and ListenForDreamNail!
            }
            catch (System.Exception __apiEx_29) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L861", __apiEx_29); }
        }

        public static void HeroController_HasNeedolin_Postfix(HeroController __instance, ref bool __result)
        {
            if (!__result && __instance != null)
            {
                EnsureHeroConfigCanPlayNeedolin(__instance);
                if (PlayerData.HasInstance && PlayerData.instance != null && PlayerData.instance.hasNeedolin)
                {
                    var cfg = __instance.Config;
                    if (cfg == null || !cfg.ForceBareInventory)
                        __result = true;
                }
            }
        }

        public static void HeroController_CanPlayNeedolin_Postfix(HeroController __instance, ref bool __result)
        {
            if (!__result && __instance != null)
            {
                var cfg = __instance.Config;
                if (cfg != null && !cfg.ForceBareInventory && !cfg.CanPlayNeedolin)
                {
                    EnsureHeroConfigCanPlayNeedolin(__instance);
                }
                if (PlayerData.HasInstance && PlayerData.instance != null && PlayerData.instance.hasNeedolin && !PlayerData.instance.isInventoryOpen)
                {
                    if ((cfg == null || !cfg.ForceBareInventory) &&
                        !__instance.hardLanded &&
                        (__instance.gm == null || !__instance.gm.isPaused) &&
                        __instance.hero_state != GlobalEnums.ActorStates.no_input &&
                        __instance.cState != null &&
                        !__instance.cState.dashing &&
                        !__instance.cState.attacking &&
                        ((!__instance.controlReqlinquished && __instance.cState.onGround) || PlayerData.instance.atBench) &&
                        !__instance.cState.hazardDeath &&
                        !__instance.cState.hazardRespawning &&
                        !__instance.cState.recoilFrozen &&
                        !__instance.cState.recoiling &&
                        !__instance.cState.transitioning)
                    {
                        __result = true;
                    }
                }
            }
        }

        private static int _lastSilkSpecialsRepairedId;
        private static int _needolinPressLogCount;
        private static bool _dreamNailInputReflBound;
        private static FieldInfo _fiInputHandlerActions;
        private static FieldInfo _fiHeroActionsDreamNail;
        private static PropertyInfo _piDreamNailWasPressed;
        private static InputHandler _cachedDnIh;
        private static object _cachedDnActionObj;
        private static Func<bool> _cachedDnWasPressedDel;

        private static bool IsDreamNailWasPressedRefl(InputHandler ih)
        {
            if (ih == null) return false;
            try
            {
                if (!ReferenceEquals(ih, _cachedDnIh) || _cachedDnActionObj == null)
                {
                    if (!_dreamNailInputReflBound)
                    {
                        _dreamNailInputReflBound = true;
                        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        _fiInputHandlerActions = typeof(InputHandler).GetField("inputActions", F);
                        if (_fiInputHandlerActions != null)
                        {
                            _fiHeroActionsDreamNail = _fiInputHandlerActions.FieldType.GetField("DreamNail", F);
                            if (_fiHeroActionsDreamNail != null)
                                _piDreamNailWasPressed = _fiHeroActionsDreamNail.FieldType.GetProperty("WasPressed", F);
                        }
                    }
                    if (_fiInputHandlerActions == null || _fiHeroActionsDreamNail == null)
                        return false;
                    object ia = _fiInputHandlerActions.GetValue(ih);
                    if (ia == null) return false;
                    _cachedDnActionObj = _fiHeroActionsDreamNail.GetValue(ia);
                    _cachedDnIh = ih;
                    _cachedDnWasPressedDel = null;
                    if (_cachedDnActionObj != null && _piDreamNailWasPressed != null)
                    {
                        var gm = _piDreamNailWasPressed.GetGetMethod(true);
                        if (gm != null)
                            _cachedDnWasPressedDel = Delegate.CreateDelegate(typeof(Func<bool>), _cachedDnActionObj, gm, false) as Func<bool>;
                    }
                }
                if (_cachedDnWasPressedDel != null)
                    return _cachedDnWasPressedDel();
                if (_cachedDnActionObj != null && _piDreamNailWasPressed != null)
                    return _piDreamNailWasPressed.GetValue(_cachedDnActionObj, null) is bool b && b;
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void RepairSilkSpecialsFsmAndNeedolinState(HeroController hc)
        {
            if (hc == null) return;
            try
            {
                EnsureHeroConfigCanPlayNeedolin(hc);
                var pfsm = hc.silkSpecialFSM;
                if (pfsm == null || pfsm.Fsm == null || !pfsm.Fsm.Initialized) return;
                int id = pfsm.GetInstanceID();
                bool firstRepair = (_lastSilkSpecialsRepairedId != id);
                _lastSilkSpecialsRepairedId = id;

                var activeVar = pfsm.Fsm.Variables?.FindFsmBool("Active");
                if (activeVar != null && !activeVar.Value)
                    activeVar.Value = true;

                try { pfsm.Fsm.EventTarget = null; } catch (System.Exception __apiEx_30) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L964", __apiEx_30); }
                if (pfsm.Fsm.Variables != null)
                {
                    var inChallenge = pfsm.Fsm.Variables.FindFsmBool("In Challenge Region");
                    if (inChallenge != null && inChallenge.Value && GameManager.SilentInstance != null && !GameManager.SilentInstance.isPaused)
                    {
                        // Ensure stale Challenge Region bool doesn't block normal Needolin outside challenge regions
                    }
                    var canPlayVar = pfsm.Fsm.Variables.FindFsmBool("Can Play");
                    if (canPlayVar != null && hc.CanPlayNeedolin())
                        canPlayVar.Value = true;
                }

                var rawActsField = typeof(HutongGames.PlayMaker.FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool canPlayNow = hc.CanPlayNeedolin();
                RepairFsmStatesAndSubFsms(pfsm.Fsm, hc, canPlayNow, rawActsField, 0);
                if (firstRepair)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[NeedolinGuard] Verified & repaired Hornet 'Silk Specials' FSM (state='{pfsm.ActiveStateName}', hasNeedolin={hc.HasNeedolin()}, canPlay={hc.CanPlayNeedolin()}).");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NeedolinGuard] RepairSilkSpecialsFsmAndNeedolinState warning: {ex.Message}");
            }
        }

        public static void HeroController_RegainControl_Postfix(HeroController __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.inputHandler != null && __instance.inputHandler.ForceDreamNailRePress)
                {
                    __instance.inputHandler.ForceDreamNailRePress = false;
                }
                RepairSilkSpecialsFsmAndNeedolinState(__instance);
            }
            catch (System.Exception __apiEx_31) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1002", __apiEx_31); }
        }

        public static void ListenForDreamNail_OnUpdate_Prefix(HutongGames.PlayMaker.Actions.ListenForDreamNail __instance)
        {
            if (__instance == null || Time.timeScale == 0f) return;
            try
            {
                if (__instance.gm == null)
                    __instance.gm = GameManager.instance;
                if (__instance.inputHandler == null && __instance.gm != null)
                    __instance.inputHandler = __instance.gm.inputHandler ?? ManagerSingleton<InputHandler>.Instance;
                if (__instance.activeBool != null && !__instance.activeBool.IsNone && !__instance.activeBool.Value)
                {
                    if (PlayerData.HasInstance && PlayerData.instance != null && (PlayerData.instance.hasNeedolin || PlayerData.instance.hasSilkSpecial))
                    {
                        __instance.activeBool.Value = true;
                    }
                }
                var ih = __instance.inputHandler;
                if (ih != null)
                {
                    if (ih.ForceDreamNailRePress)
                        ih.ForceDreamNailRePress = false;
                    if (IsDreamNailWasPressedRefl(ih))
                    {
                        var hc = HeroController.SilentInstance;
                        if (hc != null) RepairSilkSpecialsFsmAndNeedolinState(hc);
                        if (!SuppressVerboseCombatLogs && _needolinPressLogCount < 1)
                        {
                            _needolinPressLogCount++;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo(
                                $"[NeedolinDiag] DreamNail.WasPressed=true in state='{__instance.State?.Name}' " +
                                $"(fsm='{__instance.Fsm?.Name}', activeBool={__instance.activeBool?.Value}, " +
                                $"wasPressedEvt='{__instance.wasPressed?.Name}', isPressedEvt='{__instance.isPressed?.Name}', " +
                                $"canPlayNeedolin={hc?.CanPlayNeedolin()}, onGround={hc?.cState?.onGround}, heroState={hc?.hero_state})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("SilksongMobileAPI.NeedolinGuard", "ListenForDreamNail_OnUpdate_Prefix", ex);
            }
        }

        public static void ListenForDreamNail_OnUpdate_Postfix(HutongGames.PlayMaker.Actions.ListenForDreamNail __instance)
        {
            if (__instance == null) return;
            try
            {
                var ih = __instance.inputHandler;
                if (ih != null && IsDreamNailWasPressedRefl(ih))
                {
                    var hc = HeroController.SilentInstance;
                    var fsm = __instance.Fsm;
                    if (fsm != null && hc != null && fsm.Name == "Silk Specials")
                    {
                        if (fsm.ActiveStateName == "Idle" && hc.CanPlayNeedolin())
                        {
                            RepairSilkSpecialsFsmAndNeedolinState(hc);
                            var idleSt = fsm.ActiveState;
                            if (!SuppressVerboseCombatLogs && idleSt != null && idleSt.Transitions != null && _needolinPressLogCount <= 1)
                            {
                                var trDesc = new List<string>();
                                for (int t = 0; t < idleSt.Transitions.Length; t++)
                                {
                                    var tr = idleSt.Transitions[t];
                                    if (tr != null)
                                        trDesc.Add($"{tr.EventName}->{tr.ToState}(resolved={tr.ToFsmState != null})");
                                }
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[NeedolinDiag] 'Idle' transitions: [{string.Join(", ", trDesc)}]");
                            }
                            if (__instance.isPressed != null && !string.IsNullOrEmpty(__instance.isPressed.Name))
                            {
                                fsm.EventTarget = null;
                                fsm.Event(__instance.isPressed);
                            }
                            if (fsm.ActiveStateName == "Idle" && idleSt != null && idleSt.Transitions != null)
                            {
                                for (int t = 0; t < idleSt.Transitions.Length; t++)
                                {
                                    var tr = idleSt.Transitions[t];
                                    if (tr != null && tr.EventName == "BUTTON DOWN")
                                    {
                                        var nextSt = tr.ToFsmState ?? fsm.GetState(tr.ToState);
                                        if (!SuppressVerboseCombatLogs && nextSt != null && _needolinPressLogCount <= 1)
                                        {
                                            var nextActs = nextSt.Actions;
                                            var actNames = nextActs != null ? nextActs.Where(a => a != null).Select(a => a.GetType().Name).ToArray() : Array.Empty<string>();
                                            var nextTrs = nextSt.Transitions != null ? nextSt.Transitions.Where(x => x != null).Select(x => $"{x.EventName}->{x.ToState}").ToArray() : Array.Empty<string>();
                                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[NeedolinDiag] Target state '{nextSt.Name}' actions=[{string.Join(", ", actNames)}], transitions=[{string.Join(", ", nextTrs)}]");
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        if (!SuppressVerboseCombatLogs && _needolinPressLogCount <= 1)
                        {
                            SilksongMobileAPIPlugin.LogDiagnosticInfo(
                                $"[NeedolinDiag] Post-ListenForDreamNail state='{fsm.ActiveStateName}' (prev='{fsm.PreviousActiveState?.Name}', switched={fsm.SwitchedState}, canPlay={hc.CanPlayNeedolin()})");
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_32) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1108", __apiEx_32); }
        }

        private static void RepairFsmStatesAndSubFsms(HutongGames.PlayMaker.Fsm fsm, HeroController hc, bool canPlayNow, FieldInfo rawActsField, int depth)
        {
            if (fsm == null || depth > 3) return;
            try
            {
                if (fsm.Variables != null)
                {
                    var activeVar = fsm.Variables.FindFsmBool("Active");
                    if (activeVar != null && !activeVar.Value) activeVar.Value = true;
                    var canPlayVar = fsm.Variables.FindFsmBool("Can Play");
                    if (canPlayVar != null && canPlayNow) canPlayVar.Value = true;
                    var canDoSpecialVar = fsm.Variables.FindFsmBool("Can Do Special");
                    if (canDoSpecialVar != null && canPlayNow) canDoSpecialVar.Value = true;
                }

                var states = fsm.States;
                if (states == null) return;
                for (int s = 0; s < states.Length; s++)
                {
                    var st = states[s];
                    if (st == null) continue;
                    if (st.Transitions != null)
                    {
                        for (int t = 0; t < st.Transitions.Length; t++)
                        {
                            var tr = st.Transitions[t];
                            if (tr != null && tr.ToFsmState == null && !string.IsNullOrEmpty(tr.ToState))
                            {
                                tr.ToFsmState = fsm.GetState(tr.ToState);
                            }
                        }
                    }
                    var acts = rawActsField != null ? rawActsField.GetValue(st) as HutongGames.PlayMaker.FsmStateAction[] : null;
                    if (acts == null) continue;
                    for (int a = 0; a < acts.Length; a++)
                    {
                        var act = acts[a];
                        if (act == null) continue;
                        if (act.State != st || act.Fsm == null)
                        {
                            try { act.Init(st); } catch (System.Exception __apiEx_33) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1151", __apiEx_33); }
                        }
                        if (act is HutongGames.PlayMaker.Actions.CallMethodProper cmp)
                        {
                            cmp.errorString = string.Empty;
                            if (cmp.storeResult != null && !string.IsNullOrEmpty(cmp.storeResult.variableName) && fsm.Variables != null)
                            {
                                var nv = fsm.Variables.GetVariable(cmp.storeResult.variableName);
                                if (nv != null)
                                {
                                    cmp.storeResult.NamedVar = nv;
                                    cmp.storeResult.Type = nv.VariableType;
                                }
                                string mn = cmp.methodName?.Value ?? "";
                                if ((mn == "CanPlayNeedolin" || mn == "HasNeedolin" || mn == "CanDoSpecial") && canPlayNow)
                                {
                                    cmp.storeResult.boolValue = true;
                                    try { cmp.storeResult.SetValue(true); } catch (System.Exception __apiEx_34) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1168", __apiEx_34); }
                                    var fb = fsm.Variables.FindFsmBool(cmp.storeResult.variableName);
                                    if (fb != null) fb.Value = true;
                                }
                            }
                        }
                        else if (act is HutongGames.PlayMaker.Actions.RunFSM runFsmAct)
                        {
                            if (runFsmAct.runFsm != null)
                            {
                                RepairFsmStatesAndSubFsms(runFsmAct.runFsm, hc, canPlayNow, rawActsField, depth + 1);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_35) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1184", __apiEx_35); }
        }

        private static int _needolinSubDiagCount;

        public static void PlayerDataBoolTest_OnEnter_Needolin_Prefix(HutongGames.PlayMaker.Actions.PlayerDataBoolTest __instance)
        {
            if (__instance == null) return;
            try
            {
                string fsmName = __instance.Fsm?.Name ?? "";
                string stName = __instance.State?.Name ?? "";
                if (fsmName == "Silk Specials" || stName == "Needolin Sub" || (__instance.Fsm?.RootFsm != null && __instance.Fsm.RootFsm.Name == "Silk Specials"))
                {
                    string bName = __instance.boolName?.Value ?? "";
                    if (PlayerData.HasInstance && PlayerData.instance != null)
                    {
                        if (bName == "hasNeedolin" || bName == "hasSilkSpecial" || bName == "hasNeedolinMemoryPowerup")
                        {
                            PlayerData.instance.hasNeedolin = true;
                            PlayerData.instance.hasSilkSpecial = true;
                            PlayerData.instance.hasNeedolinMemoryPowerup = true;
                        }
                        else if (bName == "disableSilkAbilities" || bName == "disablePause" || bName == "isInventoryOpen")
                        {
                            if (bName == "disableSilkAbilities")
                                PlayerData.instance.disableSilkAbilities = false;
                        }
                    }
                    if (!SuppressVerboseCombatLogs && _needolinSubDiagCount < 1)
                    {
                        _needolinSubDiagCount++;
                        bool curVal = (GameManager.SilentInstance != null && !string.IsNullOrEmpty(bName)) && GameManager.SilentInstance.GetPlayerDataBool(bName);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo(
                            $"[NeedolinDiag] PlayerDataBoolTest in {fsmName}/{stName}: boolName={bName}, curVal={curVal}, isTrue={__instance.isTrue?.Name}, isFalse={__instance.isFalse?.Name}");
                    }
                }
            }
            catch (System.Exception __apiEx_36) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1222", __apiEx_36); }
        }

        public static void RunFSM_OnEnter_Needolin_Prefix(HutongGames.PlayMaker.Actions.RunFSM __instance)
        {
            if (__instance == null) return;
            try
            {
                string fsmName = __instance.Fsm?.Name ?? "";
                string stName = __instance.State?.Name ?? "";
                if (fsmName == "Silk Specials" || stName == "Needolin Sub")
                {
                    var hc = HeroController.SilentInstance;
                    if (hc != null) EnsureHeroConfigCanPlayNeedolin(hc);
                    if (__instance.runFsm == null && __instance.fsmTemplateControl != null && __instance.fsmTemplateControl.fsmTemplate != null && __instance.Fsm != null)
                    {
                        try
                        {
                            __instance.fsmTemplateControl.Init();
                            __instance.runFsm = __instance.Fsm.CreateSubFsm(__instance.fsmTemplateControl);
                        }
                        catch (System.Exception __apiEx_37) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1243", __apiEx_37); }
                    }
                    var subFsm = __instance.runFsm;
                    if (subFsm != null)
                    {
                        var rawActsField = typeof(HutongGames.PlayMaker.FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        bool canPlayNow = hc == null || hc.CanPlayNeedolin();
                        RepairFsmStatesAndSubFsms(subFsm, hc, canPlayNow, rawActsField, 1);
                        if (!SuppressVerboseCombatLogs && _needolinSubDiagCount < 1)
                        {
                            _needolinSubDiagCount++;
                            var stNames = subFsm.States != null ? subFsm.States.Where(x => x != null).Select(x => x.Name).ToArray() : Array.Empty<string>();
                            SilksongMobileAPIPlugin.LogDiagnosticInfo(
                                $"[NeedolinDiag] RunFSM.OnEnter Prefix in {fsmName}/{stName}: subFsm={subFsm.Name}, started={subFsm.Started}, activeState={subFsm.ActiveStateName}, states=[{string.Join(", ", stNames)}]");
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_38) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1261", __apiEx_38); }
        }

        public static void RunFSM_OnEnter_Needolin_Postfix(HutongGames.PlayMaker.Actions.RunFSM __instance)
        {
            if (__instance == null) return;
            try
            {
                string fsmName = __instance.Fsm?.Name ?? "";
                string stName = __instance.State?.Name ?? "";
                if (!SuppressVerboseCombatLogs && (fsmName == "Silk Specials" || stName == "Needolin Sub") && _needolinSubDiagCount < 1)
                {
                    _needolinSubDiagCount++;
                    var subFsm = __instance.runFsm;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo(
                        $"[NeedolinDiag] RunFSM.OnEnter Postfix in {fsmName}/{stName}: hostState={__instance.Fsm?.ActiveStateName}, subState={subFsm?.ActiveStateName}, subPrev={subFsm?.PreviousActiveState?.Name}, subFinished={subFsm?.Finished}");
                }
            }
            catch (System.Exception __apiEx_39) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1279", __apiEx_39); }
        }

        public static void CallMethodProper_OnEnter_Needolin_Prefix(HutongGames.PlayMaker.Actions.CallMethodProper __instance)
        {
            if (__instance == null) return;
            try
            {
                // Link non-serialized storeResult.NamedVar once when null BEFORE DoMethodCall() and base.Finish() run!
                if (__instance.storeResult != null && __instance.storeResult.NamedVar == null && !string.IsNullOrEmpty(__instance.storeResult.variableName) && __instance.Fsm?.Variables != null)
                {
                    var nv = __instance.Fsm.Variables.GetVariable(__instance.storeResult.variableName);
                    if (nv != null)
                    {
                        __instance.storeResult.NamedVar = nv;
                        __instance.storeResult.Type = nv.VariableType;
                    }
                }
                string mn = __instance.methodName?.Value ?? "";
                if (mn == "CanPlayNeedolin" || mn == "HasNeedolin" || mn == "CanDoSpecial")
                {
                    var hc = HeroController.SilentInstance;
                    if (hc != null)
                    {
                        EnsureHeroConfigCanPlayNeedolin(hc);
                        if (__instance.Fsm != null && __instance.gameObject != null && __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject) == null)
                        {
                            __instance.gameObject.OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject;
                            __instance.gameObject.GameObject = new HutongGames.PlayMaker.FsmGameObject { Value = hc.gameObject };
                        }
                        bool canPlay = (mn == "CanDoSpecial") ? hc.CanDoSpecial() : hc.CanPlayNeedolin();
                        if (canPlay && __instance.storeResult != null)
                        {
                            __instance.storeResult.boolValue = true;
                            try { __instance.storeResult.SetValue(true); } catch (System.Exception __apiEx_40) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1313", __apiEx_40); }
                            string varName = __instance.storeResult.variableName;
                            if (!string.IsNullOrEmpty(varName))
                            {
                                var fb = __instance.Fsm?.Variables?.FindFsmBool(varName);
                                if (fb != null) fb.Value = true;
                                var rootFb = __instance.Fsm?.RootFsm?.Variables?.FindFsmBool(varName);
                                if (rootFb != null) rootFb.Value = true;
                            }
                        }
                        if (!SuppressVerboseCombatLogs && _needolinSubDiagCount < 1)
                        {
                            _needolinSubDiagCount++;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo(
                                $"[NeedolinDiag] CallMethodProper.OnEnter Prefix: fsm={__instance.Fsm?.Name}, state={__instance.State?.Name}, method={mn}, canPlay={canPlay}, var={__instance.storeResult?.variableName}");
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_41) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1332", __apiEx_41); }
        }

        public static void CallMethodProper_OnEnter_Needolin_Postfix(HutongGames.PlayMaker.Actions.CallMethodProper __instance)
        {
            if (__instance == null || __instance.methodName == null) return;
            try
            {
                string mn = __instance.methodName.Value ?? "";
                if (mn == "CanPlayNeedolin" || mn == "HasNeedolin" || mn == "CanDoSpecial")
                {
                    var hc = HeroController.SilentInstance;
                    bool canPlay = hc != null && ((mn == "CanDoSpecial") ? hc.CanDoSpecial() : hc.CanPlayNeedolin());
                    if (canPlay && __instance.storeResult != null)
                    {
                        __instance.storeResult.boolValue = true;
                        try { __instance.storeResult.SetValue(true); } catch (System.Exception __apiEx_42) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1348", __apiEx_42); }
                        string varName = __instance.storeResult.variableName;
                        if (!string.IsNullOrEmpty(varName) && __instance.Fsm?.Variables != null)
                        {
                            var nv = __instance.Fsm.Variables.GetVariable(varName);
                            if (nv != null)
                            {
                                __instance.storeResult.NamedVar = nv;
                                __instance.storeResult.Type = nv.VariableType;
                            }
                            var fb = __instance.Fsm.Variables.FindFsmBool(varName);
                            if (fb != null) fb.Value = true;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_43) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1364", __apiEx_43); }
        }

        public static bool ToolItemManager_GetIsChangingActiveState()
        {
            return false;
        }

        private static MethodInfo _inControlGetKeyIsPressedMi;
        private static Type _inControlKeyEnumType;
        private static bool _inControlReflResolved;

        public static bool InControl_GetKeyIsPressed(object keyboardProvider, int inControlKey)
        {
            try
            {
                if (BepInEx.MobileVirtualInput.HasActiveInControlKey(inControlKey))
                    return true;
                if (keyboardProvider == null) return false;
                if (!_inControlReflResolved)
                {
                    _inControlReflResolved = true;
                    _inControlKeyEnumType = ResolveTypeRobust("InControl.Key");
                    _inControlGetKeyIsPressedMi = keyboardProvider.GetType().GetMethod("GetKeyIsPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_inControlGetKeyIsPressedMi != null && _inControlKeyEnumType != null)
                {
                    object enumVal = Enum.ToObject(_inControlKeyEnumType, inControlKey);
                    object res = _inControlGetKeyIsPressedMi.Invoke(keyboardProvider, new object[] { enumVal });
                    return res is bool b && b;
                }
            }
            catch (System.Exception __apiEx_44) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1396", __apiEx_44); }
            return false;
        }

        public static void PruneStaleTransitionPoints()
        {
            try
            {
                var list = TransitionPoint.TransitionPoints;
                if (list == null) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var tp = list[i];
                    if (tp == null || tp.gameObject == null)
                    {
                        list.RemoveAt(i);
                        continue;
                    }
                    var sc = tp.gameObject.scene;
                    if (!sc.IsValid() || !sc.isLoaded || !tp.gameObject.activeInHierarchy)
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            catch (System.Exception __apiEx_45) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1421", __apiEx_45); }
        }

        public static void SanitizeTransitionPointsForScene(string targetSceneName)
        {
            try
            {
                var list = TransitionPoint.TransitionPoints;
                if (list == null) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var tp = list[i];
                    if (tp == null || tp.gameObject == null)
                    {
                        list.RemoveAt(i);
                        continue;
                    }
                    var sc = tp.gameObject.scene;
                    if (!sc.IsValid() || !sc.isLoaded)
                    {
                        list.RemoveAt(i);
                    }
                }

                if (string.IsNullOrEmpty(targetSceneName)) return;
                var targetSc = SceneManager.GetSceneByName(targetSceneName);
                if (targetSc.IsValid() && targetSc.isLoaded)
                {
                    // Ensure all active TransitionPoints in targetSc are present and ordered at the front of TransitionPoint.TransitionPoints
                    // so GameManager.FindTransitionPoint(entryGateName, default(Scene), true) always picks the gate in targetSc!
                    var roots = targetSc.GetRootGameObjects();
                    if (roots != null)
                    {
                        int insertIdx = 0;
                        for (int r = 0; r < roots.Length; r++)
                        {
                            var root = roots[r];
                            if (root == null) continue;
                            var tps = root.GetComponentsInChildren<TransitionPoint>(true);
                            if (tps == null) continue;
                            for (int j = 0; j < tps.Length; j++)
                            {
                                var tp = tps[j];
                                if (tp == null || tp.gameObject == null) continue;
                                list.Remove(tp);
                                list.Insert(insertIdx++, tp);
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_46) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1472", __apiEx_46); }
        }

        public static void GameManager_SetupSceneRefs_Prefix(GameManager __instance)
        {
            try
            {
                string target = (__instance != null && !string.IsNullOrEmpty(__instance.sceneName))
                    ? __instance.sceneName
                    : (__instance != null ? __instance.nextSceneName : null);
                if (string.IsNullOrEmpty(target))
                    target = SceneManager.GetActiveScene().name;
                SanitizeTransitionPointsForScene(target);
            }
            catch (System.Exception __apiEx_47) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1486", __apiEx_47); }
        }

        public static void GameManager_FindTransitionPoint_Prefix(GameManager __instance, string entryGateName)
        {
            try
            {
                string target = (__instance != null && !string.IsNullOrEmpty(__instance.nextSceneName))
                    ? __instance.nextSceneName
                    : (__instance != null ? __instance.sceneName : null);
                if (string.IsNullOrEmpty(target))
                    target = SceneManager.GetActiveScene().name;
                SanitizeTransitionPointsForScene(target);
            }
            catch (System.Exception __apiEx_48) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1500", __apiEx_48); }
        }

        public static void GameManager_BeginSceneTransition_Prefix(GameManager __instance, GameManager.SceneLoadInfo info)
        {
            if (info == null) return;
            try
            {
                if (__instance != null && !string.IsNullOrEmpty(__instance.sceneName))
                {
                    var curGameplaySc = SceneManager.GetSceneByName(__instance.sceneName);
                    if (curGameplaySc.IsValid() && curGameplaySc.isLoaded && SceneManager.GetActiveScene() != curGameplaySc)
                    {
                        try { SceneManager.SetActiveScene(curGameplaySc); } catch (System.Exception __apiEx_49) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1513", __apiEx_49); }
                    }
                    AssetHelperBridge.CleanupLingeringExtractionScenesAndModObjects(__instance.sceneName);
                }
                PruneStaleTransitionPoints();
                if (AssetHelperBridge.HasPaleAutomaton())
                {
                    if (string.Equals(info.SceneName, "Arborium_11", StringComparison.OrdinalIgnoreCase))
                    {
                        if (PlayerData.HasInstance && PlayerData.instance != null)
                            PlayerData.instance.encounteredSongChevalierBoss = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HarmonyBridge] GameManager_BeginSceneTransition_Prefix error: {ex.Message}");
            }
        }

        public static void HeroController_SetConfigGroup_Postfix(HeroController __instance)
        {
            EnsureHeroConfigCanPlayNeedolin(__instance);
        }

        private static bool _eternalWeaverSpoolHooksInstalled;
        private static MethodInfo _ewSetHalfSilkPosMi;
        private static MethodInfo _ewResetHalfSilkUsingMi;
        private static MethodInfo _ewReturnTakenHalfSilkMi;
        private static FieldInfo _silkSpoolFirstChunkXField;
        private static FieldInfo _silkSpoolChunkDistXField;
        private static FieldInfo _silkSpoolChunksField;
        private static FieldInfo _silkSpoolPartsChunkField;

        private static bool _crossStitchHooksInstalled;
        private static object _crossStitchPluginInst;
        private static FieldInfo _csFreeParryField;
        private static FieldInfo _csParryAlwaysUnlockedField;
        private static FieldInfo _csParryTimeOverrideField;
        private static FieldInfo _csToolForCostField;
        private static int _lastCrossStitchPatchedFsmId;

        public static void SilkSpool_EvaluatePositions_EternalWeaver_Postfix(SilkSpool __instance)
        {
            if (__instance == null || _ewSetHalfSilkPosMi == null) return;
            try
            {
                if (_silkSpoolFirstChunkXField == null)
                    _silkSpoolFirstChunkXField = typeof(SilkSpool).GetField("firstChunk_x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_silkSpoolChunkDistXField == null)
                    _silkSpoolChunkDistXField = typeof(SilkSpool).GetField("chunkDistance_x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_silkSpoolChunksField == null)
                    _silkSpoolChunksField = typeof(SilkSpool).GetField("silkChunks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_silkSpoolPartsChunkField == null)
                    _silkSpoolPartsChunkField = typeof(SilkSpool).GetField("partsChunk", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                float currentPos = _silkSpoolFirstChunkXField != null ? (float)_silkSpoolFirstChunkXField.GetValue(__instance) : 0f;
                float dist = _silkSpoolChunkDistXField != null ? (float)_silkSpoolChunkDistXField.GetValue(__instance) : 0f;
                if (_silkSpoolChunksField?.GetValue(__instance) is System.Collections.IList chunks)
                {
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        if (chunks[i] is SilkChunk sc && sc != null && !sc.IsRegen)
                            currentPos += dist;
                    }
                }
                if (_silkSpoolPartsChunkField?.GetValue(__instance) is UnityEngine.Object partsObj && partsObj != null)
                {
                    currentPos += dist;
                }
                _ewSetHalfSilkPosMi.Invoke(null, new object[] { currentPos });
            }
            catch (System.Exception __apiEx_50) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1585", __apiEx_50); }
        }

        public static void SilkSpool_ChangeSilk_EternalWeaver_Postfix()
        {
            if (_ewResetHalfSilkUsingMi == null) return;
            try { _ewResetHalfSilkUsingMi.Invoke(null, null); } catch (System.Exception __apiEx_51) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1591", __apiEx_51); }
        }

        public static void HeroController_BindInterrupted_EternalWeaver_Postfix()
        {
            if (_ewReturnTakenHalfSilkMi == null) return;
            try { _ewReturnTakenHalfSilkMi.Invoke(null, null); } catch (System.Exception __apiEx_52) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1597", __apiEx_52); }
        }

        private static bool IsCrossStitchFreeParryEnabled()
        {
            try
            {
                if (_crossStitchPluginInst == null || _csFreeParryField == null) return false;
                var cfgEntry = _csFreeParryField.GetValue(_crossStitchPluginInst);
                if (cfgEntry == null) return false;
                var valProp = cfgEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                return valProp != null && (bool)valProp.GetValue(cfgEntry);
            }
            catch { return false; }
        }

        public static void HeroController_CanThrowTool3_CrossStitch_Prefix(ToolItem __0)
        {
            if (__0 == null || _csToolForCostField == null) return;
            try
            {
                if (string.Equals(__0.name, "Parry", StringComparison.Ordinal))
                    _csToolForCostField.SetValue(null, __0);
            }
            catch (System.Exception __apiEx_53) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1621", __apiEx_53); }
        }

        public static bool PlayerData_get_SilkSkillCost_CrossStitch_Prefix(ref int __result)
        {
            try
            {
                if (!IsCrossStitchFreeParryEnabled()) return true;
                if (_csToolForCostField != null && _csToolForCostField.GetValue(null) is ToolItem t && t != null && string.Equals(t.name, "Parry", StringComparison.Ordinal))
                {
                    __result = 0;
                    return false;
                }
                var hc = HeroController.SilentInstance;
                if (hc != null)
                {
                    if (hc.willThrowTool != null && string.Equals(hc.willThrowTool.name, "Parry", StringComparison.Ordinal))
                    {
                        __result = 0;
                        return false;
                    }
                    if (hc.silkSpecialFSM != null && hc.silkSpecialFSM.ActiveStateName != null &&
                        hc.silkSpecialFSM.ActiveStateName.IndexOf("Parry", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        __result = 0;
                        return false;
                    }
                }
                var parryTool = ToolItemManager.GetToolByName("Parry");
                if (parryTool != null && parryTool.IsEquipped)
                {
                    __result = 0;
                    return false;
                }
            }
            catch (System.Exception __apiEx_54) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1656", __apiEx_54); }
            return true;
        }

        private static bool _checkedCrossStitchAndEternalWeaverPresence;
        private static bool _hasCrossStitchOrEternalWeaverPresent;
        private static ToolItem _cachedParryToolItem;

        public static void EnsureCrossStitchMadnessAndEternalWeaverRuntime(HeroController hc)
        {
            if (_checkedCrossStitchAndEternalWeaverPresence && !_hasCrossStitchOrEternalWeaverPresent)
                return;
            try
            {
                const BindingFlags AllFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                bool foundAny = false;
                foreach (var asm in GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    string an = asm.GetName().Name ?? "";

                    // 1. EternalOne-Crest_of_the_Weaver (Weaver_Crest.dll): bridge HalfSilk ILManipulator hooks
                    if (string.Equals(an, "Weaver_Crest", StringComparison.OrdinalIgnoreCase)) { foundAny = true; }
                    if (!_eternalWeaverSpoolHooksInstalled && string.Equals(an, "Weaver_Crest", StringComparison.OrdinalIgnoreCase))
                    {
                        var hsType = asm.GetType("Weaver_Crest.Patches.HalfSilk", false);
                        if (hsType != null)
                        {
                            _eternalWeaverSpoolHooksInstalled = true;
                            _ewSetHalfSilkPosMi = hsType.GetMethod("SetHalfSilkPosition", AllFlags);
                            _ewResetHalfSilkUsingMi = hsType.GetMethod("ResetHalfSilkUsing", AllFlags);
                            _ewReturnTakenHalfSilkMi = hsType.GetMethod("ReturnTakenHalfSilk", AllFlags);

                            var evalPosMi = typeof(SilkSpool).GetMethod("EvaluatePositions", AllFlags);
                            var postEvalPos = typeof(HarmonyBridge).GetMethod(nameof(SilkSpool_EvaluatePositions_EternalWeaver_Postfix), BindingFlags.Static | BindingFlags.Public);
                            if (evalPosMi != null && postEvalPos != null)
                                RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("EternalWeaver.SpoolBridge", evalPosMi, null, postEvalPos, null);

                            var changeSilkMi = typeof(SilkSpool).GetMethod("ChangeSilk", AllFlags);
                            var postChangeSilk = typeof(HarmonyBridge).GetMethod(nameof(SilkSpool_ChangeSilk_EternalWeaver_Postfix), BindingFlags.Static | BindingFlags.Public);
                            if (changeSilkMi != null && postChangeSilk != null)
                                RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("EternalWeaver.SpoolBridge", changeSilkMi, null, postChangeSilk, null);

                            var bindIntMi = typeof(HeroController).GetMethod("BindInterrupted", AllFlags);
                            var postBindInt = typeof(HarmonyBridge).GetMethod(nameof(HeroController_BindInterrupted_EternalWeaver_Postfix), BindingFlags.Static | BindingFlags.Public);
                            if (bindIntMi != null && postBindInt != null)
                                RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("EternalWeaver.SpoolBridge", bindIntMi, null, postBindInt, null);

                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Installed EternalOne Weaver_Crest HalfSilk HUD spool & BindInterrupted bridge hooks.");
                        }
                    }

                    // 2. Cmdpro-CrossStitchMadness (CrossStitchMadness.dll): ensure Parry unlock/auto-equip, freeParry cost=0, and Parry Stance Wait override
                    if (string.Equals(an, "CrossStitchMadness", StringComparison.OrdinalIgnoreCase)) { foundAny = true; }
                    if (string.Equals(an, "CrossStitchMadness", StringComparison.OrdinalIgnoreCase))
                    {
                        var csPluginType = asm.GetType("CrossStitchMadness.CrossStitchMadnessPlugin", false);
                        if (csPluginType != null)
                        {
                            if (_crossStitchPluginInst == null)
                            {
                                var instProp = csPluginType.GetProperty("Instance", AllFlags);
                                _crossStitchPluginInst = instProp?.GetValue(null);
                                _csFreeParryField = csPluginType.GetField("freeParry", AllFlags);
                                _csParryAlwaysUnlockedField = csPluginType.GetField("parryAlwaysUnlocked", AllFlags);
                                _csParryTimeOverrideField = csPluginType.GetField("parryTimeOverride", AllFlags);
                                var hcPatchType = asm.GetType("CrossStitchMadness.Patches.HeroControllerPatch", false);
                                _csToolForCostField = hcPatchType?.GetField("TOOL_FOR_COST", AllFlags);
                            }

                            if (!_crossStitchHooksInstalled && _crossStitchPluginInst != null)
                            {
                                _crossStitchHooksInstalled = true;
                                var canThrow3Mi = typeof(HeroController).GetMethods(AllFlags)
                                    .FirstOrDefault(m => m.Name == "CanThrowTool" && m.GetParameters().Length == 3);
                                var preCanThrow3 = typeof(HarmonyBridge).GetMethod(nameof(HeroController_CanThrowTool3_CrossStitch_Prefix), BindingFlags.Static | BindingFlags.Public);
                                if (canThrow3Mi != null && preCanThrow3 != null)
                                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("CrossStitchMadness.CostBridge", canThrow3Mi, preCanThrow3, null, null, HarmonyLib.Priority.First);

                                var silkCostMi = typeof(PlayerData).GetMethod("get_SilkSkillCost", AllFlags);
                                var preSilkCost = typeof(HarmonyBridge).GetMethod(nameof(PlayerData_get_SilkSkillCost_CrossStitch_Prefix), BindingFlags.Static | BindingFlags.Public);
                                if (silkCostMi != null && preSilkCost != null)
                                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("CrossStitchMadness.CostBridge", silkCostMi, preSilkCost, null, null, HarmonyLib.Priority.First);
                            }

                            if (_crossStitchPluginInst != null)
                            {
                                bool alwaysUnlocked = true;
                                try
                                {
                                    var auEntry = _csParryAlwaysUnlockedField?.GetValue(_crossStitchPluginInst);
                                    var valProp = auEntry?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                                    if (valProp != null) alwaysUnlocked = (bool)valProp.GetValue(auEntry);
                                }
                                catch (System.Exception __apiEx_55) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1741", __apiEx_55); }

                                if (alwaysUnlocked && PlayerData.HasInstance && PlayerData.instance != null)
                                {
                                    var parryTool = _cachedParryToolItem ?? (_cachedParryToolItem = ToolItemManager.GetToolByName("Parry"));
                                    if (parryTool != null)
                                    {
                                        if (!parryTool.IsUnlocked)
                                        {
                                            try { parryTool.Unlock(null, ToolItem.PopupFlags.Default); } catch (System.Exception __apiEx_56) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1750", __apiEx_56); }
                                        }
                                        if (!parryTool.IsEquipped)
                                        {
                                            try { ToolItemManager.AutoEquip(parryTool); } catch (System.Exception __apiEx_57) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1754", __apiEx_57); }
                                        }
                                    }
                                }

                                if (hc != null && hc.silkSpecialFSM != null && hc.silkSpecialFSM.Fsm != null &&
                                    hc.silkSpecialFSM.Fsm.Initialized && hc.silkSpecialFSM.ActiveStateName == "Idle")
                                {
                                    int fsmId = hc.silkSpecialFSM.GetInstanceID();
                                    if (_lastCrossStitchPatchedFsmId != fsmId)
                                    {
                                        float parryTime = 0.25f;
                                        try
                                        {
                                            var ptEntry = _csParryTimeOverrideField?.GetValue(_crossStitchPluginInst);
                                            var valProp = ptEntry?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                                            if (valProp != null) parryTime = (float)valProp.GetValue(ptEntry);
                                        }
                                        catch (System.Exception __apiEx_58) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1772", __apiEx_58); }

                                        var parryStance = PlayMakerHybridCLRBridge.GetFsmStateSafe(hc.silkSpecialFSM.Fsm, "Parry Stance");
                                        if (parryStance != null)
                                        {
                                            var acts = PlayMakerHybridCLRBridge.GetStateActionsSafe(parryStance);
                                            if (acts != null)
                                            {
                                                for (int a = 0; a < acts.Length; a++)
                                                {
                                                    if (acts[a] is HutongGames.PlayMaker.Actions.Wait waitAct && waitAct.time != null)
                                                    {
                                                        waitAct.time.Value = parryTime;
                                                        _lastCrossStitchPatchedFsmId = fsmId;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        if (IsCrossStitchFreeParryEnabled() && hc.silkSpecialFSM.Fsm.States != null)
                                        {
                                            foreach (var st in hc.silkSpecialFSM.Fsm.States)
                                            {
                                                if (st == null || st.Name == null || st.Name.IndexOf("Parry", StringComparison.OrdinalIgnoreCase) < 0)
                                                    continue;
                                                var stActs = PlayMakerHybridCLRBridge.GetStateActionsSafe(st);
                                                if (stActs == null) continue;
                                                for (int a = 0; a < stActs.Length; a++)
                                                {
                                                    if (stActs[a] is HutongGames.PlayMaker.Actions.TakeSilk tsAct && tsAct.amount != null)
                                                    {
                                                        tsAct.amount.Value = 0;
                                                        tsAct.amount.UseVariable = false;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                _hasCrossStitchOrEternalWeaverPresent = _eternalWeaverSpoolHooksInstalled || _crossStitchPluginInst != null || foundAny;
                _checkedCrossStitchAndEternalWeaverPresence = true;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HarmonyBridge] EnsureCrossStitchMadnessAndEternalWeaverRuntime warning: {ex.Message}");
            }
        }

        private static Assembly[] _cachedAssembliesArray;
        private static int _lastAssemblyCacheTick;

        public static void InvalidateCachedLoadedAssemblies()
        {
            _cachedAssembliesArray = null;
            _checkedCrossStitchAndEternalWeaverPresence = false;
        }

        public static Assembly[] GetCachedLoadedAssemblies()
        {
            int nowTick = Environment.TickCount;
            var arr = _cachedAssembliesArray;
            if (arr == null || unchecked(nowTick - _lastAssemblyCacheTick) > 2000)
            {
                try
                {
                    arr = AppDomain.CurrentDomain.GetAssemblies();
                    _cachedAssembliesArray = arr;
                    _lastAssemblyCacheTick = nowTick;
                }
                catch
                {
                    return arr ?? Array.Empty<Assembly>();
                }
            }
            return arr;
        }

        public static void AppDomain_NoOpEventHandler(object domain, object handler)
        {
            // Stripped in Unity 6000 Android IL2CPP (AppDomain.add_AssemblyLoad / add_ProcessExit)
        }

        public static void BepInExHookPatchSafe(MethodInfo original, MethodInfo prefix, MethodInfo postfix, int priority = 0)
        {
            if (original == null) return;
            try
            {
                string modName = (prefix ?? postfix)?.DeclaringType?.Assembly?.GetName()?.Name ?? "BepInExHookMod";
                RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(modName, original, prefix, postfix, null, priority);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "BepInExHookPatchSafe", ex);
            }
        }

        public static void ToolItemManager_GetAttackToolBinding_Postfix(ToolItem __0, ref AttackToolBinding? __result)
        {
            try
            {
                if (!__result.HasValue && __0 != null && string.Equals(__0.name, "Parry", StringComparison.OrdinalIgnoreCase))
                {
                    __result = AttackToolBinding.Neutral;
                }
            }
            catch (System.Exception __apiEx_60) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L1829", __apiEx_60); }
        }

        public static string SteamInput_GetGlyphPNGForActionOrigin(int origin, int size, uint flags)
        {
            return string.Empty;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Diagnostics.ProcessStartInfo, System.Collections.ObjectModel.Collection<string>> _psiArgLists =
            new System.Runtime.CompilerServices.ConditionalWeakTable<System.Diagnostics.ProcessStartInfo, System.Collections.ObjectModel.Collection<string>>();

        public static System.Collections.ObjectModel.Collection<string> ProcessStartInfo_GetArgumentList(System.Diagnostics.ProcessStartInfo psi)
        {
            if (psi == null) return new System.Collections.ObjectModel.Collection<string>();
            if (!_psiArgLists.TryGetValue(psi, out var list))
            {
                list = new System.Collections.ObjectModel.Collection<string>();
                _psiArgLists.Add(psi, list);
            }
            return list;
        }

        public static string PathJoinTwoStrings(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? string.Empty;
            if (string.IsNullOrEmpty(b)) return a;
            return Path.Combine(a, b);
        }

        public static string ColorUtility_ToHtmlStringRGB(Color color)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255);
            return $"{r:X2}{g:X2}{b:X2}";
        }

        public static string ColorUtility_ToHtmlStringRGBA(Color color)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255);
            int a = Mathf.Clamp(Mathf.RoundToInt(color.a * 255f), 0, 255);
            return $"{r:X2}{g:X2}{b:X2}{a:X2}";
        }

        private static bool _seededBuiltinModTranslations;
        private static void SeedBuiltinModTranslationsIfNeeded()
        {
            if (_seededBuiltinModTranslations) return;
            _seededBuiltinModTranslations = true;

            var weaverEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CREST_NAME"] = "Weaver",
                ["CREST_DESC"] = "Gain more silk, at the cost of weaving it into every attack.",
                ["VYRMLING_NAME"] = "Noodlevyrn",
                ["VYRMLING_DESC"] = "A charged worm that has infested the baths of the Citadel, it discharges electricity to defend itself.",
                ["VYRMLING_NOTES"] = "A young pest, its only fitting that creatures contained, will breach their confines"
            };

            string[] weaverSheets = new[]
            {
                "Mods.io.github.eternalone11.Weaver_Crest",
                "Mods.Weaver_Crest",
                "io.github.eternalone11.Weaver_Crest",
                "Weaver_Crest"
            };

            foreach (var sn in weaverSheets)
            {
                if (!_i18nOverridesBySheet.TryGetValue(sn, out var sd))
                {
                    sd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _i18nOverridesBySheet[sn] = sd;
                }
                foreach (var kv in weaverEntries)
                {
                    sd[kv.Key] = kv.Value;
                    _i18nFlatKeys[kv.Key] = kv.Value;
                    _i18nFlatKeys[$"{sn}.{kv.Key}"] = kv.Value;
                    _i18nFlatKeys[$"{sn}/{kv.Key}"] = kv.Value;
                    _i18nFlatKeys[$"!!{sn}/{kv.Key}!!"] = kv.Value;
                    _i18nFlatKeys[$"!_{kv.Key}"] = kv.Value;
                }
            }

            var susulWeaverEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ABILITY_AIR_USE"] = "Attack in midair",
                ["ABILITY_BLINK_USE"] = "Hold both directions",
                ["ABILITY_BACKSTEP_USE"] = "Backstep",
                ["NAME"] = "Weaver Crest",
                ["DESCRIPTION"] = "Crest left behind by the Weavers. Forsake the needle's point and weave an unbroken assault of silk and claw.",
                ["PREFIX"] = "Weaver",
                ["GET_DESC"] = "Acquire the Weaver Crest",
                ["EQUIP"] = "Equip the Weaver Crest",
                ["SACRED_HEART_NAME"] = "Weaver Sacred Heart",
                ["SACRED_HEART_DESC"] = "Blue tool. Thrown red tools seek out nearby foes.",
                ["THREAD_NEEDLE_NAME"] = "Threading Needle",
                ["THREAD_NEEDLE_DESC"] = "An ancient Weaver implement that swiftly draws out silk from within, hastening silk skills. Casting the same skill over and over weakens it.",
                ["CROSS_SLASH_NAME"] = "Silk Waltz",
                ["CROSS_SLASH_DESC"] = "Weave a field of silk, draw the foes caught within to its heart, cut through them again and again, then drive them into the ground.",
                ["PHANTOM_DIVE_NAME"] = "Phantom Descent",
                ["PHANTOM_DIVE_DESC"] = "Leap beyond sight, choose where to fall, then descend upon it, striking everything around.",
                ["BRUTAL_SMASH_NAME"] = "Brutal Smash",
                ["BRUTAL_SMASH_DESC"] = "Gather silk and take aim, then hurl a blade of silk that bursts where it strikes.",
                ["COCOON_NAME"] = "Cocoon Shell",
                ["COCOON_DESC"] = "Wind a cocoon of silk around yourself that stops attacks flying in.",
                ["RECORD_NAME"] = "Otherworld Rune Harp",
                ["RECORD_DESC"] = "A Weaver relic recording knowledge from another world. An unfamiliar art lies within its silk strings. The relic dealer in Bellhart may decipher it once her collection holds at least eight relics for reference.",
                ["RECORD_SHOP_DESC"] = "A rune harp brought from afar, its strings marked with unfamiliar patterns. They say it records knowledge from another world. A collector in Bellhart studies such old things.",
                ["RECORD_DECODE"] = "Ah! These silk strings carry the cadence of an old Weaver art from beyond Pharloom. Let me transcribe its melody for you...",
                ["RECORD_REFUSE"] = "Such intricate markings on this rune harp... Bring me at least eight relics for my collection so I have enough reference to decipher it.",
                ["SILK_WALTZ_NAME"] = "Silk Waltz",
                ["SILK_WALTZ_DESC"] = "Weave a field of silk, draw the foes caught within to its heart, cut through them again and again, then drive them into the ground."
            };

            string[] susulSheets = new[]
            {
                "WeaverCrest",
                "Mods.WeaverCrest",
                "Mods.codex.silksong.weavercrest",
                "codex.silksong.weavercrest"
            };

            foreach (var sn in susulSheets)
            {
                if (!_i18nOverridesBySheet.TryGetValue(sn, out var sd))
                {
                    sd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _i18nOverridesBySheet[sn] = sd;
                }
                foreach (var kv in susulWeaverEntries)
                {
                    string[] keyVariants = kv.Key.StartsWith("WEAVER_", StringComparison.OrdinalIgnoreCase)
                        ? new[] { kv.Key }
                        : new[] { kv.Key, "WEAVER_" + kv.Key };
                    foreach (var kVar in keyVariants)
                    {
                        sd[kVar] = kv.Value;
                        _i18nFlatKeys[kVar] = kv.Value;
                        _i18nFlatKeys[$"{sn}.{kVar}"] = kv.Value;
                        _i18nFlatKeys[$"{sn}/{kVar}"] = kv.Value;
                        _i18nFlatKeys[$"!!{sn}/{kVar}!!"] = kv.Value;
                        _i18nFlatKeys[$"!_{kVar}"] = kv.Value;
                        _i18nFlatKeys[$"#!#{kVar}#I#"] = kv.Value;
                        _i18nFlatKeys[$"#!#{kVar}#!#"] = kv.Value;
                    }
                }
            }

            // Seed Bonemaster title card & memory prompt keys into mod sheets and flat keys (never overwrite vanilla sheet names)
            var bonemasterEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BONEMASTER_SUPER"] = "Bonemaster",
                ["BONEMASTER_MAIN"] = "Skarr",
                ["BONEMASTER_SUB"] = "",
                ["THREAD_MEMORY_BONEMASTER"] = "Enter Memory",
                ["HUNTER_MESSAGE"] = "A proud warrior who mastered the art of bone and claw."
            };
            string[] bonemasterModSheets = new[]
            {
                "Mods.Bonemaster",
                "Mods.io.github.kiahah.bonemaster",
                "Bonemaster"
            };
            foreach (var sn in bonemasterModSheets)
            {
                if (!_i18nOverridesBySheet.TryGetValue(sn, out var sd))
                {
                    sd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _i18nOverridesBySheet[sn] = sd;
                }
                foreach (var kv in bonemasterEntries)
                {
                    sd[kv.Key] = kv.Value;
                    _i18nFlatKeys[kv.Key] = kv.Value;
                    _i18nFlatKeys[$"{sn}.{kv.Key}"] = kv.Value;
                    _i18nFlatKeys[$"{sn}/{kv.Key}"] = kv.Value;
                    _i18nFlatKeys[$"!!{sn}/{kv.Key}!!"] = kv.Value;
                    _i18nFlatKeys[$"!_{kv.Key}"] = kv.Value;
                }
            }
            foreach (var kv in bonemasterEntries)
            {
                _i18nFlatKeys[$"Titles.{kv.Key}"] = kv.Value;
                _i18nFlatKeys[$"Titles/{kv.Key}"] = kv.Value;
                _i18nFlatKeys[$"!!Titles/{kv.Key}!!"] = kv.Value;
                _i18nFlatKeys[$"Song/{kv.Key}"] = kv.Value;
                _i18nFlatKeys[$"!!Song/{kv.Key}!!"] = kv.Value;
                _i18nFlatKeys[$"Hornet/{kv.Key}"] = kv.Value;
                _i18nFlatKeys[$"!!Hornet/{kv.Key}!!"] = kv.Value;
            }
        }

        public static void RegisterModFolderTranslations(string modName, string modDir)
        {
            SeedBuiltinModTranslationsIfNeeded();
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return;
            try
            {
                var jsonFiles = Directory.GetFiles(modDir, "*.json", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
                            fn.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                            fn.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                            return false;
                        return true;
                    })
                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Equals("EN", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                string modGuid = SilksongMobileAPIPlugin.GetModGuidForFolder(modDir) ?? modName;
                string[] targetSheets = new[]
                {
                    $"Mods.{modGuid}",
                    $"Mods.{modName}",
                    modGuid,
                    modName
                };

                if (_i18nOverridesBySheet.TryGetValue($"Mods.{modName}", out var existingEmbedded) && existingEmbedded != null && existingEmbedded.Count > 0)
                {
                    foreach (var sName in targetSheets)
                    {
                        if (string.IsNullOrEmpty(sName)) continue;
                        if (!_i18nOverridesBySheet.TryGetValue(sName, out var sheetDict))
                        {
                            sheetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            _i18nOverridesBySheet[sName] = sheetDict;
                        }
                        foreach (var kv in existingEmbedded)
                        {
                            if (!sheetDict.ContainsKey(kv.Key))
                                sheetDict[kv.Key] = kv.Value;
                            _i18nFlatKeys[kv.Key] = kv.Value;
                            _i18nFlatKeys[$"{sName}.{kv.Key}"] = kv.Value;
                            _i18nFlatKeys[$"{sName}/{kv.Key}"] = kv.Value;
                            _i18nFlatKeys[$"!!{sName}/{kv.Key}!!"] = kv.Value;
                            _i18nFlatKeys[$"!_{kv.Key}"] = kv.Value;
                        }
                    }
                    _lastSyncedSheetCount = -1;
                }

                int count = 0;
                foreach (var jf in jsonFiles)
                {
                    string jfName = Path.GetFileName(jf) ?? "";
                    if (jfName.Equals("hk_lang.json", StringComparison.OrdinalIgnoreCase) ||
                        jfName.Equals("hk_common.json", StringComparison.OrdinalIgnoreCase) ||
                        jfName.Equals("hk_playerdata.json", StringComparison.OrdinalIgnoreCase) ||
                        jfName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                        jfName.Equals("hall.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool isEn = Path.GetFileNameWithoutExtension(jf).Equals("EN", StringComparison.OrdinalIgnoreCase);
                    string text = File.ReadAllText(jf);
                    foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                    {
                        string k = m.Groups[1].Value;
                        string v = Regex.Unescape(m.Groups[2].Value);
                        if (string.IsNullOrEmpty(k)) continue;

                        foreach (var sName in targetSheets)
                        {
                            if (string.IsNullOrEmpty(sName)) continue;
                            if (!_i18nOverridesBySheet.TryGetValue(sName, out var sheetDict))
                            {
                                sheetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                _i18nOverridesBySheet[sName] = sheetDict;
                            }
                            if (isEn || !sheetDict.ContainsKey(k))
                            {
                                sheetDict[k] = v;
                                _i18nFlatKeys[k] = v;
                                _i18nFlatKeys[$"{sName}.{k}"] = v;
                                _i18nFlatKeys[$"{sName}/{k}"] = v;
                                _i18nFlatKeys[$"!!{sName}/{k}!!"] = v;
                                _i18nFlatKeys[$"!_{k}"] = v;
                            }
                        }
                        if (isEn) count++;
                    }
                }

                EnsureI18nHooksInstalled();
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[I18NBridge] Registered localization sheets for '{modName}' (sheet='Mods.{modGuid}', totalSheets={_i18nOverridesBySheet.Count}).");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[I18NBridge] Failed scanning localization files in {modDir}: {ex.Message}");
            }
        }

        private static FieldInfo _langCurrentEntrySheetsField;
        private static FieldInfo _invCrestNameDisplaysField;
        private static FieldInfo _invCrestDescDisplayField;
        private static bool _langFieldResolved;
        private static bool _tmpHookInstalled;
        private static bool _syncedIntoStitchwork;
        private static int _lastSyncedSheetCount = -1;
        private static object _lastSyncedEntrySheetsRef;
        private static readonly Regex _i18nBangTokenRegex = new Regex(@"!!([^!<>]+)!!", RegexOptions.Compiled);
        private static readonly Regex _i18nSheetPrefixRegex = new Regex(@"\b(UI|Fishes|Hungry Craw)\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);

        public static string ResolvePlaceholderText(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            if (_i18nFlatKeys.TryGetValue(input, out var direct))
                return direct;

            if (input.StartsWith("!!", StringComparison.Ordinal) && input.EndsWith("!!", StringComparison.Ordinal) && input.Length > 4)
            {
                string inner = input.Substring(2, input.Length - 4);
                int blankIdx = inner.IndexOf(" (BLANK)", StringComparison.Ordinal);
                if (blankIdx > 0) inner = inner.Substring(0, blankIdx);
                if (_i18nFlatKeys.TryGetValue(inner, out var innerVal))
                    return innerVal;
                int slashIdx = inner.LastIndexOf('/');
                if (slashIdx > 0 && slashIdx + 1 < inner.Length)
                {
                    string sheet = inner.Substring(0, slashIdx);
                    string key = inner.Substring(slashIdx + 1);
                    if (_i18nOverridesBySheet.TryGetValue(sheet, out var sDict) && sDict.TryGetValue(key, out var sVal))
                        return sVal;
                    if (_i18nFlatKeys.TryGetValue(key, out var kVal))
                        return kVal;
                }
            }
            else if (input.StartsWith("!_", StringComparison.Ordinal) && input.Length > 2)
            {
                string key = input.Substring(2);
                if (_i18nFlatKeys.TryGetValue(key, out var kVal))
                    return kVal;
            }
            else if (input.StartsWith("#!#", StringComparison.Ordinal) && input.EndsWith("#!#", StringComparison.Ordinal) && input.Length > 6)
            {
                string key = input.Substring(3, input.Length - 6);
                if (_i18nFlatKeys.TryGetValue(key, out var kVal))
                    return kVal;
            }
            if (input.IndexOf("!!", StringComparison.Ordinal) >= 0)
            {
                input = _i18nBangTokenRegex.Replace(input, m =>
                {
                    string token = m.Value;
                    string inner = m.Groups[1].Value;
                    int blankIdx = inner.IndexOf(" (BLANK)", StringComparison.Ordinal);
                    if (blankIdx > 0) inner = inner.Substring(0, blankIdx);
                    if (_i18nFlatKeys.TryGetValue(token, out var dv)) return dv;
                    if (_i18nFlatKeys.TryGetValue(inner, out var iv)) return iv;
                    int slashIdx = inner.LastIndexOf('/');
                    if (slashIdx > 0 && slashIdx + 1 < inner.Length)
                    {
                        string sheet = inner.Substring(0, slashIdx);
                        string key = inner.Substring(slashIdx + 1);
                        if (_i18nOverridesBySheet.TryGetValue(sheet, out var sDict) && sDict.TryGetValue(key, out var sVal))
                            return sVal;
                        if (_i18nFlatKeys.TryGetValue(key, out var kVal))
                            return kVal;
                    }
                    return token;
                });
            }
            if (input.IndexOf("UI.", StringComparison.Ordinal) >= 0 ||
                input.IndexOf("Fishes.", StringComparison.Ordinal) >= 0 ||
                input.IndexOf("Hungry Craw.", StringComparison.Ordinal) >= 0)
            {
                input = _i18nSheetPrefixRegex.Replace(input, m =>
                {
                    string full = m.Value;
                    string sheet = m.Groups[1].Value;
                    string key = m.Groups[2].Value;
                    if (_i18nFlatKeys.TryGetValue(full, out var fv)) return fv;
                    if (_i18nOverridesBySheet.TryGetValue(sheet, out var sDict) && sDict.TryGetValue(key, out var sv))
                        return sv;
                    if (_i18nFlatKeys.TryGetValue(key, out var kv)) return kv;
                    return full;
                });
            }
            return input;
        }

        public static void LocalisedString_ToString_Postfix(ref string __result)
        {
            if (!string.IsNullOrEmpty(__result) &&
                (__result.IndexOf("!!", StringComparison.Ordinal) >= 0 ||
                 __result.StartsWith("!_", StringComparison.Ordinal) ||
                 __result.StartsWith("#!#", StringComparison.Ordinal) ||
                 __result.IndexOf("UI.", StringComparison.Ordinal) >= 0 ||
                 __result.IndexOf("Fishes.", StringComparison.Ordinal) >= 0 ||
                 __result.IndexOf("Hungry Craw.", StringComparison.Ordinal) >= 0))
            {
                __result = ResolvePlaceholderText(__result);
            }
        }

        public static void TripleProjectiles_FilterObject(GameObject obj, string sourceMethod)
        {
            if (obj == null) return;
            try
            {
                string n = obj.name ?? "";
                if (string.IsNullOrEmpty(n) || n.IndexOf("[TPClone]", StringComparison.Ordinal) >= 0)
                    return;

                string lower = n.ToLowerInvariant();
                if (lower.Contains("hit") || lower.Contains("dust") || lower.Contains("pt") ||
                    lower.Contains("particle") || lower.Contains("flash") || lower.Contains("effect") ||
                    lower.Contains("splash") || lower.Contains("spatter") || lower.Contains("drip") ||
                    lower.Contains("steam") || lower.Contains("smoke") || lower.Contains("blood") ||
                    lower.Contains("audio") || lower.Contains("glow") || lower.Contains("chunk") ||
                    lower.Contains("shard") || lower.Contains("geo") || lower.Contains("rosary") ||
                    lower.Contains("hornet") || lower.Contains("slash") || lower.Contains("prompt") ||
                    lower.Contains("corpse") || lower.Contains("death") || lower.Contains("weaver") ||
                    lower.Contains("thread") || lower.Contains("silk") || lower.Contains("tool") ||
                    lower.Contains("hatchling") || lower.Contains("bomb") || lower.Contains("wisp") ||
                    lower.Contains("crystal") || lower.Contains("conch") || lower.Contains("maggot"))
                {
                    return;
                }

                // Never clone enemies or bosses
                if (obj.GetComponent<HealthManager>() != null)
                    return;

                // Only clone real physics projectiles that deal damage
                var rb = obj.GetComponent<Rigidbody2D>();
                if (rb == null)
                    return;
                if (obj.GetComponent<DamageHero>() == null && obj.GetComponent<DamageEnemies>() == null)
                    return;

                if (SilksongMobileAPIPlugin.Instance != null)
                {
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(SafeCloneProjectileCoroutine(obj));
                }
            }
            catch (System.Exception __apiEx_61) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2264", __apiEx_61); }
        }

        private static System.Collections.IEnumerator SafeCloneProjectileCoroutine(GameObject original)
        {
            yield return new WaitForSeconds(0.05f);
            if (original == null || !original.activeInHierarchy)
                yield break;

            try
            {
                var origRb = original.GetComponent<Rigidbody2D>();
                if (origRb == null) yield break;
                Vector2 baseVel = GetLinearVelocity(origRb);
                if (baseVel.sqrMagnitude < 0.01f) yield break;

                float[] angles = new float[] { -20f, 20f };
                for (int i = 0; i < angles.Length; i++)
                {
                    var clone = UnityEngine.Object.Instantiate(original, original.transform.position, original.transform.rotation, original.transform.parent);
                    if (clone == null) continue;
                    clone.name = original.name + " [TPClone]";

                    // Remove any ProjectileCloneMarker or AutoRecycleSelf on the clone so it never corrupts ObjectPool
                    var comps = clone.GetComponents<MonoBehaviour>();
                    if (comps != null)
                    {
                        for (int c = 0; c < comps.Length; c++)
                        {
                            var mb = comps[c];
                            if (mb == null) continue;
                            string tn = mb.GetType().Name ?? "";
                            if (tn == "ProjectileCloneMarker" || tn.IndexOf("Recycle", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                UnityEngine.Object.Destroy(mb);
                            }
                        }
                    }

                    var cRb = clone.GetComponent<Rigidbody2D>();
                    if (cRb != null)
                    {
                        float rad = angles[i] * Mathf.Deg2Rad;
                        float sin = Mathf.Sin(rad);
                        float cos = Mathf.Cos(rad);
                        Vector2 newVel = new Vector2(baseVel.x * cos - baseVel.y * sin, baseVel.x * sin + baseVel.y * cos);
                        SetLinearVelocity(cRb, newVel);
                        clone.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(newVel.y, newVel.x) * Mathf.Rad2Deg);
                    }
                    UnityEngine.Object.Destroy(clone, 4.5f);
                }
            }
            catch (System.Exception __apiEx_62) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2316", __apiEx_62); }
        }

        private static bool _stylemeterResolved;
        private static Assembly _stylemeterAssembly;
        private static MethodInfo _stylemeterBeforeHitMi;
        private static MethodInfo _stylemeterAfterHitMi;
        private static MethodInfo _stylemeterBeforeDieMi;
        private static MethodInfo _stylemeterFindFontsMi;
        private static MethodInfo _stylemeterBuildHudMi;
        private static FieldInfo _stylemeterRootField;
        private static bool _stylemeterHudPrebuilt;
        private static FieldInfo _hmLastHitInstanceField;

        private static bool _weaverPoolPatchesResolved;
        private static int _weaverPoolLastAsmCount = -1;
        private static MethodInfo _weaverThreadNeedlePoolPostMi;
        private static MethodInfo _weaverAttackPoolOwnerPostMi;
        private static MethodInfo _weaverNeedolinPoolPostMi;

        private static void EnsureWeaverPoolPatchesResolved()
        {
            if (_weaverPoolPatchesResolved) return;
            try
            {
                var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
                if (asms.Length == _weaverPoolLastAsmCount) return;
                _weaverPoolLastAsmCount = asms.Length;
                for (int i = 0; i < asms.Length; i++)
                {
                    var asm = asms[i];
                    if (asm == null) continue;
                    string an = asm.GetName().Name ?? "";
                    if (an.StartsWith("WeaverCrest", StringComparison.OrdinalIgnoreCase))
                    {
                        _weaverPoolPatchesResolved = true;
                        const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                        _weaverThreadNeedlePoolPostMi = asm.GetType("WeaverCrestMod.ThreadNeedlePoolPatch", false)?.GetMethod("Postfix", SFlags);
                        _weaverAttackPoolOwnerPostMi = asm.GetType("WeaverCrestMod.WeaverAttackPoolOwnerPatch", false)?.GetMethod("Postfix", SFlags);
                        _weaverNeedolinPoolPostMi = asm.GetType("WeaverCrestMod.NeedolinPooledObjectSpawnPatch", false)?.GetMethod("Postfix", SFlags);
                        break;
                    }
                }
            }
            catch (System.Exception __apiEx_63) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2360", __apiEx_63); }
        }

        public static void DamageEnemies_OnEnable_WeaverPoolBridge_Postfix(DamageEnemies __instance)
        {
            if (__instance == null) return;
            try
            {
                EnsureWeaverPoolPatchesResolved();
                if (!_weaverPoolPatchesResolved) return;
                if (HeroController.SilentInstance == null || !PlayerData.HasInstance || PlayerData.instance == null)
                    return;
                var go = __instance.gameObject;
                if (go == null) return;
                string gn = go.name ?? "";
                if (gn.IndexOf("Weaver", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Hornet", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Hero", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Tool", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Pin", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Needle", StringComparison.OrdinalIgnoreCase) < 0 &&
                    gn.IndexOf("Slash", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }
                _weaverThreadNeedlePoolPostMi?.Invoke(null, new object[] { go });
                _weaverAttackPoolOwnerPostMi?.Invoke(null, new object[] { go });
                _weaverNeedolinPoolPostMi?.Invoke(null, new object[] { go, go });
            }
            catch (System.Exception __apiEx_64) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2389", __apiEx_64); }
        }

        private static int _stylemeterLastAsmCount = -1;

        private static void EnsureStylemeterResolved()
        {
            if (_hmLastHitInstanceField == null)
            {
                try
                {
                    _hmLastHitInstanceField = typeof(HealthManager).GetField("lastHitInstance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch (System.Exception __apiEx_65) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2402", __apiEx_65); }
            }
            if (_stylemeterResolved) return;
            try
            {
                var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
                if (asms.Length == _stylemeterLastAsmCount) return;
                _stylemeterLastAsmCount = asms.Length;
                for (int i = 0; i < asms.Length; i++)
                {
                    var asm = asms[i];
                    if (asm == null) continue;
                    string an = asm.GetName().Name ?? "";
                    if (an.StartsWith("Stylemeter", StringComparison.OrdinalIgnoreCase))
                    {
                        _stylemeterAssembly = asm;
                        break;
                    }
                }
                if (_stylemeterAssembly == null) return;
                _stylemeterResolved = true;

                var hitHooksType = _stylemeterAssembly.GetType("Stylemeter.Game.HitHooks", false);
                if (hitHooksType != null)
                {
                    _stylemeterBeforeHitMi = hitHooksType.GetMethod("BeforeHit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _stylemeterAfterHitMi = hitHooksType.GetMethod("AfterHit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _stylemeterBeforeDieMi = hitHooksType.GetMethod("BeforeDie", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                var hudType = _stylemeterAssembly.GetType("Stylemeter.Game.Hud.MeterHud", false);
                if (hudType != null)
                {
                    _stylemeterFindFontsMi = hudType.GetMethod("FindFonts", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _stylemeterBuildHudMi = hudType.GetMethod("Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _stylemeterRootField = hudType.GetField("root", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }
            catch (System.Exception __apiEx_66) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2440", __apiEx_66); }
        }

        public static void PrewarmStylemeterHudIfNeeded()
        {
            if (_stylemeterHudPrebuilt) return;
            try
            {
                EnsureStylemeterResolved();
                if (_stylemeterAssembly == null || _stylemeterFindFontsMi == null || _stylemeterBuildHudMi == null) return;
                if (_stylemeterRootField != null && _stylemeterRootField.GetValue(null) as UnityEngine.Object != null)
                {
                    _stylemeterHudPrebuilt = true;
                    return;
                }
                bool fontsReady = (bool)_stylemeterFindFontsMi.Invoke(null, null);
                if (fontsReady)
                {
                    _stylemeterBuildHudMi.Invoke(null, null);
                    var rootGo = _stylemeterRootField?.GetValue(null) as GameObject;
                    if (rootGo != null)
                    {
                        rootGo.SetActive(false);
                        _stylemeterHudPrebuilt = true;
                        SilksongMobileAPIPlugin.LogDiagnosticInfo("[StylemeterBridge] Pre-built Stylemeter HUD during scene load to eliminate mid-combat lag spike.");
                    }
                }
            }
            catch (System.Exception __apiEx_67) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2468", __apiEx_67); }
        }

        // Invoked after HealthManager.NonFatalHit(bool) inside HealthManager.TakeDamage, where this.lastHitInstance
        // already holds the complete, authentic HitInstance struct constructed by DamageEnemies.DoDamage.
        private static AttackTypes _lastObservedHitAttackType = AttackTypes.Nail;
        private static bool _hasObservedHitInstance;

        public static void HealthManager_IsBlockingByDirection_Prefix(HealthManager __instance, int __0, AttackTypes __1, SpecialTypes __2)
        {
            if (__instance == null) return;
            try
            {
                RuntimeHybridCLRAdapterBuilder.InvokePreDamageHitInstanceChains(__instance, __0, __1, __2);
            }
            catch (System.Exception __apiEx_preHit)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "IsBlockingByDirection_Prefix", __apiEx_preHit);
            }
        }

        private static FieldInfo _ncStateField;
        private static FieldInfo _ncThisField;
        private static FieldInfo _ncDirField;
        private static FieldInfo _ncTagField;
        private static FieldInfo _ncPosField;
        [ThreadStatic] private static DamageHero _pendingNailClashPostfixInstance;
        [ThreadStatic] private static float _pendingNailClashDirection;
        [ThreadStatic] private static string _pendingNailClashTag;
        [ThreadStatic] private static Vector3 _pendingNailClashPos;

        private static void EnsureNailClashStateMachineFields(Type smType)
        {
            if (_ncStateField != null || smType == null) return;
            const BindingFlags IFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _ncStateField = smType.GetField("<>1__state", IFlags);
            _ncThisField = smType.GetField("<>4__this", IFlags);
            _ncDirField = smType.GetField("direction", IFlags);
            _ncTagField = smType.GetField("colliderTag", IFlags);
            _ncPosField = smType.GetField("clasherPos", IFlags);
        }

        public static void DamageHero_NailClashMoveNext_Prefix(object __instance)
        {
            _pendingNailClashPostfixInstance = null;
            if (__instance == null) return;
            try
            {
                EnsureNailClashStateMachineFields(__instance.GetType());
                if (_ncStateField == null || _ncThisField == null) return;
                int st = (int)_ncStateField.GetValue(__instance);
                if (st == 0)
                {
                    var dh = _ncThisField.GetValue(__instance) as DamageHero;
                    if (dh != null)
                    {
                        float dir = _ncDirField != null ? (float)_ncDirField.GetValue(__instance) : 0f;
                        string tag = (_ncTagField != null ? _ncTagField.GetValue(__instance) as string : null) ?? "Nail Attack";
                        Vector3 pos = _ncPosField != null ? (Vector3)_ncPosField.GetValue(__instance) : Vector3.zero;
                        _pendingNailClashPostfixInstance = dh;
                        _pendingNailClashDirection = dir;
                        _pendingNailClashTag = tag;
                        _pendingNailClashPos = pos;
                        RuntimeHybridCLRAdapterBuilder.InvokeBridgedDamageHeroNailClash(dh, dir, tag, pos, isPrefix: true);
                    }
                }
            }
            catch (System.Exception __apiEx_ncPre)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "NailClashMoveNext_Prefix", __apiEx_ncPre);
            }
        }

        public static void DamageHero_NailClashMoveNext_Postfix(object __instance)
        {
            var dh = _pendingNailClashPostfixInstance;
            if (dh == null) return;
            _pendingNailClashPostfixInstance = null;
            try
            {
                RuntimeHybridCLRAdapterBuilder.InvokeBridgedDamageHeroNailClash(
                    dh,
                    _pendingNailClashDirection,
                    _pendingNailClashTag,
                    _pendingNailClashPos,
                    isPrefix: false);
            }
            catch (System.Exception __apiEx_ncPost)
            {
                SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "NailClashMoveNext_Postfix", __apiEx_ncPost);
            }
        }

        public static void HealthManager_NonFatalHit_Postfix(HealthManager __instance)
        {
            if (__instance == null) return;
            try { AssetHelperBridge.EnforceAeternalEverwatcherParryOnlyOnHit(__instance); } catch (System.Exception __apiEx_68) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2479", __apiEx_68); }
            try
            {
                if (_hmLastHitInstanceField == null)
                    _hmLastHitInstanceField = typeof(HealthManager).GetField("lastHitInstance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_hmLastHitInstanceField != null && _hmLastHitInstanceField.GetValue(__instance) is HitInstance bridgedHit)
                {
                    _lastObservedHitAttackType = bridgedHit.AttackType;
                    _hasObservedHitInstance = true;
                    RuntimeHybridCLRAdapterBuilder.InvokeSkippedHitInstanceChains(__instance, ref bridgedHit);
                    _hmLastHitInstanceField.SetValue(__instance, bridgedHit);
                }
            }
            catch (System.Exception __apiEx_69) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2492", __apiEx_69); }
            try
            {
                EnsureStylemeterResolved();
                if (_stylemeterBeforeHitMi == null || _stylemeterAfterHitMi == null || _hmLastHitInstanceField == null) return;
                if (!_stylemeterHudPrebuilt) PrewarmStylemeterHudIfNeeded();

                object lastHitBoxed = _hmLastHitInstanceField.GetValue(__instance);
                if (!(lastHitBoxed is HitInstance hit)) return;

                int hpAfter = __instance.hp;
                float mult = hit.Multiplier > 0f ? hit.Multiplier : 1f;
                int dmg = Mathf.Max(1, Mathf.RoundToInt(hit.DamageDealt * mult));
                int hpBefore = hpAfter + dmg;

                __instance.hp = hpBefore;
                try
                {
                    _stylemeterBeforeHitMi.Invoke(null, new object[] { __instance, lastHitBoxed });
                }
                finally
                {
                    __instance.hp = hpAfter;
                }
                _stylemeterAfterHitMi.Invoke(null, new object[] { __instance });
            }
            catch (System.Exception __apiEx_70) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2518", __apiEx_70); }
        }

        // Invoked before HealthManager.SendDeathEvent() inside HealthManager.Die, scoring fatal hits and boss kills in Stylemeter.
        public static void HealthManager_SendDeathEvent_Prefix(HealthManager __instance)
        {
            if (__instance == null) return;
            try { AssetHelperBridge.EnforceAeternalEverwatcherParryOnlyOnHit(__instance); } catch (System.Exception __apiEx_71) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2525", __apiEx_71); }
            try
            {
                if (_hmLastHitInstanceField == null)
                    _hmLastHitInstanceField = typeof(HealthManager).GetField("lastHitInstance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                HitInstance bridgedFatalHit = default;
                if (_hmLastHitInstanceField != null && _hmLastHitInstanceField.GetValue(__instance) is HitInstance rawFatalHit)
                {
                    bridgedFatalHit = rawFatalHit;
                    _lastObservedHitAttackType = bridgedFatalHit.AttackType;
                    _hasObservedHitInstance = true;
                    RuntimeHybridCLRAdapterBuilder.InvokeSkippedHitInstanceChains(__instance, ref bridgedFatalHit);
                    _hmLastHitInstanceField.SetValue(__instance, bridgedFatalHit);
                }
                RuntimeHybridCLRAdapterBuilder.InvokeBridgedHealthManagerDieChains(__instance, bridgedFatalHit);
            }
            catch (System.Exception __apiEx_72) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2538", __apiEx_72); }
            try
            {
                EnsureStylemeterResolved();
                if (_stylemeterBeforeHitMi == null || _hmLastHitInstanceField == null) return;
                if (!_stylemeterHudPrebuilt) PrewarmStylemeterHudIfNeeded();

                object lastHitBoxed = _hmLastHitInstanceField.GetValue(__instance);
                if (!(lastHitBoxed is HitInstance hit)) return;

                int hpAfter = __instance.hp;
                bool wasDead = __instance.isDead;
                float mult = hit.Multiplier > 0f ? hit.Multiplier : 1f;
                int dmg = Mathf.Max(1, Mathf.RoundToInt(hit.DamageDealt * mult));

                __instance.hp = dmg;
                __instance.isDead = false;
                try
                {
                    _stylemeterBeforeHitMi.Invoke(null, new object[] { __instance, lastHitBoxed });
                }
                finally
                {
                    __instance.hp = hpAfter;
                    __instance.isDead = true;
                }

                if (_stylemeterBeforeDieMi != null)
                {
                    _stylemeterBeforeDieMi.Invoke(null, new object[] { __instance, hit.AttackType, hit.NailElement });
                }
                else if (_stylemeterAfterHitMi != null)
                {
                    _stylemeterAfterHitMi.Invoke(null, new object[] { __instance });
                }
                __instance.isDead = wasDead;
            }
            catch (System.Exception __apiEx_73) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2575", __apiEx_73); }

            // Bridge boss defeat to BossRush mod (mochasippadev) for bosses where HealthManager.Die trampoline is skipped
            try
            {
                NotifyBossRushDeath(__instance);
            }
            catch (System.Exception __apiEx_74) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2582", __apiEx_74); }
        }

        private static bool _bossRushResolved;
        private static int _bossRushLastAsmCount = -1;
        private static PropertyInfo _bossRushActiveProp;
        private static object _bossRushDiedSet;
        private static object _bossRushFatalDiedSet;
        private static MethodInfo _hashSetAddMethod;

        private static void EnsureBossRushDeathResolved()
        {
            if (_bossRushResolved && _bossRushActiveProp != null) return;
            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            if (_bossRushLastAsmCount == asms.Length) return;
            _bossRushLastAsmCount = asms.Length;

            foreach (var asm in asms)
            {
                string asmName = asm.GetName().Name;
                if (asmName == "BossRush" || asmName == "BossRush_MobileRt")
                {
                    var runType = asm.GetType("BossRush.Run.BossRushRun", false);
                    if (runType != null)
                    {
                        _bossRushActiveProp = runType.GetProperty("Active", BindingFlags.Static | BindingFlags.Public);
                    }

                    var fcType = asm.GetType("BossRush.Run.FightControl", false);
                    if (fcType != null)
                    {
                        var diedField = fcType.GetField("Died", BindingFlags.Static | BindingFlags.Public);
                        var fatalDiedField = fcType.GetField("FatalDied", BindingFlags.Static | BindingFlags.Public);
                        _bossRushDiedSet = diedField?.GetValue(null);
                        _bossRushFatalDiedSet = fatalDiedField?.GetValue(null);
                        if (_bossRushDiedSet != null)
                        {
                            _hashSetAddMethod = _bossRushDiedSet.GetType().GetMethod("Add", new Type[] { typeof(string) });
                        }
                    }
                    _bossRushResolved = true;
                    break;
                }
            }
        }

        private static void NotifyBossRushDeath(HealthManager hm)
        {
            if (hm == null) return;
            EnsureBossRushDeathResolved();

            if (_bossRushActiveProp == null || _hashSetAddMethod == null) return;

            bool isActive = false;
            try
            {
                isActive = (bool)_bossRushActiveProp.GetValue(null);
            }
            catch (System.Exception __apiEx_75) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2640", __apiEx_75); }

            if (!isActive) return;

            string objName = hm.gameObject?.name;
            if (string.IsNullOrEmpty(objName)) return;

            try
            {
                if (_bossRushDiedSet != null)
                    _hashSetAddMethod.Invoke(_bossRushDiedSet, new object[] { objName });
                if (_bossRushFatalDiedSet != null)
                    _hashSetAddMethod.Invoke(_bossRushFatalDiedSet, new object[] { objName });
            }
            catch (System.Exception __apiEx_76) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2654", __apiEx_76); }
        }

        private static MethodInfo _projVelSetDesiredMethod;
        private static bool _resolvedProjVelMethod;

        public static void WeaverSacredHeart_ApplyVelocitySafe(MonoBehaviour homingInstance, Vector2 velocity)
        {
            if (homingInstance == null) return;
            try
            {
                var hType = homingInstance.GetType();
                var vmField = hType.GetField("velocityManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var bodyField = hType.GetField("body", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var damagerField = hType.GetField("damager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var allDamagersField = hType.GetField("allDamagers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var vm = vmField?.GetValue(homingInstance) as UnityEngine.Object;
                if (vm != null)
                {
                    if (!_resolvedProjVelMethod)
                    {
                        _resolvedProjVelMethod = true;
                        _projVelSetDesiredMethod = vm.GetType().GetProperty("DesiredVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
                    }
                    _projVelSetDesiredMethod?.Invoke(vm, new object[] { velocity });
                }
                else
                {
                    var body = bodyField?.GetValue(homingInstance) as Rigidbody2D;
                    if (body != null)
                        SetLinearVelocity(body, velocity);
                }

                float angle = Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg;
                if (angle < 0f) angle += 360f;

                var dmg = damagerField?.GetValue(homingInstance) as DamageEnemies;
                if (dmg != null) dmg.direction = angle;

                var allDmgs = allDamagersField?.GetValue(homingInstance) as DamageEnemies[];
                if (allDmgs != null)
                {
                    for (int i = 0; i < allDmgs.Length; i++)
                    {
                        if (allDmgs[i] != null) allDmgs[i].direction = angle;
                    }
                }

                // Safely rotate the projectile transform in the new velocity direction without calling GetComponent<IProjectile>()!
                var rb2 = bodyField?.GetValue(homingInstance) as Rigidbody2D;
                Transform tr = rb2 != null ? rb2.transform : homingInstance.transform;
                if (tr != null && velocity.sqrMagnitude > 0.001f)
                {
                    float signedAngle = Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg;
                    if (tr.localScale.x < 0f)
                        signedAngle += 180f;
                    tr.localEulerAngles = new Vector3(0f, 0f, signedAngle);
                }
            }
            catch (System.Exception __apiEx_77) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2714", __apiEx_77); }
        }

        public static void WeaverProgression_SafeUnlockTool(ToolItem tool)
        {
            if (tool == null) return;
            try
            {
                var pd = PlayerData.instance;
                if (pd == null) return;
                string toolName = tool.name;
                if (string.IsNullOrEmpty(toolName)) return;
                var data = pd.GetToolData(toolName);
                data.IsUnlocked = true;
                data.IsHidden = false;
                data.AmountLeft = 0;
                data.HasBeenSeen = true;
                pd.SetToolData(toolName, data);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[WeaverSaveFix] Safely unlocked '{toolName}' in PlayerData without spawning tutorial popup.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[WeaverSaveFix] SafeUnlockTool warning: {ex.Message}");
            }
        }

        private static TMProOld.TMP_FontAsset _cachedValidGameFont;
        private static bool _searchedValidGameFont;

        private static TMProOld.TMP_FontAsset GetValidGameFont()
        {
            if (_cachedValidGameFont != null && _cachedValidGameFont.material != null && _cachedValidGameFont.atlas != null)
                return _cachedValidGameFont;
            if (_searchedValidGameFont)
                return _cachedValidGameFont;
            _searchedValidGameFont = true;
            try
            {
                var fonts = UnityEngine.Resources.FindObjectsOfTypeAll<TMProOld.TMP_FontAsset>();
                if (fonts != null)
                {
                    TMProOld.TMP_FontAsset best = null;
                    foreach (var f in fonts)
                    {
                        if (f == null || f.material == null || f.atlas == null) continue;
                        string n = f.name ?? "";
                        if (n.EndsWith(" +border", StringComparison.Ordinal)) continue;
                        try
                        {
                            if (!f.HasCharacter('A') || !f.HasCharacter('a') || !f.HasCharacter('0'))
                                continue;
                        }
                        catch { continue; }

                        if (n.IndexOf("perpetua", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("trajan", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            best = f;
                            break;
                        }
                        if (best == null) best = f;
                    }
                    _cachedValidGameFont = best;
                }
            }
            catch (System.Exception __apiEx_78) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2779", __apiEx_78); }
            return _cachedValidGameFont;
        }

        public static void TMP_Text_set_text_Prefix(object __instance, ref string __0)
        {
            if (!string.IsNullOrEmpty(__0))
            {
                if (__0.IndexOf("!!", StringComparison.Ordinal) >= 0 ||
                    __0.StartsWith("!_", StringComparison.Ordinal) ||
                    __0.StartsWith("#!#", StringComparison.Ordinal) ||
                    __0.IndexOf("UI.", StringComparison.Ordinal) >= 0 ||
                    __0.IndexOf("Fishes.", StringComparison.Ordinal) >= 0 ||
                    __0.IndexOf("Hungry Craw.", StringComparison.Ordinal) >= 0)
                {
                    __0 = ResolvePlaceholderText(__0);
                }
                else if ((__0.StartsWith("BONEMASTER_", StringComparison.Ordinal) || __0.StartsWith("WEAVER_", StringComparison.Ordinal)) &&
                         _i18nFlatKeys.TryGetValue(__0, out var directVal))
                {
                    __0 = directVal;
                }
            }
            if (__instance is TMProOld.TMP_Text tmp)
            {
                try
                {
                    var curFont = tmp.font;
                    bool needsFontFix = curFont == null || curFont.material == null || curFont.atlas == null;
                    if (needsFontFix)
                    {
                        var validFont = GetValidGameFont();
                        if (validFont != null)
                        {
                            tmp.font = validFont;
                            tmp.fontSharedMaterial = validFont.material;
                        }
                    }
                    else if (tmp.fontSharedMaterial == null && curFont.material != null)
                    {
                        tmp.fontSharedMaterial = curFont.material;
                    }
                }
                catch (System.Exception __apiEx_79) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2822", __apiEx_79); }
            }
        }

        public static void InventoryItemSelectable_get_DisplayName_Postfix(ref string __result)
        {
            if (!string.IsNullOrEmpty(__result) && (__result.StartsWith("!!", StringComparison.Ordinal) || __result.StartsWith("!_", StringComparison.Ordinal) || __result.StartsWith("#!#", StringComparison.Ordinal)))
            {
                __result = ResolvePlaceholderText(__result);
            }
        }

        public static void InventoryItemSelectable_get_Description_Postfix(ref string __result)
        {
            if (!string.IsNullOrEmpty(__result) && (__result.StartsWith("!!", StringComparison.Ordinal) || __result.StartsWith("!_", StringComparison.Ordinal) || __result.StartsWith("#!#", StringComparison.Ordinal)))
            {
                __result = ResolvePlaceholderText(__result);
            }
        }

        public static void EnsureI18nHooksInstalled()
        {
            SeedBuiltinModTranslationsIfNeeded();
            SyncModLocalizationIntoGame();
            if (!_tmpHookInstalled)
            {
                _tmpHookInstalled = true;
                try
                {
                    var tmpType = AccessTools.TypeByName("TMProOld.TMP_Text") ?? AccessTools.TypeByName("TMPro.TMP_Text");
                    var setTextMethod = tmpType?.GetMethod("set_text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
                    var prefixMi = typeof(HarmonyBridge).GetMethod(nameof(TMP_Text_set_text_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (setTextMethod != null && prefixMi != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", setTextMethod, prefixMi, null, null);
                    }

                    var uiTextSetMi = typeof(UnityEngine.UI.Text).GetMethod("set_text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
                    var uiTextPrefixMi = typeof(HarmonyBridge).GetMethod(nameof(UIText_set_text_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (uiTextSetMi != null && uiTextPrefixMi != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", uiTextSetMi, uiTextPrefixMi, null, null);
                    }

                    var langType = AccessTools.TypeByName("TeamCherry.Localization.Language") ?? AccessTools.TypeByName("Language");
                    if (langType != null)
                    {
                        var langGet2 = langType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(string), typeof(string) }, null);
                        var postLangGet2 = typeof(HarmonyBridge).GetMethod(nameof(Language_Get_2_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (langGet2 != null && postLangGet2 != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", langGet2, null, postLangGet2, null, HarmonyLib.Priority.Low);

                        var langGet1 = langType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(string) }, null);
                        var postLangGet1 = typeof(HarmonyBridge).GetMethod(nameof(Language_Get_1_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (langGet1 != null && postLangGet1 != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", langGet1, null, postLangGet1, null, HarmonyLib.Priority.Low);

                        var langHas2 = langType.GetMethod("Has", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(string), typeof(string) }, null);
                        var postLangHas2 = typeof(HarmonyBridge).GetMethod(nameof(Language_Has_2_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (langHas2 != null && postLangHas2 != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", langHas2, null, postLangHas2, null, HarmonyLib.Priority.Low);

                        var langDoSwitch = langType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "DoSwitch" && m.GetParameters().Length == 1);
                        var postDoSwitch = typeof(HarmonyBridge).GetMethod(nameof(Language_DoSwitch_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (langDoSwitch != null && postDoSwitch != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", langDoSwitch, null, postDoSwitch, null, HarmonyLib.Priority.Low);
                    }

                    var locStrType = AccessTools.TypeByName("TeamCherry.Localization.LocalisedString") ?? AccessTools.TypeByName("LocalisedString");
                    var postLocStr = typeof(HarmonyBridge).GetMethod(nameof(LocalisedString_ToString_Postfix), BindingFlags.Static | BindingFlags.Public);
                    if (locStrType != null && postLocStr != null)
                    {
                        var toStrBool = locStrType.GetMethod("ToString", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(bool) }, null);
                        if (toStrBool != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", toStrBool, null, postLocStr, null, HarmonyLib.Priority.Last);
                        var toStrZero = locStrType.GetMethod("ToString", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                        if (toStrZero != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", toStrZero, null, postLocStr, null, HarmonyLib.Priority.Last);
                    }

                    var isBlockingMethod = typeof(HealthManager).GetMethod("IsBlockingByDirection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int), typeof(AttackTypes), typeof(SpecialTypes) }, null);
                    var preIsBlocking = typeof(HarmonyBridge).GetMethod(nameof(HealthManager_IsBlockingByDirection_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (isBlockingMethod != null && preIsBlocking != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("StylemeterDamageBridge", isBlockingMethod, preIsBlocking, null, null, HarmonyLib.Priority.First);
                    }

                    Type nailClashSmType = typeof(DamageHero).GetNestedType("<NailClash>d__69", BindingFlags.Public | BindingFlags.NonPublic);
                    if (nailClashSmType == null)
                    {
                        foreach (var nt in typeof(DamageHero).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (nt != null && nt.Name.StartsWith("<NailClash>d__", StringComparison.Ordinal))
                            {
                                nailClashSmType = nt;
                                break;
                            }
                        }
                    }
                    var nailClashMoveNextMi = nailClashSmType?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    var preNailClashMn = typeof(HarmonyBridge).GetMethod(nameof(DamageHero_NailClashMoveNext_Prefix), BindingFlags.Static | BindingFlags.Public);
                    var postNailClashMn = typeof(HarmonyBridge).GetMethod(nameof(DamageHero_NailClashMoveNext_Postfix), BindingFlags.Static | BindingFlags.Public);
                    if (nailClashMoveNextMi != null && (preNailClashMn != null || postNailClashMn != null))
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("StylemeterDamageBridge", nailClashMoveNextMi, preNailClashMn, postNailClashMn, null);
                    }

                    var nonFatalMethod = typeof(HealthManager).GetMethod("NonFatalHit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                    var postNonFatal = typeof(HarmonyBridge).GetMethod(nameof(HealthManager_NonFatalHit_Postfix), BindingFlags.Static | BindingFlags.Public);
                    if (nonFatalMethod != null && postNonFatal != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("StylemeterDamageBridge", nonFatalMethod, null, postNonFatal, null);
                    }

                    var sendDeathMethod = typeof(HealthManager).GetMethod("SendDeathEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    var preSendDeath = typeof(HarmonyBridge).GetMethod(nameof(HealthManager_SendDeathEvent_Prefix), BindingFlags.Static | BindingFlags.Public);
                    if (sendDeathMethod != null && preSendDeath != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("StylemeterDamageBridge", sendDeathMethod, preSendDeath, null, null);
                    }

                    try
                    {
                        var hasNeedolinMi = typeof(HeroController).GetMethod("HasNeedolin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var postHasNeedolin = typeof(HarmonyBridge).GetMethod(nameof(HeroController_HasNeedolin_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (hasNeedolinMi != null && postHasNeedolin != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", hasNeedolinMi, null, postHasNeedolin, null, HarmonyLib.Priority.Last);

                        var canNeedolinMi = typeof(HeroController).GetMethod("CanPlayNeedolin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var postCanNeedolin = typeof(HarmonyBridge).GetMethod(nameof(HeroController_CanPlayNeedolin_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (canNeedolinMi != null && postCanNeedolin != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", canNeedolinMi, null, postCanNeedolin, null, HarmonyLib.Priority.Last);

                        var beginTransMi = typeof(GameManager).GetMethod("BeginSceneTransition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(GameManager.SceneLoadInfo) }, null);
                        var preBeginTrans = typeof(HarmonyBridge).GetMethod(nameof(GameManager_BeginSceneTransition_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (beginTransMi != null && preBeginTrans != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.SceneTransitionGuard", beginTransMi, preBeginTrans, null, null, HarmonyLib.Priority.Last);

                        var setupRefsMi = typeof(GameManager).GetMethod("SetupSceneRefs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                        var preSetupRefs = typeof(HarmonyBridge).GetMethod(nameof(GameManager_SetupSceneRefs_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (setupRefsMi != null && preSetupRefs != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.SceneTransitionGuard", setupRefsMi, preSetupRefs, null, null, HarmonyLib.Priority.First);

                        var findTpMi = typeof(GameManager).GetMethod("FindTransitionPoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var preFindTp = typeof(HarmonyBridge).GetMethod(nameof(GameManager_FindTransitionPoint_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (findTpMi != null && preFindTp != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.SceneTransitionGuard", findTpMi, preFindTp, null, null, HarmonyLib.Priority.First);

                        var setCfgGroupMi = typeof(HeroController).GetMethod("SetConfigGroup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var postSetCfg = typeof(HarmonyBridge).GetMethod(nameof(HeroController_SetConfigGroup_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (setCfgGroupMi != null && postSetCfg != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", setCfgGroupMi, null, postSetCfg, null, HarmonyLib.Priority.Last);

                        var hcStartMi = typeof(HeroController).GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var postHcStart = typeof(HarmonyBridge).GetMethod(nameof(HeroController_Start_MobileGuard_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (hcStartMi != null && postHcStart != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", hcStartMi, null, postHcStart, null, HarmonyLib.Priority.Last);

                        var regainCtrlMi = typeof(HeroController).GetMethod("RegainControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                        var postRegainCtrl = typeof(HarmonyBridge).GetMethod(nameof(HeroController_RegainControl_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (regainCtrlMi != null && postRegainCtrl != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", regainCtrlMi, null, postRegainCtrl, null, HarmonyLib.Priority.Last);

                        var listenDnMi = typeof(HutongGames.PlayMaker.Actions.ListenForDreamNail).GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var preListenDn = typeof(HarmonyBridge).GetMethod(nameof(ListenForDreamNail_OnUpdate_Prefix), BindingFlags.Static | BindingFlags.Public);
                        var postListenDn = typeof(HarmonyBridge).GetMethod(nameof(ListenForDreamNail_OnUpdate_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (listenDnMi != null && preListenDn != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", listenDnMi, preListenDn, postListenDn, null, HarmonyLib.Priority.First);

                        var cmpOnEnterMi = typeof(HutongGames.PlayMaker.Actions.CallMethodProper).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var preCmpNeedolin = typeof(HarmonyBridge).GetMethod(nameof(CallMethodProper_OnEnter_Needolin_Prefix), BindingFlags.Static | BindingFlags.Public);
                        var postCmpNeedolin = typeof(HarmonyBridge).GetMethod(nameof(CallMethodProper_OnEnter_Needolin_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (cmpOnEnterMi != null && (preCmpNeedolin != null || postCmpNeedolin != null))
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", cmpOnEnterMi, preCmpNeedolin, postCmpNeedolin, null, HarmonyLib.Priority.First);

                        var pdbtOnEnterMi = typeof(HutongGames.PlayMaker.Actions.PlayerDataBoolTest).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var prePdbtNeedolin = typeof(HarmonyBridge).GetMethod(nameof(PlayerDataBoolTest_OnEnter_Needolin_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (pdbtOnEnterMi != null && prePdbtNeedolin != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", pdbtOnEnterMi, prePdbtNeedolin, null, null, HarmonyLib.Priority.First);

                        var runFsmOnEnterMi = typeof(HutongGames.PlayMaker.Actions.RunFSM).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        var preRunFsmNeedolin = typeof(HarmonyBridge).GetMethod(nameof(RunFSM_OnEnter_Needolin_Prefix), BindingFlags.Static | BindingFlags.Public);
                        var postRunFsmNeedolin = typeof(HarmonyBridge).GetMethod(nameof(RunFSM_OnEnter_Needolin_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (runFsmOnEnterMi != null && preRunFsmNeedolin != null)
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.NeedolinGuard", runFsmOnEnterMi, preRunFsmNeedolin, postRunFsmNeedolin, null, HarmonyLib.Priority.First);
                    }
                    catch (System.Exception __apiEx_80) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L2982", __apiEx_80); }

                    var dmgOnEnableMethod = typeof(DamageEnemies).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    var postDmgOnEnable = typeof(HarmonyBridge).GetMethod(nameof(DamageEnemies_OnEnable_WeaverPoolBridge_Postfix), BindingFlags.Static | BindingFlags.Public);
                    if (dmgOnEnableMethod != null && postDmgOnEnable != null)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("WeaverPoolSpawnBridge", dmgOnEnableMethod, null, postDmgOnEnable, null);
                    }

                    try
                    {
                        var isToolEquippedMi = typeof(ToolItemManager).GetMethod("IsToolEquipped", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(ToolItem), typeof(ToolEquippedReadSource) }, null);
                        var preIsToolEquipped = typeof(HarmonyBridge).GetMethod(nameof(ToolItemManager_IsToolEquipped_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (isToolEquippedMi != null && preIsToolEquipped != null)
                        {
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.ToolGuard", isToolEquippedMi, preIsToolEquipped, null, null, HarmonyLib.Priority.VeryHigh);
                        }

                        var isToolEquippedStrMi = typeof(ToolItemManager).GetMethod("IsToolEquipped", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(string) }, null);
                        var preIsToolEquippedStr = typeof(HarmonyBridge).GetMethod(nameof(ToolItemManager_IsToolEquippedString_Prefix), BindingFlags.Static | BindingFlags.Public);
                        if (isToolEquippedStrMi != null && preIsToolEquippedStr != null)
                        {
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.ToolGuard", isToolEquippedStrMi, preIsToolEquippedStr, null, null, HarmonyLib.Priority.VeryHigh);
                        }

                        var getAtkBindingMi = typeof(ToolItemManager).GetMethod("GetAttackToolBinding", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(ToolItem) }, null);
                        var postGetAtkBinding = typeof(HarmonyBridge).GetMethod(nameof(ToolItemManager_GetAttackToolBinding_Postfix), BindingFlags.Static | BindingFlags.Public);
                        if (getAtkBindingMi != null && postGetAtkBinding != null)
                        {
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.ToolGuard", getAtkBindingMi, null, postGetAtkBinding, null, HarmonyLib.Priority.Last);
                        }
                    }
                    catch (System.Exception __apiEx_81) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3014", __apiEx_81); }

                    var postDisp = typeof(HarmonyBridge).GetMethod(nameof(InventoryItemSelectable_get_DisplayName_Postfix), BindingFlags.Static | BindingFlags.Public);
                    var postDesc = typeof(HarmonyBridge).GetMethod(nameof(InventoryItemSelectable_get_Description_Postfix), BindingFlags.Static | BindingFlags.Public);
                    string[] selectableTypes = new[] { "InventoryToolCrest", "InventoryItemTool", "ToolItemBasic", "ToolItem" };
                    foreach (var stName in selectableTypes)
                    {
                        var st = AccessTools.TypeByName(stName);
                        if (st == null) continue;
                        var getDispMi = st.GetMethod("get_DisplayName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        var getDescMi = st.GetMethod("get_Description", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (getDispMi != null && postDisp != null && getDispMi.ReturnType == typeof(string))
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", getDispMi, null, postDisp, null);
                        if (getDescMi != null && postDesc != null && getDescMi.ReturnType == typeof(string))
                            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("I18NBridge", getDescMi, null, postDesc, null);
                    }

                    EnsureVirtualInputHarmonyHooksInstalled();
                }
                catch (System.Exception __apiEx_82) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3033", __apiEx_82); }
            }
        }

        public static void SanitizeAssetBundleObjectShaders(UnityEngine.Object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is Material mat)
                {
                    AssetHelperBridge.RepairMaterialShaderIfUnsupported(mat);
                }
                else if (obj is Texture2D tex)
                {
                    AssetHelperBridge.EnforceMobileTextureVramGuard(tex);
                }
                else if (obj is Sprite sp && sp.texture != null)
                {
                    AssetHelperBridge.EnforceMobileTextureVramGuard(sp.texture);
                }
                else if (obj is GameObject go)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        if (mats == null) continue;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            if (mats[i] != null)
                                AssetHelperBridge.RepairMaterialShaderIfUnsupported(mats[i]);
                        }
                    }
                }
            }
            catch { }
        }

        public static void AssetBundle_LoadAllAssets_Postfix(ref UnityEngine.Object[] __result)
        {
            if (__result == null) return;
            for (int i = 0; i < __result.Length; i++)
                SanitizeAssetBundleObjectShaders(__result[i]);
        }

        public static void AssetBundle_LoadAsset_Postfix(ref UnityEngine.Object __result)
        {
            if (__result != null)
                SanitizeAssetBundleObjectShaders(__result);
        }

        public static void Shader_Find_Postfix(string __0, ref Shader __result)
        {
            try
            {
                if (__result == null || !__result.isSupported || __result.name == "Hidden/InternalErrorShader")
                {
                    var fallback = AssetHelperBridge.GetFallbackAndroidShader(__0);
                    if (fallback != null)
                        __result = fallback;
                }
            }
            catch { }
        }

        public static void ImageConversion_LoadImage_Postfix(Texture2D __0, ref bool __result)
        {
            if (__result && __0 != null)
            {
                AssetHelperBridge.EnforceMobileTextureVramGuard(__0);
            }
        }

        public static int CurrentSaveSlotIndex { get; private set; } = 1;
        public static event Action<int, string> OnSaveSlotLoaded;
        public static event Action<int, string> OnSaveSlotSaved;
        public static event Action<int, string> OnSaveSlotCleared;

        public static string GetModSaveSlotDirectory(int slotIndex)
        {
            if (slotIndex <= 0 && GameManager.UnsafeInstance != null)
                slotIndex = GameManager.UnsafeInstance.profileID;
            if (slotIndex <= 0) slotIndex = CurrentSaveSlotIndex;
            string dir = Path.Combine(Application.persistentDataPath, "ModSaveData", $"slot_{slotIndex}");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static void SaveModSlotJson(int slotIndex, string modGuid, string jsonContent)
        {
            if (string.IsNullOrEmpty(modGuid)) return;
            try
            {
                string safeId = Regex.Replace(modGuid, @"[^A-Za-z0-9_.-]", "_");
                string path = Path.Combine(GetModSaveSlotDirectory(slotIndex), safeId + ".json");
                File.WriteAllText(path, jsonContent ?? "{}");
            }
            catch { }
        }

        public static string LoadModSlotJson(int slotIndex, string modGuid)
        {
            if (string.IsNullOrEmpty(modGuid)) return null;
            try
            {
                string safeId = Regex.Replace(modGuid, @"[^A-Za-z0-9_.-]", "_");
                string path = Path.Combine(GetModSaveSlotDirectory(slotIndex), safeId + ".json");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { }
            return null;
        }

        public static void GameManager_SetLoadedGameData_Postfix(int __1)
        {
            try
            {
                if (__1 > 0) CurrentSaveSlotIndex = __1;
                string dir = GetModSaveSlotDirectory(CurrentSaveSlotIndex);
                OnSaveSlotLoaded?.Invoke(CurrentSaveSlotIndex, dir);
            }
            catch { }
        }

        public static void GameManager_LoadGameFromUI_Prefix(int __0)
        {
            try
            {
                if (__0 > 0) CurrentSaveSlotIndex = __0;
                string dir = GetModSaveSlotDirectory(CurrentSaveSlotIndex);
                OnSaveSlotLoaded?.Invoke(CurrentSaveSlotIndex, dir);
            }
            catch { }
        }

        public static void GameManager_SaveGame_Prefix(int __0)
        {
            try
            {
                if (__0 > 0) CurrentSaveSlotIndex = __0;
                string dir = GetModSaveSlotDirectory(CurrentSaveSlotIndex);
                OnSaveSlotSaved?.Invoke(CurrentSaveSlotIndex, dir);
            }
            catch { }
        }

        public static void GameManager_ClearSaveFile_Postfix(int __0)
        {
            try
            {
                string dir = GetModSaveSlotDirectory(__0);
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                OnSaveSlotCleared?.Invoke(__0, dir);
            }
            catch { }
        }

        private static bool _extraGlobalBridgesInstalled;

        private static void InstallAssetBundleAndSaveDataBridges()
        {
            if (_extraGlobalBridgesInstalled) return;
            _extraGlobalBridgesInstalled = true;
            try
            {
                var abType = typeof(AssetBundle);
                var postLoadAll = typeof(HarmonyBridge).GetMethod(nameof(AssetBundle_LoadAllAssets_Postfix), BindingFlags.Static | BindingFlags.Public);
                var postLoadAsset = typeof(HarmonyBridge).GetMethod(nameof(AssetBundle_LoadAsset_Postfix), BindingFlags.Static | BindingFlags.Public);

                var loadAllMi = abType.GetMethod("LoadAllAssets", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (loadAllMi != null && postLoadAll != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", loadAllMi, null, postLoadAll, null);

                var loadAllTypeMi = abType.GetMethod("LoadAllAssets", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(Type) }, null);
                if (loadAllTypeMi != null && postLoadAll != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", loadAllTypeMi, null, postLoadAll, null);

                var loadAssetMi = abType.GetMethod("LoadAsset", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(string), typeof(Type) }, null);
                if (loadAssetMi != null && postLoadAsset != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", loadAssetMi, null, postLoadAsset, null);

                var loadAsset1Mi = abType.GetMethod("LoadAsset", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(string) }, null);
                if (loadAsset1Mi != null && postLoadAsset != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", loadAsset1Mi, null, postLoadAsset, null);

                var loadSubMi = abType.GetMethod("LoadAssetWithSubAssets", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(string), typeof(Type) }, null);
                if (loadSubMi != null && postLoadAll != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", loadSubMi, null, postLoadAll, null);

                var shaderFindMi = typeof(Shader).GetMethod("Find", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(string) }, null);
                var postShaderFind = typeof(HarmonyBridge).GetMethod(nameof(Shader_Find_Postfix), BindingFlags.Static | BindingFlags.Public);
                if (shaderFindMi != null && postShaderFind != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("AssetBundleShaderRebinder", shaderFindMi, null, postShaderFind, null);

                var imgConvType = typeof(ImageConversion);
                var postLoadImg = typeof(HarmonyBridge).GetMethod(nameof(ImageConversion_LoadImage_Postfix), BindingFlags.Static | BindingFlags.Public);
                var loadImg3Mi = imgConvType.GetMethod("LoadImage", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }, null);
                if (loadImg3Mi != null && postLoadImg != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("MobileVramGuard", loadImg3Mi, null, postLoadImg, null);

                var loadImg2Mi = imgConvType.GetMethod("LoadImage", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(Texture2D), typeof(byte[]) }, null);
                if (loadImg2Mi != null && postLoadImg != null)
                    RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("MobileVramGuard", loadImg2Mi, null, postLoadImg, null);

                var gmType = typeof(GameManager);
                foreach (var m in gmType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "SetLoadedGameData" && ps.Length == 2 && ps[1].ParameterType == typeof(int))
                    {
                        var postSetLoaded = typeof(HarmonyBridge).GetMethod(nameof(GameManager_SetLoadedGameData_Postfix), BindingFlags.Static | BindingFlags.Public);
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("ModSaveDataBridge", m, null, postSetLoaded, null);
                    }
                    else if (m.Name == "LoadGameFromUI" && ps.Length >= 1 && ps[0].ParameterType == typeof(int))
                    {
                        var preLoadUi = typeof(HarmonyBridge).GetMethod(nameof(GameManager_LoadGameFromUI_Prefix), BindingFlags.Static | BindingFlags.Public);
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("ModSaveDataBridge", m, preLoadUi, null, null);
                    }
                    else if (m.Name == "SaveGame" && ps.Length >= 1 && ps[0].ParameterType == typeof(int))
                    {
                        var preSave = typeof(HarmonyBridge).GetMethod(nameof(GameManager_SaveGame_Prefix), BindingFlags.Static | BindingFlags.Public);
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("ModSaveDataBridge", m, preSave, null, null);
                    }
                    else if (m.Name == "ClearSaveFile" && ps.Length >= 1 && ps[0].ParameterType == typeof(int))
                    {
                        var postClear = typeof(HarmonyBridge).GetMethod(nameof(GameManager_ClearSaveFile_Postfix), BindingFlags.Static | BindingFlags.Public);
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("ModSaveDataBridge", m, null, postClear, null);
                    }
                }
            }
            catch { }
        }

        private static bool _virtualInputHooksInstalled;
        public static void EnsureVirtualInputHarmonyHooksInstalled()
        {
            InstallAssetBundleAndSaveDataBridges();
            if (_virtualInputHooksInstalled) return;
            _virtualInputHooksInstalled = true;
            // Native Harmony hooks on UnityEngine.Input are intentionally omitted because InControl polls
            // Input.GetKey(KeyCode) ~120 times per frame (14,400+ transitions/sec), and CecilEngine redirects
            // all PC mod Input.GetKey / GetKeyDown / GetKeyUp / anyKey / GetMouseButton calls at the IL level.
            SilksongMobileAPIPlugin.LogDiagnosticInfo("[VirtualInputBridge] Active via direct Cecil IL call redirection (zero native Input hook overhead).");
        }

        public static void Input_GetKey_KeyCode_Postfix(KeyCode __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKey(__0))
                __result = true;
        }

        public static void Input_GetKeyDown_KeyCode_Postfix(KeyCode __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKeyDown(__0))
                __result = true;
        }

        public static void Input_GetKeyUp_KeyCode_Postfix(KeyCode __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKeyUp(__0))
                __result = true;
        }

        public static void Input_GetKey_String_Postfix(string __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKeyString(__0))
                __result = true;
        }

        public static void Input_GetKeyDown_String_Postfix(string __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKeyDownString(__0))
                __result = true;
        }

        public static void Input_GetKeyUp_String_Postfix(string __0, ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasActiveKeyUpString(__0))
                __result = true;
        }

        public static void Input_get_anyKey_Postfix(ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasAnyKey())
                __result = true;
        }

        public static void Input_get_anyKeyDown_Postfix(ref bool __result)
        {
            if (!__result && BepInEx.MobileVirtualInput.HasAnyKeyDown())
                __result = true;
        }

        public static void SyncModLocalizationIntoGame()
        {
            SeedBuiltinModTranslationsIfNeeded();
            if (_i18nOverridesBySheet.Count == 0) return;
            try
            {
                if (!_langFieldResolved)
                {
                    _langFieldResolved = true;
                    var langType = AccessTools.TypeByName("TeamCherry.Localization.Language") ?? AccessTools.TypeByName("Language");
                    if (langType != null)
                    {
                        _langCurrentEntrySheetsField = langType.GetField("_currentEntrySheets", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    var crestListType = AccessTools.TypeByName("InventoryToolCrestList");
                    if (crestListType != null)
                    {
                        _invCrestNameDisplaysField = crestListType.GetField("crestNameDisplays", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        _invCrestDescDisplayField = crestListType.GetField("crestDescriptionDisplay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }

                if (_langCurrentEntrySheetsField != null)
                {
                    var currentSheets = _langCurrentEntrySheetsField.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
                    if (currentSheets != null)
                    {
                        if (ReferenceEquals(currentSheets, _lastSyncedEntrySheetsRef) && _lastSyncedSheetCount == _i18nOverridesBySheet.Count)
                            return;
                        _lastSyncedEntrySheetsRef = currentSheets;
                        _lastSyncedSheetCount = _i18nOverridesBySheet.Count;
                        // Ensure MainMenu sheet also has StartMultiplayerBtn
                        if (currentSheets.TryGetValue("MainMenu", out var mmSheet) && mmSheet != null && !mmSheet.ContainsKey("StartMultiplayerBtn"))
                        {
                            mmSheet["StartMultiplayerBtn"] = "Start Multiplayer";
                        }
                        int injectedSheets = 0;
                        foreach (var kv in _i18nOverridesBySheet)
                        {
                            string sheetName = kv.Key;
                            var modEntries = kv.Value;
                            if (string.IsNullOrEmpty(sheetName) || modEntries == null || modEntries.Count == 0) continue;

                            if (!currentSheets.TryGetValue(sheetName, out var gameSheet) || gameSheet == null)
                            {
                                gameSheet = new Dictionary<string, string>();
                                currentSheets[sheetName] = gameSheet;
                                injectedSheets++;
                            }

                            foreach (var entry in modEntries)
                            {
                                gameSheet[entry.Key] = entry.Value;
                            }
                        }

                        if (injectedSheets > 0 && !_i18nHooksInstalled)
                        {
                            _i18nHooksInstalled = true;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[I18NBridge] Injected {_i18nOverridesBySheet.Count} localization sheet(s) directly into Language._currentEntrySheets.");
                        }
                    }
                }

                // Also sync into Stitchwork DialogueHandler dictionaries once if Stitchwork is loaded
                if (!_syncedIntoStitchwork)
                {
                    _syncedIntoStitchwork = true;
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        string an = asm.GetName().Name ?? "";
                        if (an.IndexOf("Stitchwork", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        foreach (var t in asm.GetTypes())
                        {
                            if (t == null || t.Name.IndexOf("Dialogue", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                try
                                {
                                    var val = f.GetValue(null);
                                    if (val is Dictionary<string, Dictionary<string, string>> nestedDict && nestedDict.Count > 0)
                                    {
                                        foreach (var kv in _i18nOverridesBySheet)
                                        {
                                            if (!nestedDict.TryGetValue(kv.Key, out var sub) || sub == null)
                                            {
                                                sub = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                nestedDict[kv.Key] = sub;
                                            }
                                            foreach (var entry in kv.Value)
                                                sub[entry.Key] = entry.Value;
                                        }
                                        _syncedIntoStitchwork = true;
                                    }
                                    else if (val is Dictionary<string, string> flatDict && flatDict.Count > 0)
                                    {
                                        foreach (var kv in _i18nFlatKeys)
                                            flatDict[kv.Key] = kv.Value;
                                        _syncedIntoStitchwork = true;
                                    }
                                }
                                catch (System.Exception __apiEx_83) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3196", __apiEx_83); }
                            }
                        }
                    }
                }

                // Avoid calling FindObjectsOfType<InventoryToolCrestList>() every 4s during gameplay; TMP_Text_set_text_Prefix handles all text automatically.
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[I18NBridge] SyncModLocalizationIntoGame warning: {ex.Message}");
            }
        }

        private static void SanitizeTmpTextObject(object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is Array arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                        SanitizeTmpTextObject(arr.GetValue(i));
                    return;
                }
                var t = obj.GetType();
                var textProp = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (textProp != null && textProp.PropertyType == typeof(string))
                {
                    string cur = textProp.GetValue(obj, null) as string;
                    if (!string.IsNullOrEmpty(cur) && (cur.StartsWith("!!", StringComparison.Ordinal) || cur.StartsWith("!_", StringComparison.Ordinal)))
                    {
                        string fixedStr = ResolvePlaceholderText(cur);
                        if (fixedStr != cur)
                            textProp.SetValue(obj, fixedStr, null);
                    }
                }
            }
            catch (System.Exception __apiEx_84) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3234", __apiEx_84); }
        }

        public static void Language_Get_2_Postfix(string key, string sheetTitle, ref string __result)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!string.IsNullOrEmpty(sheetTitle) &&
                _i18nOverridesBySheet.TryGetValue(sheetTitle, out var dict) &&
                dict.TryGetValue(key, out var val))
            {
                __result = val;
                return;
            }
            if (_i18nFlatKeys.TryGetValue(key, out var flatVal))
            {
                __result = flatVal;
                return;
            }
            if (!string.IsNullOrEmpty(__result) && (__result.StartsWith("!_") || __result.StartsWith("!!")))
            {
                string stripped = __result.TrimStart('!', '_');
                if (_i18nFlatKeys.TryGetValue(stripped, out var sVal))
                    __result = sVal;
            }
        }

        public static void Language_Get_1_Postfix(string key, ref string __result)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_i18nFlatKeys.TryGetValue(key, out var flatVal))
            {
                __result = flatVal;
            }
        }

        public static void Language_Has_2_Postfix(string key, string sheetTitle, ref bool __result)
        {
            if (__result || string.IsNullOrEmpty(key)) return;
            if (!string.IsNullOrEmpty(sheetTitle) &&
                _i18nOverridesBySheet.TryGetValue(sheetTitle, out var dict) &&
                dict.ContainsKey(key))
            {
                __result = true;
                return;
            }
            if (_i18nFlatKeys.ContainsKey(key))
                __result = true;
        }

        public static void Language_DoSwitch_Postfix()
        {
            _i18nHooksInstalled = false;
            SyncModLocalizationIntoGame();
        }

        public static void UIText_set_text_Prefix(ref string __0)
        {
            if (!string.IsNullOrEmpty(__0))
            {
                if (__0.StartsWith("!!", StringComparison.Ordinal) || __0.StartsWith("!_", StringComparison.Ordinal) || __0.StartsWith("#!#", StringComparison.Ordinal))
                {
                    __0 = ResolvePlaceholderText(__0);
                }
                else if (_i18nFlatKeys.TryGetValue(__0, out var directVal))
                {
                    __0 = directVal;
                }
            }
        }

        // ======================================================================
        // Assembly Location & Embedded Resource Registry
        // ======================================================================
        private static readonly HashSet<Assembly> _managedPcModAssemblies = new HashSet<Assembly>();
        private static GameObject _modAudioPoolGo;
        private static AudioSource[] _modAudioSources;
        private static int _nextModAudioIdx;
        private static UnityEngine.Audio.AudioMixerGroup _cachedHeroMixerGroup;
        private static MethodInfo _audioClipSetDataMethod;
        private static MethodInfo _playOneShotHelperMethod;
        private static bool _resolvedPlayOneShotHelper;
        private static int _loggedAudioPlays;

        public static void RegisterAssemblyLocation(Assembly asm, string dllPath)
        {
            if (asm == null) return;
            lock (_managedPcModAssemblies)
            {
                _managedPcModAssemblies.Add(asm);
            }
            if (string.IsNullOrEmpty(dllPath)) return;
            string name = asm.GetName().Name ?? "";
            lock (_assemblyLocations)
            {
                _assemblyLocations[name] = dllPath;
                if (name.EndsWith("_MobileRt", StringComparison.Ordinal))
                    _assemblyLocations[name.Substring(0, name.Length - "_MobileRt".Length)] = dllPath;
                _assemblyLocations[asm.FullName ?? name] = dllPath;
            }
        }

        public static bool IsManagedPcModAssembly(Assembly asm)
        {
            if (asm == null) return false;
            lock (_managedPcModAssemblies)
            {
                if (_managedPcModAssemblies.Contains(asm)) return true;
            }
            string n = asm.GetName().Name ?? "";
            return n == "Needleforge" || n == "Weaver_Crest" || n == "RebalancedCrests" || n.EndsWith("_MobileRt", StringComparison.Ordinal);
        }

        public static void DispatchManagedZeroArgPostfix(string methodKey, object instance)
        {
            RuntimeHybridCLRAdapterBuilder.DispatchManagedZeroArgPostfix(methodKey, instance);
        }

        public static AudioClip LoadEmbeddedWavByModName(string modName, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            try
            {
                byte[] wavBytes = null;
                lock (_embeddedResourcesByAssembly)
                {
                    if (!string.IsNullOrEmpty(modName) && _embeddedResourcesByAssembly.TryGetValue(modName, out var dict))
                    {
                        if (!dict.TryGetValue(fileName, out wavBytes))
                        {
                            var match = dict.FirstOrDefault(kv =>
                                kv.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                                kv.Key.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                            wavBytes = match.Value;
                        }
                    }
                    if (wavBytes == null)
                    {
                        foreach (var kvDict in _embeddedResourcesByAssembly.Values)
                        {
                            var match = kvDict.FirstOrDefault(kv =>
                                kv.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                                kv.Key.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                            if (match.Value != null)
                            {
                                wavBytes = match.Value;
                                break;
                            }
                        }
                    }
                }

                if (wavBytes == null || wavBytes.Length < 44)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[AudioBridge] Embedded WAV '{fileName}' not found for '{modName}'.");
                    return null;
                }

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
                if (dataOffset + dataSize > wavBytes.Length)
                {
                    dataSize = wavBytes.Length - dataOffset;
                }

                int sampleCount = dataSize / 2;
                if (sampleCount <= 0) return null;

                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short pcm = BitConverter.ToInt16(wavBytes, dataOffset + i * 2);
                    samples[i] = pcm / 32768f;
                }

                int lengthSamples = sampleCount / channels;
                // Use the 5-argument AudioClip.Create(..., stream: false) + clip.SetData(samples, 0)
                // so FMOD stores the PCM buffer persistently across PlayOneShot calls (matching Weaver_Crest Sounds.LoadEmbeddedWav)
                AudioClip clip = AudioClip.Create(fileName, lengthSamples, channels, sampleRate, false);
                if (clip != null)
                {
                    if (_audioClipSetDataMethod == null)
                    {
                        _audioClipSetDataMethod = typeof(AudioClip).GetMethod(
                            "SetData",
                            BindingFlags.Instance | BindingFlags.Public,
                            null,
                            new Type[] { typeof(float[]), typeof(int) },
                            null);
                    }
                    _audioClipSetDataMethod?.Invoke(clip, new object[] { samples, 0 });
                }

                if (clip != null)
                {
                    clip.name = fileName;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AudioBridge] Loaded embedded WAV '{fileName}' for '{modName}' ({lengthSamples} frames, {channels}ch, {sampleRate}Hz).");
                }
                return clip;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException(modName ?? "AudioBridge", $"LoadEmbeddedWav({fileName})", ex);
                return null;
            }
        }

        public static void PlayModAudioClip(AudioClip clip, float volume)
        {
            if (clip == null) return;
            try
            {
                if (_modAudioPoolGo == null || _modAudioSources == null || _modAudioSources.Length == 0 || _modAudioSources[0] == null)
                {
                    _modAudioPoolGo = new GameObject("SilksongMobileAPI_ModAudioPool");
                    UnityEngine.Object.DontDestroyOnLoad(_modAudioPoolGo);
                    _modAudioSources = new AudioSource[4];
                    for (int i = 0; i < _modAudioSources.Length; i++)
                    {
                        var s = _modAudioPoolGo.AddComponent<AudioSource>();
                        s.playOnAwake = false;
                        s.priority = 0;
                        s.spatialBlend = 0f;
                        s.volume = 1f;
                        s.pitch = 1f;
                        s.mute = false;
                        s.bypassEffects = false;
                        s.ignoreListenerPause = false;
                        _modAudioSources[i] = s;
                    }
                }

                if (_cachedHeroMixerGroup == null && HeroController.instance != null)
                {
                    try
                    {
                        var comps = HeroController.instance.GetComponentsInChildren(typeof(AudioSource), true);
                        if (comps != null)
                        {
                            for (int i = 0; i < comps.Length; i++)
                            {
                                if (comps[i] is AudioSource hs && hs != null && hs.outputAudioMixerGroup != null)
                                {
                                    _cachedHeroMixerGroup = hs.outputAudioMixerGroup;
                                    break;
                                }
                            }
                        }
                    }
                    catch (System.Exception __apiEx_85) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3507", __apiEx_85); }
                }

                var src = _modAudioSources[(_nextModAudioIdx++) & 3];
                src.enabled = true;
                src.mute = false;
                src.spatialBlend = 0f;
                src.pitch = 1f;
                src.volume = 1f;
                if (_cachedHeroMixerGroup != null && src.outputAudioMixerGroup == null)
                {
                    src.outputAudioMixerGroup = _cachedHeroMixerGroup;
                }

                float effVol = volume <= 0f ? 1f : Mathf.Clamp(volume, 0.05f, 2f);

                if (!_resolvedPlayOneShotHelper)
                {
                    _resolvedPlayOneShotHelper = true;
                    _playOneShotHelperMethod = typeof(AudioSource).GetMethod("PlayOneShotHelper", BindingFlags.Static | BindingFlags.NonPublic);
                }

                if (_playOneShotHelperMethod != null)
                {
                    _playOneShotHelperMethod.Invoke(null, new object[] { src, clip, effVol });
                }
                else
                {
                    src.PlayOneShot(clip, effVol);
                }

                if (_loggedAudioPlays < 15)
                {
                    _loggedAudioPlays++;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[AudioBridge] Played mod AudioClip '{clip.name}' (vol={effVol:F2}, mixer='{src.outputAudioMixerGroup?.name ?? "Default"}').");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("AudioBridge", $"PlayModAudioClip({clip?.name})", ex);
            }
        }

        public static FieldInfo GetFieldBestEffort(Type type, string name, BindingFlags bindingAttr)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var fi = type.GetField(name, bindingAttr);
                if (fi != null) return fi;
                return type.GetField(name, bindingAttr | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return null;
            }
        }

        public static object GetFieldValueBestEffort(FieldInfo fi, object obj)
        {
            if (fi == null) return null;
            try
            {
                if (obj is HutongGames.PlayMaker.Actions.SendRandomEventV4 sre4)
                {
                    PlayMakerHybridCLRBridge.SanitizeSendRandomEventV4IfNeeded(sre4);
                }
                var val = fi.GetValue(obj);
                if (val == null && fi.FieldType == typeof(int[]))
                {
                    return Array.Empty<int>();
                }
                return val;
            }
            catch
            {
                return fi.FieldType == typeof(int[]) ? Array.Empty<int>() : null;
            }
        }

        public static void SetFieldValueBestEffort(FieldInfo fi, object obj, object val)
        {
            if (fi == null) return;
            try
            {
                fi.SetValue(obj, val);
                if (obj is HutongGames.PlayMaker.Actions.SendRandomEventV4 sre4)
                {
                    PlayMakerHybridCLRBridge.SanitizeSendRandomEventV4IfNeeded(sre4);
                }
            }
            catch { }
        }

        public static string GetAssemblyLocation(Assembly asm)
        {
            if (asm == null) return "";
            try
            {
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc)) return loc;
            }
            catch (System.Exception __apiEx_86) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3558", __apiEx_86); }
            string name = asm.GetName().Name ?? "";
            lock (_assemblyLocations)
            {
                if (_assemblyLocations.TryGetValue(name, out var path) && !string.IsNullOrEmpty(path))
                    return path;
                if (_assemblyLocations.TryGetValue(asm.FullName ?? "", out path) && !string.IsNullOrEmpty(path))
                    return path;
            }
            return Path.Combine(Paths.PluginPath, name + ".dll");
        }

        public static string GetModBaseDirectory(Assembly asm)
        {
            string loc = GetAssemblyLocation(asm);
            if (!string.IsNullOrEmpty(loc))
            {
                string dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return Paths.PluginPath;
        }

        public static UnityEngine.Networking.UnityWebRequest GetAudioClipSafe(string uri, AudioType audioType)
        {
            if (!string.IsNullOrEmpty(uri))
            {
                try
                {
                    string cleaned = uri.Replace(@"\\?\", "").Replace(@"\?\", "").Replace("\\", "/");
                    if (cleaned.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    {
                        string localPart = cleaned.Substring("file:///".Length).Replace("%20", " ");
                        if (!localPart.StartsWith("/") && File.Exists("/" + localPart))
                            localPart = "/" + localPart;
                        if (File.Exists(localPart))
                            cleaned = new Uri(localPart).AbsoluteUri;
                    }
                    else if (cleaned.StartsWith("/") && File.Exists(cleaned))
                    {
                        cleaned = new Uri(cleaned).AbsoluteUri;
                    }
                    uri = cleaned;
                }
                catch (System.Exception __apiEx_87) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3602", __apiEx_87); }
            }
            if (audioType == AudioType.UNKNOWN || (int)audioType == 0)
            {
                string lower = uri.ToLowerInvariant();
                if (lower.EndsWith(".mp3"))
                    audioType = AudioType.MPEG;
                else if (lower.EndsWith(".ogg"))
                    audioType = AudioType.OGGVORBIS;
                else if (lower.EndsWith(".wav"))
                    audioType = AudioType.WAV;
                else if (lower.EndsWith(".aif") || lower.EndsWith(".aiff"))
                    audioType = AudioType.AIFF;
            }
            return UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
        }

        private static Font _cachedFallbackFont;
        public static UnityEngine.Object GetBuiltinResourceSafe(Type type, string path)
        {
            if (type == typeof(Font) || (path != null && (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))))
            {
                return GetFallbackFont();
            }
            UnityEngine.Object res = null;
            try
            {
                res = Resources.GetBuiltinResource(type, path);
            }
            catch (System.Exception __apiEx_88) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3631", __apiEx_88); }
            if (res == null && type == typeof(Font))
            {
                res = GetFallbackFont();
            }
            return res;
        }

        public static T GetBuiltinResourceGenericSafe<T>(string path) where T : UnityEngine.Object
        {
            if (typeof(T) == typeof(Font) || (path != null && (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))))
            {
                return GetFallbackFont() as T;
            }
            T res = null;
            try
            {
                res = Resources.GetBuiltinResource<T>(path);
            }
            catch (System.Exception __apiEx_89) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3650", __apiEx_89); }
            if (res == null && typeof(T) == typeof(Font))
            {
                res = GetFallbackFont() as T;
            }
            return res;
        }

        public static Font GetFallbackFont()
        {
            if (_cachedFallbackFont != null && _cachedFallbackFont.material != null && _cachedFallbackFont.material.mainTexture != null)
                return _cachedFallbackFont;

            // 1. Search all loaded Font assets (Trajan, Perpetua, Cinzel, Serif)
            try
            {
                var allFonts = Resources.FindObjectsOfTypeAll<Font>();
                if (allFonts != null && allFonts.Length > 0)
                {
                    // Pass 2a: Preferred Silksong game fonts (Trajan, Perpetua, Cinzel, Serif) with textures
                    foreach (var f in allFonts)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf("LegacyRuntime", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (f.name.IndexOf("Trajan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            f.name.IndexOf("Perpetua", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            f.name.IndexOf("Cinzel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            f.name.IndexOf("Serif", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { f.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:.-!?/()[] ", 36); } catch (System.Exception __apiEx_90) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3679", __apiEx_90); }
                            if (f.material != null && f.material.mainTexture != null)
                            {
                                _cachedFallbackFont = f;
                                return _cachedFallbackFont;
                            }
                        }
                    }

                    // Pass 2b: Any non-LegacyRuntime font with a valid texture
                    foreach (var f in allFonts)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf("LegacyRuntime", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        try { f.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:.-!?/()[] ", 36); } catch (System.Exception __apiEx_91) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3693", __apiEx_91); }
                        if (f.material != null && f.material.mainTexture != null)
                        {
                            _cachedFallbackFont = f;
                            return _cachedFallbackFont;
                        }
                    }

                    // Pass 2c: Any non-LegacyRuntime font even if material.mainTexture check is delayed
                    foreach (var f in allFonts)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf("LegacyRuntime", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        try { f.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:.-!?/()[] ", 36); } catch (System.Exception __apiEx_92) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3706", __apiEx_92); }
                        _cachedFallbackFont = f;
                        return _cachedFallbackFont;
                    }
                }
            }
            catch (System.Exception __apiEx_93) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3712", __apiEx_93); }

            // 3. Create dynamic font from Android OS fonts
            try
            {
                string[] osFonts = new string[] { "Roboto", "Roboto-Regular", "sans-serif", "Droid Sans", "Arial" };
                var dynFont = Font.CreateDynamicFontFromOSFont(osFonts, 36);
                if (dynFont != null)
                {
                    try { dynFont.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:.-!?/()[] ", 36); } catch (System.Exception __apiEx_94) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3721", __apiEx_94); }
                    _cachedFallbackFont = dynFont;
                    return _cachedFallbackFont;
                }
            }
            catch (System.Exception __apiEx_95) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3726", __apiEx_95); }

            // 4. Fallback to OS font single name
            try
            {
                var dynFont = Font.CreateDynamicFontFromOSFont("sans-serif", 36);
                if (dynFont != null)
                {
                    try { dynFont.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:.-!?/()[] ", 36); } catch (System.Exception __apiEx_96) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3734", __apiEx_96); }
                    _cachedFallbackFont = dynFont;
                    return _cachedFallbackFont;
                }
            }
            catch (System.Exception __apiEx_97) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3739", __apiEx_97); }

            // 5. Ultimate fallback: whatever Unity has
            try
            {
                _cachedFallbackFont = Resources.GetBuiltinResource<Font>("Arial.ttf") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (System.Exception __apiEx_98) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3746", __apiEx_98); }

            return _cachedFallbackFont;
        }

        public static string GetBepInExProcessName() => "Hollow Knight Silksong";

        public static void RegisterEmbeddedResources(string asmName, Dictionary<string, byte[]> resources)
        {
            if (string.IsNullOrEmpty(asmName) || resources == null) return;
            string cleanAsm = asmName.EndsWith("_MobileRt", StringComparison.Ordinal)
                ? asmName.Substring(0, asmName.Length - "_MobileRt".Length)
                : asmName;
            lock (_embeddedResourcesByAssembly)
            {
                _embeddedResourcesByAssembly[asmName] = resources;
                _embeddedResourcesByAssembly[cleanAsm] = resources;
            }
            try
            {
                SeedBuiltinModTranslationsIfNeeded();
                foreach (var kv in resources)
                {
                    string resName = kv.Key ?? "";
                    if (!resName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (resName.EndsWith("deps.json", StringComparison.OrdinalIgnoreCase) ||
                        resName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                        resName.EndsWith("plugin.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (kv.Value == null || kv.Value.Length == 0) continue;

                    bool isEn = resName.IndexOf("en.json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                resName.IndexOf(".en.", StringComparison.OrdinalIgnoreCase) >= 0;
                    string jsonText = System.Text.Encoding.UTF8.GetString(kv.Value);
                    string[] sheetCandidates = new[] { $"Mods.{cleanAsm}", cleanAsm };
                    foreach (Match m in Regex.Matches(jsonText, "\"([^\"]+)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                    {
                        string k = m.Groups[1].Value;
                        string v = Regex.Unescape(m.Groups[2].Value);
                        if (string.IsNullOrEmpty(k)) continue;
                        foreach (var sName in sheetCandidates)
                        {
                            if (!_i18nOverridesBySheet.TryGetValue(sName, out var sDict))
                            {
                                sDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                _i18nOverridesBySheet[sName] = sDict;
                            }
                            if (isEn || !sDict.ContainsKey(k))
                            {
                                sDict[k] = v;
                                _i18nFlatKeys[k] = v;
                                _i18nFlatKeys[$"{sName}.{k}"] = v;
                                _i18nFlatKeys[$"{sName}/{k}"] = v;
                                _i18nFlatKeys[$"!!{sName}/{k}!!"] = v;
                                _i18nFlatKeys[$"!_{k}"] = v;
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_99) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3806", __apiEx_99); }
        }

        public static Assembly GetCallingAssemblySafe()
        {
            try
            {
                var st = new StackTrace(false);
                Assembly firstModAsm = null;
                for (int i = 1; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var asm = m?.DeclaringType?.Assembly;
                    if (asm == null || asm == typeof(HarmonyBridge).Assembly) continue;
                    string an = asm.GetName().Name ?? "";
                    if (an == "mscorlib" || an == "netstandard" || an == "System" || an == "System.Core" || an == "BepInEx")
                        continue;
                    if (firstModAsm == null)
                        firstModAsm = asm;
                    else if (asm != firstModAsm)
                        return asm;
                }
                if (firstModAsm != null) return firstModAsm;
            }
            catch (System.Exception __apiEx_100) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3830", __apiEx_100); }
            return Assembly.GetCallingAssembly();
        }

        public static Stream GetManifestResourceStream(Assembly asm, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string asmName = asm != null ? (asm.GetName().Name ?? "") : "";
            string cleanAsm = asmName.EndsWith("_MobileRt", StringComparison.Ordinal)
                ? asmName.Substring(0, asmName.Length - "_MobileRt".Length)
                : asmName;
            lock (_embeddedResourcesByAssembly)
            {
                if (!string.IsNullOrEmpty(asmName) &&
                    (_embeddedResourcesByAssembly.TryGetValue(asmName, out var dict) ||
                     _embeddedResourcesByAssembly.TryGetValue(cleanAsm, out dict)))
                {
                    if (dict.TryGetValue(name, out var bytes))
                        return new MemoryStream(bytes, writable: false);
                    var match = dict.FirstOrDefault(kv => kv.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                    if (match.Value != null)
                        return new MemoryStream(match.Value, writable: false);
                    var subMatch = dict.FirstOrDefault(kv => kv.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (subMatch.Value != null)
                        return new MemoryStream(subMatch.Value, writable: false);
                }
                foreach (var kvDict in _embeddedResourcesByAssembly)
                {
                    var d = kvDict.Value;
                    if (d == null) continue;
                    if (d.TryGetValue(name, out var b2))
                        return new MemoryStream(b2, writable: false);
                    var m2 = d.FirstOrDefault(kv => kv.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                    if (m2.Value != null)
                        return new MemoryStream(m2.Value, writable: false);
                }
            }
            try { return asm?.GetManifestResourceStream(name); } catch { return null; }
        }

        public static Stream GetManifestResourceStreamByType(Assembly asm, Type type, string name)
        {
            if (type != null && !string.IsNullOrEmpty(type.Namespace))
            {
                var s = GetManifestResourceStream(asm, $"{type.Namespace}.{name}");
                if (s != null) return s;
            }
            return GetManifestResourceStream(asm, name);
        }

        public static string[] GetManifestResourceNames(Assembly asm)
        {
            if (asm == null) return Array.Empty<string>();
            string asmName = asm.GetName().Name ?? "";
            lock (_embeddedResourcesByAssembly)
            {
                if (_embeddedResourcesByAssembly.TryGetValue(asmName, out var dict))
                    return dict.Keys.ToArray();
            }
            try { return asm.GetManifestResourceNames(); } catch { return Array.Empty<string>(); }
        }

        // ======================================================================
        // BaseUnityPlugin.Info Polyfill
        // ======================================================================
        public static void RegisterPluginInfo(BaseUnityPlugin plugin, BepInPlugin attr, string location)
        {
            if (plugin == null) return;
            var info = CreatePluginInfo(plugin, attr, location);
            string key = plugin.GetType().FullName ?? plugin.GetType().Name;
            lock (_pluginInfosByType)
            {
                _pluginInfosByType[key] = info;
            }
            try
            {
                var f = typeof(BaseUnityPlugin).GetField("<Info>k__BackingField", AllFlags);
                f?.SetValue(plugin, info);
            }
            catch (System.Exception __apiEx_101) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3909", __apiEx_101); }
        }

        public static BaseUnityPlugin GetPluginInfoInstance(PluginInfo info)
        {
            if (info == null) return null;
            try
            {
                return info.Instance as BaseUnityPlugin;
            }
            catch
            {
                return null;
            }
        }

        public static string FormattableStringInvariant(FormattableString formattable)
        {
            if (formattable == null) return string.Empty;
            try
            {
                return formattable.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return formattable.ToString();
            }
        }

        public static HarmonyMethod CreateHarmonyMethod(MethodInfo method)
        {
            return new HarmonyMethod
            {
                method = method,
                declaringType = method?.DeclaringType,
                methodName = method?.Name,
                priority = HarmonyLib.Priority.Normal
            };
        }

        public static HarmonyMethod CreateHarmonyMethodFromDelegate(Delegate del)
        {
            return CreateHarmonyMethod(del?.Method);
        }

        public static HarmonyMethod CreateHarmonyMethodFromType(Type methodType, string methodName, Type[] argumentTypes = null)
        {
            MethodInfo resolved = null;
            if (methodType != null && !string.IsNullOrEmpty(methodName))
            {
                try { resolved = FindMethodFlexible(methodType, methodName, argumentTypes); } catch (System.Exception __apiEx_102) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3959", __apiEx_102); }
            }
            return new HarmonyMethod
            {
                method = resolved,
                declaringType = methodType,
                methodName = methodName,
                argumentTypes = argumentTypes,
                priority = HarmonyLib.Priority.Normal
            };
        }

        public static HarmonyMethod CreateHarmonyMethodWithPrio(
            MethodInfo method,
            int priority = -1,
            string[] before = null,
            string[] after = null,
            bool? debug = null)
        {
            return new HarmonyMethod
            {
                method = method,
                declaringType = method?.DeclaringType,
                methodName = method?.Name,
                priority = priority >= 0 ? priority : HarmonyLib.Priority.Normal,
                debug = debug
            };
        }

        public static PluginInfo GetPluginInfo(BaseUnityPlugin plugin)
        {
            if (plugin == null) return null;
            try
            {
                var f = typeof(BaseUnityPlugin).GetField("<Info>k__BackingField", AllFlags);
                var existing = f?.GetValue(plugin) as PluginInfo;
                if (existing != null && existing.Metadata != null) return existing;
            }
            catch (System.Exception __apiEx_103) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L3997", __apiEx_103); }

            string key = plugin.GetType().FullName ?? plugin.GetType().Name;
            lock (_pluginInfosByType)
            {
                if (_pluginInfosByType.TryGetValue(key, out var cached) && cached != null)
                    return cached;
            }

            var attr = GetCustomAttributeSafe<BepInPlugin>(plugin.GetType(), true)
                    ?? new BepInPlugin(key, plugin.GetType().Name, "1.0.0");
            var created = CreatePluginInfo(plugin, attr, GetAssemblyLocation(plugin.GetType().Assembly));
            lock (_pluginInfosByType)
            {
                _pluginInfosByType[key] = created;
            }
            return created;
        }

        private static PluginInfo CreatePluginInfo(BaseUnityPlugin plugin, BepInPlugin attr, string location)
        {
            var info = new PluginInfo();
            try
            {
                var meta = new BepInPlugin(
                    attr?.GUID?.TrimStart('#', ' ') ?? plugin.GetType().FullName ?? "unknown.guid",
                    attr?.Name ?? plugin.GetType().Name ?? "UnknownMod",
                    attr?.Version?.ToString() ?? "1.0.0");

                SetMemberValue(info, "Metadata", meta);
                SetMemberValue(info, "Instance", plugin);
                SetMemberValue(info, "Location", location ?? "");
            }
            catch (System.Exception __apiEx_104) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4030", __apiEx_104); }
            return info;
        }

        private static void SetMemberValue(object target, string name, object value)
        {
            if (target == null) return;
            var t = target.GetType();
            var f = t.GetField($"<{name}>k__BackingField", AllFlags) ?? t.GetField(name, AllFlags) ?? t.GetField("_" + char.ToLowerInvariant(name[0]) + name.Substring(1), AllFlags);
            if (f != null) { f.SetValue(target, value); return; }
            var p = t.GetProperty(name, AllFlags);
            if (p != null && p.CanWrite) p.SetValue(target, value, null);
        }

        public static void PopulateChainloaderPlugins(Dictionary<string, PluginInfo> dict)
        {
            if (dict == null) return;
            lock (_pluginInfosByType)
            {
                foreach (var kv in _pluginInfosByType)
                {
                    if (kv.Value?.Metadata?.GUID != null)
                        dict[kv.Value.Metadata.GUID] = kv.Value;
                }
            }
            try
            {
                var allPlugins = UnityEngine.Object.FindObjectsOfType<BaseUnityPlugin>();
                if (allPlugins != null)
                {
                    foreach (var p in allPlugins)
                    {
                        if (p == null) continue;
                        var info = GetPluginInfo(p);
                        if (info?.Metadata?.GUID != null && !dict.ContainsKey(info.Metadata.GUID))
                            dict[info.Metadata.GUID] = info;
                    }
                }
            }
            catch (System.Exception __apiEx_105) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4069", __apiEx_105); }
        }

        // ======================================================================
        // Harmony Creation & Patching Shims
        // ======================================================================
        public static Harmony CreateHarmonyNoArgs()
        {
            return CreateHarmony("BepInEx.Harmony.Default");
        }

        public static Assembly CurrentlyInitializingModAssembly;
        private static readonly Dictionary<string, Assembly> _harmonyOwnerAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        public static Harmony CreateHarmony(string id)
        {
            var h = new Harmony();
            string key = GetInstanceKey(h);
            lock (_harmonyIds)
            {
                _harmonyIds[key] = id ?? "com.silksong.mobile.harmony";
                if (CurrentlyInitializingModAssembly != null)
                    _harmonyOwnerAssemblies[key] = CurrentlyInitializingModAssembly;
            }
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HarmonyBridge] Created Harmony instance (id='{id}')");
            return h;
        }

        public static string GetHarmonyId(Harmony harmony)
        {
            if (harmony == null) return "";
            string key = GetInstanceKey(harmony);
            lock (_harmonyIds)
            {
                return _harmonyIds.TryGetValue(key, out var id) ? id : "com.silksong.mobile.harmony";
            }
        }

        private static string GetInstanceKey(object obj)
        {
            if (obj == null) return "";
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj).ToString();
        }

        public static Harmony CreateAndPatchAllAssembly(Assembly assembly, string harmonyInstanceId = null)
        {
            var h = CreateHarmony(harmonyInstanceId ?? assembly?.GetName().Name ?? "com.silksong.mobile");
            PatchAllAssembly(h, assembly);
            return h;
        }

        public static Harmony CreateAndPatchAllType(Type type, string harmonyInstanceId = null)
        {
            var h = CreateHarmony(harmonyInstanceId ?? type?.FullName ?? "com.silksong.mobile");
            PatchAllType(h, type);
            return h;
        }

        public static void PatchAllByModName(Harmony harmony, string modOrAsmName)
        {
            if (!string.IsNullOrEmpty(modOrAsmName))
            {
                foreach (var asm in GetCachedLoadedAssemblies())
                {
                    if (asm == null) continue;
                    string aName = asm.GetName().Name ?? "";
                    if (string.Equals(aName, modOrAsmName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(aName, modOrAsmName + "_MobileRt", StringComparison.OrdinalIgnoreCase))
                    {
                        PatchAllAssembly(harmony, asm);
                        return;
                    }
                }
            }
            PatchAllNoArgs(harmony);
        }

        public static void PatchAllNoArgs(Harmony harmony)
        {
            Assembly callingAsm = null;
            if (harmony != null)
            {
                string key = GetInstanceKey(harmony);
                lock (_harmonyIds)
                {
                    _harmonyOwnerAssemblies.TryGetValue(key, out callingAsm);
                }
            }
            if (callingAsm == null && CurrentlyInitializingModAssembly != null)
            {
                callingAsm = CurrentlyInitializingModAssembly;
            }
            if (callingAsm == null && harmony != null)
            {
                string hId = GetHarmonyId(harmony);
                if (!string.IsNullOrEmpty(hId))
                {
                    foreach (var asm in GetCachedLoadedAssemblies())
                    {
                        if (asm == null || !IsManagedPcModAssembly(asm)) continue;
                        string aName = asm.GetName().Name ?? "";
                        if (!string.IsNullOrEmpty(aName) &&
                            (hId.IndexOf(aName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             aName.IndexOf(hId, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            callingAsm = asm;
                            break;
                        }
                    }
                }
            }
            if (callingAsm == null)
            {
                try
                {
                    var st = new StackTrace(false);
                    for (int i = 0; i < st.FrameCount; i++)
                    {
                        var m = st.GetFrame(i)?.GetMethod();
                        var asm = m?.DeclaringType?.Assembly;
                        if (asm == null || asm == typeof(HarmonyBridge).Assembly) continue;
                        string an = asm.GetName().Name ?? "";
                        if (an == "BepInEx" || an == "mscorlib" || an == "netstandard" ||
                            an == "Assembly-CSharp" || an.StartsWith("System", StringComparison.Ordinal) ||
                            an.StartsWith("UnityEngine", StringComparison.Ordinal) || an.StartsWith("Unity.", StringComparison.Ordinal))
                            continue;
                        callingAsm = asm;
                        break;
                    }
                }
                catch (System.Exception __apiEx_106) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4139", __apiEx_106); }
            }
            if (callingAsm != null)
                PatchAllAssembly(harmony, callingAsm);
        }

        public static void PatchAllAssembly(Harmony harmony, Assembly assembly)
        {
            if (assembly == null) return;
            string modName = assembly.GetName().Name ?? "UnknownMod";
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException(modName, "PatchAllAssembly.GetTypes", ex);
                return;
            }

            int count = 0;
            foreach (var type in types)
            {
                if (type == null || type.DeclaringType != null) continue;
                try
                {
                    if (IsPatchType(type))
                    {
                        PatchAllTypeInternal(modName, harmony, type, parentInfos: null);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException(modName, $"PatchAllType({type.FullName})", ex);
                }
            }
            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[HarmonyBridge] Completed PatchAll for '{modName}' ({count} top-level patch class(es) processed).");
            ReplayMissedSingletonHooksForMod(modName);
        }

        private static readonly HashSet<string> _replayedSingletonMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void RegisterPharloomFishingNestedLocalization()
        {
            try
            {
                using (var s = GetManifestResourceStream(null, "PharloomFishing.Data.Languages.EN.json"))
                {
                    if (s == null) return;
                    using (var sr = new StreamReader(s))
                    {
                        string json = sr.ReadToEnd();
                        if (string.IsNullOrEmpty(json)) return;
                        foreach (Match sm in Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\\{([^{}]*)\\}"))
                        {
                            string sheet = sm.Groups[1].Value;
                            string body = sm.Groups[2].Value;
                            if (string.IsNullOrEmpty(sheet)) continue;
                            if (!_i18nOverridesBySheet.TryGetValue(sheet, out var sheetDict))
                            {
                                sheetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                _i18nOverridesBySheet[sheet] = sheetDict;
                            }
                            foreach (Match km in Regex.Matches(body, "\"([^\"]+)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                            {
                                string k = km.Groups[1].Value;
                                string v = Regex.Unescape(km.Groups[2].Value);
                                if (string.IsNullOrEmpty(k)) continue;
                                sheetDict[k] = v;
                                _i18nFlatKeys[k] = v;
                                _i18nFlatKeys[$"{sheet}.{k}"] = v;
                                _i18nFlatKeys[$"{sheet}/{k}"] = v;
                                _i18nFlatKeys[$"!!{sheet}/{k}!!"] = v;
                                _i18nFlatKeys[$"!_{k}"] = v;
                            }
                        }
                        _lastSyncedSheetCount = -1;
                        SyncModLocalizationIntoGame();
                    }
                }
            }
            catch (System.Exception __apiEx_107) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4225", __apiEx_107); }
        }

        public static void ReplayMissedSingletonHooksForMod(string modName)
        {
            if (string.IsNullOrEmpty(modName)) return;
            if (modName.IndexOf("PharloomFishing", StringComparison.OrdinalIgnoreCase) < 0) return;
            try
            {
                RegisterPharloomFishingNestedLocalization();

                // 1. Replay Patches.Language_DoSwitchPostfix if present
                try
                {
                    foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (asm == null) continue;
                        string an = asm.GetName().Name ?? "";
                        if (an.IndexOf("PharloomFishing", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var patchesType = asm.GetType("PharloomFishing.Patches", false);
                        if (patchesType != null)
                        {
                            var langPost = patchesType.GetMethod("Language_DoSwitchPostfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            try { langPost?.Invoke(null, null); } catch (System.Exception __apiEx_108) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4248", __apiEx_108); }
                            break;
                        }
                    }
                }
                catch (System.Exception __apiEx_109) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4253", __apiEx_109); }

                // 2. Replay GameCameras.Awake hook on active GameCameras singleton
                var gc = GameCameras.SilentInstance;
                if (gc != null && _replayedSingletonMods.Add("PharloomFishing.GameCameras"))
                {
                    var awakeMi = typeof(GameCameras).GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (awakeMi != null)
                    {
                        int cid = RuntimeHybridCLRAdapterBuilder.GetChainIdForMethod(awakeMi);
                        if (cid > 0)
                        {
                            object[] args = Array.Empty<object>();
                            object dummyRes = null;
                            if (RuntimeHybridCLRAdapterBuilder.ShouldRunPrefix(cid))
                                RuntimeHybridCLRAdapterBuilder.DispatchPrefix(cid, gc, args, ref dummyRes);
                            if (RuntimeHybridCLRAdapterBuilder.ShouldRunPostfix(cid))
                                RuntimeHybridCLRAdapterBuilder.DispatchPostfix(cid, gc, args, ref dummyRes);
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Replayed GameCameras::Awake hook for PharloomFishing on active GameCameras singleton.");
                        }
                    }
                }

                // 3. Replay InventoryPaneList.OnValidate
                var paneLists = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
                if (paneLists != null && paneLists.Length > 0)
                {
                    var onValMi = typeof(InventoryPaneList).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < paneLists.Length; i++)
                    {
                        var pl = paneLists[i];
                        if (pl == null || pl.gameObject == null || !pl.gameObject.scene.IsValid()) continue;
                        if (_replayedSingletonMods.Add("PharloomFishing.InventoryPaneList." + pl.GetInstanceID()))
                        {
                            try { onValMi?.Invoke(pl, null); } catch (System.Exception __apiEx_110) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4287", __apiEx_110); }
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Replayed InventoryPaneList::OnValidate for PharloomFishing.");
                        }
                    }
                }

                // 4. Replay GameMap.SetMapManager & SetupMap so Fishing Spot Pins are created and positioned on the map!
                var mapMgrs = Resources.FindObjectsOfTypeAll<InventoryMapManager>();
                if (mapMgrs != null && mapMgrs.Length > 0)
                {
                    var gmField = typeof(InventoryMapManager).GetField("gameMap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < mapMgrs.Length; i++)
                    {
                        var mm = mapMgrs[i];
                        if (mm == null || mm.gameObject == null || !mm.gameObject.scene.IsValid()) continue;
                        var gMap = (gmField?.GetValue(mm) as GameMap) ?? (GameManager.SilentInstance != null ? GameManager.SilentInstance.gameMap : null);
                        if (gMap != null)
                        {
                            if (_replayedSingletonMods.Add("PharloomFishing.GameMap." + gMap.GetInstanceID()))
                            {
                                try
                                {
                                    gMap.SetMapManager(mm);
                                    gMap.SetupMap(false);
                                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Replayed GameMap::SetMapManager & SetupMap for PharloomFishing.");
                                }
                                catch (System.Exception __apiEx_111) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4313", __apiEx_111); }
                            }
                        }
                    }
                }
                else if (GameManager.SilentInstance != null && GameManager.SilentInstance.gameMap != null)
                {
                    var gMap = GameManager.SilentInstance.gameMap;
                    var mmField = typeof(GameMap).GetField("mapManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var mm = mmField?.GetValue(gMap) as InventoryMapManager;
                    if (mm != null && _replayedSingletonMods.Add("PharloomFishing.GameMap." + gMap.GetInstanceID()))
                    {
                        try
                        {
                            gMap.SetMapManager(mm);
                            gMap.SetupMap(false);
                            SilksongMobileAPIPlugin.LogDiagnosticInfo("[HarmonyBridge] Replayed GameMap::SetMapManager & SetupMap via GameManager.gameMap for PharloomFishing.");
                        }
                        catch (System.Exception __apiEx_112) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4331", __apiEx_112); }
                    }
                }
            }
            catch (System.Exception __apiEx_113) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4335", __apiEx_113); }
        }

        public static void PatchAllType(Harmony harmony, Type type)
        {
            if (type == null) return;
            string modName = type.Assembly.GetName().Name ?? "UnknownMod";
            try
            {
                PatchAllTypeInternal(modName, harmony, type, parentInfos: null);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException(modName, $"PatchAllType({type.FullName})", ex);
            }
        }

        public static void RegisterExtractedPatchInfo(string memberKey, ExtractedHarmonyPatchInfo info)
        {
            if (string.IsNullOrEmpty(memberKey) || info == null) return;
            lock (_extractedPatchInfos)
            {
                if (!_extractedPatchInfos.TryGetValue(memberKey, out var list))
                {
                    list = new List<ExtractedHarmonyPatchInfo>();
                    _extractedPatchInfos[memberKey] = list;
                }
                list.Add(info);
            }
        }

        private static List<ExtractedHarmonyPatchInfo> GetCombinedExtractedInfos(MemberInfo member)
        {
            var result = new List<ExtractedHarmonyPatchInfo>();
            if (member == null) return result;

            string asmName = member.Module?.Assembly?.GetName().Name ?? "";
            string memberKey = (member is Type t)
                ? $"{asmName}::T:{t.FullName}"
                : $"{asmName}::M:{member.DeclaringType?.FullName}::{member.Name}";

            lock (_extractedPatchInfos)
            {
                if (_extractedPatchInfos.TryGetValue(memberKey, out var cached))
                    result.AddRange(cached);
            }

            foreach (var meta in GetCustomAttributesSafe<MobileHarmonyPatchMetaAttribute>(member, false))
            {
                var argNames = string.IsNullOrEmpty(meta.ArgumentTypeNamesJoined)
                    ? null
                    : meta.ArgumentTypeNamesJoined.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                result.Add(new ExtractedHarmonyPatchInfo
                {
                    DeclaringTypeName = string.IsNullOrEmpty(meta.DeclaringTypeName) ? null : meta.DeclaringTypeName,
                    MethodName = string.IsNullOrEmpty(meta.MethodName) ? null : meta.MethodName,
                    PatchMethodType = meta.MethodTypeInt >= 0 ? (MethodType?)meta.MethodTypeInt : null,
                    ArgumentTypeNames = argNames
                });
            }

            foreach (var hp in GetCustomAttributesSafe<HarmonyPatch>(member, false))
            {
                if (hp?.info == null) continue;
                result.Add(new ExtractedHarmonyPatchInfo
                {
                    DeclaringTypeName = hp.info.declaringType?.AssemblyQualifiedName ?? hp.info.declaringType?.FullName,
                    MethodName = hp.info.methodName,
                    PatchMethodType = null,
                    ArgumentTypeNames = hp.info.argumentTypes?.Select(at => at?.AssemblyQualifiedName ?? at?.FullName ?? "").ToArray()
                });
            }

            return result;
        }

        private static bool IsPatchType(Type type)
        {
            if (type == null) return false;
            if (GetCombinedExtractedInfos(type).Count > 0) return true;
            if (GetCustomAttributesSafe<HarmonyPatch>(type, true).Count > 0) return true;

            try
            {
                foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (GetCombinedExtractedInfos(m).Count > 0) return true;
                    if (GetCustomAttributeSafe<HarmonyPrefix>(m, false) != null ||
                        GetCustomAttributeSafe<HarmonyPostfix>(m, false) != null ||
                        GetCustomAttributeSafe<HarmonyFinalizer>(m, false) != null ||
                        GetCustomAttributeSafe<HarmonyTranspiler>(m, false) != null ||
                        GetCustomAttributeSafe<HarmonyILManipulator>(m, false) != null)
                        return true;
                }
                foreach (var nt in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (IsPatchType(nt)) return true;
                }
            }
            catch (System.Exception __apiEx_114) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4434", __apiEx_114); }
            return false;
        }

        private static void PatchAllTypeInternal(string modName, Harmony harmony, Type type, List<ExtractedHarmonyPatchInfo> parentInfos)
        {
            if (type == null) return;

            var classInfos = new List<ExtractedHarmonyPatchInfo>();
            if (parentInfos != null) classInfos.AddRange(parentInfos);
            classInfos.AddRange(GetCombinedExtractedInfos(type));

            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var prepareMethod = methods.FirstOrDefault(m => m.Name == "Prepare" || GetCustomAttributeSafe<HarmonyPrepare>(m, false) != null);
            if (prepareMethod != null && prepareMethod.ReturnType == typeof(bool))
            {
                try
                {
                    var ps = prepareMethod.GetParameters();
                    object[] args = ps.Length == 0 ? Array.Empty<object>() : new object[ps.Length];
                    if (prepareMethod.Invoke(null, args) is bool shouldPatch && !shouldPatch)
                        return;
                }
                catch (System.Exception __apiEx_115) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4458", __apiEx_115); }
            }

            var explicitTargets = new List<MethodBase>();
            var targetMethodProvider = methods.FirstOrDefault(m => m.Name == "TargetMethod" || GetCustomAttributeSafe<HarmonyTargetMethod>(m, false) != null);
            if (targetMethodProvider != null)
            {
                try
                {
                    var mb = targetMethodProvider.Invoke(null, new object[targetMethodProvider.GetParameters().Length]) as MethodBase;
                    if (mb != null) explicitTargets.Add(mb);
                }
                catch (System.Exception __apiEx_116) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4470", __apiEx_116); }
            }
            var targetMethodsProvider = methods.FirstOrDefault(m => m.Name == "TargetMethods" || GetCustomAttributeSafe<HarmonyTargetMethods>(m, false) != null);
            if (targetMethodsProvider != null)
            {
                try
                {
                    if (targetMethodsProvider.Invoke(null, new object[targetMethodsProvider.GetParameters().Length]) is IEnumerable enm)
                    {
                        foreach (var item in enm)
                            if (item is MethodBase mb) explicitTargets.Add(mb);
                    }
                }
                catch (System.Exception __apiEx_117) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4483", __apiEx_117); }
            }

            var classPrefixes = new List<MethodInfo>();
            var classPostfixes = new List<MethodInfo>();
            var classFinalizers = new List<MethodInfo>();

            foreach (var m in methods)
            {
                var mInfos = GetCombinedExtractedInfos(m);
                bool isPrefix = m.Name == "Prefix" || GetCustomAttributeSafe<HarmonyPrefix>(m, false) != null;
                bool isPostfix = m.Name == "Postfix" || GetCustomAttributeSafe<HarmonyPostfix>(m, false) != null;
                bool isFinalizer = m.Name == "Finalizer" || GetCustomAttributeSafe<HarmonyFinalizer>(m, false) != null;
                bool isTranspiler = m.Name == "Transpiler" || GetCustomAttributeSafe<HarmonyTranspiler>(m, false) != null;

                if (mInfos.Count > 0)
                {
                    var combined = new List<ExtractedHarmonyPatchInfo>(classInfos);
                    combined.AddRange(mInfos);
                    var resolvedTargets = ResolveTargetMethodsFromInfos(combined);
                    foreach (var target in resolvedTargets)
                    {
                        if (isPrefix || isPostfix || isFinalizer)
                        {
                            ApplySinglePatch(
                                modName,
                                harmony,
                                target,
                                isPrefix ? m : null,
                                isPostfix ? m : null,
                                isTranspiler ? m : null,
                                isFinalizer ? m : null);
                        }
                    }
                }
                else
                {
                    if (isPrefix) classPrefixes.Add(m);
                    if (isPostfix) classPostfixes.Add(m);
                    if (isFinalizer) classFinalizers.Add(m);
                }
            }

            if (classPrefixes.Count > 0 || classPostfixes.Count > 0 || classFinalizers.Count > 0)
            {
                var classTargets = explicitTargets.Count > 0 ? explicitTargets : ResolveTargetMethodsFromInfos(classInfos);
                foreach (var target in classTargets)
                {
                    int maxCount = Math.Max(classPrefixes.Count, Math.Max(classPostfixes.Count, classFinalizers.Count));
                    for (int i = 0; i < maxCount; i++)
                    {
                        var pre = i < classPrefixes.Count ? classPrefixes[i] : null;
                        var post = i < classPostfixes.Count ? classPostfixes[i] : null;
                        var fin = i < classFinalizers.Count ? classFinalizers[i] : null;
                        ApplySinglePatch(modName, harmony, target, pre, post, null, fin);
                    }
                }
            }

            try
            {
                foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (IsPatchType(nested))
                        PatchAllTypeInternal(modName, harmony, nested, classInfos);
                }
            }
            catch (System.Exception __apiEx_118) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4550", __apiEx_118); }
        }

        private static List<MethodBase> ResolveTargetMethodsFromInfos(List<ExtractedHarmonyPatchInfo> infos)
        {
            var targets = new List<MethodBase>();
            if (infos == null || infos.Count == 0) return targets;

            Type declaringType = null;
            string methodName = null;
            MethodType methodType = MethodType.Normal;
            Type[] argTypes = null;

            foreach (var info in infos)
            {
                if (!string.IsNullOrEmpty(info.DeclaringTypeName))
                {
                    var t = ResolveTypeRobust(info.DeclaringTypeName);
                    if (t != null) declaringType = t;
                }
                if (!string.IsNullOrEmpty(info.MethodName))
                    methodName = info.MethodName;
                if (info.PatchMethodType.HasValue)
                    methodType = info.PatchMethodType.Value;
                if (info.ArgumentTypeNames != null && info.ArgumentTypeNames.Length > 0)
                {
                    if (info.ArgumentTypeNames.Length == 1 && info.ArgumentTypeNames[0] == "__ZERO_ARGS__")
                    {
                        argTypes = Type.EmptyTypes;
                    }
                    else
                    {
                        var resolvedArgs = new List<Type>();
                        bool allResolved = true;
                        foreach (var atn in info.ArgumentTypeNames)
                        {
                            var at = ResolveTypeRobust(atn);
                            if (at == null) { allResolved = false; break; }
                            resolvedArgs.Add(at);
                        }
                        if (allResolved) argTypes = resolvedArgs.ToArray();
                    }
                }
            }

            if (declaringType == null) return targets;

            try
            {
                switch (methodType)
                {
                    case MethodType.Getter:
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            var p = AccessTools.Property(declaringType, methodName);
                            var gm = p?.GetGetMethod(true) ?? AccessTools.Method(declaringType, "get_" + methodName, argTypes);
                            if (gm != null) targets.Add(gm);
                        }
                        break;

                    case MethodType.Setter:
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            var p = AccessTools.Property(declaringType, methodName);
                            var sm = p?.GetSetMethod(true) ?? AccessTools.Method(declaringType, "set_" + methodName, argTypes);
                            if (sm != null) targets.Add(sm);
                        }
                        break;

                    case MethodType.Constructor:
                        var ctor = argTypes != null
                            ? declaringType.GetConstructor(AllFlags, null, argTypes, null)
                            : declaringType.GetConstructors(AllFlags).FirstOrDefault();
                        if (ctor != null) targets.Add(ctor);
                        break;

                    case MethodType.StaticConstructor:
                        var cctor = declaringType.TypeInitializer;
                        if (cctor != null) targets.Add(cctor);
                        break;

                    case MethodType.Enumerator:
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            var orig = FindMethodFlexible(declaringType, methodName, argTypes);
                            var moveNext = EnumeratorMoveNext(orig) ?? FindStateMachineMoveNext(declaringType, methodName);
                            if (moveNext != null) targets.Add(moveNext);
                        }
                        break;

                    default:
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            var m = FindMethodFlexible(declaringType, methodName, argTypes);
                            if (m != null) targets.Add(m);
                        }
                        break;
                }
            }
            catch (System.Exception __apiEx_119) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4649", __apiEx_119); }

            return targets;
        }

        private static MethodInfo FindMethodFlexible(Type type, string methodName, Type[] argTypes)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return null;
            if (argTypes != null)
            {
                var exact = type.GetMethod(methodName, AllFlags, null, argTypes, null);
                if (exact != null) return exact;
                foreach (var m in type.GetMethods(AllFlags))
                {
                    if (m.Name != methodName) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != argTypes.Length) continue;
                    bool match = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                        var at = argTypes[i].IsByRef ? argTypes[i].GetElementType() : argTypes[i];
                        if (pt != at && pt?.FullName != at?.FullName) { match = false; break; }
                    }
                    if (match) return m;
                }
            }

            var candidates = type.GetMethods(AllFlags).Where(m => m.Name == methodName && !m.IsGenericMethodDefinition).ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1)
            {
                var nonObsolete = candidates.Where(m => !m.IsDefined(typeof(ObsoleteAttribute), false)).ToArray();
                if (nonObsolete.Length == 1) return nonObsolete[0];
                if (nonObsolete.Length > 1)
                    return nonObsolete.OrderByDescending(m => m.GetParameters().Length).First();
                return candidates.OrderByDescending(m => m.GetParameters().Length).First();
            }

            return AccessTools.Method(type, methodName, argTypes);
        }

        private static readonly Dictionary<string, Type> _resolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<string> _negativeTypeCache = new HashSet<string>(StringComparer.Ordinal);
        private static int _lastResolvedTypeAsmCount = -1;

        public static Type ResolveTypeRobust(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string cleaned = typeName.Trim();
            while (cleaned.Length > 1 && (cleaned[0] < 'A' || (cleaned[0] > 'Z' && cleaned[0] < 'a' && cleaned[0] != '_')))
                cleaned = cleaned.Substring(1);

            var asms = SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies();
            lock (_resolvedTypeCache)
            {
                if (asms.Length != _lastResolvedTypeAsmCount)
                {
                    _lastResolvedTypeAsmCount = asms.Length;
                    _negativeTypeCache.Clear();
                }
                if (_resolvedTypeCache.TryGetValue(cleaned, out var cached) && cached != null)
                    return cached;
                if (_negativeTypeCache.Contains(cleaned))
                    return null;
            }

            Type found = null;
            try
            {
                found = Type.GetType(cleaned, false);
            }
            catch (System.Exception __apiEx_120) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4721", __apiEx_120); }

            if (found == null)
            {
                int commaIdx = cleaned.IndexOf(',');
                string shortTypeName = commaIdx > 0 ? cleaned.Substring(0, commaIdx).Trim() : cleaned;
                string normalizedNested = shortTypeName.Replace('/', '+');

                for (int i = 0; i < asms.Length; i++)
                {
                    var asm = asms[i];
                    if (asm == null) continue;
                    try
                    {
                        found = asm.GetType(normalizedNested, false) ?? asm.GetType(shortTypeName, false);
                        if (found != null) break;
                    }
                    catch (System.Exception __apiEx_121) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4738", __apiEx_121); }
                }

                if (found == null)
                {
                    try
                    {
                        found = AccessTools.TypeByName(normalizedNested);
                    }
                    catch (System.Exception __apiEx_122) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4747", __apiEx_122); }
                }
            }

            lock (_resolvedTypeCache)
            {
                if (found != null)
                    _resolvedTypeCache[cleaned] = found;
                else
                    _negativeTypeCache.Add(cleaned);
            }
            return found;
        }

        public static MethodInfo PatchWithHarmonyMethods(
            Harmony harmony,
            MethodBase original,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null,
            HarmonyMethod ilmanipulator = null)
        {
            if (original == null) return null;
            string modName = prefix?.method?.DeclaringType?.Assembly?.GetName().Name
                          ?? postfix?.method?.DeclaringType?.Assembly?.GetName().Name
                          ?? finalizer?.method?.DeclaringType?.Assembly?.GetName().Name
                          ?? GetHarmonyId(harmony);

            int prio = HarmonyLib.Priority.Normal;
            if (prefix != null && prefix.priority >= 0) prio = prefix.priority;
            else if (postfix != null && postfix.priority >= 0) prio = postfix.priority;

            ApplySinglePatch(
                modName,
                harmony,
                original,
                prefix?.method,
                postfix?.method,
                transpiler?.method,
                finalizer?.method,
                prio);

            return original as MethodInfo;
        }

        public static MethodInfo Patch5Args(
            Harmony harmony,
            MethodBase original,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod transpiler,
            HarmonyMethod finalizer)
        {
            return PatchWithHarmonyMethods(harmony, original, prefix, postfix, transpiler, finalizer, null);
        }

        public static MethodInfo Patch4Args(
            Harmony harmony,
            MethodBase original,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod transpiler)
        {
            return PatchWithHarmonyMethods(harmony, original, prefix, postfix, transpiler, null, null);
        }

        public static HookResult PatchMethodInfos2(Harmony harmony, MethodBase original, MethodInfo prefix)
        {
            return PatchMethodInfos5(harmony, original, prefix, null, null, null);
        }

        public static HookResult PatchMethodInfos3(Harmony harmony, MethodBase original, MethodInfo prefix, MethodInfo postfix)
        {
            return PatchMethodInfos5(harmony, original, prefix, postfix, null, null);
        }

        public static HookResult PatchMethodInfos4(Harmony harmony, MethodBase original, MethodInfo prefix, MethodInfo postfix, MethodInfo transpiler)
        {
            return PatchMethodInfos5(harmony, original, prefix, postfix, transpiler, null);
        }

        public static HookResult PatchMethodInfos5(
            Harmony harmony,
            MethodBase original,
            MethodInfo prefix,
            MethodInfo postfix,
            MethodInfo transpiler,
            MethodInfo finalizer)
        {
            if (original == null) return HookResult.TargetIsNull;
            string modName = prefix?.DeclaringType?.Assembly?.GetName().Name
                          ?? postfix?.DeclaringType?.Assembly?.GetName().Name
                          ?? finalizer?.DeclaringType?.Assembly?.GetName().Name
                          ?? GetHarmonyId(harmony);
            ApplySinglePatch(modName, harmony, original, prefix, postfix, transpiler, finalizer, HarmonyLib.Priority.Normal);
            return HookResult.Success;
        }

        public static MethodInfo PatchMethodInfos2MethodInfo(Harmony harmony, MethodBase original, MethodInfo prefix)
        {
            PatchMethodInfos2(harmony, original, prefix);
            return original as MethodInfo;
        }

        public static MethodInfo PatchMethodInfos3MethodInfo(Harmony harmony, MethodBase original, MethodInfo prefix, MethodInfo postfix)
        {
            PatchMethodInfos3(harmony, original, prefix, postfix);
            return original as MethodInfo;
        }

        public static MethodInfo PatchMethodInfos4MethodInfo(Harmony harmony, MethodBase original, MethodInfo prefix, MethodInfo postfix, MethodInfo transpiler)
        {
            PatchMethodInfos4(harmony, original, prefix, postfix, transpiler);
            return original as MethodInfo;
        }

        public static MethodInfo PatchMethodInfos5MethodInfo(
            Harmony harmony,
            MethodBase original,
            MethodInfo prefix,
            MethodInfo postfix,
            MethodInfo transpiler,
            MethodInfo finalizer)
        {
            PatchMethodInfos5(harmony, original, prefix, postfix, transpiler, finalizer);
            return original as MethodInfo;
        }

        public static void ApplySinglePatch(
            string modName,
            Harmony harmony,
            MethodBase target,
            MethodInfo prefix,
            MethodInfo postfix,
            MethodInfo transpiler,
            MethodInfo finalizer,
            int priority = HarmonyLib.Priority.Normal)
        {
            if (target == null) return;
            if (prefix == null && postfix == null && finalizer == null) return;

            try
            {
                string ownerId = harmony != null ? GetHarmonyId(harmony) : modName;
                if ((modName != null && modName.IndexOf("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (ownerId != null && ownerId.IndexOf("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    priority = HarmonyLib.Priority.Last;
                }
                RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain(modName, target, prefix, postfix, finalizer, priority, ownerId);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException(modName, $"Harmony.Patch({target.DeclaringType?.Name}.{target.Name})", ex);
            }
        }

        public static void UnpatchSelf(Harmony harmony) { }
        public static void UnpatchId(Harmony harmony, string harmonyID) { }
        public static void UnpatchMethod(Harmony harmony, MethodBase original, MethodInfo patch) { }

        public static IEnumerable<MethodBase> GetPatchedMethods(Harmony harmony)
        {
            return RuntimeHybridCLRAdapterBuilder.GetAllPatchedMethods();
        }

        public static IEnumerable<MethodBase> GetAllPatchedMethods()
        {
            return RuntimeHybridCLRAdapterBuilder.GetAllPatchedMethods();
        }

        public static PatchClassProcessor CreateClassProcessor(Harmony harmony, Type type)
        {
            return new PatchClassProcessor(harmony, type);
        }

        public static PatchClassProcessor CreateClassProcessorBool(Harmony harmony, Type type, bool allowUnannotatedType)
        {
            return new PatchClassProcessor(harmony, type, allowUnannotatedType);
        }

        public static Patches GetPatchInfo(MethodBase method)
        {
            return RuntimeHybridCLRAdapterBuilder.GetPatchInfo(method);
        }

        public static Patches InstanceGetPatchInfo(Harmony harmony, MethodBase method)
        {
            return RuntimeHybridCLRAdapterBuilder.GetPatchInfo(method);
        }

        public static bool ToolItemManager_IsToolEquipped_Prefix(ToolItem tool, ToolEquippedReadSource readSource, ref bool __result)
        {
            if (tool == null || !PlayerData.HasInstance || PlayerData.instance == null || ManagerSingleton<ToolItemManager>.Instance == null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool ToolItemManager_IsToolEquippedString_Prefix(string name, ref bool __result)
        {
            if (string.IsNullOrEmpty(name) || !PlayerData.HasInstance || PlayerData.instance == null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static MethodInfo AccessToolsMethodSafe(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            MethodInfo mi = null;
            try
            {
                mi = AccessTools.Method(type, name, parameters, generics);
            }
            catch (System.Exception __apiEx_123) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4962", __apiEx_123); }
            if (mi != null) return mi;

            try
            {
                var candidates = type.GetMethods(AllFlags).Where(m => m.Name == name && !m.IsGenericMethodDefinition).ToArray();
                if (candidates.Length == 1) return candidates[0];
                if (candidates.Length > 1 && parameters != null)
                {
                    MethodInfo best = null;
                    int maxMatch = -1;
                    foreach (var c in candidates)
                    {
                        var ps = c.GetParameters();
                        int match = 0;
                        int minLen = Math.Min(ps.Length, parameters.Length);
                        for (int i = 0; i < minLen; i++)
                        {
                            var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                            var at = parameters[i].IsByRef ? parameters[i].GetElementType() : parameters[i];
                            if (pt == at || pt?.FullName == at?.FullName) match++;
                        }
                        if (match > maxMatch)
                        {
                            maxMatch = match;
                            best = c;
                        }
                    }
                    if (best != null && maxMatch >= 2) return best;
                }
            }
            catch (System.Exception __apiEx_124) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L4993", __apiEx_124); }
            return null;
        }

        public static MethodBase EnumeratorMoveNext(MethodBase method)
        {
            if (method == null || method.DeclaringType == null) return null;
            return FindStateMachineMoveNext(method.DeclaringType, method.Name);
        }

        private static MethodInfo FindStateMachineMoveNext(Type declaringType, string methodName)
        {
            if (declaringType == null || string.IsNullOrEmpty(methodName)) return null;
            try
            {
                foreach (var nt in declaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (nt.Name.StartsWith($"<{methodName}>d__") || nt.Name.Contains($"<{methodName}>"))
                    {
                        var mn = nt.GetMethod("MoveNext", AllFlags);
                        if (mn != null) return mn;
                    }
                }
            }
            catch (System.Exception __apiEx_125) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5017", __apiEx_125); }
            return null;
        }

        // ======================================================================
        // Stripped UnityEngine Property/Method Polyfills for PC Mods
        // ======================================================================
        public static float GetLinearDrag(Rigidbody2D rb)
        {
            if (rb == null) return 0f;
            try
            {
                var p = typeof(Rigidbody2D).GetProperty("linearDamping") ?? typeof(Rigidbody2D).GetProperty("drag");
                if (p != null) return (float)p.GetValue(rb, null);
            }
            catch (System.Exception __apiEx_126) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5032", __apiEx_126); }
            return 0f;
        }

        public static void SetLinearDrag(Rigidbody2D rb, float val)
        {
            if (rb == null) return;
            try
            {
                var p = typeof(Rigidbody2D).GetProperty("linearDamping") ?? typeof(Rigidbody2D).GetProperty("drag");
                p?.SetValue(rb, val, null);
            }
            catch (System.Exception __apiEx_127) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5044", __apiEx_127); }
        }

        public static Vector2 GetLinearVelocity(Rigidbody2D rb)
        {
            return rb != null ? rb.velocity : Vector2.zero;
        }

        public static void SetLinearVelocity(Rigidbody2D rb, Vector2 val)
        {
            if (rb != null) rb.velocity = val;
        }

        public static float GetLinearVelocityX(Rigidbody2D rb)
        {
            return rb != null ? rb.velocity.x : 0f;
        }

        public static void SetLinearVelocityX(Rigidbody2D rb, float x)
        {
            if (rb != null)
            {
                var v = rb.velocity;
                v.x = x;
                rb.velocity = v;
            }
        }

        public static float GetLinearVelocityY(Rigidbody2D rb)
        {
            return rb != null ? rb.velocity.y : 0f;
        }

        public static void SetLinearVelocityY(Rigidbody2D rb, float y)
        {
            if (rb != null)
            {
                var v = rb.velocity;
                v.y = y;
                rb.velocity = v;
            }
        }

        public static float GetLinearDamping(Rigidbody2D rb) => GetLinearDrag(rb);
        public static void SetLinearDamping(Rigidbody2D rb, float val) => SetLinearDrag(rb, val);

        public static float GetAngularDamping(Rigidbody2D rb)
        {
            if (rb == null) return 0f;
            try { return rb.angularDrag; } catch { return 0f; }
        }

        public static void SetAngularDamping(Rigidbody2D rb, float val)
        {
            if (rb == null) return;
            try { rb.angularDrag = val; } catch (System.Exception __apiEx_128) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5099", __apiEx_128); }
        }

        public static UnityEngine.AssetBundleUnloadOperation AssetBundleUnloadAsync(AssetBundle bundle, bool unloadAllLoadedObjects)
        {
            try { if (bundle != null) bundle.Unload(unloadAllLoadedObjects); } catch (System.Exception __apiEx_129) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5104", __apiEx_129); }
            return new UnityEngine.AssetBundleUnloadOperation();
        }

        private static FieldInfo _fiInputFieldEndEdit;
        private static bool _fiInputFieldEndEditChecked;

        private static FieldInfo GetInputFieldEndEditField()
        {
            if (!_fiInputFieldEndEditChecked)
            {
                _fiInputFieldEndEditChecked = true;
                _fiInputFieldEndEdit = typeof(UnityEngine.UI.InputField).GetField("m_EndEdit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                    ?? typeof(UnityEngine.UI.InputField).GetField("m_OnSubmit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            return _fiInputFieldEndEdit;
        }

        public static UnityEngine.UI.InputField.SubmitEvent GetInputFieldOnSubmit(UnityEngine.UI.InputField inputField)
        {
            if (inputField != null)
            {
                try
                {
                    var fi = GetInputFieldEndEditField();
                    if (fi != null)
                    {
                        if (fi.GetValue(inputField) is UnityEngine.UI.InputField.SubmitEvent existing)
                            return existing;
                        var created = new UnityEngine.UI.InputField.SubmitEvent();
                        fi.SetValue(inputField, created);
                        return created;
                    }
                }
                catch { }
            }
            return new UnityEngine.UI.InputField.SubmitEvent();
        }

        public static void SetInputFieldOnSubmit(UnityEngine.UI.InputField inputField, UnityEngine.UI.InputField.SubmitEvent ev)
        {
            if (inputField != null && ev != null)
            {
                try
                {
                    GetInputFieldEndEditField()?.SetValue(inputField, ev);
                }
                catch { }
            }
        }

        public static bool GetCanvasVertexColorAlwaysGammaSpace(Canvas canvas) => false;
        public static void SetCanvasVertexColorAlwaysGammaSpace(Canvas canvas, bool val) { }

        public static int GetCollider2DCompositeOperation(Collider2D col) => 0;
        public static void SetCollider2DCompositeOperation(Collider2D col, int op) { }

        public static Vector3 GetInputMousePositionDelta() => Vector3.zero;

        private static readonly Dictionary<string, FieldInfo> _pdFieldCache = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PropertyInfo> _pdPropCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        public static T GetPlayerDataField<T>(string fieldName)
        {
            try
            {
                var pd = PlayerData.instance;
                if (pd == null || string.IsNullOrEmpty(fieldName)) return default;
                if (!_pdFieldCache.TryGetValue(fieldName, out var fi))
                {
                    fi = typeof(PlayerData).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pdFieldCache[fieldName] = fi;
                }
                if (fi != null)
                {
                    object v = fi.GetValue(pd);
                    if (v is T tv) return tv;
                }
                if (!_pdPropCache.TryGetValue(fieldName, out var pi))
                {
                    pi = typeof(PlayerData).GetProperty(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pdPropCache[fieldName] = pi;
                }
                if (pi != null)
                {
                    object v = pi.GetValue(pd, null);
                    if (v is T tv) return tv;
                }
            }
            catch (System.Exception __apiEx_130) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5156", __apiEx_130); }
            return default;
        }

        public static void SetPlayerDataField<T>(T value, string fieldName)
        {
            try
            {
                var pd = PlayerData.instance;
                if (pd == null || string.IsNullOrEmpty(fieldName)) return;
                if (!_pdFieldCache.TryGetValue(fieldName, out var fi))
                {
                    fi = typeof(PlayerData).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pdFieldCache[fieldName] = fi;
                }
                if (fi != null)
                {
                    fi.SetValue(pd, value);
                    return;
                }
                if (!_pdPropCache.TryGetValue(fieldName, out var pi))
                {
                    pi = typeof(PlayerData).GetProperty(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pdPropCache[fieldName] = pi;
                }
                if (pi != null && pi.CanWrite)
                {
                    pi.SetValue(pd, value, null);
                }
            }
            catch (System.Exception __apiEx_131) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5186", __apiEx_131); }
        }

        public static bool GetColliderDistanceIsOverlapped(ColliderDistance2D cd)
        {
            try { return cd.distance < 0f; } catch { return false; }
        }

        // ======================================================================
        // PrepatcherPlugin PlayerDataVariableEvents Runtime Bridge
        // ======================================================================
        public static void RegisterPrepatcherEvent(object delObj, string eventSignature)
        {
            if (!(delObj is Delegate del)) return;
            bool isRemove = eventSignature != null && eventSignature.IndexOf("remove_", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isSet = eventSignature != null && eventSignature.IndexOf("OnSet", StringComparison.OrdinalIgnoreCase) >= 0;

            var targetList = isSet ? _prepatcherSetHandlers : _prepatcherGetHandlers;
            lock (targetList)
            {
                if (isRemove)
                    targetList.Remove(del);
                else if (!targetList.Contains(del))
                    targetList.Add(del);
            }

            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[PrepatcherBridge] {(isRemove ? "Removed" : "Registered")} PlayerData handler '{del.Method.DeclaringType?.FullName}.{del.Method.Name}' for '{eventSignature}'");
            if (!isRemove)
                EnsurePrepatcherHooksInstalled();
        }

        public static void EnsurePrepatcherHooksInstalled()
        {
            if (_prepatcherHooksInstalled) return;
            _prepatcherHooksInstalled = true;
            try
            {
                var pdType = typeof(PlayerData);
                HookPlayerDataMethod(pdType, "GetBool", null, nameof(PD_GetBool_Postfix));
                HookPlayerDataMethod(pdType, "SetBool", nameof(PD_SetBool_Prefix), null);
                HookPlayerDataMethod(pdType, "GetInt", null, nameof(PD_GetInt_Postfix));
                HookPlayerDataMethod(pdType, "SetInt", nameof(PD_SetInt_Prefix), null);
                HookPlayerDataMethod(pdType, "GetFloat", null, nameof(PD_GetFloat_Postfix), new[] { typeof(string) });
                HookPlayerDataMethod(pdType, "SetFloat", nameof(PD_SetFloat_Prefix), null, new[] { typeof(string), typeof(float) });
                HookPlayerDataMethod(pdType, "GetString", null, nameof(PD_GetString_Postfix));
                HookPlayerDataMethod(pdType, "SetString", nameof(PD_SetString_Prefix), null);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[PrepatcherBridge] Hook install warning: {ex.Message}");
            }
        }

        private static void HookPlayerDataMethod(Type pdType, string methodName, string prefixName, string postfixName, Type[] argTypes = null)
        {
            var m = argTypes != null
                ? pdType.GetMethod(methodName, AllFlags, null, argTypes, null)
                : pdType.GetMethods(AllFlags).FirstOrDefault(mi => mi.Name == methodName && !mi.IsGenericMethodDefinition);
            if (m == null) return;
            var pre = prefixName != null ? typeof(HarmonyBridge).GetMethod(prefixName, BindingFlags.Static | BindingFlags.Public) : null;
            var post = postfixName != null ? typeof(HarmonyBridge).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public) : null;
            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SilksongMobileAPI.Prepatcher", m, pre, post, null, HarmonyLib.Priority.High);
        }

        private static T RunPrepatcherHandlers<T>(List<Delegate> handlers, PlayerData pd, string fieldName, T current)
        {
            Delegate[] snapshot;
            lock (handlers)
            {
                if (handlers.Count == 0) return current;
                snapshot = handlers.ToArray();
            }
            T val = current;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var d = snapshot[i];
                if (d == null) continue;
                try
                {
                    var ps = d.Method.GetParameters();
                    if (ps.Length >= 3)
                    {
                        var valParamType = ps[ps.Length - 1].ParameterType;
                        if (valParamType.IsAssignableFrom(typeof(T)) || valParamType == typeof(object))
                        {
                            var res = d.DynamicInvoke(pd, fieldName, val);
                            if (res is T typedRes)
                                val = typedRes;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogModException("PrepatcherBridge", $"PlayerDataEvent({fieldName})", ex);
                }
            }
            return val;
        }


        // =====================================================================
        // RebalancedCrests Mobile Helpers (Wanderer Swipe + Reaper Payout)
        // =====================================================================
        public static void RebalancedCrests_SafeStartSwipeSlash(object nailSlashObj)
        {
            if (!(nailSlashObj is NailSlash ns) || ns == null) return;
            try
            {
                if (ns.transform.parent != null && !ns.transform.parent.gameObject.activeSelf)
                {
                    ns.transform.parent.gameObject.SetActive(true);
                }
                if (!ns.gameObject.activeSelf)
                {
                    ns.gameObject.SetActive(true);
                }
                EnsureCustomNailSlashColliderAndAnimReady(ns);
                ns.StartSlash();
                EnsureCustomNailSlashColliderAndAnimReady(ns);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("RebalancedCrests", "SafeStartSwipeSlash", ex);
            }
        }

        public static bool RebalancedCrests_SafeBaseChargedClip(HeroAnimationController animCtrl, string clipName, out tk2dSpriteAnimationClip resultClip)
        {
            resultClip = null;
            try
            {
                if (animCtrl != null && animCtrl.animator != null && !string.IsNullOrEmpty(clipName))
                {
                    var found = animCtrl.animator.GetClipByName(clipName);
                    if (found != null)
                    {
                        resultClip = found;
                        return false; // Skip original GetClip and use base Slash_Charged clip
                    }
                }
            }
            catch (System.Exception __apiEx_132) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5327", __apiEx_132); }
            return true; // Fallback to original GetClip if null
        }

        // =====================================================================
        // HeroController::reaperModeEffect Safe Field Bridge & Auric Aegis (PrimedSentinel) Boss Fight Guards
        // =====================================================================
        private static FieldInfo _hcReaperModeEffectField;
        private static bool _hcReaperModeEffectFieldSearched;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HeroController, GameObject> _fallbackReaperModeEffectTable =
            new System.Runtime.CompilerServices.ConditionalWeakTable<HeroController, GameObject>();

        public static GameObject GetHeroReaperModeEffect(HeroController hc)
        {
            if (hc == null) return null;
            try
            {
                if (!_hcReaperModeEffectFieldSearched)
                {
                    _hcReaperModeEffectFieldSearched = true;
                    _hcReaperModeEffectField = typeof(HeroController).GetField("reaperModeEffect", AllFlags);
                }
                if (_hcReaperModeEffectField != null)
                {
                    return _hcReaperModeEffectField.GetValue(hc) as GameObject;
                }
            }
            catch
            {
                _hcReaperModeEffectField = null;
            }
            if (_fallbackReaperModeEffectTable.TryGetValue(hc, out var cachedGo))
                return cachedGo;
            return null;
        }

        public static void SetHeroReaperModeEffect(HeroController hc, GameObject value)
        {
            if (hc == null) return;
            try
            {
                if (!_hcReaperModeEffectFieldSearched)
                {
                    _hcReaperModeEffectFieldSearched = true;
                    _hcReaperModeEffectField = typeof(HeroController).GetField("reaperModeEffect", AllFlags);
                }
                if (_hcReaperModeEffectField != null)
                {
                    _hcReaperModeEffectField.SetValue(hc, value);
                    return;
                }
            }
            catch
            {
                _hcReaperModeEffectField = null;
            }
            _fallbackReaperModeEffectTable.Remove(hc);
            if (value != null)
                _fallbackReaperModeEffectTable.Add(hc, value);
        }

        private static Type _primedSentinelType;
        private static FieldInfo _psControlField;
        private static FieldInfo _psEnableField;
        private static FieldInfo _psGameObjField;
        private static FieldInfo _psHealthManagerField;
        private static FieldInfo _psStunCounterField;
        private static FieldInfo _psPhase2Field;
        private static FieldInfo _psInitialHpField;
        private static FieldInfo _psPhase2ThresholdField;
        private static FieldInfo _psStunQuotaField;
        private static FieldInfo _psIsCrossStitchingField;
        private static FieldInfo _psRisingSlashingField;
        private static MethodInfo _psDeathResetMethod;
        private static FieldInfo _hmStunControlFsmField;

        private static bool EnsurePrimedSentinelResolved()
        {
            if (_primedSentinelType != null) return true;
            foreach (var asm in GetCachedLoadedAssemblies())
            {
                if (asm == null) continue;
                var t = asm.GetType("Silksong.Mods.PrimedSentinel.PrimedSentinel", false);
                if (t != null)
                {
                    _primedSentinelType = t;
                    _psControlField = t.GetField("control", AllFlags);
                    _psEnableField = t.GetField("enable", AllFlags);
                    _psGameObjField = t.GetField("gameObj", AllFlags);
                    _psHealthManagerField = t.GetField("healthManager", AllFlags);
                    _psStunCounterField = t.GetField("stunCounter", AllFlags);
                    _psPhase2Field = t.GetField("phase2", AllFlags);
                    _psInitialHpField = t.GetField("INITIAL_HP", AllFlags);
                    _psPhase2ThresholdField = t.GetField("PHASE_2_THRESHOLD", AllFlags);
                    _psStunQuotaField = t.GetField("STUN_QUOTA", AllFlags);
                    _psIsCrossStitchingField = t.GetField("isCrossStitching", AllFlags);
                    _psRisingSlashingField = t.GetField("risingSlashing", AllFlags);
                    _psDeathResetMethod = t.GetMethod("deathReset", AllFlags);
                    _hmStunControlFsmField = typeof(HealthManager).GetField("stunControlFsm", AllFlags);
                    return true;
                }
            }
            return false;
        }

        public static void AuricAegis_DisableStunControlOnBoss(GameObject bossGo, HealthManager hm)
        {
            try
            {
                if (hm != null && _hmStunControlFsmField != null)
                {
                    var cachedStunFsm = _hmStunControlFsmField.GetValue(hm) as PlayMakerFSM;
                    if (cachedStunFsm != null)
                    {
                        cachedStunFsm.enabled = false;
                        UnityEngine.Object.Destroy(cachedStunFsm);
                    }
                    _hmStunControlFsmField.SetValue(hm, null);
                }
                if (bossGo != null)
                {
                    var fsms = bossGo.GetComponents<PlayMakerFSM>();
                    if (fsms != null)
                    {
                        for (int i = 0; i < fsms.Length; i++)
                        {
                            var f = fsms[i];
                            if (f != null && string.Equals(f.FsmName, "Stun Control", StringComparison.OrdinalIgnoreCase))
                            {
                                f.enabled = false;
                                UnityEngine.Object.Destroy(f);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "AuricAegis_DisableStunControlOnBoss", ex);
            }
        }

        public static void AuricAegis_PreFsmEnabled(PlayMakerFSM fsm)
        {
            try
            {
                if (fsm == null || !EnsurePrimedSentinelResolved()) return;
                var curControl = _psControlField?.GetValue(null) as PlayMakerFSM;
                // If entering Hang_17b with a new Song Knight Control FSM while a stale reference exists, reset cleanly
                if (string.Equals(fsm.FsmName, "Control", StringComparison.Ordinal) &&
                    fsm.gameObject != null &&
                    string.Equals(fsm.gameObject.scene.name, "Hang_17b", StringComparison.OrdinalIgnoreCase))
                {
                    if (curControl == null || curControl != fsm)
                    {
                        _psDeathResetMethod?.Invoke(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "AuricAegis_PreFsmEnabled", ex);
            }
        }

        public static void AuricAegis_PostFsmStateEntered(HutongGames.PlayMaker.FsmState state)
        {
            try
            {
                if (state == null || !EnsurePrimedSentinelResolved()) return;
                var ctrl = _psControlField?.GetValue(null) as PlayMakerFSM;
                if (ctrl == null) return;
                if (state.Fsm != ctrl.Fsm) return;

                var hm = _psHealthManagerField?.GetValue(null) as HealthManager;
                var go = _psGameObjField?.GetValue(null) as GameObject;
                if (hm == null) return;

                string stName = state.Name ?? "";
                int initHp = (int)(_psInitialHpField?.GetValue(null) ?? 1800);
                int phase2Thresh = (int)(_psPhase2ThresholdField?.GetValue(null) ?? 1500);
                bool isPhase2 = (bool)(_psPhase2Field?.GetValue(null) ?? false);

                if (stName == "Battle Start")
                {
                    // Prevent Pantheon of Pharloom's 2908 HP scaling from trapping Phase 1 above PHASE_2_THRESHOLD (1500)
                    if (!isPhase2 && hm.hp > initHp)
                    {
                        hm.hp = initHp;
                    }
                    // Disable vanilla Stun Control during Phase 1 so needle hits don't cause vanilla staggers on top of parry staggers
                    if (_hmStunControlFsmField != null)
                    {
                        var stunFsm = _hmStunControlFsmField.GetValue(hm) as PlayMakerFSM;
                        if (stunFsm != null) stunFsm.enabled = false;
                    }
                }
                else if (stName == "Stun Recover" || stName == "Damage Recover")
                {
                    _psStunCounterField?.SetValue(null, 0);
                    if (isPhase2 || hm.hp <= phase2Thresh)
                    {
                        if (!isPhase2)
                        {
                            hm.hp = phase2Thresh;
                            _psPhase2Field?.SetValue(null, true);
                        }
                        hm.IsInvincible = false;
                        _psIsCrossStitchingField?.SetValue(null, false);
                        AuricAegis_DisableStunControlOnBoss(go, hm);
                    }
                }
                else if (isPhase2 && !hm.isDead && hm.hp > 0)
                {
                    // In Phase 2, ensure invincibility never gets stuck on unless currently in Parry Clash (isCrossStitching)
                    bool crossStitching = (bool)(_psIsCrossStitchingField?.GetValue(null) ?? false);
                    if (!crossStitching && hm.IsInvincible)
                    {
                        hm.IsInvincible = false;
                    }
                    AuricAegis_DisableStunControlOnBoss(go, hm);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "AuricAegis_PostFsmStateEntered", ex);
            }
        }

        public static bool AuricAegis_CanContinueCoroutine()
        {
            try
            {
                if (!EnsurePrimedSentinelResolved()) return false;
                bool en = (bool)(_psEnableField?.GetValue(null) ?? false);
                if (!en) return false;
                var ctrl = _psControlField?.GetValue(null) as PlayMakerFSM;
                if (ctrl == null || !ctrl.isActiveAndEnabled) return false;
                var hm = _psHealthManagerField?.GetValue(null) as HealthManager;
                if (hm == null || hm.isDead || hm.hp <= 0)
                {
                    if (hm != null) hm.IsInvincible = false;
                    try { ctrl.StopAllCoroutines(); } catch { }
                    _psDeathResetMethod?.Invoke(null, null);
                    return false;
                }
                string activeSt = ctrl.ActiveStateName ?? "";
                if (activeSt.StartsWith("Death", StringComparison.OrdinalIgnoreCase) ||
                    activeSt.StartsWith("Defeat", StringComparison.OrdinalIgnoreCase) ||
                    activeSt.Equals("Battle End", StringComparison.OrdinalIgnoreCase))
                {
                    hm.IsInvincible = false;
                    try { ctrl.StopAllCoroutines(); } catch { }
                    _psDeathResetMethod?.Invoke(null, null);
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void AuricAegis_PreDeathReset()
        {
            try
            {
                if (!EnsurePrimedSentinelResolved()) return;
                var ctrl = _psControlField?.GetValue(null) as PlayMakerFSM;
                var hm = _psHealthManagerField?.GetValue(null) as HealthManager;
                var go = _psGameObjField?.GetValue(null) as GameObject;
                if (hm != null)
                {
                    hm.IsInvincible = false;
                }
                if (go != null || hm != null)
                {
                    AuricAegis_DisableStunControlOnBoss(go, hm);
                }
                if (ctrl != null)
                {
                    try { ctrl.StopAllCoroutines(); } catch { }
                }
            }
            catch { }
        }

        public static bool RebalancedCrests_OverrideReaperPayout(HeroController hc, ref int result)
        {
            try
            {
                if (!_hasObservedHitInstance || hc == null || !hc.ReaperState.IsInReaperMode)
                    return true;

                Assembly rcAsm = null;
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    if (a.GetName().Name != null && a.GetName().Name.StartsWith("RebalancedCrests", StringComparison.OrdinalIgnoreCase))
                    {
                        rcAsm = a;
                        break;
                    }
                }
                if (rcAsm == null) return true;

                if (_lastObservedHitAttackType == AttackTypes.Heavy)
                {
                    var nsType = rcAsm.GetType("RebalancedCrests.Crests.Reaper.NeedleStrikeSilkBundlesTweak", false);
                    var nsInst = nsType?.GetField("NeedleStrike", AllFlags)?.GetValue(null);
                    if (nsInst != null)
                    {
                        bool isActive = (bool)(nsType.GetProperty("IsActive", AllFlags)?.GetValue(nsInst, null) ?? false);
                        if (isActive)
                        {
                            var countEntry = nsType.GetProperty("Count", AllFlags)?.GetValue(nsInst, null) as BepInEx.Configuration.ConfigEntry<int>;
                            if (countEntry != null)
                            {
                                result = countEntry.Value;
                                return false;
                            }
                        }
                    }
                }

                var fixedType = rcAsm.GetType("RebalancedCrests.Crests.Reaper.FixedSilkBundlesTweak", false);
                var fixedInst = fixedType?.GetField("Fixed", AllFlags)?.GetValue(null);
                if (fixedInst != null)
                {
                    bool isActive = (bool)(fixedType.GetProperty("IsActive", AllFlags)?.GetValue(fixedInst, null) ?? false);
                    if (isActive)
                    {
                        var countEntry = fixedType.GetProperty("Count", AllFlags)?.GetValue(fixedInst, null) as BepInEx.Configuration.ConfigEntry<int>;
                        if (countEntry != null)
                        {
                            result = countEntry.Value;
                            return false;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_133) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5384", __apiEx_133); }
            return true;
        }

        // =====================================================================
        // SSMP (Silksong Multiplayer 0.3.1) MonoMod.RuntimeDetour & Mobile UI Bridge
        // =====================================================================
        private static bool _ssmpHooksInstalled;
        private static Type _ssmpEventHooksType;
        private static Type _ssmpCustomHooksType;
        private static Type _ssmpFsmActionHooksType;
        private static MethodInfo _ssmpInvokeFsmHookActionsMi;
        private static object _ssmpPluginInstance;
        private static object _ssmpGameManagerInstance;
        private static object _ssmpUiManagerInstance;
        private static object _ssmpClientManagerInstance;
        private static object _ssmpServerManagerInstance;
        private static object _ssmpModSettingsInstance;
        private static bool _ssmpInitializedOnMainMenu;

        public static bool IsSSMPLoaded => _ssmpEventHooksType != null || _ssmpPluginInstance != null;
        public static MethodInfo AccessToolsMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var exact = HarmonyLib.AccessTools.Method(type, name, parameters, generics);
                if (exact != null) return exact;
            }
            catch (System.Exception __apiEx_134) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5413", __apiEx_134); }
            MethodInfo best = null;
            try
            {
                var methods = type.GetMethods(AllFlags);
                if (methods != null)
                {
                    int desiredCount = parameters != null ? parameters.Length : -1;
                    int bestDiff = int.MaxValue;
                    for (int i = 0; i < methods.Length; i++)
                    {
                        var m = methods[i];
                        if (m == null || !string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                        var ps = m.GetParameters();
                        int pLen = ps != null ? ps.Length : 0;
                        int diff = desiredCount >= 0 ? Math.Abs(pLen - desiredCount) : 0;
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            best = m;
                            if (diff == 0) break;
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_135) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5438", __apiEx_135); }
            if (best != null && generics != null && generics.Length > 0 && best.IsGenericMethodDefinition)
            {
                try { return best.MakeGenericMethod(generics); } catch (System.Exception __apiEx_136) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5441", __apiEx_136); }
            }
            return best;
        }

        public static MethodInfo AccessToolsDeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            return AccessToolsMethod(type, name, parameters, generics);
        }

        public static MethodInfo AccessToolsMethodByString(string typeColonMethodname, Type[] parameters = null, Type[] generics = null)
        {
            if (string.IsNullOrEmpty(typeColonMethodname)) return null;
            try
            {
                var exact = HarmonyLib.AccessTools.Method(typeColonMethodname, parameters, generics);
                if (exact != null) return exact;
            }
            catch (System.Exception __apiEx_137) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5459", __apiEx_137); }
            int idx = typeColonMethodname.LastIndexOf(':');
            if (idx > 0 && idx + 1 < typeColonMethodname.Length)
            {
                string tName = typeColonMethodname.Substring(0, idx);
                string mName = typeColonMethodname.Substring(idx + 1);
                var t = ResolveTypeRobust(tName);
                if (t != null) return AccessToolsMethod(t, mName, parameters, generics);
            }
            return null;
        }

        private static string _lastFishingActivatedScene;
        public static void EnsurePharloomFishingPinsAndKitActive()
        {
            try
            {
                var saveMgrType = ResolveTypeRobust("PharloomFishing.SaveManager");
                if (saveMgrType == null) return;
                var slotField = saveMgrType.GetField("CurrentSlotData", AllFlags);
                var slotProp = saveMgrType.GetProperty("CurrentSlotData", AllFlags);
                object slotData = slotField != null ? slotField.GetValue(null) : slotProp?.GetValue(null, null);
                if (slotData == null)
                {
                    var sdTypeCreate = ResolveTypeRobust("PharloomFishing.SaveData");
                    if (sdTypeCreate != null)
                    {
                        try
                        {
                            slotData = Activator.CreateInstance(sdTypeCreate);
                            slotField?.SetValue(null, slotData);
                            slotProp?.SetValue(null, slotData, null);
                        }
                        catch (System.Exception __apiEx_138) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5492", __apiEx_138); }
                    }
                }
                if (slotData != null)
                {
                    var sdType = slotData.GetType();
                    foreach (var flagName in new[] { "HasFishingKit", "HasSeenFishingKit", "HasFishingSpotPins" })
                    {
                        sdType.GetField(flagName, AllFlags)?.SetValue(slotData, true);
                        sdType.GetProperty(flagName, AllFlags)?.SetValue(slotData, true, null);
                    }
                }
                string curScene = SceneManager.GetActiveScene().name ?? "";
                if (curScene == _lastFishingActivatedScene && !string.IsNullOrEmpty(curScene)) return;
                _lastFishingActivatedScene = curScene;

                int foundPins = 0;
                var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
                if (allTransforms != null)
                {
                    for (int i = 0; i < allTransforms.Length; i++)
                    {
                        var tr = allTransforms[i];
                        if (tr == null || tr.gameObject == null) continue;
                        string n = tr.gameObject.name ?? "";
                        if (n == "Fishing Spot Pin" || n == "Fishing Spot Key Entry" || n == "FishingSpot")
                        {
                            if (n == "Fishing Spot Pin") foundPins++;
                            if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                        }
                    }
                }
                if (foundPins == 0)
                {
                    ReplayMissedSingletonHooksForMod("PharloomFishing");
                }
            }
            catch (System.Exception __apiEx_139) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5529", __apiEx_139); }
        }

        public static void FixSSMPConnectInterfaceLayout(object connectInterface)
        {
            if (connectInterface == null) return;
            try
            {
                var ciType = connectInterface.GetType();
                var uiMgrType = ciType.Assembly.GetType("SSMP.Ui.UiManager", false);
                var uiGo = uiMgrType?.GetField("UiGameObject", AllFlags)?.GetValue(null) as GameObject;
                if (uiGo != null)
                {
                    var canvas = uiGo.GetComponent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                        canvas.sortingOrder = 100;
                    }
                    var rootRt = uiGo.GetComponent<RectTransform>();
                    if (rootRt != null && (rootRt.sizeDelta.x < 100f || rootRt.sizeDelta.y < 100f))
                        rootRt.sizeDelta = new Vector2(1920f, 1080f);
                }

                var bgPanel = ciType.GetField("_backgroundPanel", AllFlags)?.GetValue(connectInterface) as GameObject;
                if (bgPanel == null) return;
                var bgRt = bgPanel.GetComponent<RectTransform>();
                if (bgRt != null)
                {
                    bgRt.anchorMin = new Vector2(0.5f, 805f / 1080f);
                    bgRt.anchorMax = new Vector2(0.5f, 805f / 1080f);
                    bgRt.pivot = new Vector2(0.5f, 1.0f);
                    bgRt.sizeDelta = new Vector2(450f, 600f);
                    bgRt.anchoredPosition = Vector2.zero;
                    bgRt.localScale = Vector3.one;
                }

                for (int i = 0; i < bgPanel.transform.childCount; i++)
                {
                    var ch = bgPanel.transform.GetChild(i) as RectTransform;
                    if (ch == null) continue;
                    if (Mathf.Abs(ch.anchorMin.y - 1.0f) > 0.001f || Mathf.Abs(ch.anchorMin.x - 0.5f) > 0.001f)
                    {
                        float origX = ch.anchorMin.x * 1920f;
                        float origY = ch.anchorMin.y * 1080f;
                        ch.anchorMin = new Vector2(0.5f, 1.0f);
                        ch.anchorMax = new Vector2(0.5f, 1.0f);
                        ch.localScale = Vector3.one;
                        ch.anchoredPosition = new Vector2(origX - 960f, origY - 805f);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[SSMPBridge] FixSSMPConnectInterfaceLayout warning: {ex.Message}");
            }
        }

        public static void ConfigureSSMPMultiplayerButton(object uiManager, GameObject buttonObj)
        {
            if (uiManager == null || buttonObj == null) return;
            try
            {
                var onReqMi = uiManager.GetType().GetMethod("OnMultiplayerMenuRequested", AllFlags);
                var menuBtn = buttonObj.GetComponent<UnityEngine.UI.MenuButton>();
                if (menuBtn != null && onReqMi != null)
                {
                    menuBtn.cancelAction = GlobalEnums.CancelAction.DoNothing;
                    menuBtn.customGoBack = null;
                    menuBtn.hasCancelEventTrigger = false;
                    menuBtn.eventTrigger = null;
                    if (menuBtn.OnSubmitPressed != null) menuBtn.OnSubmitPressed.RemoveAllListeners();
                    menuBtn.OnSubmitPressed = new UnityEngine.Events.UnityEvent();
                    menuBtn.OnSubmitPressed.AddListener(() =>
                    {
                        try { onReqMi.Invoke(uiManager, null); } catch (System.Exception __apiEx_140) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5604", __apiEx_140); }
                    });
                }
                foreach (var trg in buttonObj.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true))
                {
                    if (trg != null && trg.triggers != null) trg.triggers.Clear();
                }
            }
            catch (System.Exception __apiEx_141) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5612", __apiEx_141); }
        }

        public static string SSMPMobileServerIp = "127.0.0.1";
        public static string SSMPMobileServerPort = "26950";
        public static string SSMPMobileUsername = "MobileHornet";
        public static string SSMPMobileChatMessage = "/list";
        public static string SSMPMobileStatusText = "Ready (Use Direct Connect or Host Server)";

        private static readonly List<MethodBase> _deferredSsmpFsmHookTargets = new List<MethodBase>();
        private static bool _ssmpMultiplayerHooksActivated;

        public static void HeroController_Start_MobileGuard_Postfix(HeroController __instance)
        {
            try
            {
                if (CheatManager.Invincibility == (CheatManager.InvincibilityStates)3)
                    CheatManager.Invincibility = CheatManager.InvincibilityStates.Off;
            }
            catch { }
            EnsurePharloomFishingPinsAndKitActive();
            try { RepairSilkSpecialsFsmAndNeedolinState(__instance ?? HeroController.SilentInstance); } catch (System.Exception __apiEx_142) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5627", __apiEx_142); }
        }

        public static void ActivateSSMPMultiplayerHooksIfNeeded()
        {
            EnsureSSMPBridgeHooksInstalled();
            if (_ssmpMultiplayerHooksActivated) return;
            _ssmpMultiplayerHooksActivated = true;
            try
            {
                HookSSMPTarget(typeof(tk2dSpriteAnimator), "Play", null, nameof(SSMP_SpriteAnimator_Play_Postfix), new[] { typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float) });
                HookSSMPTarget(typeof(tk2dSpriteAnimator), "WarpClipToLocalTime", null, nameof(SSMP_SpriteAnimator_WarpClip_Postfix));
                HookSSMPTarget(typeof(tk2dSpriteAnimator), "ProcessEvents", null, nameof(SSMP_SpriteAnimator_ProcessEvents_Postfix));
                HookSSMPTarget(typeof(HeroController), "Update", null, nameof(SSMP_HeroController_Update_Postfix));
                var postFsm = typeof(HarmonyBridge).GetMethod(nameof(SSMP_FsmStateAction_OnEnter_Postfix), BindingFlags.Static | BindingFlags.Public);
                lock (_deferredSsmpFsmHookTargets)
                {
                    for (int i = 0; i < _deferredSsmpFsmHookTargets.Count; i++)
                    {
                        RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SSMP.FsmActionHooks", _deferredSsmpFsmHookTargets[i], null, postFsm, null);
                    }
                }
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[SSMPBridge] Activated multiplayer sync hooks ({_deferredSsmpFsmHookTargets.Count} FSM action hooks + animator/update hooks).");
            }
            catch (System.Exception __apiEx_143) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5651", __apiEx_143); }
        }

        public static void RegisterMonoModHook(MethodBase from, MethodInfo to, object target)
        {
            if (from == null || to == null) return;
            try
            {
                string toDecl = to.DeclaringType?.FullName ?? "";
                if (toDecl == "SSMP.Hooks.EventHooks" || toDecl == "SSMP.Hooks.CustomHooks")
                {
                    if (toDecl == "SSMP.Hooks.EventHooks") _ssmpEventHooksType = to.DeclaringType;
                    if (toDecl == "SSMP.Hooks.CustomHooks") _ssmpCustomHooksType = to.DeclaringType;
                    EnsureSSMPBridgeHooksInstalled();
                    return;
                }
                if (toDecl == "SSMP.Hooks.FsmActionHooks")
                {
                    _ssmpFsmActionHooksType = to.DeclaringType;
                    _ssmpInvokeFsmActionMiResolve();
                    lock (_deferredSsmpFsmHookTargets)
                    {
                        if (!_deferredSsmpFsmHookTargets.Contains(from))
                            _deferredSsmpFsmHookTargets.Add(from);
                    }
                    return;
                }

                string modName = to.DeclaringType?.Assembly?.GetName()?.Name ?? "MonoModHook";
                RuntimeHybridCLRAdapterBuilder.RegisterMonoModDetourChain(modName, from, to, target);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[MonoModBridge] RegisterMonoModHook notice: {ex.Message}");
            }
        }

        public static void RegisterMonoModILHook(MethodBase from, MonoMod.Cil.ILContext.Manipulator manipulator)
        {
            if (from == null || manipulator == null) return;
            try
            {
                string manipDecl = manipulator.Method?.DeclaringType?.FullName ?? "";
                if (manipDecl.StartsWith("SSMP.", StringComparison.Ordinal))
                {
                    if (_ssmpCustomHooksType == null)
                        _ssmpCustomHooksType = manipulator.Method.DeclaringType.Assembly.GetType("SSMP.Hooks.CustomHooks", false);
                    if (_ssmpEventHooksType == null)
                        _ssmpEventHooksType = manipulator.Method.DeclaringType.Assembly.GetType("SSMP.Hooks.EventHooks", false);
                    EnsureSSMPBridgeHooksInstalled();
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[MonoModBridge] RegisterMonoModILHook notice: {ex.Message}");
            }
        }

        private static void _ssmpInvokeFsmActionMiResolve()
        {
            if (_ssmpInvokeFsmHookActionsMi == null && _ssmpFsmActionHooksType != null)
            {
                _ssmpInvokeFsmHookActionsMi = _ssmpFsmActionHooksType.GetMethod("InvokeHookActions", AllFlags);
            }
        }

        public static void SSMP_FsmStateAction_OnEnter_Postfix(HutongGames.PlayMaker.FsmStateAction __instance)
        {
            if (__instance == null || _ssmpInvokeFsmHookActionsMi == null || (_ssmpClientManagerInstance == null && _ssmpServerManagerInstance == null)) return;
            try
            {
                _ssmpInvokeFsmHookActionsMi.Invoke(null, new object[] { __instance });
            }
            catch (System.Exception __apiEx_144) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5721", __apiEx_144); }
        }

        private static void EnsureSSMPBridgeHooksInstalled()
        {
            if (_ssmpHooksInstalled) return;
            _ssmpHooksInstalled = true;
            try
            {
                HookSSMPTarget(typeof(UIManager), "SetState", null, nameof(SSMP_UIManager_SetState_Postfix));
                HookSSMPTarget(typeof(UIManager), "UIGoToMainMenu", null, nameof(SSMP_UIManager_UIGoToMainMenu_Postfix));
                HookSSMPTarget(typeof(UIManager), "ReturnToMainMenu", nameof(SSMP_UIManager_ReturnToMainMenu_Prefix), null);
                HookSSMPTarget(typeof(GameManager), "StartNewGame", null, nameof(SSMP_GameManager_StartNewGame_Postfix), new[] { typeof(bool), typeof(bool) });
                HookSSMPTarget(typeof(GameManager), "ContinueGame", null, nameof(SSMP_GameManager_ContinueGame_Postfix));
                HookSSMPTarget(typeof(GameManager), "QuitGame", nameof(SSMP_GameManager_QuitGame_Prefix), null);
                HookSSMPTarget(typeof(HeroController), "Start", null, nameof(SSMP_HeroController_Start_Postfix));
                HookSSMPTarget(typeof(HeroController), "Die", nameof(SSMP_HeroController_Die_Prefix), null);
                HookSSMPTarget(typeof(HeroController), "UseLavaBell", null, nameof(SSMP_HeroController_UseLavaBell_Postfix));
                HookSSMPTarget(typeof(HeroController), "SendHeroInPosition", null, nameof(SSMP_HeroController_SendHeroInPosition_Postfix));
                HookSSMPTarget(typeof(GameMap), "PositionCompassAndCorpse", null, nameof(SSMP_GameMap_PositionCompass_Postfix));
                HookSSMPTarget(typeof(GameMap), "CloseQuickMap", null, nameof(SSMP_GameMap_CloseQuickMap_Postfix));
                HookSSMPTarget(typeof(ToolItemManager), "SetEquippedCrest", nameof(SSMP_ToolItemManager_SetEquippedCrest_Prefix), null, new[] { typeof(string) });
                HookSSMPTarget(typeof(CameraLockArea), "Awake", null, nameof(SSMP_CameraLockArea_Awake_Postfix));
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[SSMPBridge] Installed 18 managed/native hooks for SSMP EventHooks & CustomHooks.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[SSMPBridge] Hook install warning: {ex.Message}");
            }
        }

        private static void HookSSMPTarget(Type targetType, string methodName, string preName, string postName, Type[] paramTypes = null)
        {
            if (targetType == null) return;
            MethodInfo m = paramTypes != null
                ? targetType.GetMethod(methodName, AllFlags, null, paramTypes, null)
                : targetType.GetMethods(AllFlags).FirstOrDefault(mi => mi.Name == methodName && !mi.IsGenericMethodDefinition);
            if (m == null) return;
            var pre = preName != null ? typeof(HarmonyBridge).GetMethod(preName, BindingFlags.Static | BindingFlags.Public) : null;
            var post = postName != null ? typeof(HarmonyBridge).GetMethod(postName, BindingFlags.Static | BindingFlags.Public) : null;
            RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain("SSMP.Bridge", m, pre, post, null);
        }

        private static void InvokeSSMPEventField(Type holderType, string fieldName, params object[] args)
        {
            if (holderType == null) return;
            try
            {
                var f = holderType.GetField(fieldName, AllFlags);
                if (f?.GetValue(null) is Delegate del)
                {
                    del.DynamicInvoke(args);
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("SSMP", $"Event({fieldName})", ex);
            }
        }

        public static void SSMP_UIManager_SetState_Postfix(UIManager __instance, GlobalEnums.UIState __0)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "UIManagerSetState", __instance, __0);
        }

        public static void SSMP_UIManager_UIGoToMainMenu_Postfix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "UIManagerUIGoToMainMenu");
        }

        public static void SSMP_UIManager_ReturnToMainMenu_Prefix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "UIManagerReturnToMainMenu");
        }

        public static void SSMP_GameManager_StartNewGame_Postfix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "GameManagerStartNewGame");
        }

        public static void SSMP_GameManager_ContinueGame_Postfix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "GameManagerContinueGame");
        }

        public static void SSMP_GameManager_QuitGame_Prefix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "GameManagerQuitGame");
        }

        public static void SSMP_SpriteAnimator_Play_Postfix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip __0, float __1, float __2)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "SpriteAnimatorPlay", __instance, __0, __1, __2);
        }

        public static void SSMP_SpriteAnimator_WarpClip_Postfix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip __0, float __1)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "SpriteAnimatorWarpClipToLocalTime", __instance, __0, __1);
        }

        public static void SSMP_SpriteAnimator_ProcessEvents_Postfix(tk2dSpriteAnimator __instance, int __0, int __1, int __2)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "SpriteAnimatorProcessEvents", __instance, __0, __1, __2);
        }

        public static void SSMP_HeroController_Start_Postfix()
        {
            EnsurePharloomFishingPinsAndKitActive();
            try { EnsureHeroConfigCanPlayNeedolin(HeroController.SilentInstance); } catch (System.Exception __apiEx_145) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5829", __apiEx_145); }
            InvokeSSMPEventField(_ssmpCustomHooksType, "HeroControllerStartActionInternal");
            InvokeSSMPEventField(_ssmpCustomHooksType, "AfterEnterSceneHeroTransformed");
        }

        public static void SSMP_HeroController_Update_Postfix(HeroController __instance)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "HeroControllerUpdate", __instance);
        }

        public static void SSMP_HeroController_Die_Prefix(bool __0, bool __1)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "HeroControllerDie", __0, __1);
        }

        public static void SSMP_HeroController_UseLavaBell_Postfix()
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "UseLavaBell");
        }

        public static void SSMP_HeroController_SendHeroInPosition_Postfix()
        {
            InvokeSSMPEventField(_ssmpCustomHooksType, "AfterEnterSceneHeroTransformed");
        }

        public static void SSMP_GameMap_PositionCompass_Postfix(GameMap __instance)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "GameMapPositionCompassAndCorpse", __instance);
        }

        public static void SSMP_GameMap_CloseQuickMap_Postfix(GameMap __instance)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "GameMapCloseQuickMap", __instance);
        }

        public static bool SSMP_ToolItemManager_SetEquippedCrest_Prefix(string __0)
        {
            if (_ssmpEventHooksType == null) return true;
            try
            {
                var f = _ssmpEventHooksType.GetField("ToolItemManagerSetEquippedCrest", AllFlags);
                if (f?.GetValue(null) is Delegate del)
                {
                    Action<string> origAction = (crestId) => ToolItemManager.SetEquippedCrest(crestId);
                    del.DynamicInvoke(origAction, __0);
                    return false;
                }
            }
            catch (System.Exception __apiEx_146) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L5877", __apiEx_146); }
            return true;
        }

        public static void SSMP_CameraLockArea_Awake_Postfix(CameraLockArea __instance)
        {
            InvokeSSMPEventField(_ssmpEventHooksType, "CameraLockAreaAwake", __instance);
        }

        public static void RegisterSSMPAddonsFromAssembly(Assembly asm, bool hasBaseUnityPlugin)
        {
            return; // Disabled in IRIS v2.9.8.23 (SSMP & SSMP addons blocked)
            if (asm == null) return;
            try
            {
                // Only auto-register ClientAddon/ServerAddon subclasses if the assembly has no BaseUnityPlugin (e.g. SSMPEnemySync.dll)
                if (hasBaseUnityPlugin) return;

                Assembly ssmpAsm = _ssmpEventHooksType?.Assembly;
                if (ssmpAsm == null)
                {
                    foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                    {
                        if (a.GetName().Name != null && a.GetName().Name.Equals("SSMP", StringComparison.OrdinalIgnoreCase))
                        {
                            ssmpAsm = a;
                            break;
                        }
                    }
                }
                if (ssmpAsm == null) return;

                var clientAddonBase = ssmpAsm.GetType("SSMP.Api.Client.ClientAddon", false);
                var serverAddonBase = ssmpAsm.GetType("SSMP.Api.Server.ServerAddon", false);
                var regClientMi = clientAddonBase?.GetMethod("RegisterAddon", BindingFlags.Static | BindingFlags.Public);
                var regServerMi = serverAddonBase?.GetMethod("RegisterAddon", BindingFlags.Static | BindingFlags.Public);

                foreach (var t in asm.GetTypes())
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    if (clientAddonBase != null && clientAddonBase.IsAssignableFrom(t) && regClientMi != null)
                    {
                        var inst = Activator.CreateInstance(t);
                        regClientMi.Invoke(null, new object[] { inst });
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[SSMPBridge] Auto-registered ClientAddon '{t.FullName}' from {asm.GetName().Name}.");
                    }
                    else if (serverAddonBase != null && serverAddonBase.IsAssignableFrom(t) && regServerMi != null)
                    {
                        var inst = Activator.CreateInstance(t);
                        regServerMi.Invoke(null, new object[] { inst });
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[SSMPBridge] Auto-registered ServerAddon '{t.FullName}' from {asm.GetName().Name}.");
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[SSMPBridge] RegisterSSMPAddonsFromAssembly warning: {ex.Message}");
            }
        }

        public static void BootstrapSSMPOnMainMenuIfReady(List<BepInEx.BaseUnityPlugin> plugins)
        {
            return; // Disabled in IRIS v2.9.8.23 (SSMP blocked)
            if (_ssmpInitializedOnMainMenu) return;
            try
            {
                if (plugins != null)
                {
                    foreach (var p in plugins)
                    {
                        if (p != null && p.GetType().FullName == "SSMP.SSMPPlugin")
                        {
                            _ssmpPluginInstance = p;
                            var asm = p.GetType().Assembly;
                            if (_ssmpEventHooksType == null) _ssmpEventHooksType = asm.GetType("SSMP.Hooks.EventHooks", false);
                            if (_ssmpCustomHooksType == null) _ssmpCustomHooksType = asm.GetType("SSMP.Hooks.CustomHooks", false);
                            EnsureSSMPBridgeHooksInstalled();
                            break;
                        }
                    }
                }

                if (_ssmpEventHooksType == null) return;
                var ui = UIManager.instance;
                if (ui != null)
                {
                    _ssmpInitializedOnMainMenu = true;
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[SSMPBridge] Bootstrapping SSMPPlugin.Initialize() on Main Menu...");
                    InvokeSSMPEventField(_ssmpEventHooksType, "UIManagerUIGoToMainMenu");
                    InvokeSSMPEventField(_ssmpEventHooksType, "UIManagerSetState", ui, GlobalEnums.UIState.MAIN_MENU_HOME);
                    ResolveSSMPManagers();
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[SSMPBridge] BootstrapSSMPOnMainMenuIfReady warning: {ex.Message}");
            }
        }

        private static void ResolveSSMPManagers()
        {
            try
            {
                if (_ssmpPluginInstance == null) return;
                var gmField = _ssmpPluginInstance.GetType().GetField("_gameManager", AllFlags);
                _ssmpGameManagerInstance = gmField?.GetValue(_ssmpPluginInstance);
                if (_ssmpGameManagerInstance == null) return;

                var gmType = _ssmpGameManagerInstance.GetType();
                _ssmpUiManagerInstance = gmType.GetField("_uiManager", AllFlags)?.GetValue(_ssmpGameManagerInstance);
                _ssmpClientManagerInstance = gmType.GetField("_clientManager", AllFlags)?.GetValue(_ssmpGameManagerInstance);
                _ssmpServerManagerInstance = gmType.GetField("_serverManager", AllFlags)?.GetValue(_ssmpGameManagerInstance);

                if (_ssmpClientManagerInstance != null)
                {
                    _ssmpModSettingsInstance = _ssmpClientManagerInstance.GetType().GetField("_modSettings", AllFlags)?.GetValue(_ssmpClientManagerInstance);
                    if (_ssmpModSettingsInstance != null)
                    {
                        var msType = _ssmpModSettingsInstance.GetType();
                        string savedIp = msType.GetProperty("ConnectAddress", AllFlags)?.GetValue(_ssmpModSettingsInstance, null) as string;
                        int savedPort = (int)(msType.GetProperty("ConnectPort", AllFlags)?.GetValue(_ssmpModSettingsInstance, null) ?? 26950);
                        string savedUser = msType.GetProperty("Username", AllFlags)?.GetValue(_ssmpModSettingsInstance, null) as string;
                        if (!string.IsNullOrEmpty(savedIp)) SSMPMobileServerIp = savedIp;
                        if (savedPort > 0) SSMPMobileServerPort = savedPort.ToString();
                        if (!string.IsNullOrEmpty(savedUser)) SSMPMobileUsername = savedUser;
                    }
                }
            }
            catch (System.Exception __apiEx_147) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L6003", __apiEx_147); }
        }

        public static void SSMPMobileDirectConnect(string ip, string portStr, string username)
        {
            try
            {
                ActivateSSMPMultiplayerHooksIfNeeded();
                ResolveSSMPManagers();
                if (_ssmpClientManagerInstance == null)
                {
                    SSMPMobileStatusText = "SSMP not initialized yet (enter Main Menu first).";
                    return;
                }
                string cleanIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
                if (!int.TryParse(portStr?.Trim(), out int port) || port <= 0 || port > 65535)
                    port = 26950;
                string cleanUser = string.IsNullOrWhiteSpace(username) ? "MobileHornet" : username.Trim();

                if (_ssmpModSettingsInstance != null)
                {
                    var msType = _ssmpModSettingsInstance.GetType();
                    msType.GetProperty("ConnectAddress", AllFlags)?.SetValue(_ssmpModSettingsInstance, cleanIp, null);
                    msType.GetProperty("ConnectPort", AllFlags)?.SetValue(_ssmpModSettingsInstance, port, null);
                    msType.GetProperty("Username", AllFlags)?.SetValue(_ssmpModSettingsInstance, cleanUser, null);
                }

                // Invoke ConnectGroup's ConnectButtonPressed or ClientManager.Connect(address, port, username)
                var cmType = _ssmpClientManagerInstance.GetType();
                var connectMi = cmType.GetMethod("Connect", AllFlags, null, new[] { typeof(string), typeof(int), typeof(string) }, null);
                if (connectMi != null)
                {
                    connectMi.Invoke(_ssmpClientManagerInstance, new object[] { cleanIp, port, cleanUser });
                    SSMPMobileStatusText = $"Connecting to {cleanIp}:{port} as '{cleanUser}'...";
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[SSMPBridge] Triggered Direct Connect to {cleanIp}:{port} as '{cleanUser}'.");
                    return;
                }

                if (_ssmpUiManagerInstance != null)
                {
                    var cg = _ssmpUiManagerInstance.GetType().GetField("_connectGroup", AllFlags)?.GetValue(_ssmpUiManagerInstance);
                    if (cg != null)
                    {
                        var cgType = cg.GetType();
                        var addrInput = cgType.GetField("_addressInput", AllFlags)?.GetValue(cg);
                        var portInput = cgType.GetField("_portInput", AllFlags)?.GetValue(cg);
                        var userInput = cgType.GetField("_usernameInput", AllFlags)?.GetValue(cg);
                        addrInput?.GetType().GetMethod("SetInput", AllFlags)?.Invoke(addrInput, new object[] { cleanIp });
                        portInput?.GetType().GetMethod("SetInput", AllFlags)?.Invoke(portInput, new object[] { port.ToString() });
                        userInput?.GetType().GetMethod("SetInput", AllFlags)?.Invoke(userInput, new object[] { cleanUser });
                        cgType.GetMethod("OnConnectButtonPressed", AllFlags)?.Invoke(cg, null);
                        SSMPMobileStatusText = $"Triggered SSMP Connect to {cleanIp}:{port} as '{cleanUser}'!";
                    }
                }
            }
            catch (Exception ex)
            {
                SSMPMobileStatusText = $"Connect error: {ex.GetBaseException().Message}";
                SilksongMobileAPIPlugin.LogModException("SSMP", "SSMPMobileDirectConnect", ex);
            }
        }

        public static void SSMPMobileStartHost(string portStr, string username)
        {
            try
            {
                ResolveSSMPManagers();
                if (_ssmpServerManagerInstance == null)
                {
                    SSMPMobileStatusText = "SSMP not initialized yet (enter Main Menu first).";
                    return;
                }
                if (!int.TryParse(portStr?.Trim(), out int port) || port <= 0 || port > 65535)
                    port = 26950;
                string cleanUser = string.IsNullOrWhiteSpace(username) ? "MobileHornet" : username.Trim();

                var startMi = _ssmpServerManagerInstance.GetType().GetMethod("Start", AllFlags, null, new[] { typeof(int) }, null);
                startMi?.Invoke(_ssmpServerManagerInstance, new object[] { port });
                SSMPMobileDirectConnect("127.0.0.1", port.ToString(), cleanUser);
                SSMPMobileStatusText = $"Started Local/LAN Host on UDP port {port} and connected as '{cleanUser}'!";
            }
            catch (Exception ex)
            {
                SSMPMobileStatusText = $"Host error: {ex.GetBaseException().Message}";
                SilksongMobileAPIPlugin.LogModException("SSMP", "SSMPMobileStartHost", ex);
            }
        }

        public static void SSMPMobileDisconnect()
        {
            try
            {
                ResolveSSMPManagers();
                _ssmpClientManagerInstance?.GetType().GetMethod("Disconnect", AllFlags)?.Invoke(_ssmpClientManagerInstance, null);
                _ssmpServerManagerInstance?.GetType().GetMethod("Stop", AllFlags)?.Invoke(_ssmpServerManagerInstance, null);
                SSMPMobileStatusText = "Disconnected from SSMP server / stopped host.";
            }
            catch (Exception ex)
            {
                SSMPMobileStatusText = $"Disconnect error: {ex.GetBaseException().Message}";
            }
        }

        public static void SSMPMobileSendChat(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                ResolveSSMPManagers();
                if (_ssmpUiManagerInstance == null)
                {
                    SSMPMobileStatusText = "SSMP UI not active yet.";
                    return;
                }
                var chatEventField = _ssmpUiManagerInstance.GetType().GetField("ChatInputEvent", AllFlags);
                if (chatEventField?.GetValue(null) is Delegate del)
                {
                    del.DynamicInvoke(message.Trim());
                    SSMPMobileStatusText = $"Sent to SSMP Chat: {message.Trim()}";
                }
                else
                {
                    SSMPMobileStatusText = "Enter a save file while connected to use SSMP chat commands.";
                }
            }
            catch (Exception ex)
            {
                SSMPMobileStatusText = $"Chat error: {ex.GetBaseException().Message}";
            }
        }

        public static void PD_GetBool_Postfix(PlayerData __instance, string __0, ref bool __result)
        {
            __result = RunPrepatcherHandlers(_prepatcherGetHandlers, __instance, __0, __result);
        }

        public static void PD_SetBool_Prefix(PlayerData __instance, string __0, ref bool __1)
        {
            __1 = RunPrepatcherHandlers(_prepatcherSetHandlers, __instance, __0, __1);
        }

        public static void PD_GetInt_Postfix(PlayerData __instance, string __0, ref int __result)
        {
            __result = RunPrepatcherHandlers(_prepatcherGetHandlers, __instance, __0, __result);
        }

        public static void PD_SetInt_Prefix(PlayerData __instance, string __0, ref int __1)
        {
            __1 = RunPrepatcherHandlers(_prepatcherSetHandlers, __instance, __0, __1);
        }

        public static void PD_GetFloat_Postfix(PlayerData __instance, string __0, ref float __result)
        {
            __result = RunPrepatcherHandlers(_prepatcherGetHandlers, __instance, __0, __result);
        }

        public static void PD_SetFloat_Prefix(PlayerData __instance, string __0, ref float __1)
        {
            __1 = RunPrepatcherHandlers(_prepatcherSetHandlers, __instance, __0, __1);
        }

        public static void PD_GetString_Postfix(PlayerData __instance, string __0, ref string __result)
        {
            __result = RunPrepatcherHandlers(_prepatcherGetHandlers, __instance, __0, __result);
        }

        public static void PD_SetString_Prefix(PlayerData __instance, string __0, ref string __1)
        {
            __1 = RunPrepatcherHandlers(_prepatcherSetHandlers, __instance, __0, __1);
        }

        private static int _lastStitchworkTabActivateFrame = -1;

        public static void InvokeStitchworkTabActivateSafe(object tab)
        {
            if (tab == null) return;
            int fc = -1;
            try { fc = Time.frameCount; } catch (System.Exception __apiEx_148) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L6180", __apiEx_148); }
            if (fc >= 0 && _lastStitchworkTabActivateFrame == fc) return;
            _lastStitchworkTabActivateFrame = fc;
            try
            {
                var tabType = tab.GetType();
                int idVal = 0;
                var idField = tabType.GetField("Id", AllFlags);
                if (idField != null)
                {
                    idVal = Convert.ToInt32(idField.GetValue(tab));
                }
                var asm = tabType.Assembly;
                if (idVal == 0)
                {
                    var cpmType = asm.GetType("Stitchwork.GUI.CanvasPackManager", false);
                    cpmType?.GetMethod("Toggle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
                }
                else if (idVal == 7)
                {
                    var swType = asm.GetType("Stitchwork.GUI.SettingsWindow", false);
                    swType?.GetMethod("Toggle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
                }
                else
                {
                    var dhType = asm.GetType("Stitchwork.GUI.DevHub", false);
                    var toggleAtMi = dhType?.GetMethod("ToggleAt", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (toggleAtMi != null)
                    {
                        var pType = toggleAtMi.GetParameters()[0].ParameterType;
                        object argVal = pType.IsEnum ? Enum.ToObject(pType, idVal - 1) : (object)(idVal - 1);
                        toggleAtMi.Invoke(null, new object[] { argVal });
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogModException("Stitchwork", "InvokeStitchworkTabActivateSafe", ex);
            }
        }

        public static object AttachStitchworkTabButton(object tab)
        {
            if (tab == null) return null;
            try
            {
                var tabType = tab.GetType();
                var boxGo = tabType.GetField("Box", AllFlags)?.GetValue(tab) as GameObject;
                if (boxGo != null)
                {
                    var img = boxGo.GetComponent<UnityEngine.UI.Image>() ?? boxGo.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0f, 0f, 0f, 0.005f);
                    img.raycastTarget = true;
                    var btn = boxGo.GetComponent<UnityEngine.UI.Button>() ?? boxGo.AddComponent<UnityEngine.UI.Button>();
                    btn.targetGraphic = img;
                    var nav = btn.navigation;
                    nav.mode = UnityEngine.UI.Navigation.Mode.None;
                    btn.navigation = nav;
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => InvokeStitchworkTabActivateSafe(tab));
                }
                var iconImg = tabType.GetField("Icon", AllFlags)?.GetValue(tab) as UnityEngine.UI.Image;
                if (iconImg != null && iconImg.sprite != null)
                {
                    iconImg.enabled = true;
                    if (iconImg.color.a < 0.1f)
                        iconImg.color = new Color(0.95f, 0.92f, 0.88f, 0.9f);
                }
            }
            catch (System.Exception __apiEx_149) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L6249", __apiEx_149); }
            return tab;
        }

        public static void AttachStitchworkKitButton(object compObj)
        {
            if (!(compObj is Component comp) || comp == null) return;
            try
            {
                var go = comp.gameObject;
                if (go == null) return;
                var img = go.GetComponent<UnityEngine.UI.Image>();
                if (img == null)
                {
                    img = go.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0f, 0f, 0f, 0.005f);
                }
                img.raycastTarget = true;
                var btn = go.GetComponent<UnityEngine.UI.Button>() ?? go.AddComponent<UnityEngine.UI.Button>();
                btn.targetGraphic = img;
                var nav = btn.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                btn.navigation = nav;
                var cType = comp.GetType();
                var clickField = cType.GetField("Click", AllFlags);
                var confirmField = cType.GetField("_confirm", AllFlags);
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() =>
                {
                    try
                    {
                        if (confirmField != null && confirmField.GetValue(comp) is Action confAct)
                        {
                            confAct();
                            return;
                        }
                        if (clickField != null && clickField.GetValue(comp) is Action clickAct)
                        {
                            clickAct();
                        }
                    }
                    catch (Exception ex)
                    {
                        SilksongMobileAPIPlugin.LogModException("Stitchwork", "KitButton.Click", ex);
                    }
                });
            }
            catch (System.Exception __apiEx_150) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("HarmonyBridge", "L6296", __apiEx_150); }
        }
    }
}
