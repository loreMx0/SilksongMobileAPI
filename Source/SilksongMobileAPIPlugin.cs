using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilksongMobileAPI
{
    public sealed class LoadedModConfigEntryInfo
    {
        public string ModName;
        public string Section;
        public string Key;
        public string Description;
        public ConfigEntryBase Entry;
        public Type SettingType;
    }

    public sealed class DiscoveredPcModDll
    {
        public string DllPath;
        public string ModDir;
        public string DirName;
        public string AsmShortName;
        public int PriorityOrder;
    }

    [BepInPlugin("com.silksong.mobile.pcmodapi", "IRIS", "2.9.6")]
    public class SilksongMobileAPIPlugin : BaseUnityPlugin
    {
        public static SilksongMobileAPIPlugin Instance;
        public static ManualLogSource StaticLogger { get; private set; }
        internal static ManualLogSource Log => Instance != null ? Instance.Logger : StaticLogger;
        public static string DiagnosticLogPath { get; private set; }
        public static string VersionedDiagnosticLogPath { get; private set; }

        // Config entries for granular feature toggles and full API logging
        public static ConfigEntry<bool> CfgEnableVerboseApiLogging { get; private set; }
        public static ConfigEntry<bool> CfgEnableCombatSpamFilter { get; private set; }
        public static ConfigEntry<bool> CfgEnableCircularBubbleButton { get; private set; }
        public static ConfigEntry<bool> CfgEnablePatchworkBubbleBridge { get; private set; }
        public static ConfigEntry<bool> CfgEnableJsonAotFix { get; private set; }
        public static ConfigEntry<bool> CfgEnableCrashReporter { get; private set; }

        private static readonly HashSet<int> _registeredCircularWindowIds = new HashSet<int>();
        private static readonly HashSet<int> _registeredGodhomeCircularWindowIds = new HashSet<int>();
        private static readonly HashSet<int> _registeredPatchworkBubbleWindowIds = new HashSet<int>();
        private static bool _patchworkRuntimeHooksApplied;
        private bool _inSetShowModPanel;

        private static readonly object _logLock = new object();
        private static readonly List<string> _inMemoryLogLines = new List<string>(256);
        private static readonly Dictionary<string, int> _errorCountBySite = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loadedModDllNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _loadedModNames = new List<string>();
        private static readonly List<BaseUnityPlugin> _instantiatedPcPlugins = new List<BaseUnityPlugin>();
        private static readonly List<LoadedModConfigEntryInfo> _discoveredConfigs = new List<LoadedModConfigEntryInfo>();
        private static readonly Dictionary<string, string> _modGuidByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _hasScannedAndLoaded;
        private static string _copyStatusMessage = "";

        // Touch UI state for PC Mod Keybinds & Config Menu (Resolution-Independent 1920x1080 Virtual Coordinates)
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;
        private bool _showModPanel;
        private bool _extraLargeFloatingButton;
        private Vector2 _scrollPos;
        private Rect _panelRect = new Rect(180f, 90f, 1520f, 880f);
        private string _selectedModTab = "Hotkeys";

        // Zero-allocation cached Silksong Dark-Crimson & Gold IMGUI Theme
        private static bool _themeInitialized;
        private static Texture2D _texWinBg;
        private static Texture2D _texCardBg;
        private static Texture2D _texBtnNormal;
        private static Texture2D _texBtnHover;
        private static Texture2D _texBtnActive;
        private static Texture2D _texFloatBtn;
        private static Texture2D _texFloatBtnOpen;
        private static GUIStyle _styleWindow;
        private static GUIStyle _styleCard;
        private static GUIStyle _styleFloatBtn;
        private static GUIStyle _styleFloatBtnOpen;
        private static GUIStyle _styleTabBtn;
        private static GUIStyle _styleTabBtnActive;
        private static GUIStyle _styleActionBtn;
        private static GUIStyle _styleDangerBtn;
        private static GUIStyle _styleHeaderLabel;
        private static GUIStyle _styleBodyLabel;

        public static Matrix4x4 ApplyResolutionIndependentMatrix(out float vWidth, out float vHeight)
        {
            Matrix4x4 old = GUI.matrix;
            vWidth = ReferenceWidth;
            vHeight = ReferenceHeight;
            float sw = Mathf.Max(1f, (float)Screen.width);
            float sh = Mathf.Max(1f, (float)Screen.height);
            float sx = sw / ReferenceWidth;
            float sy = sh / ReferenceHeight;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sx, sy, 1f));
            return old;
        }

        private static Texture2D MakeBorderedTex(int w, int h, Color fill, Color border, int borderPx = 2)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool isBorder = (x < borderPx || x >= w - borderPx || y < borderPx || y >= h - borderPx);
                    pixels[y * w + x] = isBorder ? border : fill;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        private static void EnsureGuiThemeInitialized()
        {
            if (_themeInitialized && _texWinBg != null && _styleWindow != null) return;
            _themeInitialized = true;

            _texWinBg = MakeBorderedTex(16, 16, new Color(0.08f, 0.06f, 0.09f, 0.96f), new Color(0.76f, 0.60f, 0.34f, 0.95f), 2);
            _texCardBg = MakeBorderedTex(12, 12, new Color(0.14f, 0.10f, 0.14f, 0.92f), new Color(0.45f, 0.32f, 0.24f, 0.80f), 1);
            _texBtnNormal = MakeBorderedTex(12, 12, new Color(0.24f, 0.11f, 0.15f, 0.95f), new Color(0.68f, 0.50f, 0.30f, 0.90f), 1);
            _texBtnHover = MakeBorderedTex(12, 12, new Color(0.38f, 0.15f, 0.20f, 0.98f), new Color(0.92f, 0.76f, 0.45f, 1.00f), 1);
            _texBtnActive = MakeBorderedTex(12, 12, new Color(0.52f, 0.16f, 0.22f, 0.98f), new Color(1.00f, 0.86f, 0.55f, 1.00f), 2);
            _texFloatBtn = MakeBorderedTex(16, 16, new Color(0.30f, 0.09f, 0.13f, 0.94f), new Color(0.88f, 0.70f, 0.38f, 0.98f), 2);
            _texFloatBtnOpen = MakeBorderedTex(16, 16, new Color(0.16f, 0.32f, 0.20f, 0.96f), new Color(0.55f, 0.92f, 0.65f, 0.98f), 2);

            _styleWindow = new GUIStyle(GUI.skin.window)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                border = new RectOffset(4, 4, 4, 4),
                padding = new RectOffset(14, 14, 34, 14)
            };
            _styleWindow.normal.background = _texWinBg;
            _styleWindow.onNormal.background = _texWinBg;
            _styleWindow.normal.textColor = new Color(0.96f, 0.86f, 0.62f, 1f);
            _styleWindow.onNormal.textColor = new Color(0.96f, 0.86f, 0.62f, 1f);

            _styleCard = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(4, 4, 4, 4)
            };
            _styleCard.normal.background = _texCardBg;
            _styleCard.normal.textColor = new Color(0.94f, 0.92f, 0.90f, 1f);

            _styleFloatBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(4, 4, 4, 4),
                padding = new RectOffset(8, 8, 6, 6)
            };
            _styleFloatBtn.normal.background = _texFloatBtn;
            _styleFloatBtn.hover.background = _texBtnHover;
            _styleFloatBtn.active.background = _texBtnActive;
            _styleFloatBtn.normal.textColor = new Color(0.98f, 0.92f, 0.80f, 1f);
            _styleFloatBtn.hover.textColor = Color.white;
            _styleFloatBtn.active.textColor = Color.white;

            _styleFloatBtnOpen = new GUIStyle(_styleFloatBtn);
            _styleFloatBtnOpen.normal.background = _texFloatBtnOpen;
            _styleFloatBtnOpen.normal.textColor = new Color(0.65f, 1.0f, 0.75f, 1f);

            _styleActionBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(10, 10, 6, 6),
                richText = true
            };
            _styleActionBtn.normal.background = _texBtnNormal;
            _styleActionBtn.hover.background = _texBtnHover;
            _styleActionBtn.active.background = _texBtnActive;
            _styleActionBtn.normal.textColor = new Color(0.95f, 0.92f, 0.88f, 1f);
            _styleActionBtn.hover.textColor = Color.white;
            _styleActionBtn.active.textColor = new Color(1f, 0.92f, 0.65f, 1f);

            _styleTabBtn = new GUIStyle(_styleActionBtn)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(14, 10, 8, 8)
            };

            _styleTabBtnActive = new GUIStyle(_styleTabBtn);
            _styleTabBtnActive.normal.background = _texBtnActive;
            _styleTabBtnActive.normal.textColor = new Color(1f, 0.90f, 0.55f, 1f);

            _styleDangerBtn = new GUIStyle(_styleActionBtn);
            _styleDangerBtn.normal.background = _texBtnActive;
            _styleDangerBtn.normal.textColor = new Color(1f, 0.75f, 0.75f, 1f);

            _styleHeaderLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                richText = true
            };
            _styleHeaderLabel.normal.textColor = new Color(0.96f, 0.86f, 0.64f, 1f);

            _styleBodyLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                wordWrap = true
            };
            _styleBodyLabel.normal.textColor = new Color(0.92f, 0.90f, 0.88f, 1f);
        }

        private bool _autoCloseMenuOnKeyTap = true;

        private static readonly KeyCode[] _quickFunctionKeys = new[]
        {
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4,
            KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8,
            KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
            KeyCode.Escape, KeyCode.Return, KeyCode.Space, KeyCode.Backspace,
            KeyCode.BackQuote, KeyCode.Tab, KeyCode.Insert, KeyCode.Delete,
            KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown
        };

        private static readonly KeyCode[] _modifierKeys = new[]
        {
            KeyCode.LeftShift, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.RightShift
        };

        private static readonly KeyCode[] _navigationKeys = new[]
        {
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.Minus, KeyCode.Equals, KeyCode.LeftBracket, KeyCode.RightBracket
        };

        private static readonly KeyCode[] _numberKeys0To9 = new[]
        {
            KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9
        };

        private static readonly KeyCode[] _letterKeysAToZ = new[]
        {
            KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G,
            KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N,
            KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U,
            KeyCode.V, KeyCode.W, KeyCode.X, KeyCode.Y, KeyCode.Z
        };

        private static readonly KeyCode[] _alphaNumericKeys = new[]
        {
            KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
            KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F,
            KeyCode.G, KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L,
            KeyCode.M, KeyCode.N, KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R,
            KeyCode.S, KeyCode.T, KeyCode.U, KeyCode.V, KeyCode.W, KeyCode.X,
            KeyCode.Y, KeyCode.Z
        };

        private bool _uGuiKeyHoldMode = false;

        private void TriggerVirtualKeyFromUi(KeyCode key)
        {
            if (key == KeyCode.None) return;
            if (_uGuiKeyHoldMode)
            {
                bool curHeld = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(key);
                BepInEx.MobileVirtualInput.SetKeyHeld(key, !curHeld);
                if (_uGuiModConsoleStatusText != null)
                    _uGuiModConsoleStatusText.text = !curHeld ? $"Holding {key} (Tap again to release)" : $"Released {key}";
                RebuildUGuiModConsoleContent();
                return;
            }
            if (_autoCloseMenuOnKeyTap)
            {
                BepInEx.MobileVirtualInput.PulseKeyWithDelayedRepeat(key, 0.05f);
                SetShowModPanel(false);
            }
            else
            {
                BepInEx.MobileVirtualInput.PulseKey(key, 0.16f);
            }
        }

        private void DrawSSMPMultiplayerControlsBox()
        {
            GUILayout.Space(6f);
            GUILayout.BeginVertical("box");
            GUILayout.Label("<b>=== SSMP Multiplayer Mobile Controls (Direct IP / LAN / Hotspot / Playit.gg) ===</b>");
            if (!HarmonyBridge.IsSSMPLoaded)
            {
                HarmonyBridge.BootstrapSSMPOnMainMenuIfReady(_instantiatedPcPlugins);
            }
            string ssmpState = HarmonyBridge.IsSSMPLoaded
                ? HarmonyBridge.SSMPMobileStatusText
                : "SSMP not yet active (Install 'SSMP' from Online Mod Store or return to Main Menu to initialize)";
            GUILayout.Label($"<color=#88ddff>Status: {ssmpState}</color>");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Username:", GUILayout.Width(85f));
            HarmonyBridge.SSMPMobileUsername = GUILayout.TextField(HarmonyBridge.SSMPMobileUsername ?? "MobileHornet", 24, GUILayout.Width(170f), GUILayout.Height(34f));
            GUILayout.Label("Server IP:", GUILayout.Width(75f));
            HarmonyBridge.SSMPMobileServerIp = GUILayout.TextField(HarmonyBridge.SSMPMobileServerIp ?? "127.0.0.1", 64, GUILayout.Width(190f), GUILayout.Height(34f));
            GUILayout.Label("Port:", GUILayout.Width(45f));
            HarmonyBridge.SSMPMobileServerPort = GUILayout.TextField(HarmonyBridge.SSMPMobileServerPort ?? "26950", 8, GUILayout.Width(85f), GUILayout.Height(34f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Direct Connect (IP:Port)", _styleActionBtn, GUILayout.Height(40f)))
            {
                HarmonyBridge.SSMPMobileDirectConnect(HarmonyBridge.SSMPMobileServerIp, HarmonyBridge.SSMPMobileServerPort, HarmonyBridge.SSMPMobileUsername);
            }
            if (GUILayout.Button("Host Server (Local/LAN)", _styleActionBtn, GUILayout.Height(40f)))
            {
                HarmonyBridge.SSMPMobileStartHost(HarmonyBridge.SSMPMobileServerPort, HarmonyBridge.SSMPMobileUsername);
            }
            if (GUILayout.Button("Disconnect", _styleDangerBtn, GUILayout.Width(130f), GUILayout.Height(40f)))
            {
                HarmonyBridge.SSMPMobileDisconnect();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Chat/Cmd:", GUILayout.Width(85f));
            HarmonyBridge.SSMPMobileChatMessage = GUILayout.TextField(HarmonyBridge.SSMPMobileChatMessage ?? "/list", 120, GUILayout.Width(280f), GUILayout.Height(34f));
            if (GUILayout.Button("Send", _styleActionBtn, GUILayout.Width(90f), GUILayout.Height(34f)))
            {
                HarmonyBridge.SSMPMobileSendChat(HarmonyBridge.SSMPMobileChatMessage);
            }
            if (GUILayout.Button("/list", GUILayout.Width(65f), GUILayout.Height(34f)))
            {
                HarmonyBridge.SSMPMobileSendChat("/list");
            }
            if (GUILayout.Button("/huddle", GUILayout.Width(80f), GUILayout.Height(34f)))
            {
                HarmonyBridge.SSMPMobileSendChat("/huddle");
            }
            if (GUILayout.Button("/tpaccept", GUILayout.Width(95f), GUILayout.Height(34f)))
            {
                HarmonyBridge.SSMPMobileSendChat("/tpaccept");
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private static readonly HashSet<string> _knownNativeMobileFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "00_IRIS",
            "00_SilksongMobileAPI",
            
            "AssetHelper",
            "HornetLaceKiss",
            "ModDeleter",
            
            "PantheonOfPharloom",
            
            
            "SilkenImpact",
            "Silksong.FsmUtil",
            "Skip Intro",
            "ThirdAcolyte",
            "TouchButtonSkins",
            "WhiteKnightLace",
            "FasterPreloaderUI",
            "FastPreloaderUI",
            "FasterUI"
        };

        private static void DisableRawDobbyPatchesInLoadedAssembly(Assembly asm)
        {
            if (asm == null) return;
            try
            {
                const BindingFlags SFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                string asmName = asm.GetName().Name ?? "";
                if (string.Equals(asmName, "AssetHelper", StringComparison.OrdinalIgnoreCase))
                {
                    var repType = asm.GetType("Silksong.AssetHelper.Android.AndroidSceneAssetRepacker", false);
                    var hgField = repType?.GetField("_harmonyGuardsInstalled", SFlags);
                    if (hgField != null)
                    {
                        hgField.SetValue(null, true);
                        LogDiagnosticInfo("[EarlyPatchGuard] Intercepted AssetHelper.AndroidSceneAssetRepacker._harmonyGuardsInstalled -> routed to SilksongMobileAPI multiplexed lifecycle guards.");
                    }
                }
                else if (string.Equals(asmName, "Silksong.FsmUtil", StringComparison.OrdinalIgnoreCase))
                {
                    var fuType = asm.GetType("Silksong.FsmUtil.FsmUtil", false);
                    var piField = fuType?.GetField("_patchInstalled", SFlags);
                    if (piField != null)
                    {
                        piField.SetValue(null, true);
                        LogDiagnosticInfo("[EarlyPatchGuard] Intercepted Silksong.FsmUtil._patchInstalled -> routed to PlayMakerHybridCLRBridge multiplexed LoadActions hook.");
                    }
                }
            }
            catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L391", __apiEx_1); }
        }

        private static void TryEarlyLoadPcFsmUtilIfPresent()
        {
            try
            {
                string pluginsRoot = Paths.PluginPath;
                if (!string.IsNullOrEmpty(pluginsRoot) && Directory.Exists(pluginsRoot))
                {
                    foreach (var dllPath in Directory.GetFiles(pluginsRoot, "Silksong.FsmUtil.dll", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string parentDir = Path.GetDirectoryName(dllPath) ?? "";
                            if (!string.Equals(Path.GetFileName(parentDir), "plugins", StringComparison.OrdinalIgnoreCase))
                            {
                                string subPlugins = Path.Combine(parentDir, "plugins");
                                Directory.CreateDirectory(subPlugins);
                                string moved = Path.Combine(subPlugins, "Silksong.FsmUtil.dll");
                                if (File.Exists(moved)) File.Delete(moved);
                                File.Move(dllPath, moved);
                            }
                        }
                        catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L415", __apiEx_2); }
                    }
                }

                string selfDllPath = null;
                try { selfDllPath = Instance?.Info?.Location; } catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L420", __apiEx_3); }
                if (string.IsNullOrEmpty(selfDllPath) && !string.IsNullOrEmpty(pluginsRoot))
                {
                    selfDllPath = Path.Combine(pluginsRoot, "00_IRIS", "IRIS.dll");
                    if (!File.Exists(selfDllPath)) selfDllPath = Path.Combine(pluginsRoot, "00_IRIS", "SilksongMobileAPI.dll");
                    if (!File.Exists(selfDllPath)) selfDllPath = Path.Combine(pluginsRoot, "00_SilksongMobileAPI", "SilksongMobileAPI.dll");
                }
                if (!string.IsNullOrEmpty(selfDllPath) && File.Exists(selfDllPath))
                {
                    CecilRuntimeRewriter.EnsureUnifiedFsmUtilLoaded(selfDllPath);
                }
                _loadedModDllNames.Add("Silksong.FsmUtil");
                _loadedModDllNames.Add("Silksong.ModMenu");
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[UnifiedFsmUtil] Setup notice: {ex.Message}");
            }
        }

        private void Awake()
        {
            Instance = this; // Must be the very first line!
            StaticLogger = Logger;
            // Ensure GameObject survives scene transitions immediately in Awake() so BepInEx.PluginLoader._loadPlugin
            // can never leave this object without DontDestroyOnLoad if any subsystem throws.
            try { DontDestroyOnLoad(gameObject); } catch { }
            Log?.LogInfo("[SilksongMobileAPI] Plugin is awake and logging is ready! (v2.9.0)");
            try
            {
                Application.targetFrameRate = 60;
                QualitySettings.vSyncCount = 0;
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.TargetFrameRate", ex);
            }
            HarmonyBridge.RegisterPluginInfo(this, new BepInPlugin("com.silksong.mobile.pcmodapi", "IRIS", "2.9.6"), Paths.PluginPath);

            DiagnosticLogPath = Path.Combine(Paths.BepInExRootPath, "ModLog.log");
            VersionedDiagnosticLogPath = null;
            InitDiagnosticLog();

            LogDiagnosticInfo("Initializing IRIS v2.9.6...");
            InstallCrashAndLogcatCapture();
            try { Modding.HybridCLRRuntimeTuner.TuneInterpreterStacks(); } catch (Exception ex) { LogApiCatch("SilksongMobileAPIPlugin", "Awake.TuneInterpreterStacks", ex); }
            try { Modding.ReflectionHelper.PreloadCommonTypes(); } catch (Exception ex) { LogApiCatch("SilksongMobileAPIPlugin", "Awake.PreloadCommonTypes", ex); }
            LogDiagnosticInfo($"Structured Multi-Section Diagnostic Log: {DiagnosticLogPath}");

            try
            {
                if (!string.IsNullOrEmpty(Paths.ConfigPath) && !Directory.Exists(Paths.ConfigPath))
                    Directory.CreateDirectory(Paths.ConfigPath);
            }
            catch { }
            HarmonyBridge.EnsureTomlConvertersRegistered();
            PreOrganizeTopLevelPcModDlls();

            CecilRuntimeRewriter.EnsureInitialized();
            PlayMakerHybridCLRBridge.Install();
            TryEarlyLoadPcFsmUtilIfPresent();

            // Unconditionally register active scene changed listener for all mods
            try
            {
                SceneManager.activeSceneChanged -= AssetHelperBridge.OnActiveSceneChanged;
                SceneManager.activeSceneChanged += AssetHelperBridge.OnActiveSceneChanged;
            }
            catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L481", __apiEx_4); }

            // Ensure Bonemaster AlwaysHis & mobile performance settings early
            try
            {
                AssetHelperBridge.EnsureBonemasterAlwaysHisEnabled();
            }
            catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L488", __apiEx_5); }

            // Ensure Audio protection hook early
            try
            {
                AssetHelperBridge.EnsureAudioProtectionHookInstalled();
                AssetHelperBridge.InstallUniversalAssetHelperHooks();
            }
            catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L496", __apiEx_6); }

            // Launch Mod Preloader Screen overlay with Silksong Crimson & Gold theme + light animations
            try
            {
                var preloaderGo = new GameObject("SilksongMobileAPI_ModPreloader");
                DontDestroyOnLoad(preloaderGo);
                preloaderGo.AddComponent<ModPreloaderScreen>();
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[ModPreloader] Failed to spawn preloader: {ex.Message}");
            }

            // Initialize In-Game Mod Manager
            try
            {
                InGameModManager.Initialize(gameObject);
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[ModManager] Failed to init InGameModManager: {ex.Message}");
            }

            // Register SilksongMobileAPI internal Harmony patches (UIManager hooks, Crash Guards)
            try
            {
                LogDiagnosticInfo("Installing SilksongMobileAPI internal Harmony patches (UIManager hooks, Crash Guards)...");
                HarmonyBridge.CreateAndPatchAllAssembly(typeof(SilksongMobileAPIPlugin).Assembly, "com.silksong.mobile.pcmodapi");
            }
            catch (Exception ex)
            {
                LogDiagnosticError($"Failed to register internal Harmony patches: {ex}");
            }

            // Bind API Configuration toggles
            try
            {
                CfgEnableVerboseApiLogging = Config.Bind("Diagnostics", "EnableVerboseApiLogging", true,
                    "Enable comprehensive logging across all SilksongMobileAPI subsystems in LogOutput.log and SilksongMobileAPI_PCMods.log.");
                CfgEnableCombatSpamFilter = Config.Bind("Performance", "SuppressPerFrameCombatLogSpam", true,
                    "Suppress repetitive per-frame combat log spam during boss fights to prevent disk I/O stutters.");
                CfgEnableCircularBubbleButton = Config.Bind("UI", "EnableCircularBubbleButton", true,
                    "Register 'PC Mods & Keys' circular button inside the native CircularFloatingWindow mod bubble.");
                CfgEnablePatchworkBubbleBridge = Config.Bind("UI", "EnablePatchworkBubbleBridge", true,
                    "Ensure Patchwork ('Patchwork Skins') is registered in the CircularFloatingWindow mod bubble and supports in-game skin switching in Player mode.");
                CfgEnableJsonAotFix = Config.Bind("Compatibility", "EnableNewtonsoftJsonAotFix", true,
                    "Apply Hollow Knight Mobile API AotContractResolver & Vector2/Vector3 JSON converters for IL2CPP/HybridCLR safety.");
                CfgEnableCrashReporter = Config.Bind("Diagnostics", "EnableAndroidCrashReporter", true,
                    "Capture threaded Unity errors and AppDomain unhandled exceptions to Crash.log and SilksongMobileAPI_PCMods.log.");

                HarmonyBridge.SuppressVerboseCombatLogs = CfgEnableCombatSpamFilter.Value;
                LogDiagnosticInfo($"[Config] Bound API configs: VerboseLogging={CfgEnableVerboseApiLogging.Value}, CombatSpamFilter={CfgEnableCombatSpamFilter.Value}, CircularBubble={CfgEnableCircularBubbleButton.Value}, PatchworkBubbleBridge={CfgEnablePatchworkBubbleBridge.Value}, JsonAotFix={CfgEnableJsonAotFix.Value}, CrashReporter={CfgEnableCrashReporter.Value}");
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.BindConfigs", ex);
            }

            // Initialize Hollow Knight Mobile API CrashReporter & Newtonsoft.Json AOT ContractResolver
            try
            {
                if (CfgEnableCrashReporter == null || CfgEnableCrashReporter.Value)
                    Modding.AndroidCrashReporter.Initialize();
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.AndroidCrashReporter", ex);
            }
            try
            {
                if (CfgEnableJsonAotFix == null || CfgEnableJsonAotFix.Value)
                    Modding.Patches.JsonRuntimeFix.ApplyAotDefaults();
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.JsonRuntimeFix", ex);
            }

            // Register with native BepInEx.PluginLoader editor toggle & schedule CircularFloatingWindow button setup
            try
            {
                BepInEx.PluginLoader.RegisterEditor("SilksongMobileAPI", OnToggleSettings);
                LogDiagnosticInfo("[PluginLoader] Registered editor callback for 'SilksongMobileAPI'.");
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.RegisterEditor", ex);
            }

            try
            {
                Invoke(nameof(SetupButtons), 1f);
                LogDiagnosticInfo("[CircularBubble] Scheduled SetupButtons(1.0s) for CircularFloatingWindow registration.");
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.InvokeSetupButtons", ex);
            }

            // Register sceneLoaded watchdog to inject NativeModMenu if UIManager was already started
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoadedNativeMenuWatchdog;
                SceneManager.sceneLoaded += OnSceneLoadedNativeMenuWatchdog;
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "Awake.SceneLoadedWatchdog", ex);
            }

            LogDiagnosticInfo("IRIS v2.9.6 Awake setup complete. Waiting for native plugins (AssetHelper, FsmUtil) to finish loading before instantiating PC mods...");
        }

        private static void OnSceneLoadedNativeMenuWatchdog(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (string.Equals(scene.name, "Arborium_11", StringComparison.OrdinalIgnoreCase) && PlayerData.instance != null)
                {
                    PlayerData.instance.encounteredSongChevalierBoss = true;
                }
                bool repackerBusy = AssetHelperBridge.IsExtractingPharloomsGloryScene || AssetHelperBridge.IsAnyRepackerBusy();
                LogDiagnosticInfo($"[SceneBreadcrumb] SceneLoaded: '{scene.name}' (mode={mode}, activeScene='{SceneManager.GetActiveScene().name}', repackerExtracting={repackerBusy})");
                if (repackerBusy && !AssetHelperBridge.IsExtractingPharloomsGloryScene && mode == LoadSceneMode.Additive)
                {
                    try
                    {
                        var roots = scene.GetRootGameObjects();
                        if (roots != null)
                        {
                            for (int i = 0; i < roots.Length; i++)
                            {
                                if (roots[i] != null && roots[i].activeSelf)
                                    roots[i].SetActive(false);
                            }
                        }
                    }
                    catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L627", __apiEx_7); }
                }
                else if (!repackerBusy)
                {
                    HarmonyBridge.SanitizeTransitionPointsForScene(scene.name);
                }
                if (string.Equals(scene.name, "Memory_Ant_Queen", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scene.name, "Ant_Queen", StringComparison.OrdinalIgnoreCase))
                {
                    AssetHelperBridge.ResetBonemasterRuntimeCaches();
                }

                if (string.Equals(scene.name, "Menu_Title", StringComparison.OrdinalIgnoreCase))
                {
                    NativeModMenu.HasInjectedMainMenu = false;
                    if (Instance != null)
                    {
                        try { Instance.StartCoroutine(AssetHelperBridge.RunPreloaderPharloomsGloryBatchExtract()); } catch { }
                    }
                }

                if (UIManager.instance != null && UIManager.instance.mainMenuButtons != null)
                {
                    NativeModMenu.Inject(UIManager.instance);
                }

                if (Instance != null)
                {
                    Instance.SetupButtons();
                    Instance.SyncPatchworkSceneMode(scene.name);
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "OnSceneLoadedNativeMenuWatchdog", ex);
            }
        }

        private void Start()
        {
            LogDiagnosticInfo("[Lifecycle] SilksongMobileAPIPlugin.Start() invoked.");
            EnsureFloatingModButtonCreated();
            SetupButtons();
            TriggerDeferredScanAndLoad("MonoBehaviour.Start");
            try
            {
                InGameModManager.Instance?.ScanInstalledMods();
            }
            catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L671", __apiEx_8); }
            try
            {
                AssetHelperBridge.InstallUniversalAssetHelperHooks();
                AssetHelperBridge.EnsurePharloomsGloryAudioReloaded();
                AssetHelperBridge.SyncPharloomsGloryAudioAliases();
            }
            catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L678", __apiEx_9); }
        }

        private float _nextI18nSyncTime;
        private bool _autoSyncedGameplayModSkills;
        private string _unlockStatusMessage = "";
        private HeroController _cachedHero;
        private Renderer _cachedHeroRenderer;
        private ModConsoleIMGUIRenderer _imguiRenderer;
        private GameObject _uGuiFloatBtnCanvasGo;
        private UnityEngine.UI.Text _uGuiFloatBtnText;
        private UnityEngine.UI.Image _uGuiFloatBtnBg;
        private RectTransform _uGuiFloatBtnRect;
        private GameObject _uGuiModConsolePanelGo;
        private Transform _uGuiModConsoleContentRoot;
        private UnityEngine.UI.Text _uGuiModConsoleStatusText;
        private int _uGuiPageIndex;

        private static GameObject CreateUGuiButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchoredPos,
            Vector2 size,
            Color bgColor,
            Color textColor,
            int fontSize,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.color = bgColor;

            var btn = go.GetComponent<UnityEngine.UI.Button>();
            btn.targetGraphic = img;
            if (onClick != null)
                btn.onClick.AddListener(onClick);

            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            txtGo.transform.SetParent(go.transform, false);
            var trt = txtGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(4f, 2f);
            trt.offsetMax = new Vector2(-4f, -2f);

            var txt = txtGo.GetComponent<UnityEngine.UI.Text>();
            txt.font = HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.text = label;
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = textColor;
            txt.raycastTarget = false;
            return go;
        }

        private static UnityEngine.UI.Text CreateUGuiLabel(
            Transform parent,
            string text,
            Vector2 anchoredPos,
            Vector2 size,
            int fontSize,
            Color color,
            TextAnchor align = TextAnchor.MiddleLeft)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            var txt = go.GetComponent<UnityEngine.UI.Text>();
            txt.font = HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.text = text;
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = align;
            txt.color = color;
            txt.raycastTarget = false;
            return txt;
        }

        internal void EnsureFloatingModButtonCreated()
        {
            // On-screen rectangular "PC Mods" / "Keys" button has been removed per user request.
            // We only create the invisible host Canvas for the modal console panel; the panel itself
            // is opened via the native CircularFloatingWindow ("Mod Bubble"), PluginLoader editor toggle, or Main Menu Mods screen.
            if (_uGuiFloatBtnCanvasGo != null) return;
            try
            {
                _uGuiFloatBtnCanvasGo = new GameObject("SilksongMobileAPI_ModalHostCanvas");
                DontDestroyOnLoad(_uGuiFloatBtnCanvasGo);

                var canvas = _uGuiFloatBtnCanvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1002;
                canvas.pixelPerfect = false;

                var scaler = _uGuiFloatBtnCanvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                _uGuiFloatBtnCanvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                LogDiagnosticInfo("[UI] Created modal host canvas (on-screen PC Mods floating button removed; using CircularFloatingWindow mod bubble).");
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "EnsureFloatingModButtonCreated", ex);
            }
        }

        private void OnToggleSettings(bool visible)
        {
            LogDiagnosticInfo($"[PluginLoader] OnToggleSettings called for 'SilksongMobileAPI': visible={visible}");
            SetShowModPanel(visible, fromPluginLoader: true);
        }

        private static GameObject _hkNativeRoot;
        private static HkNativeJournalController _hkNativeController;

        private static readonly (string Name, string Scene)[] HkBossCatalog = new[]
        {
            ("Pure Vessel [APEX]", "GG_Hollow_Knight"),
            ("Nightmare King Grimm [DREAM]", "GG_Grimm_Nightmare"),
            ("Troupe Master Grimm", "GG_Grimm"),
            ("Absolute Radiance [APEX]", "GG_Radiance"),
            ("Sisters of Battle [ALT]", "GG_Mantis_Lords_V"),
            ("Mantis Lords", "GG_Mantis_Lords"),
            ("Hornet Sentinel [ALT]", "GG_Hornet_2"),
            ("Hornet Protector", "GG_Hornet_1"),
            ("Great Nailsage Sly", "GG_Sly"),
            ("Paintmaster Sheo", "GG_Painter"),
            ("Oro & Mato", "GG_Nailmasters"),
            ("Lost Kin [DREAM]", "GG_Lost_Kin"),
            ("Broken Vessel", "GG_Broken_Vessel"),
            ("Soul Tyrant [DREAM]", "GG_Soul_Tyrant"),
            ("Soul Master", "GG_Soul_Master"),
            ("Failed Champion [DREAM]", "GG_Failed_Champion"),
            ("False Knight", "GG_False_Knight"),
            ("White Defender [DREAM]", "GG_White_Defender"),
            ("Dung Defender", "GG_Dung_Defender"),
            ("Traitor Lord", "GG_Traitor_Lord"),
            ("Watcher Knights", "GG_Watcher_Knights"),
            ("Grey Prince Zote", "GG_Grey_Prince_Zote"),
            ("Hive Knight", "GG_Hive_Knight"),
            ("God Tamer", "GG_God_Tamer"),
            ("Collector", "GG_Collector"),
            ("Brooding Mawlek", "GG_Brooding_Mawlek"),
            ("Gruz Mother", "GG_Gruz_Mother"),
            ("Vengefly King", "GG_Vengefly"),
            ("Crystal Guardian", "GG_Crystal_Guardian"),
            ("Enraged Guardian [ALT]", "GG_Crystal_Guardian_2"),
            ("Soul Warrior", "GG_Mage_Knight"),
            ("Flukemarm", "GG_Flukemarm"),
            ("Uumuu", "GG_Uumuu"),
            ("Nosk", "GG_Nosk"),
            ("Winged Nosk [ALT]", "GG_Nosk_Hornet"),
            ("Oblobbles", "GG_Oblobbles"),
            ("Massive Moss Charger", "GG_Mega_Moss_Charger"),
            ("Elder Hu", "GG_Ghost_Hu"),
            ("Galien", "GG_Ghost_Galien"),
            ("Gorb", "GG_Ghost_Gorb"),
            ("Markoth", "GG_Ghost_Markoth"),
            ("Marmu", "GG_Ghost_Marmu"),
            ("No Eyes", "GG_Ghost_No_Eyes"),
            ("Xero", "GG_Ghost_Xero"),
        };

        private static readonly (string Code, string Title, string Subtitle, string[] Scenes)[] HkPantheonCatalog = new[]
        {
            ("NO.I", "Pantheon of the Master", "10 Bosses  -  Apex: Oro & Mato", new[] { "GG_Vengefly", "GG_Gruz_Mother", "GG_False_Knight", "GG_Mega_Moss_Charger", "GG_Hornet_1", "GG_Ghost_Gorb", "GG_Dung_Defender", "GG_Mage_Knight", "GG_Brooding_Mawlek", "GG_Nailmasters" }),
            ("NO.II", "Pantheon of the Artist", "10 Bosses  -  Apex: Paintmaster Sheo", new[] { "GG_Ghost_Xero", "GG_Crystal_Guardian", "GG_Soul_Master", "GG_Oblobbles", "GG_Mantis_Lords", "GG_Ghost_Marmu", "GG_Nosk", "GG_Flukemarm", "GG_Broken_Vessel", "GG_Painter" }),
            ("NO.III", "Pantheon of the Sage", "10 Bosses  -  Apex: Great Nailsage Sly", new[] { "GG_Hive_Knight", "GG_Ghost_Hu", "GG_Collector", "GG_God_Tamer", "GG_Grimm", "GG_Ghost_Galien", "GG_Grey_Prince_Zote", "GG_Uumuu", "GG_Hornet_2", "GG_Sly" }),
            ("NO.IV", "Pantheon of the Knight", "10 Bosses  -  Apex: Pure Vessel", new[] { "GG_Crystal_Guardian_2", "GG_Lost_Kin", "GG_Ghost_No_Eyes", "GG_Traitor_Lord", "GG_White_Defender", "GG_Failed_Champion", "GG_Ghost_Markoth", "GG_Watcher_Knights", "GG_Soul_Tyrant", "GG_Hollow_Knight" }),
            ("FINAL", "Pantheon of Hallownest", "10 Apex Bosses  -  Finale: Absolute Radiance", new[] { "GG_Mantis_Lords_V", "GG_Nailmasters", "GG_Painter", "GG_Sly", "GG_Nosk_Hornet", "GG_White_Defender", "GG_Soul_Tyrant", "GG_Grimm_Nightmare", "GG_Hollow_Knight", "GG_Radiance" }),
        };

        private const string HkBossPortraitAtlasGzBase64 = "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";
        private static Sprite[] _hkBossPortraits;

        private static Sprite[] _nativeBindingSprites;
        private static Sprite[] GetNativeBindingSprites()
        {
            if (_nativeBindingSprites != null) return _nativeBindingSprites;
            _nativeBindingSprites = new Sprite[5];
            try
            {
                var invPane = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory/Inv");
                if (invPane != null)
                {
                    Sprite FindSprite(string rel)
                    {
                        var tr = invPane.transform.Find(rel);
                        return tr != null ? tr.GetComponentInChildren<SpriteRenderer>(true)?.sprite : null;
                    }
                    _nativeBindingSprites[0] = FindSprite("Inv_Items/Needle");
                    _nativeBindingSprites[1] = FindSprite("Inv_Items/Needle Shift/Heart Pieces");
                    _nativeBindingSprites[3] = FindSprite("Inv_Items/Needle Shift/Spool Pieces");
                }
                var pouch = CollectableItemManager.GetItemByName("Tool Pouch&Kit Inv");
                if (pouch != null) _nativeBindingSprites[2] = pouch.GetIcon(0);
                _nativeBindingSprites[4] = GetOrCreateGodhomeIconSprite();
            }
            catch { }
            return _nativeBindingSprites;
        }

        private static Sprite[] GetOrBuildHkBossPortraits()
        {
            if (_hkBossPortraits != null) return _hkBossPortraits;
            _hkBossPortraits = new Sprite[HkBossCatalog.Length];
            try
            {
                byte[] gz = Convert.FromBase64String(HkBossPortraitAtlasGzBase64);
                byte[] png;
                using (var ms = new MemoryStream(gz))
                using (var gzs = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    gzs.CopyTo(outMs);
                    png = outMs.ToArray();
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(tex, png, false))
                {
                    tex.filterMode = FilterMode.Bilinear;
                    const int cols = 8, rows = 6, cell = 64;
                    for (int i = 0; i < HkBossCatalog.Length; i++)
                    {
                        int c = i % cols;
                        int r = i / cols;
                        // Unity Texture2D Y=0 is bottom; PIL top row r=0 maps to (rows - 1 - r) * cell
                        float x = c * cell;
                        float y = (rows - 1 - r) * cell;
                        _hkBossPortraits[i] = Sprite.Create(tex, new Rect(x, y, cell, cell), new Vector2(0.5f, 0.5f), 58f);
                    }
                    LogDiagnosticInfo($"[HkNativeJournal] Decoded 44-boss portrait sprite atlas ({tex.width}x{tex.height}).");
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("HkNativeJournal", "GetOrBuildHkBossPortraits", ex);
            }
            return _hkBossPortraits;
        }

        // 100% Native Silksong HudCamera Inventory Journal with 4x5 Boss Face Portrait Grid (modeled on PantheonOfPharloom)
        private sealed class HkNativeJournalController : MonoBehaviour
        {
            public GameObject Border;
            public GameObject ScreenPlane;
            public GameObject ContentRoot;
            public InventoryCursor Cursor;
            public TMProOld.TextMeshPro PaneTitleTmp;
            public TMProOld.TextMeshPro DetailTitleTmp;
            public TMProOld.TextMeshPro DetailBodyTmp;
            public TMProOld.TextMeshPro PromptBarTmp;
            public SpriteRenderer DetailPortraitSr;
            public GameObject PortraitGridRoot;
            public GameObject[] PortraitSlots;
            public SpriteRenderer[] PortraitSrs;
            public GameObject ListRoot;
            public GameObject BindingsInvRoot;
            public Transform[] BindInvTargets; // 0=Needle, 1=Heart, 2=Tools(SpoolGroup), 3=Spool, 4=VisitGodhomeBtn
            public TMProOld.TextMeshPro[] BindInvBadges;
            public GameObject[] RowSlots;
            public TMProOld.TextMeshPro[] RowTopTmps;
            public TMProOld.TextMeshPro[] RowNameTmps;
            public SpriteRenderer[] RowIconSrs;
            public AudioSource UiAudio;
            public AudioClip MoveClip;

            public bool IsOpen { get; private set; }
            private int _page = 0; // 0=Hall of Gods (4x5 Portrait Grid), 1=Pantheons, 2=Bindings
            private int _bossIdx = 0;
            private int _bossRowOffset = 0; // top visible row in 4-col grid
            private int _panIdx = 0;
            private int _bindIdx = 0;
            private int _diff = 0; // 0=Attuned, 1=Ascended, 2=Radiant
            private float _navCooldown = 0f;

            private const int GridCols = 4;
            private const int GridVisibleRows = 5;
            private static readonly int[] PantheonApexBossIdx = { 10, 9, 8, 0, 3 };

            private static readonly string[] DiffNames = { "ATTUNED", "ASCENDED", "RADIANT" };
            private static readonly Color[] DiffColors = {
                new Color(0.92f, 0.89f, 0.80f, 1f),
                new Color(0.98f, 0.82f, 0.44f, 1f),
                new Color(1.00f, 0.62f, 0.42f, 1f)
            };

            private void PlaySound()
            {
                try { if (UiAudio != null && MoveClip != null) UiAudio.PlayOneShot(MoveClip, 0.65f); }
                catch { }
            }

            private static void InvokeBorderFade(GameObject borderGo, float targetAlpha, float duration, bool recursive = false)
            {
                if (borderGo == null) return;
                try
                {
                    var fgType = HarmonyLib.AccessTools.TypeByName("TeamCherry.NestedFadeGroup.NestedFadeGroupBase") ?? HarmonyLib.AccessTools.TypeByName("NestedFadeGroupBase");
                    if (fgType == null) return;
                    var fadeMi = fgType.GetMethod("FadeTo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float), typeof(float), typeof(AnimationCurve), typeof(bool), typeof(Action<bool>) }, null);
                    var alphaProp = fgType.GetProperty("AlphaSelf", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Component[] comps = recursive ? borderGo.GetComponentsInChildren(fgType, true) : new[] { borderGo.GetComponent(fgType) };
                    if (comps == null) return;
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var comp = comps[i];
                        if (comp == null) continue;
                        if (fadeMi != null && duration > 0f)
                            fadeMi.Invoke(comp, new object[] { targetAlpha, duration, AnimationCurve.Linear(0f, 0f, 1f, 1f), true, null });
                        if (alphaProp != null && alphaProp.CanWrite)
                            alphaProp.SetValue(comp, targetAlpha);
                    }
                }
                catch { }
            }

            public void EnableUI()
            {
                if (IsOpen) return;
                IsOpen = true;
                try
                {
                    var hudCanvas = GameCameras.instance?.hudCamera?.transform.Find("In-game/Anchor TL/Hud Canvas Offset/Hud Canvas")?.gameObject;
                    if (hudCanvas != null) FSMUtility.LocateMyFSM(hudCanvas, "Slide Out")?.SendEvent("OUT");
                    EventRegister.SendEvent("INVENTORY OPENED", null);
                    if (ScreenPlane != null)
                    {
                        ScreenPlane.SetActive(true);
                        ScreenPlane.GetComponent<DisplayFrozenCamera>()?.Freeze();
                    }
                    if (Border != null)
                    {
                        Border.SetActive(true);
                        InvokeBorderFade(Border, 1f, 0.18f);
                    }
                    GameManager.instance?.SetIsInventoryOpen(true);
                    if (PlayerData.instance != null) PlayerData.instance.disablePause = true;
                    if (ContentRoot != null) ContentRoot.SetActive(true);
                    if (Cursor != null) { Cursor.gameObject.SetActive(true); Cursor.ResetClampedPos(); }
                    RefreshPage();
                    PlaySound();
                    LogDiagnosticInfo($"[HkNativeJournal] Opened native HudCamera Godhome Journal (page={_page}).");
                }
                catch (Exception ex)
                {
                    LogApiCatch("HkNativeJournal", "EnableUI", ex);
                }
            }

            public void DisableUI()
            {
                if (!IsOpen) return;
                IsOpen = false;
                try
                {
                    var hudCanvas = GameCameras.instance?.hudCamera?.transform.Find("In-game/Anchor TL/Hud Canvas Offset/Hud Canvas")?.gameObject;
                    if (hudCanvas != null) FSMUtility.LocateMyFSM(hudCanvas, "Slide Out")?.SendEvent("IN");
                    if (PlayerData.instance != null) PlayerData.instance.disablePause = false;
                    EventRegister.SendEvent("INVENTORY CLOSED", null);
                    if (ScreenPlane != null)
                    {
                        ScreenPlane.GetComponent<DisplayFrozenCamera>()?.Unfreeze();
                        ScreenPlane.SetActive(false);
                    }
                    if (Border != null)
                    {
                        InvokeBorderFade(Border, 0f, 0.15f);
                        Border.SetActive(false);
                    }
                    if (ContentRoot != null) ContentRoot.SetActive(false);
                    if (Cursor != null) Cursor.gameObject.SetActive(false);
                    GameManager.instance?.SetIsInventoryOpen(false);
                    LogDiagnosticInfo("[HkNativeJournal] Closed native HudCamera Godhome Journal.");
                }
                catch (Exception ex)
                {
                    LogApiCatch("HkNativeJournal", "DisableUI", ex);
                }
            }

            private void RefreshPage()
            {
                var portraits = GetOrBuildHkBossPortraits();
                if (PaneTitleTmp != null)
                    PaneTitleTmp.text = _page == 0 ? "HALL OF GODS" : (_page == 1 ? "PANTHEONS" : "BINDINGS");

                if (PortraitGridRoot != null) PortraitGridRoot.SetActive(_page == 0);
                if (ListRoot != null) ListRoot.SetActive(_page == 1);
                if (BindingsInvRoot != null)
                {
                    BindingsInvRoot.SetActive(_page == 2);
                    if (_page == 2) InvokeBorderFade(BindingsInvRoot, 1f, 0f, true);
                }

                Transform cursorTarget = null;

                if (_page == 0)
                {
                    int selRow = _bossIdx / GridCols;
                    int totalRows = (HkBossCatalog.Length + GridCols - 1) / GridCols;
                    if (selRow < _bossRowOffset) _bossRowOffset = selRow;
                    if (selRow >= _bossRowOffset + GridVisibleRows) _bossRowOffset = selRow - GridVisibleRows + 1;
                    _bossRowOffset = Mathf.Clamp(_bossRowOffset, 0, Mathf.Max(0, totalRows - GridVisibleRows));

                    int startIdx = _bossRowOffset * GridCols;
                    for (int s = 0; s < PortraitSlots.Length; s++)
                    {
                        int idx = startIdx + s;
                        bool valid = idx < HkBossCatalog.Length;
                        PortraitSlots[s].SetActive(valid);
                        if (!valid) continue;
                        if (PortraitSrs[s] != null)
                        {
                            PortraitSrs[s].sprite = portraits[idx] ?? GetOrCreateGodhomeIconSprite();
                            PortraitSrs[s].color = (idx == _bossIdx) ? Color.white : new Color(0.72f, 0.70f, 0.68f, 0.88f);
                        }
                        if (idx == _bossIdx) cursorTarget = PortraitSlots[s].transform;
                    }

                    var entry = HkBossCatalog[_bossIdx];
                    if (DetailPortraitSr != null)
                    {
                        DetailPortraitSr.gameObject.SetActive(true);
                        DetailPortraitSr.sprite = portraits[_bossIdx] ?? GetOrCreateGodhomeIconSprite();
                    }
                    if (DetailTitleTmp != null) DetailTitleTmp.text = entry.Name.ToUpperInvariant();
                    if (DetailBodyTmp != null)
                    {
                        DetailBodyTmp.text =
                            $"Difficulty: <color=#F2D88B>< {DiffNames[_diff]} ></color>  (Attack to switch)\n" +
                            $"{GetBindingsSummaryLine()}\n\n" +
                            $"Arena: {entry.Scene}\n" +
                            $"Boss {_bossIdx + 1} / {HkBossCatalog.Length}   |   Row {selRow + 1} / {totalRows}";
                    }
                }
                else if (_page == 1)
                {
                    int slotCount = RowSlots != null ? RowSlots.Length : 0;
                    for (int s = 0; s < slotCount; s++)
                    {
                        bool valid = s < HkPantheonCatalog.Length;
                        RowSlots[s].SetActive(valid);
                        if (!valid) continue;
                        var pan = HkPantheonCatalog[s];
                        if (RowTopTmps[s] != null)
                        {
                            RowTopTmps[s].text = $"{pan.Code}  -  {DiffNames[_diff]}";
                            RowTopTmps[s].color = (s == _panIdx) ? DiffColors[_diff] : new Color(0.68f, 0.64f, 0.56f, 0.9f);
                        }
                        if (RowNameTmps[s] != null)
                        {
                            RowNameTmps[s].text = pan.Title;
                            RowNameTmps[s].color = (s == _panIdx) ? Color.white : new Color(0.78f, 0.76f, 0.72f, 0.9f);
                        }
                        if (RowIconSrs[s] != null)
                        {
                            int apexIdx = PantheonApexBossIdx[s];
                            RowIconSrs[s].sprite = portraits[apexIdx] ?? GetOrCreateGodhomeIconSprite();
                        }
                    }
                    cursorTarget = RowSlots[Mathf.Clamp(_panIdx, 0, HkPantheonCatalog.Length - 1)].transform;

                    var sel = HkPantheonCatalog[_panIdx];
                    if (DetailPortraitSr != null)
                    {
                        DetailPortraitSr.gameObject.SetActive(true);
                        DetailPortraitSr.sprite = portraits[PantheonApexBossIdx[_panIdx]] ?? GetOrCreateGodhomeIconSprite();
                    }
                    if (DetailTitleTmp != null) DetailTitleTmp.text = sel.Title.ToUpperInvariant();
                    if (DetailBodyTmp != null)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"Difficulty: <color=#F2D88B>< {DiffNames[_diff]} ></color>  |  {GetBindingsSummaryLine()}");
                        sb.AppendLine("1 arena in RAM at a time (midpoint Heal + Silk).");
                        sb.AppendLine();
                        for (int k = 0; k < sel.Scenes.Length; k++)
                        {
                            string bn = sel.Scenes[k];
                            for (int c = 0; c < HkBossCatalog.Length; c++)
                                if (HkBossCatalog[c].Scene == bn) { bn = HkBossCatalog[c].Name; break; }
                            sb.Append(k + 1).Append(". ").Append(bn);
                            if (k < sel.Scenes.Length - 1) sb.Append(k % 2 == 0 ? "  |  " : "\n");
                        }
                        DetailBodyTmp.text = sb.ToString();
                    }
                }
                else
                {
                    string[] bTitles = { "Needle Binding", "Shell Binding", "Tools Binding", "Silk Binding", "Visit Godhome Hall" };
                    string[] bDescs = {
                        "Reduces Needle damage to base level during Hollow Knight fights.",
                        "Limits maximum Health to 4 Masks during Hollow Knight fights.",
                        "Restricts Tools during Hollow Knight fights.",
                        "Restricts Silk capacity to 1 heal during Hollow Knight fights.",
                        "Teleport into the full Hall of Gods (GG_Workshop). Recommended for high-RAM devices (8 GB+)."
                    };
                    bool[] bStates = { AssetHelperBridge.HkBindNail, AssetHelperBridge.HkBindShell, AssetHelperBridge.HkBindTools, AssetHelperBridge.HkBindSilk, false };
                    if (BindInvBadges != null)
                    {
                        for (int b = 0; b < 4 && b < BindInvBadges.Length; b++)
                        {
                            if (BindInvBadges[b] != null)
                            {
                                BindInvBadges[b].text = bStates[b] ? "<color=#F8D670>[ BOUND ]</color>" : "";
                            }
                        }
                    }
                    if (BindInvTargets != null && _bindIdx >= 0 && _bindIdx < BindInvTargets.Length && BindInvTargets[_bindIdx] != null)
                        cursorTarget = BindInvTargets[_bindIdx];
                    if (DetailPortraitSr != null) DetailPortraitSr.gameObject.SetActive(false);
                    if (DetailTitleTmp != null) DetailTitleTmp.text = bTitles[_bindIdx].ToUpperInvariant();
                    if (DetailBodyTmp != null)
                    {
                        if (_bindIdx == 4)
                        {
                            DetailBodyTmp.text = bDescs[4] + "\n\nPress Jump to unlock all statues & teleport to GG_Workshop.";
                        }
                        else
                        {
                            DetailBodyTmp.text =
                                bDescs[_bindIdx] + "\n\n" +
                                $"Status: {(bStates[_bindIdx] ? "<color=#F8D670>BOUND</color>" : "UNBOUND")}\n\n" +
                                GetBindingsSummaryLine() + "\n\nPress Jump to toggle this Binding.";
                        }
                    }
                }

                if (Cursor != null && cursorTarget != null)
                    Cursor.SetTarget(cursorTarget);

                if (PromptBarTmp != null)
                    PromptBarTmp.text = "[D-Pad] Select   |   [Jump] Challenge/Toggle   |   [Attack] Difficulty   |   [Dash/L/R] Page   |   [Cancel] Close";
            }

            private static string GetBindingsSummaryLine()
            {
                var active = new List<string>(4);
                if (AssetHelperBridge.HkBindNail) active.Add("Needle");
                if (AssetHelperBridge.HkBindShell) active.Add("Shell");
                if (AssetHelperBridge.HkBindTools) active.Add("Tools");
                if (AssetHelperBridge.HkBindSilk) active.Add("Silk");
                return active.Count == 0 ? "Bindings: None" : "Bindings: " + string.Join(", ", active);
            }

            private void ConfirmSelection()
            {
                PlaySound();
                if (_page == 0)
                {
                    var entry = HkBossCatalog[_bossIdx];
                    DisableUI();
                    AssetHelperBridge.StartDirectHkBossFight(entry.Scene, _diff);
                }
                else if (_page == 1)
                {
                    var pan = HkPantheonCatalog[_panIdx];
                    DisableUI();
                    AssetHelperBridge.StartDirectHkPantheon(pan.Title, pan.Scenes, _diff);
                }
                else
                {
                    if (_bindIdx == 4)
                    {
                        DisableUI();
                        AssetHelperBridge.UnlockAllGodhomeStatuesAndDoors();
                        AssetHelperBridge.TeleportToGodhomeScene("GG_Workshop");
                        return;
                    }
                    if (_bindIdx == 0) AssetHelperBridge.HkBindNail = !AssetHelperBridge.HkBindNail;
                    else if (_bindIdx == 1) AssetHelperBridge.HkBindShell = !AssetHelperBridge.HkBindShell;
                    else if (_bindIdx == 2) AssetHelperBridge.HkBindTools = !AssetHelperBridge.HkBindTools;
                    else if (_bindIdx == 3) AssetHelperBridge.HkBindSilk = !AssetHelperBridge.HkBindSilk;
                    RefreshPage();
                }
            }

            private object _cachedInputActions;
            private object _actCancel, _actPaneL, _actPaneR, _actDash, _actAttack, _actJump, _actLeft, _actRight, _actUp, _actDown;
            private PropertyInfo _wasPressedPi;
            private PropertyInfo _isPressedPi;
            private MethodInfo _clearStateMi;

            private void EnsureInputActionsCached()
            {
                var ih = GameManager.instance?.inputHandler;
                if (ih == null) return;
                object act = ih.inputActions;
                if (act == null || ReferenceEquals(act, _cachedInputActions)) return;
                _cachedInputActions = act;
                var t = act.GetType();
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object GetFieldOrProp(string n) => t.GetField(n, bf)?.GetValue(act) ?? t.GetProperty(n, bf)?.GetValue(act);
                _actCancel = GetFieldOrProp("MenuCancel");
                _actPaneL  = GetFieldOrProp("PaneLeft");
                _actPaneR  = GetFieldOrProp("PaneRight");
                _actDash   = GetFieldOrProp("Dash");
                _actAttack = GetFieldOrProp("Attack");
                _actJump   = GetFieldOrProp("Jump");
                _actLeft   = GetFieldOrProp("Left");
                _actRight  = GetFieldOrProp("Right");
                _actUp     = GetFieldOrProp("Up");
                _actDown   = GetFieldOrProp("Down");
                var sample = _actJump ?? _actCancel ?? _actUp;
                if (sample != null)
                {
                    var st = sample.GetType();
                    _wasPressedPi = st.GetProperty("WasPressed", bf);
                    _isPressedPi = st.GetProperty("IsPressed", bf);
                    _clearStateMi = st.GetMethod("ClearInputState", bf, null, Type.EmptyTypes, null);
                }
            }

            private bool WasPressed(object a) => a != null && _wasPressedPi != null && (bool)_wasPressedPi.GetValue(a);
            private bool IsPressed(object a) => a != null && _isPressedPi != null && (bool)_isPressedPi.GetValue(a);
            private void ClearState(object a) { if (a != null && _clearStateMi != null) _clearStateMi.Invoke(a, null); }

            private void Update()
            {
                if (!IsOpen) return;
                EnsureInputActionsCached();
                if (_cachedInputActions == null || _wasPressedPi == null) return;

                if (WasPressed(_actCancel))
                {
                    ClearState(_actCancel);
                    DisableUI();
                    return;
                }

                if (WasPressed(_actPaneL))
                {
                    _page = (_page + 2) % 3;
                    PlaySound();
                    RefreshPage();
                    return;
                }
                if (WasPressed(_actPaneR) || WasPressed(_actDash))
                {
                    _page = (_page + 1) % 3;
                    PlaySound();
                    RefreshPage();
                    return;
                }

                if (WasPressed(_actAttack))
                {
                    ClearState(_actAttack);
                    _diff = (_diff + 1) % 3;
                    PlaySound();
                    RefreshPage();
                    return;
                }

                if (WasPressed(_actJump))
                {
                    ClearState(_actJump);
                    ConfirmSelection();
                    return;
                }

                float now = Time.unscaledTime;
                if (_page == 0)
                {
                    bool lTap = WasPressed(_actLeft), rTap = WasPressed(_actRight), uTap = WasPressed(_actUp), dTap = WasPressed(_actDown);
                    int dx = (lTap || (IsPressed(_actLeft) && now >= _navCooldown)) ? -1 : ((rTap || (IsPressed(_actRight) && now >= _navCooldown)) ? 1 : 0);
                    int dy = (uTap || (IsPressed(_actUp) && now >= _navCooldown)) ? -1 : ((dTap || (IsPressed(_actDown) && now >= _navCooldown)) ? 1 : 0);
                    if (dx != 0 || dy != 0)
                    {
                        _navCooldown = now + ((lTap || rTap || uTap || dTap) ? 0.24f : 0.14f);
                        int next = _bossIdx + dx + (dy * GridCols);
                        if (next < 0) next = HkBossCatalog.Length - 1;
                        else if (next >= HkBossCatalog.Length) next = 0;
                        _bossIdx = next;
                        PlaySound();
                        RefreshPage();
                    }
                }
                else
                {
                    if (WasPressed(_actLeft)) { _diff = (_diff + 2) % 3; PlaySound(); RefreshPage(); return; }
                    if (WasPressed(_actRight)) { _diff = (_diff + 1) % 3; PlaySound(); RefreshPage(); return; }
                    bool uTap = WasPressed(_actUp), dTap = WasPressed(_actDown);
                    int moveDir = (uTap || (IsPressed(_actUp) && now >= _navCooldown)) ? -1 : ((dTap || (IsPressed(_actDown) && now >= _navCooldown)) ? 1 : 0);
                    if (moveDir != 0)
                    {
                        _navCooldown = now + ((uTap || dTap) ? 0.26f : 0.15f);
                        if (_page == 1) _panIdx = (_panIdx + moveDir + HkPantheonCatalog.Length) % HkPantheonCatalog.Length;
                        else _bindIdx = (_bindIdx + moveDir + 5) % 5;
                        PlaySound();
                        RefreshPage();
                    }
                }
            }
        }

        private static GameObject CloneNativeQuestButton(GameObject questTemplate, string name, Transform parent, out TMProOld.TextMeshPro topTmp, out TMProOld.TextMeshPro nameTmp, out SpriteRenderer iconSr)
        {
            topTmp = null;
            nameTmp = null;
            iconSr = null;
            var go = UnityEngine.Object.Instantiate(questTemplate, parent);
            go.name = name;
            var mq = go.GetComponent<InventoryItemMainQuest>();
            if (mq != null) UnityEngine.Object.Destroy(mq);
            var ed = go.GetComponent<InventoryItemExtraDescription>();
            if (ed != null) UnityEngine.Object.Destroy(ed);
            var sub = go.transform.Find("Sub Quests Layout");
            if (sub != null) UnityEngine.Object.Destroy(sub.gameObject);
            var amt = go.transform.Find("Main Quest/Group/Amount Icons");
            if (amt != null) amt.gameObject.SetActive(false);
            var orb = go.transform.Find("Main Quest/Group/New Item Orb");
            if (orb != null) orb.gameObject.SetActive(false);
            var topTr = go.transform.Find("Main Quest/Group/Text Type");
            if (topTr != null) topTmp = topTr.GetComponent<TMProOld.TextMeshPro>();
            var nameTr = go.transform.Find("Main Quest/Group/Text Name");
            if (nameTr != null) nameTmp = nameTr.GetComponent<TMProOld.TextMeshPro>();
            var iconTr = go.transform.Find("Main Quest/Group/Icon");
            if (iconTr != null)
            {
                iconSr = iconTr.GetComponent<SpriteRenderer>();
                if (iconSr != null) iconSr.color = Color.white;
            }
            go.SetActive(true);
            return go;
        }

        private static bool EnsureNativeHkJournalBuilt()
        {
            if (_hkNativeController != null && _hkNativeController.Border != null) return true;
            try
            {
                var inv = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory");
                var borderOrig = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory/Border");
                var journalOrig = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory/Journal");
                var questTemplate = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory/Quests/Quest List/Main Quests Layout/Template Main Quest Item");
                if (inv == null || borderOrig == null || journalOrig == null || questTemplate == null)
                {
                    LogDiagnosticWarn($"[HkNativeJournal] Native Inventory hierarchy not ready yet (inv={inv != null}, border={borderOrig != null}, journal={journalOrig != null}, questTpl={questTemplate != null}).");
                    return false;
                }

                var borderClone = UnityEngine.Object.Instantiate(borderOrig, inv.transform);
                borderClone.name = "IRIS_HkBorder";
                var paneListDisp = borderClone.transform.Find("PaneListDisplay");
                if (paneListDisp != null) paneListDisp.gameObject.SetActive(false);
                borderClone.SetActive(false);

                var screenPlane = borderClone.transform.Find("Inventory ScreenPlane")?.gameObject;
                var paneNameTmp = borderClone.transform.Find("Pane Name")?.GetComponent<TMProOld.TextMeshPro>();

                var contentRoot = new GameObject("IRIS_HkJournalContent");
                contentRoot.transform.SetParent(inv.transform, false);
                contentRoot.transform.localPosition = Vector3.zero;
                contentRoot.layer = inv.layer;
                contentRoot.SetActive(false);

                InventoryCursor nativeCursor = null;
                var cursorOrig = journalOrig.transform.Find("Cursor");
                if (cursorOrig != null)
                {
                    var cursorClone = UnityEngine.Object.Instantiate(cursorOrig.gameObject, contentRoot.transform);
                    cursorClone.name = "IRIS_HkCursor";
                    cursorClone.SetActive(false);
                    nativeCursor = cursorClone.GetComponent<InventoryCursor>();
                }

                float topY = (paneNameTmp != null) ? paneNameTmp.transform.localPosition.y : 2.5f;
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer < 0) uiLayer = inv.layer;

                // Page 0: Native 4x5 Boss Face Portrait Grid (like Pantheon of Pharloom's Journal!)
                var gridRoot = new GameObject("HkBossPortraitGrid");
                gridRoot.transform.SetParent(contentRoot.transform, false);
                gridRoot.layer = uiLayer;
                const int gridCount = 20;
                var pSlots = new GameObject[gridCount];
                var pSrs = new SpriteRenderer[gridCount];
                for (int i = 0; i < gridCount; i++)
                {
                    int c = i % 4;
                    int r = i / 4;
                    var slot = new GameObject("BossFace_" + i);
                    slot.transform.SetParent(gridRoot.transform, false);
                    slot.layer = uiLayer;
                    slot.transform.localPosition = new Vector3(-7.4f + (c * 1.95f), (topY - 2.3f) - (r * 1.72f), 0f);
                    slot.transform.localScale = new Vector3(0.90f, 0.90f, 1f);
                    var sr = slot.AddComponent<SpriteRenderer>();
                    sr.sortingLayerName = "Inventory";
                    sr.sortingOrder = 5;
                    var col = slot.AddComponent<BoxCollider2D>();
                    col.size = new Vector2(1.55f, 1.55f);
                    pSlots[i] = slot;
                    pSrs[i] = sr;
                }

                // Pages 1 & 2: Native Template Main Quest Item rows (Pantheons & Bindings)
                var listRoot = new GameObject("HkListRoot");
                listRoot.transform.SetParent(contentRoot.transform, false);
                listRoot.layer = uiLayer;
                const int listSlots = 5;
                var slots = new GameObject[listSlots];
                var topTmps = new TMProOld.TextMeshPro[listSlots];
                var nameTmps = new TMProOld.TextMeshPro[listSlots];
                var iconSrs = new SpriteRenderer[listSlots];
                for (int i = 0; i < listSlots; i++)
                {
                    slots[i] = CloneNativeQuestButton(questTemplate, "HkRow_" + i, listRoot.transform, out topTmps[i], out nameTmps[i], out iconSrs[i]);
                    slots[i].transform.localPosition = new Vector3(-5.6f, (topY - 2.4f) - (i * 1.55f), 0f);
                }
                listRoot.SetActive(false);

                // Page 2: Exact cloned Silksong Inventory/Inv page for Bindings (same native page as PantheonOfPharloom!) + Visit Godhome Hall button
                GameObject bindingsInvRoot = null;
                Transform[] bindInvTargets = new Transform[5];
                TMProOld.TextMeshPro[] bindInvBadges = new TMProOld.TextMeshPro[4];
                var invOrig = GameObject.Find("_GameCameras/HudCamera/In-game/Inventory/Inv");
                if (invOrig != null)
                {
                    bindingsInvRoot = UnityEngine.Object.Instantiate(invOrig, contentRoot.transform);
                    bindingsInvRoot.name = "IRIS_HkBindingsInv";
                    bindingsInvRoot.transform.localPosition = invOrig.transform.localPosition;
                    // Disable cloned FSMs & clutter so our HkNativeJournalController drives InventoryCursor cleanly
                    foreach (var fsm in bindingsInvRoot.GetComponentsInChildren<PlayMakerFSM>(true))
                        if (fsm != null) fsm.enabled = false;
                    var invCursor = bindingsInvRoot.transform.Find("Cursor");
                    if (invCursor != null) invCursor.gameObject.SetActive(false);
                    var tc = bindingsInvRoot.transform.Find("Text Completion");
                    if (tc != null) tc.gameObject.SetActive(false);
                    var bp = bindingsInvRoot.transform.Find("Button Prompts");
                    if (bp != null) bp.gameObject.SetActive(false);
                    var sh = bindingsInvRoot.transform.Find("Inv_Items/Needle Shift/Shards");
                    if (sh != null) sh.gameObject.SetActive(false);
                    var geo = bindingsInvRoot.transform.Find("Inv_Items/Needle Shift/Geo");
                    if (geo != null) geo.gameObject.SetActive(false);
                    var mem = bindingsInvRoot.transform.Find("Memory Use Msg");
                    if (mem != null) mem.gameObject.SetActive(false);
                    // Keep ONLY Inv_Items (Needle, Heart Pieces, Spool Pieces, Spool Group) and hide EVERY OTHER child (rosaries, collectables, dividers, cloned Heavy Rosary Necklace description)!
                    foreach (Transform child in bindingsInvRoot.transform)
                    {
                        if (child == null) continue;
                        bool keep = string.Equals(child.name, "Inv_Items", StringComparison.OrdinalIgnoreCase);
                        child.gameObject.SetActive(keep);
                    }
                    foreach (var coll in bindingsInvRoot.GetComponentsInChildren<InventoryItemCollectable>(true))
                    {
                        if (coll != null) coll.gameObject.SetActive(false);
                    }
                    var collectableMgr = bindingsInvRoot.GetComponent<InventoryItemCollectableManager>();
                    if (collectableMgr != null) collectableMgr.enabled = false;

                    bindInvTargets[0] = bindingsInvRoot.transform.Find("Inv_Items/Needle");
                    bindInvTargets[1] = bindingsInvRoot.transform.Find("Inv_Items/Needle Shift/Heart Pieces");
                    bindInvTargets[2] = bindingsInvRoot.transform.Find("Inv_Items/Needle Shift/Spool Group");
                    bindInvTargets[3] = bindingsInvRoot.transform.Find("Inv_Items/Needle Shift/Spool Pieces");

                    if (paneNameTmp != null)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            if (bindInvTargets[b] == null) continue;
                            var bGo = UnityEngine.Object.Instantiate(paneNameTmp.gameObject, bindInvTargets[b]);
                            bGo.name = "BoundBadge_" + b;
                            bGo.transform.localPosition = new Vector3(0f, 0f, -0.5f);
                            var bTmp = bGo.GetComponent<TMProOld.TextMeshPro>();
                            bTmp.fontSize = 3.2f;
                            bTmp.alignment = TMProOld.TextAlignmentOptions.Center;
                            bTmp.text = "";
                            bindInvBadges[b] = bTmp;
                        }
                    }

                    // Add 5th option right on the Bindings page: Visit Godhome Hall (High RAM)
                    var visitBtn = CloneNativeQuestButton(questTemplate, "VisitGodhomeHallBtn", bindingsInvRoot.transform, out var vTop, out var vName, out var vIcon);
                    visitBtn.transform.localPosition = new Vector3(-0.6f, topY - 4.2f, 0f);
                    if (vTop != null) { vTop.text = "HIGH RAM  -  GODHOME"; vTop.color = new Color(0.96f, 0.82f, 0.44f, 1f); }
                    if (vName != null) { vName.text = "Visit Godhome Hall"; vName.color = Color.white; }
                    if (vIcon != null) vIcon.sprite = GetOrCreateGodhomeIconSprite();
                    bindInvTargets[4] = visitBtn.transform;

                    bindingsInvRoot.SetActive(false);
                }

                // Right-hand Large Selected Boss Portrait
                var detailPortGo = new GameObject("HkDetailPortrait");
                detailPortGo.transform.SetParent(contentRoot.transform, false);
                detailPortGo.layer = uiLayer;
                detailPortGo.transform.localPosition = new Vector3(4.8f, topY - 2.6f, 0f);
                detailPortGo.transform.localScale = new Vector3(2.15f, 2.15f, 1f);
                var detailPortraitSr = detailPortGo.AddComponent<SpriteRenderer>();
                detailPortraitSr.sortingLayerName = "Inventory";
                detailPortraitSr.sortingOrder = 6;

                // Right-hand Journal Title, Description & Bottom Prompt Bar
                TMProOld.TextMeshPro detailTitle = null;
                TMProOld.TextMeshPro detailBody = null;
                TMProOld.TextMeshPro promptBar = null;
                if (paneNameTmp != null)
                {
                    var dtGo = UnityEngine.Object.Instantiate(paneNameTmp.gameObject, contentRoot.transform);
                    dtGo.name = "HkDetailTitle";
                    dtGo.transform.localPosition = new Vector3(4.8f, topY - 4.7f, 0f);
                    detailTitle = dtGo.GetComponent<TMProOld.TextMeshPro>();
                    detailTitle.fontSize = 5.4f;
                    detailTitle.alignment = TMProOld.TextAlignmentOptions.Center;
                    detailTitle.color = new Color(0.98f, 0.94f, 0.84f, 1f);
                    var dtRt = dtGo.GetComponent<RectTransform>();
                    if (dtRt != null) dtRt.sizeDelta = new Vector2(10.5f, 1.3f);

                    var dbGo = UnityEngine.Object.Instantiate(paneNameTmp.gameObject, contentRoot.transform);
                    dbGo.name = "HkDetailBody";
                    dbGo.transform.localPosition = new Vector3(4.8f, topY - 7.8f, 0f);
                    detailBody = dbGo.GetComponent<TMProOld.TextMeshPro>();
                    detailBody.fontSize = 3.4f;
                    detailBody.alignment = TMProOld.TextAlignmentOptions.Top;
                    detailBody.enableWordWrapping = true;
                    detailBody.color = new Color(0.86f, 0.84f, 0.78f, 0.95f);
                    var dbRt = dbGo.GetComponent<RectTransform>();
                    if (dbRt != null) dbRt.sizeDelta = new Vector2(10.5f, 5.2f);

                    var pbGo = UnityEngine.Object.Instantiate(paneNameTmp.gameObject, contentRoot.transform);
                    pbGo.name = "HkPromptBar";
                    pbGo.transform.localPosition = new Vector3(0f, topY - 11.4f, 0f);
                    promptBar = pbGo.GetComponent<TMProOld.TextMeshPro>();
                    promptBar.fontSize = 2.9f;
                    promptBar.alignment = TMProOld.TextAlignmentOptions.Center;
                    promptBar.color = new Color(0.84f, 0.80f, 0.72f, 0.92f);
                    var pbRt = pbGo.GetComponent<RectTransform>();
                    if (pbRt != null) pbRt.sizeDelta = new Vector2(22f, 1.0f);
                }

                AudioSource uiAudio = GameObject.Find("Audio Player UI")?.GetComponent<AudioSource>();
                AudioClip moveClip = null;
                var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
                if (clips != null)
                {
                    foreach (var cl in clips)
                    {
                        if (cl != null && (cl.name.IndexOf("ui_change_selection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           cl.name.IndexOf("ui_option_click", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            moveClip = cl;
                            break;
                        }
                    }
                }

                var ctrl = contentRoot.AddComponent<HkNativeJournalController>();
                ctrl.Border = borderClone;
                ctrl.ScreenPlane = screenPlane;
                ctrl.ContentRoot = contentRoot;
                ctrl.Cursor = nativeCursor;
                ctrl.PaneTitleTmp = paneNameTmp;
                ctrl.DetailTitleTmp = detailTitle;
                ctrl.DetailBodyTmp = detailBody;
                ctrl.PromptBarTmp = promptBar;
                ctrl.DetailPortraitSr = detailPortraitSr;
                ctrl.PortraitGridRoot = gridRoot;
                ctrl.PortraitSlots = pSlots;
                ctrl.PortraitSrs = pSrs;
                ctrl.ListRoot = listRoot;
                ctrl.BindingsInvRoot = bindingsInvRoot;
                ctrl.BindInvTargets = bindInvTargets;
                ctrl.BindInvBadges = bindInvBadges;
                ctrl.RowSlots = slots;
                ctrl.RowTopTmps = topTmps;
                ctrl.RowNameTmps = nameTmps;
                ctrl.RowIconSrs = iconSrs;
                ctrl.UiAudio = uiAudio;
                ctrl.MoveClip = moveClip;

                _hkNativeRoot = contentRoot;
                _hkNativeController = ctrl;
                LogDiagnosticInfo("[HkNativeJournal] Built native HudCamera Godhome Journal with 44-boss portrait grid + InventoryCursor.");
                return true;
            }
            catch (Exception ex)
            {
                LogApiCatch("HkNativeJournal", "EnsureNativeHkJournalBuilt", ex);
                return false;
            }
        }

        private static void ToggleHkBossJournalOverlay()
        {
            try
            {
                if (!EnsureNativeHkJournalBuilt()) return;
                if (_hkNativeController.IsOpen) _hkNativeController.DisableUI();
                else _hkNativeController.EnableUI();
            }
            catch (Exception ex)
            {
                LogApiCatch("CircularBubble", "ToggleHkBossJournalOverlay", ex);
            }
        }

        private static Sprite _godhomeIconSprite;
        private const string GodhomeKingsBrandIconBase64 = "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";

        private static Sprite GetOrCreateGodhomeIconSprite()
        {
            if (_godhomeIconSprite != null) return _godhomeIconSprite;
            try
            {
                byte[] pngBytes = Convert.FromBase64String(GodhomeKingsBrandIconBase64);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.name = "IRIS_GodhomeKingsBrand";
                if (ImageConversion.LoadImage(tex, pngBytes, false))
                {
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.Apply(false, true);
                    _godhomeIconSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    _godhomeIconSprite.name = "IRIS_GodhomeKingsBrand_Sprite";
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("CircularBubble", "GetOrCreateGodhomeIconSprite", ex);
            }
            return _godhomeIconSprite;
        }

        private static bool HasCircularButtonMatching(CircularFloatingWindow floatingWindow, string substringA, string substringB)
        {
            if (floatingWindow == null) return false;
            try
            {
                var fi = typeof(CircularFloatingWindow).GetField("buttonItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi?.GetValue(floatingWindow) is System.Collections.Generic.List<CircularButtonItem> items)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        string t = items[i]?.text ?? "";
                        if ((!string.IsNullOrEmpty(substringA) && t.IndexOf(substringA, StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (!string.IsNullOrEmpty(substringB) && t.IndexOf(substringB, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "HasCircularButtonMatching", ex);
            }
            return false;
        }

        public void SetupButtons()
        {
            try
            {
                if (CfgEnableCircularBubbleButton != null && !CfgEnableCircularBubbleButton.Value)
                    return;

                var floatingWindow = FindFirstObjectByType<CircularFloatingWindow>();
                if (!floatingWindow)
                    floatingWindow = FindObjectOfType<CircularFloatingWindow>();
                if (!floatingWindow)
                {
                    LogDiagnosticInfo("[CircularBubble] CircularFloatingWindow not found yet; will retry on scene load.");
                    return;
                }

                int winId = floatingWindow.GetInstanceID();
                if (_registeredCircularWindowIds.Add(winId) && !HasCircularButtonMatching(floatingWindow, "PC Mods", "PC模组"))
                {
                    floatingWindow.RegisterButton(new CircularButtonItem(
                        icon: null,
                        text: BepInEx.Utility.IsChinese ? "PC模组\n按键" : "PC Mods\n& Keys",
                        buttonColor: new Color(0.52f, 0.14f, 0.22f, 0.92f),
                        onClickCallback: () =>
                        {
                            bool nextState = !_showModPanel;
                            LogDiagnosticInfo($"[CircularBubble] 'PC Mods & Keys' circular button clicked -> opening={nextState}");
                            try { floatingWindow.HideButtonRing(); } catch (Exception exHide) { LogApiCatch("CircularBubble", "HideButtonRing", exHide); }
                            SetShowModPanel(nextState);
                        }
                    ));
                    LogDiagnosticInfo($"[CircularBubble] Registered 'PC Mods & Keys' button in CircularFloatingWindow (instanceId={winId}).");
                }

                bool hasHkGodhomeBubble = _loadedModNames.Any(n =>
                    n.IndexOf("HkCore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("HkBossLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("SsBossLoader", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasHkGodhomeBubble && _registeredGodhomeCircularWindowIds.Add(winId))
                {
                    floatingWindow.RegisterButton(new CircularButtonItem(
                        icon: GetOrCreateGodhomeIconSprite(),
                        text: "",
                        buttonColor: new Color(0.96f, 0.96f, 0.96f, 0.96f),
                        onClickCallback: () =>
                        {
                            try { floatingWindow.HideButtonRing(); } catch { }
                            ToggleHkBossJournalOverlay();
                        }
                    ));
                    LogDiagnosticInfo($"[CircularBubble] Registered 'Godhome Hall' button in CircularFloatingWindow (instanceId={winId}).");
                }

                // PatchWork active skin packs / floating bubble registration intentionally omitted per user preference
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "SetupButtons", ex);
            }
        }

        internal void EnsurePatchworkCircularBubbleRegistered(CircularFloatingWindow floatingWindow)
        {
            try
            {
                var pwPluginType = HarmonyBridge.ResolveTypeRobust("PatchworkMobile.PatchworkMobilePlugin");
                if (pwPluginType == null) return;

                EnsurePatchworkRuntimeUiHooks(pwPluginType);

                if (floatingWindow != null)
                {
                    int winId = floatingWindow.GetInstanceID();
                    if (_registeredPatchworkBubbleWindowIds.Add(winId) &&
                        !HasCircularButtonMatching(floatingWindow, "Patchwork", "皮肤") &&
                        !HasCircularButtonMatching(floatingWindow, "Skins", "切换"))
                    {
                        floatingWindow.RegisterButton(new CircularButtonItem(
                            icon: null,
                            text: BepInEx.Utility.IsChinese ? "切换\n皮肤" : "Patchwork\nSkins",
                            buttonColor: new Color(0.16f, 0.46f, 0.24f, 0.92f),
                            onClickCallback: () =>
                            {
                                LogDiagnosticInfo("[CircularBubble] 'Patchwork Skins' circular button clicked -> toggling Patchwork SkinManagerUI.");
                                try { floatingWindow.HideButtonRing(); } catch (Exception exHide) { LogApiCatch("CircularBubble", "HideButtonRing.Patchwork", exHide); }
                                TogglePatchworkSkinMenu(null);
                            }
                        ));
                        LogDiagnosticInfo($"[CircularBubble] Registered 'Patchwork Skins' button in CircularFloatingWindow (instanceId={winId}).");
                    }
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "EnsurePatchworkCircularBubbleRegistered", ex);
            }
        }

        private void EnsurePatchworkRuntimeUiHooks(Type pwPluginType)
        {
            if (_patchworkRuntimeHooksApplied || pwPluginType == null) return;
            _patchworkRuntimeHooksApplied = true;
            try
            {
                BepInEx.PluginLoader.RegisterEditor("PatchWork", visible =>
                {
                    LogDiagnosticInfo($"[PluginLoader] Editor toggle callback invoked for 'PatchWork': visible={visible}");
                    TogglePatchworkSkinMenu(visible);
                });
                LogDiagnosticInfo("[PatchworkBridge] Registered BepInEx.PluginLoader editor callback for 'PatchWork'.");
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "EnsurePatchworkRuntimeUiHooks.RegisterEditor", ex);
            }
        }

        internal void SyncPatchworkSceneMode(string sceneName)
        {
            // Do not display PatchWork active skin packs or overlay UI
            if (true) return;
            try
            {
                var pwPluginType = HarmonyBridge.ResolveTypeRobust("PatchworkMobile.PatchworkMobilePlugin");
                var skinUiType = HarmonyBridge.ResolveTypeRobust("PatchworkMobile.SkinManagerUI");
                if (pwPluginType == null || skinUiType == null) return;

                var isTitleMi = pwPluginType.GetMethod("IsTitleMenuLoaded", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var isPlayerModeMi = pwPluginType.GetMethod("IsPlayerMode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                bool isTitle = (isTitleMi != null && isTitleMi.Invoke(null, null) is bool bt && bt) ||
                               string.Equals(sceneName, "Menu_Title", StringComparison.OrdinalIgnoreCase);
                bool isPlayerMode = isPlayerModeMi == null || (isPlayerModeMi.Invoke(null, null) is bool bp && bp);

                var uiComp = EnsurePatchworkSkinManagerUiComponent(pwPluginType, skinUiType);
                if (uiComp == null) return;

                var skinsBtnField = skinUiType.GetField("_skinsBtnGo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dbgBtnField = skinUiType.GetField("_dbgBtnGo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var skinsBtnGo = skinsBtnField?.GetValue(uiComp) as GameObject;
                var dbgBtnGo = dbgBtnField?.GetValue(uiComp) as GameObject;

                // On Menu_Title: show on-screen menus ([Skins] & [Debug]).
                // In-game: if in Editor (Designer) mode, show on-screen [Skins] & [Debug] buttons; if in Player mode, hide on-screen buttons and use Mod Bubble!
                bool showOnScreen = isTitle || !isPlayerMode;
                if (skinsBtnGo != null && skinsBtnGo.activeSelf != showOnScreen)
                    skinsBtnGo.SetActive(showOnScreen);
                if (dbgBtnGo != null && dbgBtnGo.activeSelf != showOnScreen)
                    dbgBtnGo.SetActive(showOnScreen);
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "SyncPatchworkSceneMode", ex);
            }
        }

        private static Component EnsurePatchworkSkinManagerUiComponent(Type pwPluginType, Type skinUiType)
        {
            try
            {
                var existing = FindObjectOfType(skinUiType) as Component;
                if (existing != null) return existing;

                var skinMenuGoField = pwPluginType.GetField("_skinMenuGo", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var go = skinMenuGoField?.GetValue(null) as GameObject;
                if (go == null)
                {
                    go = new GameObject("PatchworkSkinMenuUI");
                    DontDestroyOnLoad(go);
                    skinMenuGoField?.SetValue(null, go);
                }
                var comp = go.GetComponent(skinUiType) ?? go.AddComponent(skinUiType);
                return comp;
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "EnsurePatchworkSkinManagerUiComponent", ex);
                return null;
            }
        }

        public static bool TogglePatchworkSkinMenu(bool? forceVisible = null)
        {
            try
            {
                var pwPluginType = HarmonyBridge.ResolveTypeRobust("PatchworkMobile.PatchworkMobilePlugin");
                var skinUiType = HarmonyBridge.ResolveTypeRobust("PatchworkMobile.SkinManagerUI");
                if (pwPluginType != null && skinUiType != null)
                {
                    var uiComp = EnsurePatchworkSkinManagerUiComponent(pwPluginType, skinUiType);
                    if (uiComp != null)
                    {
                        var isOpenField = skinUiType.GetField("_isOpen", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var panelGoField = skinUiType.GetField("_panelGo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var togglePanelMi = skinUiType.GetMethod("TogglePanel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        bool curOpen = isOpenField != null && isOpenField.GetValue(uiComp) is bool b && b;
                        bool targetOpen = forceVisible ?? !curOpen;
                        if (curOpen != targetOpen && togglePanelMi != null)
                        {
                            togglePanelMi.Invoke(uiComp, null);
                        }
                        else if (panelGoField?.GetValue(uiComp) is GameObject panelGo)
                        {
                            isOpenField?.SetValue(uiComp, targetOpen);
                            panelGo.SetActive(targetOpen);
                        }
                        if (!targetOpen)
                        {
                            try { BepInEx.PluginLoader.SetEditorVisible("PatchWork", false); } catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1024", __apiEx_10); }
                        }
                        LogDiagnosticInfo($"[PatchworkBridge] Patchwork SkinManagerUI panel open={targetOpen} (scene='{SceneManager.GetActiveScene().name}').");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogApiCatch("SilksongMobileAPIPlugin", "TogglePatchworkSkinMenu", ex);
            }
            return false;
        }

        private void EnsureUGuiModConsoleBuilt()
        {
            EnsureFloatingModButtonCreated();
            if (_uGuiModConsolePanelGo != null || _uGuiFloatBtnCanvasGo == null) return;

            _uGuiModConsolePanelGo = new GameObject("UGuiModConsoleModal", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            _uGuiModConsolePanelGo.transform.SetParent(_uGuiFloatBtnCanvasGo.transform, false);
            var prt = _uGuiModConsolePanelGo.GetComponent<RectTransform>();
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = new Vector2(0f, -20f);
            prt.sizeDelta = new Vector2(1460f, 860f);
            var pImg = _uGuiModConsolePanelGo.GetComponent<UnityEngine.UI.Image>();
            pImg.color = new Color(0.08f, 0.07f, 0.10f, 0.96f);

            CreateUGuiLabel(_uGuiModConsolePanelGo.transform, "IRIS — MOD, KEYBIND & PROFILE CONSOLE (IRIS v2.9.6)", new Vector2(-120f, 392f), new Vector2(1100f, 44f), 24, new Color(0.96f, 0.86f, 0.62f, 1f), TextAnchor.MiddleLeft);

            CreateUGuiButton(_uGuiModConsolePanelGo.transform, "CloseBtn", "Close [X]", new Vector2(630f, 392f), new Vector2(150f, 44f), new Color(0.50f, 0.14f, 0.18f, 0.96f), Color.white, 18, () => SetShowModPanel(false));

            // Top Tab Bar (7 tabs including Debug tab for Android uGUI Profiler & Inventory diagnostics)
            string[] tabs = new[] { "Hotkeys", "Keys09AZ", "Configs", "Installed", "Profile", "SSMP", "Debug" };
            string[] tabLabels = new[] { "F-Keys & Actions", "0-9 & A-Z Keys", "Mod Configs", "Installed Mods", "Profile", "SSMP", "Debug" };
            for (int i = 0; i < tabs.Length; i++)
            {
                string tabId = tabs[i];
                float x = -594f + i * 198f;
                CreateUGuiButton(_uGuiModConsolePanelGo.transform, "Tab_" + tabId, tabLabels[i], new Vector2(x, 330f), new Vector2(190f, 48f), new Color(0.22f, 0.14f, 0.22f, 0.95f), new Color(0.98f, 0.92f, 0.80f, 1f), 15, () =>
                {
                    _selectedModTab = tabId;
                    _uGuiPageIndex = 0;
                    RebuildUGuiModConsoleContent();
                });
            }

            _uGuiModConsoleStatusText = CreateUGuiLabel(_uGuiModConsolePanelGo.transform, "Ready.", new Vector2(0f, -395f), new Vector2(1380f, 36f), 17, new Color(0.65f, 0.95f, 0.75f, 1f), TextAnchor.MiddleCenter);

            var contentGo = new GameObject("ContentRoot", typeof(RectTransform));
            contentGo.transform.SetParent(_uGuiModConsolePanelGo.transform, false);
            var crt = contentGo.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0.5f, 0.5f);
            crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = new Vector2(0f, -25f);
            crt.sizeDelta = new Vector2(1400f, 640f);
            _uGuiModConsoleContentRoot = contentGo.transform;
        }

        private void RebuildUGuiModConsoleContent()
        {
            EnsureUGuiModConsoleBuilt();
            if (_uGuiModConsoleContentRoot == null) return;

            for (int i = _uGuiModConsoleContentRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(_uGuiModConsoleContentRoot.GetChild(i).gameObject);
            }

            if (_selectedModTab == "Hotkeys")
            {
                // Row 1: Quick Mod Actions (Stitchwork/Patchwork UI button removed; replaced with direct Profile shortcut)
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Act_UnlockAll", "Unlock All Crests, Tools & Weaver Skills", new Vector2(-350f, 275f), new Vector2(660f, 52f), new Color(0.16f, 0.34f, 0.22f, 0.96f), Color.white, 18, () =>
                {
                    _unlockStatusMessage = UnlockAllCrestSlotsAndModSkills();
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = _unlockStatusMessage;
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Act_OpenProfile", "Open User Profile & Mod Sync (.jsonc) ->", new Vector2(350f, 275f), new Vector2(660f, 52f), new Color(0.20f, 0.28f, 0.42f, 0.96f), Color.white, 18, () =>
                {
                    _selectedModTab = "Profile";
                    _uGuiPageIndex = 0;
                    RebuildUGuiModConsoleContent();
                });

                // Discovered Shortcut Buttons (up to 6)
                int scIdx = 0;
                lock (_discoveredConfigs)
                {
                    foreach (var cfg in _discoveredConfigs)
                    {
                        if (scIdx >= 6) break;
                        bool isInControlKey = cfg.SettingType != null && cfg.SettingType.IsEnum &&
                            (cfg.SettingType.FullName == "InControl.Key" || cfg.SettingType.Name == "Key");
                        if (cfg.SettingType == typeof(KeyboardShortcut) || cfg.SettingType == typeof(KeyCode) || isInControlKey)
                        {
                            var item = cfg;
                            int col = scIdx % 2;
                            int row = scIdx / 2;
                            float x = col == 0 ? -350f : 350f;
                            float y = 210f - row * 56f;
                            string valStr = item.Entry?.BoxedValue?.ToString() ?? "";
                            CreateUGuiButton(_uGuiModConsoleContentRoot, "SC_" + scIdx, $"{item.ModName}: {item.Key} [{valStr}]", new Vector2(x, y), new Vector2(660f, 48f), new Color(0.25f, 0.18f, 0.14f, 0.95f), new Color(1f, 0.92f, 0.75f, 1f), 16, () =>
                            {
                                var bv = item.Entry?.BoxedValue;
                                if (bv is KeyboardShortcut ks)
                                {
                                    BepInEx.MobileVirtualInput.PulseShortcut(ks, true);
                                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Pulsed shortcut {ks} for {item.ModName}";
                                }
                                else if (bv is KeyCode kc)
                                {
                                    TriggerVirtualKeyFromUi(kc);
                                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Pulsed key {kc} for {item.ModName}";
                                }
                                else if (bv != null && isInControlKey)
                                {
                                    KeyCode mapped = BepInEx.MobileVirtualInput.InControlKeyToUnityKeyCode(Convert.ToInt32(bv));
                                    if (mapped != KeyCode.None)
                                    {
                                        // Hold for 1.8s if it's a radial wheel trigger like CrestWheel so the player can tap a crest!
                                        if (item.ModName.IndexOf("CrestWheel", StringComparison.OrdinalIgnoreCase) >= 0)
                                            BepInEx.MobileVirtualInput.PulseKey(mapped, 1.8f);
                                        else
                                            TriggerVirtualKeyFromUi(mapped);
                                        if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Pulsed key {mapped} ({bv}) for {item.ModName}";
                                    }
                                }
                                SetShowModPanel(false);
                            });
                            scIdx++;
                        }
                    }
                }

                // Function Keys F1-F12 (2 rows of 6)
                KeyCode[] fKeys = new[] { KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12 };
                for (int i = 0; i < fKeys.Length; i++)
                {
                    KeyCode k = fKeys[i];
                    int col = i % 6;
                    int row = i / 6;
                    float x = -550f + col * 220f;
                    float y = 30f - row * 58f;
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "FK_" + k, k.ToString(), new Vector2(x, y), new Vector2(205f, 48f), new Color(0.20f, 0.22f, 0.30f, 0.95f), Color.white, 17, () =>
                    {
                        TriggerVirtualKeyFromUi(k);
                        if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Pulsed {k} (closing console so mod UI receives input)";
                        SetShowModPanel(false);
                    });
                }

                // Common Mod Hotkeys Row (Tab, Backquote, Insert, Home, End, PageUp, PageDown, Return, Escape)
                KeyCode[] quickKeys = new[] { KeyCode.Tab, KeyCode.BackQuote, KeyCode.Insert, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown, KeyCode.M, KeyCode.K, KeyCode.P, KeyCode.O, KeyCode.L };
                for (int i = 0; i < quickKeys.Length; i++)
                {
                    KeyCode k = quickKeys[i];
                    int col = i % 6;
                    int row = i / 6;
                    float x = -550f + col * 220f;
                    float y = -95f - row * 58f;
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "QK_" + k, k.ToString(), new Vector2(x, y), new Vector2(205f, 48f), new Color(0.24f, 0.15f, 0.20f, 0.95f), Color.white, 17, () =>
                    {
                        TriggerVirtualKeyFromUi(k);
                        if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Pulsed {k}";
                        SetShowModPanel(false);
                    });
                }

                // Modifier Hold Toggles
                KeyCode[] mods = new[] { KeyCode.LeftShift, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Return };
                for (int i = 0; i < mods.Length; i++)
                {
                    KeyCode mk = mods[i];
                    bool isMod = i < 3;
                    bool held = isMod && BepInEx.MobileVirtualInput.IsKeyManuallyHeld(mk);
                    float x = -550f + i * 220f;
                    float y = -225f;
                    string lbl = isMod ? (held ? $"[HELD] {mk}" : $"Hold {mk}") : mk.ToString();
                    Color bg = held ? new Color(0.18f, 0.45f, 0.25f, 0.96f) : new Color(0.18f, 0.18f, 0.22f, 0.95f);
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "MK_" + mk, lbl, new Vector2(x, y), new Vector2(205f, 48f), bg, Color.white, 16, () =>
                    {
                        if (isMod)
                        {
                            BepInEx.MobileVirtualInput.SetKeyHeld(mk, !held);
                            RebuildUGuiModConsoleContent();
                        }
                        else
                        {
                            TriggerVirtualKeyFromUi(mk);
                        }
                    });
                }

                // Bottom row on Hotkeys tab: Auto-Close toggle + Open Full 0-9 & A-Z Keyboard button
                string autoCloseLbl = _autoCloseMenuOnKeyTap ? "Auto-Close Console on Tap: ON" : "Auto-Close Console on Tap: OFF";
                CreateUGuiButton(_uGuiModConsoleContentRoot, "ToggleAutoClose", autoCloseLbl, new Vector2(-340f, -285f), new Vector2(640f, 46f), _autoCloseMenuOnKeyTap ? new Color(0.16f, 0.38f, 0.24f, 0.95f) : new Color(0.28f, 0.18f, 0.18f, 0.95f), Color.white, 16, () =>
                {
                    _autoCloseMenuOnKeyTap = !_autoCloseMenuOnKeyTap;
                    RebuildUGuiModConsoleContent();
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Goto09AZ", "Open Full 0-9 & A-Z Virtual Keyboard ->", new Vector2(340f, -285f), new Vector2(640f, 46f), new Color(0.24f, 0.28f, 0.44f, 0.96f), new Color(1f, 0.94f, 0.76f, 1f), 17, () =>
                {
                    _selectedModTab = "Keys09AZ";
                    RebuildUGuiModConsoleContent();
                });
            }
            else if (_selectedModTab == "Keys09AZ")
            {
                // Mode bar: Tap vs Hold toggle + Auto-Close toggle + Release All Held Keys
                string holdModeLbl = _uGuiKeyHoldMode ? "Key Mode: HOLD TOGGLE (Tap to Hold/Release)" : "Key Mode: SINGLE TAP PULSE (1-Frame Clean)";
                CreateUGuiButton(_uGuiModConsoleContentRoot, "KeyModeBtn", holdModeLbl, new Vector2(-430f, 282f), new Vector2(500f, 44f), _uGuiKeyHoldMode ? new Color(0.42f, 0.26f, 0.12f, 0.96f) : new Color(0.16f, 0.34f, 0.24f, 0.96f), Color.white, 16, () =>
                {
                    _uGuiKeyHoldMode = !_uGuiKeyHoldMode;
                    RebuildUGuiModConsoleContent();
                });
                string acLbl = _autoCloseMenuOnKeyTap ? "Auto-Close on Tap: ON" : "Auto-Close on Tap: OFF";
                CreateUGuiButton(_uGuiModConsoleContentRoot, "ACModeBtn", acLbl, new Vector2(60f, 282f), new Vector2(420f, 44f), _autoCloseMenuOnKeyTap ? new Color(0.18f, 0.34f, 0.38f, 0.96f) : new Color(0.25f, 0.18f, 0.22f, 0.96f), Color.white, 16, () =>
                {
                    _autoCloseMenuOnKeyTap = !_autoCloseMenuOnKeyTap;
                    RebuildUGuiModConsoleContent();
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "RelAllBtn", "Release All Held Keys", new Vector2(495f, 282f), new Vector2(390f, 44f), new Color(0.44f, 0.16f, 0.18f, 0.96f), Color.white, 16, () =>
                {
                    foreach (var nk in _numberKeys0To9) BepInEx.MobileVirtualInput.SetKeyHeld(nk, false);
                    foreach (var lk in _letterKeysAToZ) BepInEx.MobileVirtualInput.SetKeyHeld(lk, false);
                    foreach (var mk in _modifierKeys) BepInEx.MobileVirtualInput.SetKeyHeld(mk, false);
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = "Released all held virtual keys.";
                    RebuildUGuiModConsoleContent();
                });

                // Section 1: 0 - 9 Number Keys (fires both Alpha0-9 and Keypad0-9)
                CreateUGuiLabel(_uGuiModConsoleContentRoot, "=== NUMBER KEYS (0 - 9) [Triggers Top-Row Alpha0-9 + Numpad Keypad0-9] ===", new Vector2(0f, 232f), new Vector2(1360f, 32f), 17, new Color(0.96f, 0.86f, 0.62f, 1f), TextAnchor.MiddleCenter);
                for (int i = 0; i < _numberKeys0To9.Length; i++)
                {
                    KeyCode nk = _numberKeys0To9[i];
                    int digit = i; // Alpha0 is index 0 -> "0", Alpha1 is index 1 -> "1", ..., Alpha9 is index 9 -> "9"
                    bool isHeld = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(nk);
                    float x = -607.5f + i * 135f;
                    float y = 182f;
                    string lbl = isHeld ? $"[{digit}]*" : digit.ToString();
                    Color bg = isHeld ? new Color(0.18f, 0.50f, 0.28f, 0.96f) : new Color(0.20f, 0.24f, 0.34f, 0.96f);
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "NumKey_" + digit, lbl, new Vector2(x, y), new Vector2(124f, 52f), bg, Color.white, 20, () =>
                    {
                        TriggerVirtualKeyFromUi(nk);
                        if (_uGuiModConsoleStatusText != null && !_uGuiKeyHoldMode)
                            _uGuiModConsoleStatusText.text = $"Pulsed Number '{digit}' ({nk} + Keypad{digit})";
                    });
                }

                // Section 2: A - Z Letter Keys (All 26 keys in 4 rows: 7, 7, 7, 5)
                CreateUGuiLabel(_uGuiModConsoleContentRoot, "=== LETTER KEYS (A - Z) [All 26 Keys] ===", new Vector2(0f, 122f), new Vector2(1360f, 32f), 17, new Color(0.96f, 0.86f, 0.62f, 1f), TextAnchor.MiddleCenter);
                const int colsAZ = 7;
                for (int i = 0; i < _letterKeysAToZ.Length; i++)
                {
                    KeyCode lk = _letterKeysAToZ[i];
                    int col = i % colsAZ;
                    int row = i / colsAZ;
                    float x = -570f + col * 190f;
                    float y = 70f - row * 64f;
                    bool isHeld = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(lk);
                    string lbl = isHeld ? $"[{lk}]*" : lk.ToString();
                    Color bg = isHeld ? new Color(0.18f, 0.50f, 0.28f, 0.96f) : new Color(0.24f, 0.18f, 0.28f, 0.96f);
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "LetKey_" + lk, lbl, new Vector2(x, y), new Vector2(176f, 52f), bg, Color.white, 19, () =>
                    {
                        TriggerVirtualKeyFromUi(lk);
                        if (_uGuiModConsoleStatusText != null && !_uGuiKeyHoldMode)
                            _uGuiModConsoleStatusText.text = $"Pulsed Letter '{lk}'";
                    });
                }

                // Section 3: Punctuation, Arrows & Common Action Keys at bottom of 0-9 & A-Z tab
                KeyCode[] extraKeys = new[] { KeyCode.Minus, KeyCode.Equals, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Backslash, KeyCode.Semicolon, KeyCode.Quote, KeyCode.Comma, KeyCode.Period, KeyCode.Slash, KeyCode.Space, KeyCode.Backspace, KeyCode.Escape, KeyCode.Return };
                for (int i = 0; i < extraKeys.Length; i++)
                {
                    KeyCode ek = extraKeys[i];
                    int col = i % 7;
                    int row = i / 7;
                    float x = -570f + col * 190f;
                    float y = -195f - row * 54f;
                    bool isHeld = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(ek);
                    string lbl = isHeld ? $"[{ek}]*" : ek.ToString();
                    Color bg = isHeld ? new Color(0.18f, 0.50f, 0.28f, 0.96f) : new Color(0.18f, 0.20f, 0.24f, 0.95f);
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "ExtKey_" + ek, lbl, new Vector2(x, y), new Vector2(176f, 44f), bg, new Color(0.92f, 0.92f, 0.98f, 1f), 15, () =>
                    {
                        TriggerVirtualKeyFromUi(ek);
                        if (_uGuiModConsoleStatusText != null && !_uGuiKeyHoldMode)
                            _uGuiModConsoleStatusText.text = $"Pulsed '{ek}'";
                    });
                }
            }
            else if (_selectedModTab == "Configs")
            {
                List<LoadedModConfigEntryInfo> list;
                lock (_discoveredConfigs) { list = _discoveredConfigs.ToList(); }
                const int perPage = 8;
                int totalPages = Mathf.Max(1, (list.Count + perPage - 1) / perPage);
                _uGuiPageIndex = Mathf.Clamp(_uGuiPageIndex, 0, totalPages - 1);
                int start = _uGuiPageIndex * perPage;

                for (int i = 0; i < perPage && (start + i) < list.Count; i++)
                {
                    var item = list[start + i];
                    float y = 265f - i * 66f;
                    CreateUGuiLabel(_uGuiModConsoleContentRoot, $"{item.ModName} | {item.Section}: {item.Key}", new Vector2(-260f, y), new Vector2(820f, 52f), 17, Color.white, TextAnchor.MiddleLeft);

                    var val = item.Entry?.BoxedValue;
                    string valStr = val?.ToString() ?? "null";
                    CreateUGuiButton(_uGuiModConsoleContentRoot, "Cfg_" + i, valStr, new Vector2(420f, y), new Vector2(460f, 50f), new Color(0.24f, 0.18f, 0.28f, 0.96f), new Color(0.98f, 0.92f, 0.75f, 1f), 17, () =>
                    {
                        try
                        {
                            var curVal = item.Entry.BoxedValue;
                            if (item.SettingType == typeof(bool) && curVal is bool bVal)
                                item.Entry.BoxedValue = !bVal;
                            else if (item.SettingType != null && item.SettingType.IsEnum && curVal != null)
                            {
                                var vals = Enum.GetValues(item.SettingType);
                                int idx = Array.IndexOf(vals, curVal);
                                item.Entry.BoxedValue = vals.GetValue((idx + 1) % vals.Length);
                            }
                            else if (item.SettingType == typeof(int) && curVal is int iVal)
                                item.Entry.BoxedValue = iVal + 1;
                            else if (item.SettingType == typeof(float) && curVal is float fVal)
                                item.Entry.BoxedValue = Mathf.Round((fVal + 0.25f) * 100f) / 100f;
                            else if (curVal is KeyboardShortcut ks)
                                BepInEx.MobileVirtualInput.PulseShortcut(ks, true);
                            else if (curVal is KeyCode kc)
                                TriggerVirtualKeyFromUi(kc);
                            RebuildUGuiModConsoleContent();
                        }
                        catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1261", __apiEx_11); }
                    });
                }

                CreateUGuiButton(_uGuiModConsoleContentRoot, "PrevPage", "< Prev Page", new Vector2(-220f, -275f), new Vector2(220f, 48f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 17, () =>
                {
                    _uGuiPageIndex = Mathf.Max(0, _uGuiPageIndex - 1);
                    RebuildUGuiModConsoleContent();
                });
                CreateUGuiLabel(_uGuiModConsoleContentRoot, $"Page {_uGuiPageIndex + 1} / {totalPages} ({list.Count} settings)", new Vector2(0f, -275f), new Vector2(220f, 48f), 17, Color.white, TextAnchor.MiddleCenter);
                CreateUGuiButton(_uGuiModConsoleContentRoot, "NextPage", "Next Page >", new Vector2(220f, -275f), new Vector2(220f, 48f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 17, () =>
                {
                    _uGuiPageIndex = Mathf.Min(totalPages - 1, _uGuiPageIndex + 1);
                    RebuildUGuiModConsoleContent();
                });
            }
            else if (_selectedModTab == "Installed")
            {
                var mgr = InGameModManager.Instance;
                mgr?.ScanInstalledMods();
                var mods = mgr != null ? mgr.InstalledMods : new List<InstalledModItem>();
                const int perPage = 8;
                int totalPages = Mathf.Max(1, (mods.Count + perPage - 1) / perPage);
                _uGuiPageIndex = Mathf.Clamp(_uGuiPageIndex, 0, totalPages - 1);
                int start = _uGuiPageIndex * perPage;

                for (int i = 0; i < perPage && (start + i) < mods.Count; i++)
                {
                    var m = mods[start + i];
                    float y = 265f - i * 66f;
                    string st = m.IsEnabled ? "[ACTIVE]" : "[DISABLED]";
                    Color stCol = m.IsEnabled ? new Color(0.55f, 1f, 0.65f, 1f) : new Color(1f, 0.55f, 0.55f, 1f);
                    CreateUGuiLabel(_uGuiModConsoleContentRoot, $"{st} {m.DisplayName} (v{m.Version})", new Vector2(-220f, y), new Vector2(900f, 50f), 18, stCol, TextAnchor.MiddleLeft);
                    if (!m.IsCore)
                    {
                        CreateUGuiButton(_uGuiModConsoleContentRoot, "ModToggle_" + i, m.IsEnabled ? "Disable" : "Enable", new Vector2(460f, y), new Vector2(260f, 48f), new Color(0.26f, 0.18f, 0.24f, 0.96f), Color.white, 17, () =>
                        {
                            mgr.ToggleMod(m);
                            if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = mgr.GlobalStatusMessage;
                            RebuildUGuiModConsoleContent();
                        });
                    }
                }

                CreateUGuiButton(_uGuiModConsoleContentRoot, "PrevModPage", "< Prev Page", new Vector2(-220f, -275f), new Vector2(220f, 48f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 17, () =>
                {
                    _uGuiPageIndex = Mathf.Max(0, _uGuiPageIndex - 1);
                    RebuildUGuiModConsoleContent();
                });
                CreateUGuiLabel(_uGuiModConsoleContentRoot, $"Page {_uGuiPageIndex + 1} / {totalPages} ({mods.Count} mods)", new Vector2(0f, -275f), new Vector2(220f, 48f), 17, Color.white, TextAnchor.MiddleCenter);
                CreateUGuiButton(_uGuiModConsoleContentRoot, "NextModPage", "Next Page >", new Vector2(220f, -275f), new Vector2(220f, 48f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 17, () =>
                {
                    _uGuiPageIndex = Mathf.Min(totalPages - 1, _uGuiPageIndex + 1);
                    RebuildUGuiModConsoleContent();
                });
            }
            else if (_selectedModTab == "Profile")
            {
                BuildUGuiProfileTabContent();
            }
            else if (_selectedModTab == "Debug")
            {
                BuildUGuiDebugTabContent();
            }
            else if (_selectedModTab == "SSMP")
            {
                if (!HarmonyBridge.IsSSMPLoaded)
                {
                    HarmonyBridge.BootstrapSSMPOnMainMenuIfReady(_instantiatedPcPlugins);
                }
                string ssmpState = HarmonyBridge.IsSSMPLoaded ? HarmonyBridge.SSMPMobileStatusText : "SSMP not yet active";
                CreateUGuiLabel(_uGuiModConsoleContentRoot, $"Status: {ssmpState} | {HarmonyBridge.SSMPMobileServerIp}:{HarmonyBridge.SSMPMobileServerPort} ({HarmonyBridge.SSMPMobileUsername})", new Vector2(0f, 220f), new Vector2(1320f, 50f), 19, Color.white, TextAnchor.MiddleCenter);
                CreateUGuiButton(_uGuiModConsoleContentRoot, "SSMP_DirectJoin", $"Direct Connect ({HarmonyBridge.SSMPMobileServerIp}:{HarmonyBridge.SSMPMobileServerPort})", new Vector2(0f, 130f), new Vector2(680f, 56f), new Color(0.16f, 0.38f, 0.24f, 0.96f), Color.white, 20, () =>
                {
                    HarmonyBridge.SSMPMobileDirectConnect(HarmonyBridge.SSMPMobileServerIp, HarmonyBridge.SSMPMobileServerPort, HarmonyBridge.SSMPMobileUsername);
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = HarmonyBridge.SSMPMobileStatusText;
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "SSMP_Host", $"Host Server (Port {HarmonyBridge.SSMPMobileServerPort})", new Vector2(0f, 50f), new Vector2(680f, 56f), new Color(0.20f, 0.32f, 0.46f, 0.96f), Color.white, 20, () =>
                {
                    HarmonyBridge.SSMPMobileStartHost(HarmonyBridge.SSMPMobileServerPort, HarmonyBridge.SSMPMobileUsername);
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = HarmonyBridge.SSMPMobileStatusText;
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "SSMP_Disconnect", "Disconnect SSMP", new Vector2(0f, -30f), new Vector2(680f, 56f), new Color(0.45f, 0.16f, 0.20f, 0.96f), Color.white, 20, () =>
                {
                    HarmonyBridge.SSMPMobileDisconnect();
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = HarmonyBridge.SSMPMobileStatusText;
                });
            }
        }

        private TouchScreenKeyboard _profileNameKeyboard;
        private bool _isPollingProfileKeyboard;

        private void OpenProfileNameKeyboard()
        {
            try
            {
                IrisProfileManager.EnsureLoaded();
                _profileNameKeyboard = TouchScreenKeyboard.Open(
                    IrisProfileManager.CurrentProfileName ?? "Pharloom Wanderer",
                    TouchScreenKeyboardType.Default,
                    false,
                    false,
                    false,
                    false,
                    "Enter your IRIS Profile Name...");
                if (!_isPollingProfileKeyboard)
                {
                    StartCoroutine(CoPollProfileNameKeyboard());
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[Profile] TouchScreenKeyboard error: {ex.Message}");
            }
        }

        private IEnumerator CoPollProfileNameKeyboard()
        {
            _isPollingProfileKeyboard = true;
            while (_profileNameKeyboard != null)
            {
                var st = _profileNameKeyboard.status;
                if (st == TouchScreenKeyboard.Status.Done ||
                    st == TouchScreenKeyboard.Status.Canceled ||
                    st == TouchScreenKeyboard.Status.LostFocus ||
                    !_profileNameKeyboard.active)
                {
                    string entered = (_profileNameKeyboard.text ?? "").Trim();
                    _profileNameKeyboard = null;
                    if (!string.IsNullOrEmpty(entered))
                    {
                        IrisProfileManager.SetProfileName(entered);
                        if (_uGuiModConsoleStatusText != null)
                            _uGuiModConsoleStatusText.text = $"Saved Profile Name: '{entered}'";
                        RebuildUGuiModConsoleContent();
                    }
                    break;
                }
                yield return null;
            }
            _isPollingProfileKeyboard = false;
        }

        private string _lastInvDiagSummary = "Tap 'Dump Inv State' while the invisible inventory is open.";

        private InventoryPaneList _cachedMainInvPaneList;
        private PlayMakerFSM _cachedInvFsm;
        private Type _cachedNfgBaseType;
        private PropertyInfo _cachedNfgAlphaSelfProp;
        private PropertyInfo _cachedGmInvFsmProp;
        private float _lastInvVisCheckTime;
        private bool _loggedInvVisFixOnce;
        private bool _wasInventoryOpenByIris;
        private readonly List<Component> _irisForcedFadeGroups = new List<Component>();

        private void RestoreClosedInventoryAlpha()
        {
            try
            {
                if (_cachedNfgAlphaSelfProp == null || !_cachedNfgAlphaSelfProp.CanWrite)
                {
                    _irisForcedFadeGroups.Clear();
                    return;
                }
                for (int i = 0; i < _irisForcedFadeGroups.Count; i++)
                {
                    var nfg = _irisForcedFadeGroups[i];
                    if (nfg != null)
                    {
                        _cachedNfgAlphaSelfProp.SetValue(nfg, 0.0f, null);
                    }
                }
                _irisForcedFadeGroups.Clear();
                if (_cachedMainInvPaneList != null && _cachedMainInvPaneList.gameObject != null && _cachedNfgBaseType != null)
                {
                    Transform border = _cachedMainInvPaneList.transform.Find("Border");
                    if (border != null)
                    {
                        Component bNfg = border.GetComponent(_cachedNfgBaseType);
                        if (bNfg != null) _cachedNfgAlphaSelfProp.SetValue(bNfg, 0.0f, null);
                    }
                }
            }
            catch { }
        }

        private void EnsureOpenInventoryVisible()
        {
            float now = Time.unscaledTime;
            if (now - _lastInvVisCheckTime < 0.12f) return;
            _lastInvVisCheckTime = now;
            try
            {
                var gm = GameManager.instance;
                if (gm == null) return;
                if (_cachedGmInvFsmProp == null)
                    _cachedGmInvFsmProp = gm.GetType().GetProperty("inventoryFSM", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_cachedInvFsm == null && _cachedGmInvFsmProp != null)
                    _cachedInvFsm = _cachedGmInvFsmProp.GetValue(gm, null) as PlayMakerFSM;
                if (_cachedInvFsm == null || !string.Equals(_cachedInvFsm.ActiveStateName, "Opened", StringComparison.Ordinal))
                {
                    return;
                }

                if (_cachedNfgBaseType == null)
                {
                    _cachedNfgBaseType = HarmonyLib.AccessTools.TypeByName("TeamCherry.NestedFadeGroup.NestedFadeGroupBase") ?? HarmonyLib.AccessTools.TypeByName("NestedFadeGroupBase");
                    if (_cachedNfgBaseType != null)
                        _cachedNfgAlphaSelfProp = _cachedNfgBaseType.GetProperty("AlphaSelf", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_cachedNfgBaseType == null || _cachedNfgAlphaSelfProp == null || !_cachedNfgAlphaSelfProp.CanWrite)
                    return;

                if (_cachedMainInvPaneList == null || _cachedMainInvPaneList.gameObject == null || !_cachedMainInvPaneList.gameObject.scene.IsValid())
                {
                    var pls = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
                    if (pls != null)
                    {
                        for (int i = 0; i < pls.Length; i++)
                        {
                            var pl = pls[i];
                            if (pl != null && pl.gameObject != null && pl.gameObject.scene.IsValid() && pl.gameObject.name == "Inventory")
                            {
                                _cachedMainInvPaneList = pl;
                                break;
                            }
                        }
                    }
                }
                if (_cachedMainInvPaneList == null || !_cachedMainInvPaneList.gameObject.activeInHierarchy)
                    return;

                Transform root = _cachedMainInvPaneList.transform;
                int count = root.childCount;
                for (int i = 0; i < count; i++)
                {
                    Transform ch = root.GetChild(i);
                    if (ch == null || !ch.gameObject.activeInHierarchy) continue;
                    string cn = ch.name;
                    if (cn == "Border(Clone)" || cn == "Bosses" || cn == "BossRush") continue;
                    Component nfg = ch.GetComponent(_cachedNfgBaseType);
                    if (nfg == null) continue;
                    float curAlpha = Convert.ToSingle(_cachedNfgAlphaSelfProp.GetValue(nfg, null));
                    if (curAlpha < 0.99f)
                    {
                        _cachedNfgAlphaSelfProp.SetValue(nfg, 1.0f, null);
                        if (!_irisForcedFadeGroups.Contains(nfg)) _irisForcedFadeGroups.Add(nfg);
                        if (!_loggedInvVisFixOnce)
                        {
                            _loggedInvVisFixOnce = true;
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[InvVisFix] Restored stuck Inventory alpha ({curAlpha:F2} -> 1.00) on '{cn}' while invFSM='Opened'.");
                        }
                    }
                }
            }
            catch { }
        }

        private void RunInventoryStateDiagnostic()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[InvDiag] ");
                var fgType = HarmonyLib.AccessTools.TypeByName("TeamCherry.NestedFadeGroup.NestedFadeGroupBase") ?? HarmonyLib.AccessTools.TypeByName("NestedFadeGroupBase");
                var aProp = fgType != null ? fgType.GetProperty("AlphaSelf", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                try
                {
                    var gm = GameManager.instance;
                    string invFsmState = "none";
                    if (gm != null)
                    {
                        var invFsmProp = gm.GetType().GetProperty("inventoryFSM", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var invFsm = invFsmProp != null ? invFsmProp.GetValue(gm, null) as PlayMakerFSM : null;
                        if (invFsm != null) invFsmState = invFsm.ActiveStateName ?? "null";
                    }
                    bool pdInvOpen = PlayerData.HasInstance && PlayerData.instance != null && PlayerData.instance.isInventoryOpen;
                    sb.Append($"invFSM='{invFsmState}', pdInvOpen={pdInvOpen}; ");
                }
                catch { }
                var pls = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
                sb.Append($"PaneLists={pls?.Length ?? 0}; ");
                if (pls != null)
                {
                    foreach (var pl in pls)
                    {
                        if (pl == null || pl.gameObject == null || !pl.gameObject.scene.IsValid()) continue;
                        string fgAlphaStr = "none";
                        try
                        {
                            var fgComp = fgType != null ? pl.GetComponent(fgType) : null;
                            if (fgComp != null && aProp != null) fgAlphaStr = Convert.ToSingle(aProp.GetValue(fgComp, null)).ToString("F2");
                        }
                        catch { }
                        sb.Append($"PL('{pl.gameObject.name}', scene='{pl.gameObject.scene.name}', act={pl.gameObject.activeInHierarchy}, layer={pl.gameObject.layer}, pos={pl.transform.position}, fgA={fgAlphaStr}); ");
                        try
                        {
                            int childCount = pl.transform.childCount;
                            var chList = new List<string>();
                            for (int ci = 0; ci < childCount && ci < 14; ci++)
                            {
                                var ch = pl.transform.GetChild(ci);
                                if (ch == null) continue;
                                string cAlpha = "-";
                                if (fgType != null && aProp != null)
                                {
                                    var cfg = ch.GetComponent(fgType);
                                    if (cfg != null) cAlpha = Convert.ToSingle(aProp.GetValue(cfg, null)).ToString("F2");
                                }
                                chList.Add($"{ch.name}(a={ch.gameObject.activeInHierarchy},z={ch.position.z:F1},fg={cAlpha})");
                            }
                            sb.Append("Children=[" + string.Join(", ", chList) + "]; ");
                        }
                        catch { }
                    }
                }
                var sp = GameObject.Find("Inventory ScreenPlane");
                if (sp != null)
                {
                    var r = sp.GetComponent<Renderer>();
                    string texInfo = "none";
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.mainTexture != null)
                        texInfo = $"{r.sharedMaterial.mainTexture.name}({r.sharedMaterial.mainTexture.width}x{r.sharedMaterial.mainTexture.height})";
                    sb.Append($"ScreenPlane(act={sp.activeInHierarchy}, layer={sp.layer}, pos={sp.transform.position}, rendEn={(r != null ? r.enabled.ToString() : "none")}, mat={(r != null && r.sharedMaterial != null ? r.sharedMaterial.shader?.name : "none")}, tex={texInfo}); ");
                }
                else
                {
                    sb.Append("ScreenPlane=null; ");
                }
                var gc = GameCameras.instance;
                if (gc != null && gc.hudCamera != null)
                {
                    var hCam = gc.hudCamera.GetComponent<Camera>();
                    sb.Append($"HudCam(act={gc.hudCamera.gameObject.activeInHierarchy}, pos={gc.hudCamera.transform.position}, camEn={(hCam != null ? hCam.enabled.ToString() : "none")}, mask={(hCam != null ? "0x" + hCam.cullingMask.ToString("X") : "none")}, clip={(hCam != null ? $"{hCam.nearClipPlane:F1}..{hCam.farClipPlane:F1}" : "none")}); ");
                }
                _lastInvDiagSummary = sb.ToString();
                SilksongMobileAPIPlugin.LogDiagnosticInfo(_lastInvDiagSummary);

                // Also log RebalancedCrests live state for zero-guess verification
                try
                {
                    var rsb = new StringBuilder();
                    rsb.Append("[RebDiag] ");
                    string curCrest = (PlayerData.HasInstance && PlayerData.instance != null) ? (PlayerData.instance.CurrentCrestID ?? "null") : "noPD";
                    rsb.Append($"curCrest='{curCrest}'; ");
                    var tweakType = HarmonyLib.AccessTools.TypeByName("RebalancedCrests.Core.Tweak");
                    if (tweakType != null)
                    {
                        var allProp = tweakType.GetProperty("AllTweaks", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        var cfgEntry = allProp != null ? allProp.GetValue(null, null) : null;
                        var valProp = cfgEntry != null ? cfgEntry.GetType().GetProperty("Value") : null;
                        rsb.Append($"AllTweaks={(valProp != null ? valProp.GetValue(cfgEntry, null) : "null")}; ");
                    }
                    else rsb.Append("TweakType=null; ");
                    var reaper = ToolItemManager.GetCrestByName("Reaper");
                    if (reaper != null && reaper.slots != null && reaper.slots.Length > 1)
                        rsb.Append($"ReaperSlot1={reaper.slots[1].Type}(locked={reaper.slots[1].IsLocked}); ");
                    var beast = ToolItemManager.GetCrestByName("Warrior");
                    if (beast != null && beast.slots != null && beast.slots.Length > 3)
                        rsb.Append($"BeastSlot3={beast.slots[3].Type}(len={beast.slots.Length}); ");
                    var hc = HeroController.UnsafeInstance;
                    if (hc != null && hc.configs != null && hc.configs.Length > 0 && hc.configs[0] != null && hc.configs[0].Config != null)
                        rsb.Append($"HunterDownspikeAntic={hc.configs[0].Config.downspikeAnticTime:F3}; ");
                    SilksongMobileAPIPlugin.LogDiagnosticInfo(rsb.ToString());
                }
                catch (Exception rex)
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn("[RebDiag] error: " + rex.Message);
                }

                SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
            }
            catch (Exception ex)
            {
                _lastInvDiagSummary = "InvDiag error: " + ex.Message;
            }
        }

        private void BuildUGuiDebugTabContent()
        {
            string profLabel = RuntimeHybridCLRAdapterBuilder.EnableHookProfiler ? "Profiler: ON (Stop)" : "Profiler: OFF (Start)";
            CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_Profiler", profLabel, new Vector2(-480f, 250f), new Vector2(260f, 48f),
                RuntimeHybridCLRAdapterBuilder.EnableHookProfiler ? new Color(0.18f, 0.48f, 0.24f, 0.96f) : new Color(0.24f, 0.28f, 0.42f, 0.96f), Color.white, 16, () =>
            {
                RuntimeHybridCLRAdapterBuilder.EnableHookProfiler = !RuntimeHybridCLRAdapterBuilder.EnableHookProfiler;
                if (!RuntimeHybridCLRAdapterBuilder.EnableHookProfiler)
                {
                    var lines = RuntimeHybridCLRAdapterBuilder.GetHookProfilerSummaryLines();
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("[HookProfiler] " + string.Join(" | ", lines));
                    SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                }
                RebuildUGuiModConsoleContent();
            });

            CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_ResetProf", "Reset Profiler", new Vector2(-210f, 250f), new Vector2(210f, 48f), new Color(0.28f, 0.24f, 0.34f, 0.96f), Color.white, 16, () =>
            {
                RuntimeHybridCLRAdapterBuilder.ResetHookProfiler();
                RebuildUGuiModConsoleContent();
            });

            CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_DumpInv", "Dump Inv State", new Vector2(30f, 250f), new Vector2(220f, 48f), new Color(0.42f, 0.26f, 0.16f, 0.96f), Color.white, 16, () =>
            {
                RunInventoryStateDiagnostic();
                if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = "Dumped [InvDiag] to ModLog.log!";
                RebuildUGuiModConsoleContent();
            });

            CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_LogProf", "Log Profiler -> File", new Vector2(290f, 250f), new Vector2(250f, 48f), new Color(0.20f, 0.36f, 0.44f, 0.96f), Color.white, 16, () =>
            {
                var lines = RuntimeHybridCLRAdapterBuilder.GetHookProfilerSummaryLines();
                SilksongMobileAPIPlugin.LogDiagnosticInfo("[HookProfiler] " + string.Join(" | ", lines));
                SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = $"Logged {lines.Count} profiler line(s) to ModLog.log!";
                RebuildUGuiModConsoleContent();
            });

            CreateUGuiLabel(_uGuiModConsoleContentRoot, _lastInvDiagSummary, new Vector2(0f, 175f), new Vector2(1320f, 70f), 14, new Color(0.95f, 0.88f, 0.65f, 1f), TextAnchor.MiddleLeft);

            var profLines = RuntimeHybridCLRAdapterBuilder.GetHookProfilerSummaryLines();
            if (profLines == null || profLines.Count == 0)
            {
                CreateUGuiLabel(_uGuiModConsoleContentRoot, "Hook Profiler: tap 'Profiler: OFF (Start)', play 10s during lag, then tap 'Log Profiler -> File'.", new Vector2(0f, 95f), new Vector2(1320f, 40f), 16, new Color(0.75f, 0.85f, 0.95f, 1f), TextAnchor.MiddleLeft);
            }
            else
            {
                int maxShow = Mathf.Min(profLines.Count, 9);
                for (int i = 0; i < maxShow; i++)
                {
                    CreateUGuiLabel(_uGuiModConsoleContentRoot, profLines[i], new Vector2(0f, 105f - i * 36f), new Vector2(1320f, 34f), 15, Color.white, TextAnchor.MiddleLeft);
                }
            }

            bool hasHkGodhome = _loadedModNames.Any(n =>
                n.IndexOf("HkCore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("HkBossLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("SsBossLoader", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hasHkGodhome)
            {
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_TpAtrium", "TP: GG_Atrium", new Vector2(-360f, 195f), new Vector2(260f, 44f), new Color(0.55f, 0.42f, 0.14f, 0.96f), Color.white, 16, () =>
                {
                    string msg = AssetHelperBridge.TeleportToGodhomeScene("GG_Atrium");
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = msg;
                    SetShowModPanel(false);
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_TpWorkshop", "TP: GG_Workshop", new Vector2(-70f, 195f), new Vector2(280f, 44f), new Color(0.55f, 0.42f, 0.14f, 0.96f), Color.white, 16, () =>
                {
                    string msg = AssetHelperBridge.TeleportToGodhomeScene("GG_Workshop");
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = msg;
                    SetShowModPanel(false);
                });
                CreateUGuiButton(_uGuiModConsoleContentRoot, "Dbg_UnlockGG", "Unlock Godhome", new Vector2(240f, 195f), new Vector2(280f, 44f), new Color(0.24f, 0.42f, 0.22f, 0.96f), Color.white, 16, () =>
                {
                    string msg = AssetHelperBridge.UnlockAllGodhomeStatuesAndDoors();
                    if (_uGuiModConsoleStatusText != null) _uGuiModConsoleStatusText.text = msg;
                    RebuildUGuiModConsoleContent();
                });
            }
        }

        private void BuildUGuiProfileTabContent()
        {
            IrisProfileManager.EnsureLoaded();
            IrisProfileManager.ComputeSyncState(
                out bool isSynced,
                out List<IrisProfileModEntry> missingFromDevice,
                out List<InstalledModItem> extraOnDevice,
                out List<string> unifiedRows);

            // 1. Top Row: Profile Name Box + Edit Name + Sync Installed Mods to Profile (aligned cleanly inside [-680, +680])
            CreateUGuiLabel(
                _uGuiModConsoleContentRoot,
                $"PROFILE NAME:  {IrisProfileManager.CurrentProfileName}",
                new Vector2(-460f, 278f),
                new Vector2(440f, 46f),
                20,
                new Color(0.98f, 0.90f, 0.68f, 1f),
                TextAnchor.MiddleLeft);

            CreateUGuiButton(
                _uGuiModConsoleContentRoot,
                "Prof_EditName",
                "Edit Name",
                new Vector2(-150f, 278f),
                new Vector2(155f, 44f),
                new Color(0.22f, 0.28f, 0.42f, 0.96f),
                Color.white,
                16,
                () => OpenProfileNameKeyboard());

            CreateUGuiButton(
                _uGuiModConsoleContentRoot,
                "Prof_SyncNow",
                "Save Device Mods -> Profile",
                new Vector2(90f, 278f),
                new Vector2(290f, 44f),
                new Color(0.18f, 0.36f, 0.26f, 0.96f),
                Color.white,
                16,
                () =>
                {
                    IrisProfileManager.SyncDeviceModsToProfile();
                    if (_uGuiModConsoleStatusText != null)
                        _uGuiModConsoleStatusText.text = IrisProfileManager.LastStatusMessage;
                    RebuildUGuiModConsoleContent();
                });

            CreateUGuiButton(
                _uGuiModConsoleContentRoot,
                "Prof_Export",
                "Export Profile",
                new Vector2(355f, 278f),
                new Vector2(195f, 44f),
                new Color(0.28f, 0.22f, 0.38f, 0.96f),
                Color.white,
                16,
                () =>
                {
                    IrisProfileManager.ExportProfileWithFileManager(msg =>
                    {
                        if (_uGuiModConsoleStatusText != null)
                            _uGuiModConsoleStatusText.text = msg;
                        RebuildUGuiModConsoleContent();
                    });
                });

            CreateUGuiButton(
                _uGuiModConsoleContentRoot,
                "Prof_Import",
                "Import Profile",
                new Vector2(570f, 278f),
                new Vector2(195f, 44f),
                new Color(0.38f, 0.24f, 0.16f, 0.96f),
                Color.white,
                16,
                () =>
                {
                    IrisProfileManager.ImportProfileWithFileManager(msg =>
                    {
                        if (_uGuiModConsoleStatusText != null)
                            _uGuiModConsoleStatusText.text = msg;
                        RebuildUGuiModConsoleContent();
                    });
                });

            // 2. Sync Status Banner
            string syncMsg = isSynced
                ? $"[SYNCED] Mods synced with profile '{IrisProfileManager.CurrentProfileName}' ({IrisProfileManager.ProfileMods.Count} mod(s) matched)."
                : $"[NOT SYNCED] Mods not synced with profile (Missing on phone: {missingFromDevice.Count} | Extra on phone: {extraOnDevice.Count}).";
            Color syncCol = isSynced ? new Color(0.45f, 0.96f, 0.62f, 1f) : new Color(1f, 0.72f, 0.38f, 1f);
            CreateUGuiLabel(
                _uGuiModConsoleContentRoot,
                syncMsg,
                new Vector2(0f, 228f),
                new Vector2(1360f, 34f),
                17,
                syncCol,
                TextAnchor.MiddleLeft);

            // 3. Missing Mods Prompt + 3-Second Hold-to-Download Option with Filling Animation
            float listTopY = 175f;
            int perPage = 6;
            if (missingFromDevice.Count > 0)
            {
                listTopY = 105f;
                perPage = 5;

                string missingPrompt = IrisProfileManager.IsDownloadingMissingMods
                    ? $"Downloading missing profile mods & dependencies... ({IrisProfileManager.DownloadStatusText})"
                    : $"Missing {missingFromDevice.Count} mod(s) from the profile — do you want to download them?";
                CreateUGuiLabel(
                    _uGuiModConsoleContentRoot,
                    missingPrompt,
                    new Vector2(0f, 186f),
                    new Vector2(1360f, 30f),
                    17,
                    new Color(1f, 0.86f, 0.45f, 1f),
                    TextAnchor.MiddleCenter);

                CreateUGuiHoldDownloadButton(
                    _uGuiModConsoleContentRoot,
                    new Vector2(0f, 142f),
                    new Vector2(960f, 46f),
                    missingFromDevice.Count);
            }

            // 4. Paginated Profile & Device Mod Sync List
            int totalPages = Mathf.Max(1, (unifiedRows.Count + perPage - 1) / perPage);
            _uGuiPageIndex = Mathf.Clamp(_uGuiPageIndex, 0, totalPages - 1);
            int start = _uGuiPageIndex * perPage;

            if (unifiedRows.Count == 0)
            {
                CreateUGuiLabel(
                    _uGuiModConsoleContentRoot,
                    "No mods recorded in profile yet. Tap 'Save Device Mods -> Profile' or 'Import Profile'.",
                    new Vector2(0f, 40f),
                    new Vector2(1200f, 44f),
                    18,
                    new Color(0.75f, 0.75f, 0.82f, 1f),
                    TextAnchor.MiddleCenter);
            }
            else
            {
                for (int i = 0; i < perPage && (start + i) < unifiedRows.Count; i++)
                {
                    string rowText = unifiedRows[start + i];
                    float y = listTopY - i * 56f;
                    Color rCol = Color.white;
                    if (rowText.StartsWith("[SYNCED]", StringComparison.Ordinal))
                        rCol = new Color(0.62f, 0.96f, 0.72f, 1f);
                    else if (rowText.StartsWith("[MISSING", StringComparison.Ordinal))
                        rCol = new Color(1f, 0.62f, 0.58f, 1f);
                    else if (rowText.StartsWith("[EXTRA", StringComparison.Ordinal))
                        rCol = new Color(0.98f, 0.86f, 0.52f, 1f);

                    CreateUGuiLabel(
                        _uGuiModConsoleContentRoot,
                        rowText,
                        new Vector2(0f, y),
                        new Vector2(1340f, 46f),
                        17,
                        rCol,
                        TextAnchor.MiddleLeft);
                }
            }

            // 5. Pagination & Profile Path Footer
            CreateUGuiLabel(
                _uGuiModConsoleContentRoot,
                $"Stored at:  {IrisProfileManager.ProfileFilePath}   (also mirrored in BepInEx/config/iris_profile.jsonc)",
                new Vector2(0f, -232f),
                new Vector2(1360f, 30f),
                15,
                new Color(0.72f, 0.86f, 0.98f, 1f),
                TextAnchor.MiddleCenter);

            if (_uGuiModConsoleStatusText != null && (_uGuiModConsoleStatusText.text == "Ready." || string.IsNullOrEmpty(_uGuiModConsoleStatusText.text)))
            {
                _uGuiModConsoleStatusText.text = $"Profile File: {IrisProfileManager.ProfileFilePath}";
            }

            CreateUGuiButton(_uGuiModConsoleContentRoot, "ProfPrevPage", "< Prev Page", new Vector2(-260f, -275f), new Vector2(200f, 44f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 16, () =>
            {
                _uGuiPageIndex = Mathf.Max(0, _uGuiPageIndex - 1);
                RebuildUGuiModConsoleContent();
            });
            CreateUGuiLabel(_uGuiModConsoleContentRoot, $"Page {_uGuiPageIndex + 1} / {totalPages} ({unifiedRows.Count} entries)", new Vector2(0f, -275f), new Vector2(280f, 44f), 16, Color.white, TextAnchor.MiddleCenter);
            CreateUGuiButton(_uGuiModConsoleContentRoot, "ProfNextPage", "Next Page >", new Vector2(260f, -275f), new Vector2(200f, 44f), new Color(0.22f, 0.22f, 0.28f, 0.95f), Color.white, 16, () =>
            {
                _uGuiPageIndex = Mathf.Min(totalPages - 1, _uGuiPageIndex + 1);
                RebuildUGuiModConsoleContent();
            });
        }

        private void CreateUGuiHoldDownloadButton(Transform parent, Vector2 pos, Vector2 size, int missingCount)
        {
            var go = new GameObject("HoldDownloadMissingBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var bgImg = go.GetComponent<UnityEngine.UI.Image>();
            bgImg.color = new Color(0.12f, 0.14f, 0.20f, 0.98f);
            bgImg.raycastTarget = true;

            // Filling animation bar (expands from left to right over 3.0 seconds while held)
            var fillGo = new GameObject("HoldFillBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            fillGo.transform.SetParent(go.transform, false);
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.anchoredPosition = Vector2.zero;
            fillRt.sizeDelta = new Vector2(0f, 0f);
            var fillImg = fillGo.GetComponent<UnityEngine.UI.Image>();
            fillImg.color = new Color(0.22f, 0.62f, 0.38f, 0.95f);
            fillImg.raycastTarget = false;

            var lbl = CreateUGuiLabel(
                go.transform,
                IrisProfileManager.IsDownloadingMissingMods
                    ? $"DOWNLOADING MISSING MODS... ({IrisProfileManager.DownloadStatusText})"
                    : $"HOLD FOR 3 SECONDS TO DOWNLOAD {missingCount} MISSING MOD(S) + DEPENDENCIES",
                Vector2.zero,
                size,
                16,
                Color.white,
                TextAnchor.MiddleCenter);

            var holdComp = go.AddComponent<IrisProfileHoldDownloadBehavior>();
            holdComp.FillRect = fillRt;
            holdComp.MaxWidth = size.x;
            holdComp.LabelText = lbl;
            holdComp.MissingCount = missingCount;
            holdComp.OnHoldCompleted = () =>
            {
                IrisProfileManager.StartDownloadMissingMods(() =>
                {
                    if (_uGuiModConsoleStatusText != null)
                        _uGuiModConsoleStatusText.text = IrisProfileManager.LastStatusMessage;
                    RebuildUGuiModConsoleContent();
                });
            };
        }

        internal void SetShowModPanel(bool show, bool fromPluginLoader = false)
        {
            if (_inSetShowModPanel) return;
            _inSetShowModPanel = true;
            try
            {
                _showModPanel = show;
                LogDiagnosticInfo($"[UI] SetShowModPanel(show={show}, fromPluginLoader={fromPluginLoader}, tab='{_selectedModTab}')");
                EnsureFloatingModButtonCreated();
                if (show)
                {
                    EnsureUGuiModConsoleBuilt();
                    InGameModManager.Instance?.ScanInstalledMods();
                    RefreshDiscoveredModConfigs();
                    RebuildUGuiModConsoleContent();
                }
                if (_uGuiModConsolePanelGo != null)
                {
                    _uGuiModConsolePanelGo.SetActive(show);
                }
                if (!show && !fromPluginLoader)
                {
                    try
                    {
                        BepInEx.PluginLoader.SetEditorVisible("SilksongMobileAPI", false);
                    }
                    catch (Exception ex)
                    {
                        LogApiCatch("SilksongMobileAPIPlugin", "SetShowModPanel.SetEditorVisible", ex);
                    }
                }
            }
            finally
            {
                _inSetShowModPanel = false;
            }
        }

        private void Update()
        {
            BepInEx.MobileVirtualInput.EnsureFrameState();
            if (!_hasScannedAndLoaded)
            {
                TriggerDeferredScanAndLoad("MonoBehaviour.Update");
            }

            // Watchdog: Ensure Native In-Game "Mods" button is injected into Main Menu (throttled check)
            try
            {
                if (!NativeModMenu.HasInjectedMainMenu && UIManager._instance != null && UIManager._instance.mainMenuButtons != null)
                {
                    NativeModMenu.Inject(UIManager._instance);
                }
            }
            catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1397", __apiEx_12); }

            // Watchdog: Restore Inventory Border & active pane NestedFadeGroupBase.AlphaSelf when stuck at 0.00 while invFSM=='Opened', and reset back to 0.00 on close (O(1) when closed!)
            bool isInvOpenNow = PlayerData.HasInstance && PlayerData.instance != null && PlayerData.instance.isInventoryOpen;
            if (isInvOpenNow)
            {
                _wasInventoryOpenByIris = true;
                EnsureOpenInventoryVisible();
            }
            else if (_wasInventoryOpenByIris)
            {
                _wasInventoryOpenByIris = false;
                RestoreClosedInventoryAlpha();
            }

            // Watchdog: Ensure Hornet's native renderer is never left invisible by any mod (cached Renderer — zero per-frame GetComponent!)
            var hc = HeroController.SilentInstance;
            if (hc != null && !hc.cState.dead)
            {
                if (!ReferenceEquals(hc, _cachedHero))
                {
                    _cachedHero = hc;
                    _cachedHeroRenderer = hc.GetComponent<Renderer>();
                    HarmonyBridge.EnsureCrossStitchMadnessAndEternalWeaverRuntime(hc);
                }
                if (_cachedHeroRenderer != null && !_cachedHeroRenderer.enabled)
                {
                    _cachedHeroRenderer.enabled = true;
                }
                AssetHelperBridge.PrewarmWeaverHeroCombat(hc);
            }

            if (Time.unscaledTime >= _nextI18nSyncTime)
            {
                _nextI18nSyncTime = Time.unscaledTime + 4.0f;
                HarmonyBridge.SyncModLocalizationIntoGame();
                HarmonyBridge.EnsureHeroConfigCanPlayNeedolin(HeroController.SilentInstance);
                HarmonyBridge.EnsureCrossStitchMadnessAndEternalWeaverRuntime(HeroController.SilentInstance);
                if (!_autoSyncedGameplayModSkills && PlayerData.HasInstance && PlayerData.instance != null && hc != null)
                {
                    _autoSyncedGameplayModSkills = true;
                    try { HarmonyBridge.ReplayMissedSingletonHooksForMod("PharloomFishing"); } catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1425", __apiEx_13); }
                    try { UnlockWeaverCrestProgressionAndSkills(); } catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1426", __apiEx_14); }
                }
            }
        }

        public static bool ToggleStitchworkGui()
        {
            try
            {
                if (TogglePatchworkSkinMenu(null))
                    return true;
                var cpmType = HarmonyBridge.ResolveTypeRobust("Stitchwork.GUI.CanvasPackManager");
                if (cpmType != null)
                {
                    var toggleMi = cpmType.GetMethod("Toggle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (toggleMi != null)
                    {
                        toggleMi.Invoke(null, null);
                        var edgeType = HarmonyBridge.ResolveTypeRobust("Stitchwork.GUI.EdgeTabShell");
                        var revealMi = edgeType?.GetMethod("Reveal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { revealMi?.Invoke(null, null); } catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1446", __apiEx_15); }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[StitchworkUI] Toggle failed: {ex.Message}");
            }
            return false;
        }

        public static int UnlockWeaverCrestProgressionAndSkills()
        {
            int unlockedSkills = 0;
            try
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    string[] weaverFlags = new[]
                    {
                        "Weaver_Learned_Blink",
                        "Weaver_Learned_AirSpin",
                        "Weaver_Learned_Backstep",
                        "Weaver_Learned_SilkWaltz",
                        "Weaver_Learned_PhantomDive",
                        "Weaver_ThreadNeedle_Found"
                    };
                    foreach (var flag in weaverFlags)
                    {
                        try
                        {
                            pd.SetToolData(flag, new ToolItemsData.Data
                            {
                                IsUnlocked = true,
                                IsHidden = true,
                                HasBeenSeen = true,
                                HasBeenSelected = true
                            });
                        }
                        catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1487", __apiEx_16); }
                    }
                    try
                    {
                        if (pd.Relics != null)
                        {
                            pd.Relics.SetData("Weaver_OtherworldRecord", new CollectableRelicsData.Data
                            {
                                IsCollected = true,
                                IsDeposited = true,
                                HasSeenInRelicBoard = true
                            });
                        }
                    }
                    catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1501", __apiEx_17); }
                }

                var mgr = ManagerSingleton<ToolItemManager>.Instance;
                foreach (var asm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    string asmName = asm.GetName().Name ?? "";
                    if (asmName.IndexOf("Weaver_Crest", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            var chapelType = asm.GetType("Weaver_Crest.Patches.Chapel", false);
                            var grantMi = chapelType?.GetMethod("GrantCrest", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (grantMi != null)
                            {
                                grantMi.Invoke(null, null);
                                unlockedSkills++;
                            }
                        }
                        catch (System.Exception __apiEx_18) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1520", __apiEx_18); }
                    }
                    if (asmName.IndexOf("WeaverCrest", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var featType = asm.GetType("WeaverCrestMod.WeaverFeatures", false);
                    if (featType != null)
                    {
                        try { featType.GetField("CocoonShell", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, true); } catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1527", __apiEx_19); }
                        try { featType.GetField("SacredHeart", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, true); } catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1528", __apiEx_20); }
                    }

                    var progType = asm.GetType("WeaverCrestMod.WeaverProgression", false);
                    if (progType != null)
                    {
                        var markNeedle = progType.GetMethod("MarkThreadNeedleFound", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        try { markNeedle?.Invoke(null, null); } catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1535", __apiEx_21); }
                    }

                    string[] skillTypeNames = new[]
                    {
                        "WeaverCrestMod.WeaverBrutalSmashSkill",
                        "WeaverCrestMod.WeaverCocoonSkill",
                        "WeaverCrestMod.WeaverCrossSlashSkill",
                        "WeaverCrestMod.WeaverPhantomDiveSkill",
                        "WeaverCrestMod.WeaverSacredHeart",
                        "WeaverCrestMod.WeaverThreadNeedle",
                        "WeaverCrestMod.WeaverCrestRegistry"
                    };
                    foreach (var tn in skillTypeNames)
                    {
                        var st = asm.GetType(tn, false);
                        if (st == null) continue;
                        if (mgr != null)
                        {
                            var regMi = st.GetMethod("Register", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(ToolItemManager) }, null);
                            try { regMi?.Invoke(null, new object[] { mgr }); } catch (System.Exception __apiEx_22) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1555", __apiEx_22); }
                        }
                        var unlMi = st.GetMethod("EnsureUnlocked", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (unlMi != null)
                        {
                            try { unlMi.Invoke(null, null); unlockedSkills++; } catch (System.Exception __apiEx_23) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1560", __apiEx_23); }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_24) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1565", __apiEx_24); }
            return unlockedSkills;
        }

        public static string UnlockAllCrestSlotsAndModSkills()
        {
            int unlockedCrests = 0;
            int unlockedSlots = 0;
            int unlockedSkills = 0;
            try
            {
                ToolItemManager.UnlockAllCrests();
                ToolItemManager.UnlockAllTools();
            }
            catch (System.Exception __apiEx_25) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1579", __apiEx_25); }

            try
            {
                var pd = PlayerData.instance;
                var allCrests = ToolItemManager.GetAllCrests();
                if (pd != null && pd.ToolEquips != null && allCrests != null)
                {
                    foreach (var crest in allCrests)
                    {
                        if (crest == null || string.IsNullOrEmpty(crest.name)) continue;
                        try { crest.Unlock(); } catch (System.Exception __apiEx_26) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1590", __apiEx_26); }
                        try
                        {
                            var cData = pd.ToolEquips.GetData(crest.name);
                            cData.IsUnlocked = true;
                            if (cData.Slots == null)
                                cData.Slots = new List<ToolCrestsData.SlotData>();

                            bool isWeaverCrest = string.Equals(crest.name, "Weaver_Codex", StringComparison.Ordinal);
                            int targetSlotCount = crest.Slots != null ? crest.Slots.Length : cData.Slots.Count;
                            while (cData.Slots.Count < targetSlotCount)
                            {
                                int nextIdx = cData.Slots.Count;
                                bool shouldUnlock = true;
                                cData.Slots.Add(new ToolCrestsData.SlotData { IsUnlocked = shouldUnlock, EquippedTool = string.Empty });
                            }

                            for (int i = 0; i < cData.Slots.Count; i++)
                            {
                                var sd = cData.Slots[i];
                                bool desiredUnlock = true;
                                if (sd.IsUnlocked != desiredUnlock)
                                {
                                    sd.IsUnlocked = desiredUnlock;
                                    cData.Slots[i] = sd;
                                    if (desiredUnlock) unlockedSlots++;
                                }
                            }

                            pd.ToolEquips.SetData(crest.name, cData);
                            unlockedCrests++;
                        }
                        catch (System.Exception __apiEx_27) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1622", __apiEx_27); }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[UnlockHelper] Crest slot unlock warning: {ex.Message}");
            }

            // Also unlock all Silk Skills (ToolItemType.Skill) & Tools registered in ToolItemManager
            try
            {
                var allTools = ToolItemManager.GetAllTools();
                if (allTools != null)
                {
                    foreach (var tool in allTools)
                    {
                        if (tool == null) continue;
                        try
                        {
                            if (!tool.IsUnlocked)
                            {
                                tool.Unlock(null, ToolItem.PopupFlags.None);
                                unlockedSkills++;
                            }
                        }
                        catch (System.Exception __apiEx_28) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1648", __apiEx_28); }
                    }
                }
            }
            catch (System.Exception __apiEx_29) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1652", __apiEx_29); }

            // Also unlock any SuSul-WeaverCrest custom Silk Skills & Tools if present
            unlockedSkills += UnlockWeaverCrestProgressionAndSkills();

            // Refresh open Inventory / Crest UI if active
            try
            {
                var crestLists = FindObjectsOfType<InventoryToolCrestList>();
                if (crestLists != null)
                {
                    foreach (var cl in crestLists)
                    {
                        if (cl == null) continue;
                        try { cl.SetupCrests(); } catch (System.Exception __apiEx_30) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1666", __apiEx_30); }
                    }
                }
                var toolMgrs = FindObjectsOfType<InventoryItemToolManager>();
                if (toolMgrs != null)
                {
                    foreach (var tm in toolMgrs)
                    {
                        if (tm == null) continue;
                        try { tm.RefreshTools(true, true); } catch (System.Exception __apiEx_31) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1675", __apiEx_31); }
                    }
                }
                ToolItemManager.SendEquippedChangedEvent(true);
            }
            catch (System.Exception __apiEx_32) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1680", __apiEx_32); }

            string msg = $"Unlocked all slots ({unlockedSlots} newly unlocked across {unlockedCrests} crests" +
                         (unlockedSkills > 0 ? $", {unlockedSkills} custom mod skills" : "") + "). Re-open Crest menu to view!";
            LogDiagnosticInfo($"[UnlockHelper] {msg}");
            return msg;
        }

        public static string GrantFourMemoryLockets()
        {
            try
            {
                CollectableItem locketItem = null;
                var toolMgrs = FindObjectsOfType<InventoryItemToolManager>();
                if (toolMgrs != null && toolMgrs.Length > 0 && toolMgrs[0] != null)
                {
                    locketItem = toolMgrs[0].SlotUnlockItem;
                }
                if (locketItem == null)
                {
                    locketItem = CollectableItemManager.GetItemByName("Tool Metal")
                              ?? CollectableItemManager.GetItemByName("Memory Locket")
                              ?? CollectableItemManager.GetItemByName("Crest Socket Unlock");
                }
                if (locketItem != null)
                {
                    CollectableItemManager.AddItem(locketItem, 4);
                    string msg = $"Added +4 '{locketItem.name}' (Memory Lockets) to inventory!";
                    LogDiagnosticInfo($"[UnlockHelper] {msg}");
                    return msg;
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[UnlockHelper] GrantFourMemoryLockets warning: {ex.Message}");
            }
            return UnlockAllCrestSlotsAndModSkills();
        }

        public static void TriggerDeferredScanAndLoad(string triggerSource)
        {
            if (_hasScannedAndLoaded) return;
            _hasScannedAndLoaded = true;
            try
            {
                LogDiagnosticInfo($"Scanning and loading PC Mods (triggered by {triggerSource})...");
                ScanAndLoadPCMods();
            }
            catch (Exception ex)
            {
                LogDiagnosticError($"[ScanAndLoadPCMods] Fatal error: {ex}");
            }
        }

        public static string GetModGuidForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            lock (_modGuidByFolder)
            {
                if (_modGuidByFolder.TryGetValue(folder, out var g)) return g;
            }
            return null;
        }

        public static bool HotLoadInstalledModDirectory(string modDir)
        {
            try
            {
                if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
                    return false;
                ScanAndLoadPCMods();
                HarmonyBridge.SyncModLocalizationIntoGame();
                return true;
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[HotLoad] Failed to hot-load mod from '{modDir}': {ex.Message}");
                return false;
            }
        }

        public static void PreOrganizeTopLevelPcModDlls()
        {
            try
            {
                string pluginsRoot = Paths.PluginPath;
                if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot)) return;

                foreach (var modDir in Directory.GetDirectories(pluginsRoot))
                {
                    string dirName = Path.GetFileName(modDir) ?? "";
                    if (File.Exists(Path.Combine(modDir, "disabled.txt")))
                    {
                        foreach (var dllToDisable in Directory.GetFiles(modDir, "*.dll", SearchOption.AllDirectories))
                        {
                            try
                            {
                                string disPath = dllToDisable + ".disabled";
                                if (File.Exists(disPath)) File.Delete(disPath);
                                File.Move(dllToDisable, disPath);
                                LogDiagnosticInfo($"[PreOrganize] Disabled '{Path.GetFileName(dllToDisable)}' in '{dirName}' due to disabled.txt.");
                            }
                            catch (System.Exception __apiEx_33) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1782", __apiEx_33); }
                        }
                        continue;
                    }
                    if (_knownNativeMobileFolders.Contains(dirName))
                    {
                        try
                        {
                            string disabledPj = Path.Combine(modDir, "plugin.json.disabled_architect");
                            string activePj = Path.Combine(modDir, "plugin.json");
                            if (File.Exists(disabledPj))
                            {
                                if (File.Exists(activePj)) File.Delete(disabledPj);
                                else File.Move(disabledPj, activePj);
                                LogDiagnosticInfo($"[PreOrganize] Restored native mobile plugin.json in '{dirName}'.");
                            }
                            string subPluginDir = Path.Combine(modDir, "plugins");
                            if (Directory.Exists(subPluginDir))
                            {
                                foreach (var subDll in Directory.GetFiles(subPluginDir, "*.dll", SearchOption.TopDirectoryOnly))
                                {
                                    string fn = Path.GetFileName(subDll);
                                    string topTarget = Path.Combine(modDir, fn);
                                    if (!File.Exists(topTarget))
                                    {
                                        File.Move(subDll, topTarget);
                                        LogDiagnosticInfo($"[PreOrganize] Restored native mobile DLL '{fn}' to top-level of '{dirName}'.");
                                    }
                                }
                            }
                        }
                        catch (System.Exception __apiEx_34) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1813", __apiEx_34); }
                        continue;
                    }
                    // Auto-stage nested Patchwork & SilksongCustomizer asset packs (Batch 01 030..058)
                    try
                    {
                        StagePatchworkAndCustomizerAssetPacks(pluginsRoot, modDir, dirName);
                    }
                    catch (System.Exception __apiEx_35a) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "StagePatchworkAndCustomizerAssetPacks", __apiEx_35a); }

                    if (File.Exists(Path.Combine(modDir, "plugin.json"))) continue;
                    if (IsArchitectDependentModDir(modDir)) continue;

                    try
                    {
                        var topDlls = Directory.GetFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly);
                        foreach (var dllPath in topDlls)
                        {
                            string fileName = Path.GetFileName(dllPath);
                            string subPluginDir = Path.Combine(modDir, "plugins");
                            Directory.CreateDirectory(subPluginDir);
                            string movedPath = Path.Combine(subPluginDir, fileName);
                            if (!File.Exists(movedPath))
                                File.Move(dllPath, movedPath);
                            else
                                File.Copy(dllPath, movedPath, overwrite: true);
                            if (File.Exists(movedPath) && File.Exists(dllPath) && !string.Equals(movedPath, dllPath, StringComparison.OrdinalIgnoreCase))
                                File.Delete(dllPath);
                            LogDiagnosticInfo($"[PreOrganize] Moved top-level PC Mod DLL '{fileName}' in '{dirName}' into 'plugins/{fileName}' before Mobile BepInEx Phase 1 scans it.");
                        }
                    }
                    catch (System.Exception __apiEx_35) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1837", __apiEx_35); }
                }
            }
            catch (System.Exception __apiEx_36) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1840", __apiEx_36); }
        }

        private static void CopyDirectoryContentsNonDestructive(string sourceDir, string targetDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                if (!File.Exists(destFile))
                {
                    try { File.Copy(file, destFile, false); } catch { }
                }
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string subName = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(subName)) continue;
                CopyDirectoryContentsNonDestructive(subDir, Path.Combine(targetDir, subName));
            }
        }

        private static void NormalizeBackslashExtractedFiles(string modDir)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(modDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string fn = Path.GetFileName(file) ?? "";
                    if (fn.IndexOf('\\') < 0) continue;
                    string rel = fn.Replace('\\', '/').TrimStart('/');
                    if (rel.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring("BepInEx/plugins/".Length);
                    // Strip leading duplicate mod folder name if present
                    int slashIdx = rel.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string firstSeg = rel.Substring(0, slashIdx);
                        string dirShort = Path.GetFileName(modDir) ?? "";
                        if (dirShort.IndexOf(firstSeg, StringComparison.OrdinalIgnoreCase) >= 0)
                            rel = rel.Substring(slashIdx + 1);
                    }
                    if (string.IsNullOrEmpty(rel) || rel.EndsWith("/"))
                    {
                        try { File.Delete(file); } catch { }
                        continue;
                    }
                    string destPath = Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    string destParent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destParent))
                        Directory.CreateDirectory(destParent);
                    if (!File.Exists(destPath))
                        File.Move(file, destPath);
                    else
                        File.Delete(file);
                }
            }
            catch { }
        }

        private static void DisableBlockedStitchworkDllsInDir(string modDir, string dirName)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return;
            try
            {
                foreach (var dll in Directory.GetFiles(modDir, "*.dll", SearchOption.AllDirectories))
                {
                    string shortName = Path.GetFileNameWithoutExtension(dll) ?? "";
                    if (string.Equals(shortName, "Stitchwork", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(shortName, "StitchworkWiki", StringComparison.OrdinalIgnoreCase))
                    {
                        string disPath = dll + ".disabled";
                        if (File.Exists(disPath)) File.Delete(disPath);
                        File.Move(dll, disPath);
                        LogDiagnosticInfo($"[StitchworkBlocker] Disabled '{Path.GetFileName(dll)}' in '{dirName}' (Stitchwork/StitchworkWiki runtime is disabled on mobile; Stitchwork skins remain supported via PatchWork).");
                    }
                }
            }
            catch { }
        }

        private static void StagePatchworkAndCustomizerAssetPacks(string pluginsRoot, string modDir, string dirName)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return;
            NormalizeBackslashExtractedFiles(modDir);
            DisableBlockedStitchworkDllsInDir(modDir, dirName);

            if (string.Equals(dirName, "PatchWork", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirName, "Patchwork", StringComparison.OrdinalIgnoreCase))
                return;

            // 1. Detect nested Patchwork / Patchworks / Stitchwork skin & sound folders
            //    (e.g. BepInEx/plugins/Patchwork, Patchworks, Teto Voice/Stitchwork, plugins/VoidHornet/Stitchwork)
            string pwTargetRoot = Path.Combine(pluginsRoot, "PatchWork");
            foreach (var subDir in Directory.GetDirectories(modDir, "*", SearchOption.AllDirectories))
            {
                string subName = Path.GetFileName(subDir) ?? "";
                if (string.Equals(subName, "Patchwork", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(subName, "Patchworks", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(subName, "Stitchwork", StringComparison.OrdinalIgnoreCase))
                {
                    CopyDirectoryContentsNonDestructive(subDir, pwTargetRoot);
                    LogDiagnosticInfo($"[AssetPackStager] Staged nested '{subName}' pack from '{dirName}' into '{pwTargetRoot}'.");
                }
            }

            // 2. Detect SilksongCustomizer / CustomizerT2D packs (active.txt or Texture2D/*.png or *Cln/atlas*.png)
            var activeTxtFiles = Directory.GetFiles(modDir, "active.txt", SearchOption.AllDirectories);
            var tex2dDirs = Directory.GetDirectories(modDir, "Texture2D", SearchOption.AllDirectories);
            if (activeTxtFiles.Length > 0 || tex2dDirs.Length > 0)
            {
                string bepRoot = Paths.BepInExRootPath;
                string parentRoot = !string.IsNullOrEmpty(bepRoot) ? (Path.GetDirectoryName(bepRoot) ?? bepRoot) : pluginsRoot;
                string customizerRoot = Path.Combine(parentRoot, "Mods", "Customizer", dirName);
                CopyDirectoryContentsNonDestructive(modDir, customizerRoot);

                // Also copy any '<SpriteCollection Cln>/atlas*.png' folders into PatchWork/Spritesheets/ and Texture2D/*.png into PatchWork/Sprites/T2D/
                foreach (var t2dDir in tex2dDirs)
                {
                    CopyDirectoryContentsNonDestructive(t2dDir, Path.Combine(pwTargetRoot, "Sprites", "T2D"));
                }
                foreach (var clnDir in Directory.GetDirectories(modDir, "*Cln*", SearchOption.AllDirectories))
                {
                    if (Directory.GetFiles(clnDir, "atlas*.png", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        string clnName = Path.GetFileName(clnDir);
                        CopyDirectoryContentsNonDestructive(clnDir, Path.Combine(pwTargetRoot, "Spritesheets", clnName));
                    }
                }
            }
        }

        private static string ResolveBestAssemblyLocation(string modDir, string effectiveDllPath)
        {
            string fileName = Path.GetFileName(effectiveDllPath);
            try
            {
                string subPlugins = Path.Combine(modDir, "plugins");
                // If plugins/ itself has companion subdirectories (like plugins/languages in CustomTranslation), keep effectiveDllPath!
                if (Directory.Exists(subPlugins))
                {
                    var pluginSubDirs = Directory.GetDirectories(subPlugins)
                        .Where(d => !(Path.GetFileName(d) ?? "").StartsWith(".cecil_cache", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (pluginSubDirs.Length > 0)
                    {
                        foreach (var d in pluginSubDirs)
                        {
                            if (Directory.Exists(Path.Combine(d, "Assets")) ||
                                Directory.Exists(Path.Combine(d, "Audio")) ||
                                Directory.Exists(Path.Combine(d, "Sprites")))
                            {
                                return Path.Combine(d, fileName);
                            }
                        }
                        return effectiveDllPath;
                    }
                }

                // If effectiveDllPath was moved into modDir/plugins/ by PreOrganize, check if modDir has sibling asset folders or data files
                foreach (var topSubDir in Directory.GetDirectories(modDir))
                {
                    string sdName = Path.GetFileName(topSubDir) ?? "";
                    if (!string.Equals(sdName, "plugins", StringComparison.OrdinalIgnoreCase) &&
                        !sdName.StartsWith(".cecil_cache", StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.Combine(modDir, fileName);
                    }
                }

                if (Directory.GetFiles(modDir, "*.assetbundle", SearchOption.TopDirectoryOnly).Length > 0 ||
                    Directory.GetFiles(modDir, "*.bundle", SearchOption.TopDirectoryOnly).Length > 0 ||
                    Directory.GetFiles(modDir, "*.png", SearchOption.TopDirectoryOnly).Length > 0 ||
                    Directory.GetFiles(modDir, "*.wav", SearchOption.TopDirectoryOnly).Length > 0 ||
                    Directory.GetFiles(modDir, "*.ogg", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return Path.Combine(modDir, fileName);
                }
            }
            catch (System.Exception __apiEx_37) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1873", __apiEx_37); }
            return effectiveDllPath;
        }

        private static void DisableArchitectNativePluginsIfPresent()
        {
            try
            {
                string pluginsRoot = Paths.PluginPath;
                if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot)) return;
                foreach (var dir in Directory.GetDirectories(pluginsRoot))
                {
                    if (!IsArchitectDependentModDir(dir)) continue;
                    string pj = Path.Combine(dir, "plugin.json");
                    if (File.Exists(pj))
                    {
                        try
                        {
                            string target = pj + ".disabled_architect";
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(pj, target);
                        }
                        catch (System.Exception __apiEx_38) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1895", __apiEx_38); }
                    }
                }
                foreach (var mb in FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string an = mb.GetType().Assembly?.GetName()?.Name ?? "";
                    if (IsExcludedModIdentifier(an))
                    {
                        mb.enabled = false;
                        Destroy(mb);
                    }
                }
            }
            catch (System.Exception __apiEx_39) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1909", __apiEx_39); }
        }

        private static void EnsureMissedNativeMobilePluginsLoaded()
        {
            try
            {
                string pluginsRoot = Paths.PluginPath;
                if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot)) return;
                var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    string n = a?.GetName()?.Name;
                    if (!string.IsNullOrEmpty(n)) loadedNames.Add(n);
                }
                foreach (var modDir in Directory.GetDirectories(pluginsRoot))
                {
                    string dirName = Path.GetFileName(modDir) ?? "";
                    if (!_knownNativeMobileFolders.Contains(dirName)) continue;
                    if (string.Equals(dirName, "00_IRIS", StringComparison.OrdinalIgnoreCase) || string.Equals(dirName, "00_SilksongMobileAPI", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(Path.Combine(modDir, "disabled.txt"))) continue;

                    var topDlls = Directory.GetFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly);
                    foreach (var dllPath in topDlls)
                    {
                        string shortName = Path.GetFileNameWithoutExtension(dllPath);
                        if (loadedNames.Contains(shortName)) continue;
                        try
                        {
                            var asm = Assembly.LoadFrom(dllPath);
                            loadedNames.Add(shortName);
                            _loadedModDllNames.Add(shortName);
                            var bootGoType = Type.GetType("BepInEx.Boot, BepInEx") ?? Type.GetType("BepInEx.Bootstrap.Chainloader, BepInEx");
                            var bootGo = (bootGoType?.GetProperty("GameObject", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null) as GameObject) ?? Instance?.gameObject;
                            foreach (var t in asm.GetTypes())
                            {
                                if (t != null && !t.IsAbstract && typeof(BaseUnityPlugin).IsAssignableFrom(t))
                                {
                                    if (FindObjectOfType(t) == null)
                                    {
                                        if (bootGo != null) bootGo.AddComponent(t);
                                        else Instance?.gameObject.AddComponent(t);
                                        LogDiagnosticInfo($"[NativeMobileRecovery] Loaded & attached missed native mobile plugin '{t.FullName}' from '{dirName}/{Path.GetFileName(dllPath)}'.");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDiagnosticWarn($"[NativeMobileRecovery] Could not load '{dllPath}': {ex.Message}");
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_40) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L1963", __apiEx_40); }
        }

        private static void ScanAndLoadPCMods()
        {
            PreOrganizeTopLevelPcModDlls();
            DisableArchitectNativePluginsIfPresent();
            EnsureMissedNativeMobilePluginsLoaded();

            var candidates = new List<DiscoveredPcModDll>();

            string pluginsRoot = Paths.PluginPath;
            if (Directory.Exists(pluginsRoot))
            {
                DiscoverCandidatesInRoot(pluginsRoot, isDedicatedPcModsFolder: false, candidates);
            }

            string pcModsRoot = Path.Combine(Paths.BepInExRootPath, "pc_mods");
            if (Directory.Exists(pcModsRoot))
            {
                DiscoverCandidatesInRoot(pcModsRoot, isDedicatedPcModsFolder: true, candidates);
            }

            candidates.Sort((a, b) =>
            {
                int cmp = a.PriorityOrder.CompareTo(b.PriorityOrder);
                if (cmp != 0) return cmp;
                return string.Compare(a.AsmShortName, b.AsmShortName, StringComparison.OrdinalIgnoreCase);
            });

            try
            {
                foreach (var loadedAsm in SilksongMobileAPI.HarmonyBridge.GetCachedLoadedAssemblies())
                {
                    DisableRawDobbyPatchesInLoadedAssembly(loadedAsm);
                    string ln = loadedAsm?.GetName()?.Name;
                    if (!string.IsNullOrEmpty(ln))
                        _loadedModDllNames.Add(ln);
                }
                AssetHelperBridge.EnsureRepackerMembersResolved();
                PlayMakerHybridCLRBridge.ReassertLoadActionsHook();
            }
            catch (System.Exception __apiEx_41) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2005", __apiEx_41); }

            foreach (var cand in candidates)
            {
                if (_loadedModDllNames.Contains(cand.AsmShortName))
                {
                    LogDiagnosticInfo($"[PCMod] Skipping '{cand.AsmShortName}' from '{cand.DirName}' (assembly already loaded in AppDomain).");
                    continue;
                }
                _loadedModDllNames.Add(cand.AsmShortName);

                try
                {
                    string effectiveDllPath = cand.DllPath;

                    LogDiagnosticInfo($"Loading PC Mod DLL: {Path.GetFileName(effectiveDllPath)} from {cand.ModDir}");
                    byte[] rawBytes = File.ReadAllBytes(effectiveDllPath);
                    byte[] rewrittenBytes = CecilRuntimeRewriter.RewritePCModDll(rawBytes, effectiveDllPath);

                    var asm = Assembly.Load(rewrittenBytes);
                    HarmonyBridge.InvalidateCachedLoadedAssemblies();
                    string bestLoc = ResolveBestAssemblyLocation(cand.ModDir, effectiveDllPath);
                    HarmonyBridge.RegisterAssemblyLocation(asm, bestLoc);
                    _loadedModNames.Add(cand.AsmShortName);

                    HarmonyBridge.RegisterModFolderTranslations(cand.AsmShortName, cand.ModDir);
                    InstantiatePluginsFromAssembly(asm, bestLoc, cand.ModDir);
                }
                catch (Exception ex)
                {
                    LogModException(cand.DirName, $"LoadMod({cand.AsmShortName})", ex);
                }
            }

            _loadedModNames.Sort(StringComparer.OrdinalIgnoreCase);
            PlayMakerHybridCLRBridge.ReassertLoadActionsHook();
            AssetHelperBridge.BindAllModManagedAssetsFromTemplateCache();
            if (Instance != null && SceneManager.GetActiveScene().IsValid() &&
                string.Equals(SceneManager.GetActiveScene().name, "Menu_Title", StringComparison.OrdinalIgnoreCase))
            {
                try { Instance.StartCoroutine(AssetHelperBridge.RunPreloaderPharloomsGloryBatchExtract()); } catch { }
            }
            HarmonyBridge.BootstrapSSMPOnMainMenuIfReady(_instantiatedPcPlugins);
            RefreshDiscoveredModConfigs();
            try
            {
                IrisProfileManager.EnsureLoaded();
                LogDiagnosticInfo($"[Profile] Active profile ready at: {IrisProfileManager.ProfileFilePath} (mirror: {IrisProfileManager.ConfigMirrorProfileFilePath})");
            }
            catch (Exception exProf)
            {
                LogDiagnosticWarn($"[Profile] Startup profile init notice: {exProf.Message}");
            }
            LogDiagnosticInfo($"IRIS v2.9.7.1 scan complete. Loaded PC Mod assemblies: [{string.Join(", ", _loadedModNames)}]");
                try { AssetHelperBridge.EnsureHkCoreAddressablesSceneBridgeRegistered(); } catch { }
                try { Instance?.SetupButtons(); } catch { }
            FlushAsyncDiagnosticLogs();
        }

        private static void DiscoverCandidatesInRoot(
            string rootDir,
            bool isDedicatedPcModsFolder,
            List<DiscoveredPcModDll> candidates)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(rootDir);
            }
            catch { return; }

            var seenDllShortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in candidates)
                seenDllShortNames.Add(existing.AsmShortName);

            foreach (var modDir in subDirs)
            {
                string dirName = Path.GetFileName(modDir) ?? "";
                if (!isDedicatedPcModsFolder && _knownNativeMobileFolders.Contains(dirName))
                    continue;
                if (File.Exists(Path.Combine(modDir, "plugin.json")))
                    continue;
                if (IsArchitectDependentModDir(modDir))
                {
                    LogDiagnosticInfo($"[PCMod] Ignoring Architect-dependent mod directory '{dirName}'.");
                    continue;
                }

                try
                {
                    var allDlls = Directory.GetFiles(modDir, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".pc_orig", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal) &&
                                    f.IndexOf("__MACOSX", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    f.IndexOf("/Natives/", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    f.IndexOf("\\Natives\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    !Path.GetFileName(f).Equals("Mono.Cecil.dll", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).Equals("BepInEx.dll", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.Length)
                        .ToArray();

                    foreach (var dllPath in allDlls)
                    {
                        string asmShortName = Path.GetFileNameWithoutExtension(dllPath);
                        if (string.Equals(asmShortName, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "FastPreloaderUI", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "FasterPreloaderUI", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "AssetsTools.NET", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "AssetHelperLib", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "Silksong.FsmUtil", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "Silksong.ModMenu", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "Stitchwork", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "StitchworkWiki", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "MonoMod", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "MonoMod.RuntimeDetour.HookGen", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "BepInEx.MonoMod.AutoHookGenPatcher", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(asmShortName, "Hamunii.DetourContext.Dispose_Fix", StringComparison.OrdinalIgnoreCase) ||
                            asmShortName.EndsWith(".Patcher", StringComparison.OrdinalIgnoreCase) ||
                            asmShortName.StartsWith("0.com.github.MonoDetour", StringComparison.OrdinalIgnoreCase) ||
                            asmShortName.StartsWith("com.github.MonoDetour", StringComparison.OrdinalIgnoreCase) ||
                            asmShortName.StartsWith("MonoDetour", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!seenDllShortNames.Add(asmShortName))
                            continue;

                        int prio = GetModDependencyPriority(asmShortName, dirName);
                        candidates.Add(new DiscoveredPcModDll
                        {
                            DllPath = dllPath,
                            ModDir = modDir,
                            DirName = dirName,
                            AsmShortName = asmShortName,
                            PriorityOrder = prio
                        });
                    }
                }
                catch (System.Exception __apiEx_42) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2120", __apiEx_42); }
            }
        }

        private static readonly HashSet<string> _excludedArchitectOrPortingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Architect",
            "cometcake575-Architect",
            "Build_Your_Own_Bench",
            "BuildYourOwnBench",
            "OrionBonnie-Build_Your_Own_Bench",
            "Reflection",
            "AngeliciaSky-Reflection",
            // PantheonOfPharloom is a working native/C# plugin (not an Architect pack); never disable or destroy it
            "BlueSR_Ultra_Steel_Soul",
            "BlueSR-BlueSR_Ultra_Steel_Soul",
            "BlueSR_Ultra_Steel_Soul_Challenge",
            "BlueSR-BlueSR_Ultra_Steel_Soul_Challenge",
            "SimpleEnemyRando",
            "cometcake575-SimpleEnemyRando",
            "UltimateChickenSpider",
            "cometcake575-UltimateChickenSpider",
            "TheHuntIsOn",
            "dplochcoder-TheHuntIsOn",
            "Hard_Item_Randomizer",
            "elesanren-Hard_Item_Randomizer",
            "Hard_Item_Randomizer_w_SceneRandom_Config",
            "elesanren-Hard_Item_Randomizer_w_SceneRandom_Config",
            "ForgottenKingdoms",
            "ForgottenKingdoms-ForgottenKingdoms",
            "GauntletSong",
            "gauntletGang-GauntletSong",
            "skonggauntletguy-Gauntletsong",
            "SilkbornHornet",
            "hampther-SilkbornHornet",
            "SILKSONG_LOST_LANDS",
            "Silksong_Lost_Lands",
            "LostLands-SILKSONG_LOST_LANDS",
            "SilksongRandomizer",
            "Lucaspl-SilksongRandomizer",
            "The_Festival_of_Fools",
            "Resolve-The_Festival_of_Fools",
            "main_area_music_in_subrooms",
            "the_cool_fedora_guy-main_area_music_in_subrooms",
            "main_music_in_subrooms",
            "the_cool_fedora_guy-main_music_in_subrooms",
            "MoreLockets",
            "the_cool_fedora_guy-MoreLockets",
            "Ultimate_Architect_Modpack",
            "the_cool_fedora_guy-Ultimate_Architect_Modpack",
            "Woven_Together",
            "Woven_Together_Dev_Team-Woven_Together",
            "KnightInSilksong",
            "shownyoung-KnightInSilksong",
            "Echoes_Of_Starlight",
            "ink_ysy-Echoes_Of_Starlight",
            "Stitchwork",
            "SilksongSkinCreators-Stitchwork",
            "StitchworkWiki",
            "SilksongSkinCreators-StitchworkWiki"
        };

        public static bool IsStitchworkSkinOrAssetPackName(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.IndexOf("StitchworkWiki", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (id.IndexOf("for_Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("for-Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("Stitchwork_Skin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("StitchworkSkin", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        public static bool IsExcludedModIdentifier(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (IsStitchworkSkinOrAssetPackName(id)) return false;
            string clean = System.Text.RegularExpressions.Regex.Replace(id.Trim(), @"-\d+\.\d+.*$", "");
            if (_excludedArchitectOrPortingNames.Contains(id) || _excludedArchitectOrPortingNames.Contains(clean))
                return true;
            foreach (var ex in _excludedArchitectOrPortingNames)
            {
                if (clean.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                    clean.EndsWith("-" + ex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsArchitectDependentModDir(string modDir)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return false;
            try
            {
                string dirName = Path.GetFileName(modDir) ?? "";
                if (IsExcludedModIdentifier(dirName) ||
                    string.Equals(dirName, "Architect", StringComparison.OrdinalIgnoreCase) ||
                    dirName.IndexOf("-Architect-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dirName.EndsWith("-Architect", StringComparison.OrdinalIgnoreCase) ||
                    dirName.IndexOf("Build_Your_Own_Bench", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dirName.IndexOf("BuildYourOwnBench", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                string manifestPath = Path.Combine(modDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string json = File.ReadAllText(manifestPath);
                    if (json.IndexOf("cometcake575-Architect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        json.IndexOf("com.cometcake575.architect", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    int depIdx = json.IndexOf("\"dependencies\"", StringComparison.OrdinalIgnoreCase);
                    if (depIdx >= 0)
                    {
                        int closeBracket = json.IndexOf(']', depIdx);
                        string depSlice = closeBracket > depIdx ? json.Substring(depIdx, closeBracket - depIdx) : json.Substring(depIdx);
                        if (depSlice.IndexOf("Architect", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }

                foreach (var dllPath in Directory.GetFiles(modDir, "*.dll", SearchOption.AllDirectories))
                {
                    string fn = Path.GetFileNameWithoutExtension(dllPath) ?? "";
                    if (string.Equals(fn, "Architect", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fn, "BuildYourOwnBench", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception __apiEx_43) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2240", __apiEx_43); }
            return false;
        }

        private static int GetModDependencyPriority(string asmShortName, string dirName)
        {
            string combined = $"{asmShortName} {dirName}";
            if (combined.IndexOf("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("Aeternal_Everwatcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("Aeternal Everwatcher", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1000;
            if (combined.IndexOf("WavLib", StringComparison.OrdinalIgnoreCase) >= 0)
                return -2;
            if (combined.IndexOf("FsmUtil", StringComparison.OrdinalIgnoreCase) >= 0)
                return -1;
            if (combined.IndexOf("I18N", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("UnityHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("Prepatcher", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (asmShortName.IndexOf("BouncyCastle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                asmShortName.IndexOf("MonoMod", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (string.Equals(asmShortName, "SSMP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asmShortName, "HkCore", StringComparison.OrdinalIgnoreCase))
                return 5;
            if (combined.IndexOf("Needleforge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("BepInExUtils", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("BepInExUtilities", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("CanvasUtil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("DanielSteginkUtils", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("DataManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("EnemyBehaviorApi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("PurenailUtil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("SkongGamemodes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("TuneWeaver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("CrestComposition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("RecentItemsDisplay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("WishUtil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(asmShortName, "DebugMod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asmShortName, "CrestsEvolve", StringComparison.OrdinalIgnoreCase) ||
                combined.IndexOf("Lib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("API", StringComparison.OrdinalIgnoreCase) >= 0)
                return 10;
            if (string.Equals(asmShortName, "HkBossLoader", StringComparison.OrdinalIgnoreCase) ||
                combined.IndexOf("HkBossLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(asmShortName, "VoidCrest", StringComparison.OrdinalIgnoreCase))
                return 20;
            if (combined.IndexOf("SSMP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("EnemySync", StringComparison.OrdinalIgnoreCase) >= 0)
                return 25;
            return 50;
        }

        private static void InstantiatePluginsFromAssembly(Assembly asm, string dllPath, string modDir)
        {
            if (asm == null) return;
            string modName = Path.GetFileNameWithoutExtension(dllPath);

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                if (rtle.LoaderExceptions != null)
                {
                    foreach (var lex in rtle.LoaderExceptions)
                        if (lex != null) LogDiagnosticWarn($"[PCMod:{modName}] LoaderException: {lex.Message}");
                }
                types = rtle.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
            }
            catch (Exception ex)
            {
                LogModException(modName, "InstantiatePluginsFromAssembly.GetTypes", ex);
                return;
            }

            bool foundAnyPlugin = false;
            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type))
                    continue;
                foundAnyPlugin = true;

                try
                {
                    var pluginAttr = HarmonyBridge.GetCustomAttributeSafe<BepInPlugin>(type, true);
                    string pluginTitle = pluginAttr?.Name ?? type.Name;
                    if (pluginAttr != null && !string.IsNullOrEmpty(pluginAttr.GUID) && !string.IsNullOrEmpty(modDir))
                    {
                        lock (_modGuidByFolder)
                        {
                            _modGuidByFolder[modDir] = pluginAttr.GUID.TrimStart('#', ' ');
                        }
                        HarmonyBridge.RegisterModFolderTranslations(modName, modDir);
                    }

                    LogDiagnosticInfo($"[PCMod:{modName}] Instantiating BaseUnityPlugin: {pluginTitle} ({type.FullName})");

                    var go = new GameObject($"PCMod_{pluginTitle}");
                    DontDestroyOnLoad(go);

                    string awakeUnityException = null;
                    Application.LogCallback logHandler = (condition, stackTrace, logType) =>
                    {
                        if (logType == LogType.Exception && awakeUnityException == null)
                        {
                            awakeUnityException = $"{condition}\n{stackTrace}";
                        }
                    };
                    Application.logMessageReceived += logHandler;
                    BaseUnityPlugin comp = null;
                    var prevInitAsm = HarmonyBridge.CurrentlyInitializingModAssembly;
                    HarmonyBridge.CurrentlyInitializingModAssembly = asm;
                    try
                    {
                        comp = go.AddComponent(type) as BaseUnityPlugin;
                    }
                    finally
                    {
                        HarmonyBridge.CurrentlyInitializingModAssembly = prevInitAsm;
                        Application.logMessageReceived -= logHandler;
                    }
                    try
                    {
                        if (CheatManager.Invincibility == (CheatManager.InvincibilityStates)3)
                            CheatManager.Invincibility = CheatManager.InvincibilityStates.Off;
                    }
                    catch { }

                    if (comp != null)
                    {
                        HarmonyBridge.RegisterPluginInfo(comp, pluginAttr, dllPath);
                        lock (_instantiatedPcPlugins)
                        {
                            _instantiatedPcPlugins.Add(comp);
                        }
                    }

                    if (awakeUnityException != null)
                    {
                        LogDiagnosticError($"[PCMod:{modName}] Exception thrown inside '{pluginTitle}.Awake()':\n{awakeUnityException}");
                    }
                    else
                    {
                        LogDiagnosticInfo($"[PCMod:{modName}] Successfully initialized plugin '{pluginTitle}'.");
                    }
                    if (string.Equals(modName, "PharloomsGlory", StringComparison.OrdinalIgnoreCase))
                    {
                        AssetHelperBridge.TryBootstrapPharloomsGloryAfterAwake(asm);
                    }
                }
                catch (Exception ex)
                {
                    LogModException(modName, $"InstantiatePlugin({type.FullName})", ex);
                }
            }

            HarmonyBridge.RegisterSSMPAddonsFromAssembly(asm, foundAnyPlugin);
        }

        public static void DeactivateLoadedModRuntime(string modName, string modDirectoryPath)
        {
            try
            {
                var targetAssemblies = new HashSet<Assembly>();
                var toRemovePlugins = new List<BaseUnityPlugin>();

                lock (_instantiatedPcPlugins)
                {
                    for (int i = _instantiatedPcPlugins.Count - 1; i >= 0; i--)
                    {
                        var p = _instantiatedPcPlugins[i];
                        if (p == null)
                        {
                            _instantiatedPcPlugins.RemoveAt(i);
                            continue;
                        }
                        var pAsm = p.GetType().Assembly;
                        string pAsmName = pAsm.GetName().Name ?? "";
                        var pInfo = HarmonyBridge.GetPluginInfo(p);
                        string pLoc = pInfo?.Location ?? "";
                        string pTitle = pInfo?.Metadata?.Name ?? p.GetType().Name;

                        bool match = false;
                        if (!string.IsNullOrEmpty(modDirectoryPath) && !string.IsNullOrEmpty(pLoc) &&
                            pLoc.StartsWith(modDirectoryPath, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                        }
                        else if (!string.IsNullOrEmpty(modName) &&
                                 (string.Equals(pAsmName, modName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(pTitle, modName, StringComparison.OrdinalIgnoreCase) ||
                                  pAsmName.Replace(" ", "").Equals(modName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ||
                                  pTitle.Replace(" ", "").Equals(modName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                        {
                            match = true;
                        }

                        if (match)
                        {
                            targetAssemblies.Add(pAsm);
                            toRemovePlugins.Add(p);
                            _instantiatedPcPlugins.RemoveAt(i);
                        }
                    }
                }

                int totalHooksRemoved = RuntimeHybridCLRAdapterBuilder.UnregisterAllHooksForMod(modName, null);
                foreach (var asm in targetAssemblies)
                {
                    totalHooksRemoved += RuntimeHybridCLRAdapterBuilder.UnregisterAllHooksForMod(asm.GetName().Name, asm);
                }

                foreach (var p in toRemovePlugins)
                {
                    try
                    {
                        p.StopAllCoroutines();
                        p.enabled = false;
                        if (p.gameObject != null && p.gameObject != Instance?.gameObject)
                            Destroy(p.gameObject);
                        else
                            Destroy(p);
                    }
                    catch { }
                }

                if (targetAssemblies.Count > 0)
                {
                    try
                    {
                        var allBehaviours = FindObjectsOfType<MonoBehaviour>(true);
                        if (allBehaviours != null)
                        {
                            for (int i = 0; i < allBehaviours.Length; i++)
                            {
                                var mb = allBehaviours[i];
                                if (mb == null || mb == Instance) continue;
                                if (targetAssemblies.Contains(mb.GetType().Assembly))
                                {
                                    try
                                    {
                                        mb.StopAllCoroutines();
                                        mb.enabled = false;
                                        Destroy(mb);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }

                try
                {
                    if (CheatManager.Invincibility == (CheatManager.InvincibilityStates)3)
                        CheatManager.Invincibility = CheatManager.InvincibilityStates.Off;
                }
                catch { }

                LogDiagnosticInfo($"[ModManager] Runtime-deactivated '{modName}' ({toRemovePlugins.Count} plugin instance(s) destroyed, {totalHooksRemoved} hook(s) detached).");
            }
            catch (Exception ex)
            {
                LogDiagnosticWarn($"[ModManager] DeactivateLoadedModRuntime('{modName}') warning: {ex.Message}");
            }
        }

        public static void RefreshDiscoveredModConfigs()
        {
            lock (_discoveredConfigs)
            {
                _discoveredConfigs.Clear();
                var allPlugins = new List<BaseUnityPlugin>();
                lock (_instantiatedPcPlugins)
                {
                    allPlugins.AddRange(_instantiatedPcPlugins);
                }
                try
                {
                    var found = FindObjectsOfType<BaseUnityPlugin>();
                    if (found != null)
                    {
                        foreach (var p in found)
                            if (p != null && p != Instance && !allPlugins.Contains(p))
                                allPlugins.Add(p);
                    }
                }
                catch (System.Exception __apiEx_44) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2389", __apiEx_44); }

                foreach (var plugin in allPlugins)
                {
                    try
                    {
                        var cfg = plugin.Config;
                        if (cfg == null) continue;
                        var info = HarmonyBridge.GetPluginInfo(plugin);
                        string modTitle = info?.Metadata?.Name ?? plugin.GetType().Name;
                        string asmShort = plugin.GetType().Assembly.GetName().Name ?? "";
                        if (IsExcludedModIdentifier(modTitle) || IsExcludedModIdentifier(asmShort) ||
                            modTitle.IndexOf("Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            modTitle.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            asmShort.IndexOf("Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            asmShort.IndexOf("PatchWork", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        var entriesField = typeof(ConfigFile).GetProperty("Entries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        IDictionary dict = entriesField?.GetValue(cfg, null) as IDictionary;
                        if (dict == null)
                        {
                            foreach (var f in typeof(ConfigFile).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (typeof(IDictionary).IsAssignableFrom(f.FieldType))
                                {
                                    dict = f.GetValue(cfg) as IDictionary;
                                    if (dict != null) break;
                                }
                            }
                        }
                        if (dict == null) continue;

                        foreach (DictionaryEntry de in dict)
                        {
                            if (de.Value is ConfigEntryBase ceb)
                            {
                                _discoveredConfigs.Add(new LoadedModConfigEntryInfo
                                {
                                    ModName = modTitle,
                                    Section = ceb.Definition?.Section ?? "General",
                                    Key = ceb.Definition?.Key ?? "Setting",
                                    Description = ceb.Description?.Description ?? "",
                                    Entry = ceb,
                                    SettingType = ceb.SettingType
                                });
                            }
                        }
                    }
                    catch (System.Exception __apiEx_45) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2431", __apiEx_45); }
                }
                _discoveredConfigs.Sort((a, b) =>
                {
                    int cmp = string.Compare(a.ModName, b.ModName, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(a.Section, b.Section, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                    return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        // ======================================================================
        // Native In-Game Menu Integration (Replaced IMGUI with Team Cherry UI)
        // ======================================================================
        internal void RenderModConsoleIMGUI()
        {
            if (!_showModPanel || ModPreloaderScreen.IsActive) return;
            if (_uGuiModConsolePanelGo != null && _uGuiModConsolePanelGo.activeInHierarchy) return;

            Matrix4x4 oldMatrix = ApplyResolutionIndependentMatrix(out float vWidth, out float vHeight);
            try
            {
                EnsureGuiThemeInitialized();

                // On-screen floating button removed; modal window has its own Close button and CircularFloatingWindow toggle.

                _panelRect.x = Mathf.Clamp(_panelRect.x, 16f, Mathf.Max(16f, vWidth - _panelRect.width - 16f));
                _panelRect.y = Mathf.Clamp(_panelRect.y, 16f, Mathf.Max(16f, vHeight - _panelRect.height - 16f));
                _panelRect = GUI.Window(94821, _panelRect, DrawModControlWindow, "IRIS — MOD, KEYBIND & PROFILE CONSOLE (IRIS v2.9.6)", _styleWindow);
            }
            catch (System.Exception __apiEx_46) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L2463", __apiEx_46); }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        public void OpenModPanel(string tab = "Installed")
        {
            _selectedModTab = tab;
            SetShowModPanel(true);
            InGameModManager.Instance?.ScanInstalledMods();
            RefreshDiscoveredModConfigs();
        }

        private void DrawInstalledModsTab()
        {
            var mgr = InGameModManager.Instance;
            if (mgr == null)
            {
                GUILayout.Label("Mod Manager not initialized.");
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>Installed Mods ({mgr.InstalledMods.Count})</b>", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Refresh List", GUILayout.Width(120f), GUILayout.Height(34f)))
            {
                mgr.ScanInstalledMods();
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(mgr.GlobalStatusMessage))
            {
                GUILayout.Space(4f);
                GUI.color = Color.yellow;
                GUILayout.Label(mgr.GlobalStatusMessage);
                GUI.color = Color.white;
                GUILayout.Space(4f);
            }

            GUILayout.Space(6f);
            foreach (var mod in mgr.InstalledMods)
            {
                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                string statusColor = mod.IsEnabled ? "#55ff55" : "#ff5555";
                string statusText = mod.IsEnabled ? "[ACTIVE]" : "[DISABLED]";
                GUILayout.Label($"<color={statusColor}>{statusText}</color> <b>{mod.DisplayName}</b> (v{mod.Version})", GUILayout.ExpandWidth(true));

                if (!mod.IsCore)
                {
                    string toggleBtnText = mod.IsEnabled ? "Disable" : "Enable";
                    if (GUILayout.Button(toggleBtnText, GUILayout.Width(85f), GUILayout.Height(32f)))
                    {
                        mgr.ToggleMod(mod);
                    }

                    if (mod.PendingDeleteConfirm)
                    {
                        GUI.color = Color.red;
                        if (GUILayout.Button("CONFIRM DELETE?", GUILayout.Width(150f), GUILayout.Height(32f)))
                        {
                            mgr.UninstallMod(mod);
                        }
                        GUI.color = Color.white;
                        if (GUILayout.Button("Cancel", GUILayout.Width(65f), GUILayout.Height(32f)))
                        {
                            mod.PendingDeleteConfirm = false;
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("Uninstall", GUILayout.Width(85f), GUILayout.Height(32f)))
                        {
                            mod.PendingDeleteConfirm = true;
                        }
                    }
                }
                else
                {
                    GUILayout.Label("<color=#88ccff>[Core API]</color>", GUILayout.Width(90f));
                }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(mod.Description))
                {
                    GUILayout.Label($"<size=11>{mod.Description}</size>");
                }
                string sizeStr = mod.SizeBytes > 1048576 ? $"{(mod.SizeBytes / 1048576f):0.1f} MB" : $"{(mod.SizeBytes / 1024f):0.0f} KB";
                GUILayout.Label($"<size=10><color=#888888>Folder: {mod.FolderName} | Size: {sizeStr}</color></size>");
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }
        }

        private void DrawOnlineStoreTab()
        {
            var mgr = InGameModManager.Instance;
            if (mgr == null)
            {
                GUILayout.Label("Mod Manager not initialized.");
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Thunderstore Mobile Mod Store</b>", _styleHeaderLabel, GUILayout.ExpandWidth(true));
            string filterLbl = mgr.ShowOnlyCompatibleMods ? "Filter: Mobile-Ready Only" : "Filter: Showing All";
            if (GUILayout.Button(filterLbl, _styleActionBtn, GUILayout.Width(240f), GUILayout.Height(42f)))
            {
                mgr.ShowOnlyCompatibleMods = !mgr.ShowOnlyCompatibleMods;
            }
            if (GUILayout.Button("Refresh Store", _styleActionBtn, GUILayout.Width(150f), GUILayout.Height(42f)))
            {
                mgr.InitCatalog();
                mgr.ScanInstalledMods();
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(mgr.GlobalStatusMessage))
            {
                GUILayout.Space(4f);
                GUI.color = Color.cyan;
                GUILayout.Label(mgr.GlobalStatusMessage);
                GUI.color = Color.white;
                GUILayout.Space(4f);
            }

            GUILayout.Space(6f);
            var visibleMods = mgr.OnlineCatalog.Where(m => m.IsCompatible).ToList();
            foreach (var item in visibleMods)
            {
                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                string compatBadge = "<color=#55ff55>[MOBILE READY]</color>";
                GUILayout.Label($"{compatBadge} <b>{item.DisplayTitle}</b> <color=#aaaaaa>by {item.Author} (v{item.Version})</color>", GUILayout.ExpandWidth(true));

                if (item.IsInstalled)
                {
                    GUI.color = Color.green;
                    GUILayout.Label("[Installed]", GUILayout.Width(90f));
                    GUI.color = Color.white;
                }
                else
                {
                    if (item.InstallStatus == "Downloading...")
                    {
                        GUILayout.Label($"{(int)(item.DownloadProgress * 100)}%", GUILayout.Width(80f));
                    }
                    else if (item.InstallStatus == "Extracting..." || item.InstallStatus == "Applying Mobile Adaptations...")
                    {
                        GUILayout.Label("Extracting...", GUILayout.Width(100f));
                    }
                    else
                    {
                        if (GUILayout.Button("Install", GUILayout.Width(90f), GUILayout.Height(32f)))
                        {
                            mgr.StartInstallOnlineMod(item);
                        }
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.Label($"<size=11>{item.Description}</size>");
                if (!string.IsNullOrEmpty(item.CompatibilityReason))
                {
                    string color = item.IsCompatible ? "#88dd88" : "#ff8888";
                    GUILayout.Label($"<size=10><color={color}>Compatibility: {item.CompatibilityReason}</color></size>");
                }
                if (item.RequiresAdaptation && !string.IsNullOrEmpty(item.AdaptationDetails))
                {
                    GUILayout.Label($"<size=10><color=#ffcc66>Auto-Adaptation: {item.AdaptationDetails}</color></size>");
                }
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }
        }

        private void DrawModControlWindow(int windowId)
        {
            GUILayout.BeginHorizontal();

            // Left Vertical Sidebar (Thumb-friendly Silksong Crimson-Gold Navigation Tabs)
            GUILayout.BeginVertical(_styleCard, GUILayout.Width(250f));

            GUILayout.Label("<b>NAVIGATION</b>", _styleHeaderLabel);
            GUILayout.Space(8f);

            if (GUILayout.Button(_selectedModTab == "Hotkeys" ? "> Virtual Keys" : "  Virtual Keys", _selectedModTab == "Hotkeys" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(56f)))
            {
                _selectedModTab = "Hotkeys";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "Configs" ? "> Mod Settings" : "  Mod Settings", _selectedModTab == "Configs" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(56f)))
            {
                RefreshDiscoveredModConfigs();
                _selectedModTab = "Configs";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "Installed" ? "> Installed Mods" : "  Installed Mods", _selectedModTab == "Installed" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(56f)))
            {
                InGameModManager.Instance?.ScanInstalledMods();
                _selectedModTab = "Installed";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "Profile" ? "> Profile (.jsonc)" : "  Profile (.jsonc)", _selectedModTab == "Profile" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(56f)))
            {
                IrisProfileManager.EnsureLoaded();
                _selectedModTab = "Profile";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "Online" ? "> Online Store" : "  Online Mod Store", _selectedModTab == "Online" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(56f)))
            {
                InGameModManager.Instance?.ScanInstalledMods();
                _selectedModTab = "Online";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "SSMP" ? "> SSMP Multiplayer" : "  SSMP Multiplayer", _selectedModTab == "SSMP" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(52f)))
            {
                _selectedModTab = "SSMP";
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(_selectedModTab == "Logs" ? "> Diagnostics" : "  Logs & Status", _selectedModTab == "Logs" ? _styleTabBtnActive : _styleTabBtn, GUILayout.Height(52f)))
            {
                _selectedModTab = "Logs";
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Close Menu", _styleDangerBtn, GUILayout.Height(52f)))
            {
                SetShowModPanel(false);
            }
            GUILayout.EndVertical();

            GUILayout.Space(12f);

            // Right Vertical Pane (Scrollable Content)
            GUILayout.BeginVertical();
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            if (_selectedModTab == "Installed")
            {
                DrawInstalledModsTab();
            }
            else if (_selectedModTab == "Profile")
            {
                IrisProfileManager.DrawIMGUIProfileTab();
            }
            else if (_selectedModTab == "Online")
            {
                DrawOnlineStoreTab();
            }
            else if (_selectedModTab == "SSMP")
            {
                DrawSSMPMultiplayerControlsBox();
            }
            else if (_selectedModTab == "Hotkeys")
            {
                GUILayout.Label($"Loaded PC Mods ({_loadedModNames.Count}): [{string.Join(", ", _loadedModNames)}]");
                GUILayout.Label("=== Quick Crest & Skill Unlocks (100% Save Helper) ===");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Unlock All Crest Slots & Mod Skills", GUILayout.Height(40f)))
                {
                    _unlockStatusMessage = UnlockAllCrestSlotsAndModSkills();
                }
                if (GUILayout.Button("+4 Memory Lockets (Crest Notches)", GUILayout.Height(40f)))
                {
                    _unlockStatusMessage = GrantFourMemoryLockets();
                }
                GUILayout.EndHorizontal();
                // Stitchwork / Patchwork UI toggle removed per user preference
                string freeAtkLabel = HarmonyBridge.FreeWeaverAttacksNoSilkDrain
                    ? "Free Weaver Attacks (No Silk/Mask Drain): ON"
                    : "Free Weaver Attacks (No Silk/Mask Drain): OFF";
                if (GUILayout.Button(freeAtkLabel, _styleActionBtn, GUILayout.Height(42f)))
                {
                    HarmonyBridge.FreeWeaverAttacksNoSilkDrain = !HarmonyBridge.FreeWeaverAttacksNoSilkDrain;
                    _unlockStatusMessage = HarmonyBridge.FreeWeaverAttacksNoSilkDrain
                        ? "Enabled Free Weaver Attacks (0 Silk / 0 Mask cost on swings)!"
                        : "Restored standard Weaver Crest Silk mechanic (+1.5 Silk on hit, -0.5 Silk on miss).";
                }
                string logFilterLabel = HarmonyBridge.SuppressVerboseCombatLogs
                    ? "Suppress Per-Frame Combat Log Spam (No Disk I/O Lag): ON"
                    : "Suppress Per-Frame Combat Log Spam (No Disk I/O Lag): OFF";
                if (GUILayout.Button(logFilterLabel, _styleActionBtn, GUILayout.Height(42f)))
                {
                    HarmonyBridge.SuppressVerboseCombatLogs = !HarmonyBridge.SuppressVerboseCombatLogs;
                    _unlockStatusMessage = HarmonyBridge.SuppressVerboseCombatLogs
                        ? "Combat log suppression ON (prevents disk I/O stutters during combat)."
                        : "Combat log suppression OFF (verbose mod combat logs enabled).";
                }
                if (_loadedModNames.Any(n => n.IndexOf("HkCore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             n.IndexOf("HkBossLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             n.IndexOf("SsBossLoader", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("<b>=== Godhome (HkCore / HkBossLoader / SsBossLoader) Quick Actions ===</b>");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Teleport: Godhome Atrium (GG_Atrium)", _styleActionBtn, GUILayout.Height(40f)))
                    {
                        _unlockStatusMessage = AssetHelperBridge.TeleportToGodhomeScene("GG_Atrium");
                        SetShowModPanel(false);
                    }
                    if (GUILayout.Button("Teleport: Hall of Gods (GG_Workshop)", _styleActionBtn, GUILayout.Height(40f)))
                    {
                        _unlockStatusMessage = AssetHelperBridge.TeleportToGodhomeScene("GG_Workshop");
                        SetShowModPanel(false);
                    }
                    if (GUILayout.Button("Unlock All Godhome Statues & Doors", _styleActionBtn, GUILayout.Height(40f)))
                    {
                        _unlockStatusMessage = AssetHelperBridge.UnlockAllGodhomeStatuesAndDoors();
                    }
                    GUILayout.EndHorizontal();
                }

                if (!string.IsNullOrEmpty(_unlockStatusMessage))
                {
                    GUILayout.Label(_unlockStatusMessage);
                }

                if (HarmonyBridge.IsSSMPLoaded || _loadedModNames.Any(n => n.IndexOf("SSMP", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    DrawSSMPMultiplayerControlsBox();
                }

                GUILayout.Space(6f);
                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                string autoCloseLabel = _autoCloseMenuOnKeyTap
                    ? "Auto-Close Menu on Key Tap (Sends Key to Active Gameplay): ON"
                    : "Auto-Close Menu on Key Tap (Keep Menu Open): OFF";
                if (GUILayout.Button(autoCloseLabel, _styleActionBtn, GUILayout.Height(38f)))
                {
                    _autoCloseMenuOnKeyTap = !_autoCloseMenuOnKeyTap;
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(BepInEx.MobileVirtualInput.LastPulsedKeySummary))
                {
                    GUILayout.Label($"<color=#88ff88>Last Virtual Key Sent: {BepInEx.MobileVirtualInput.LastPulsedKeySummary}</color>");
                }
                GUILayout.EndVertical();

                lock (_discoveredConfigs)
                {
                    var keyEntries = _discoveredConfigs.Where(c => c.SettingType == typeof(KeyboardShortcut) || c.SettingType == typeof(KeyCode)).ToList();
                    if (keyEntries.Count > 0)
                    {
                        GUILayout.Label("<b>=== Mod-Registered Keybinds ===</b>");
                        foreach (var ke in keyEntries)
                        {
                            if (ke.Entry.BoxedValue is KeyboardShortcut ks && ks.MainKey != KeyCode.None)
                            {
                                if (GUILayout.Button($"[{ke.ModName}] {ke.Key} ({ks})", GUILayout.Height(40f)))
                                {
                                    BepInEx.MobileVirtualInput.PulseShortcut(ks, withDelayedRepeat: _autoCloseMenuOnKeyTap);
                                    if (_autoCloseMenuOnKeyTap) SetShowModPanel(false);
                                }
                            }
                            else if (ke.Entry.BoxedValue is KeyCode kc && kc != KeyCode.None)
                            {
                                if (GUILayout.Button($"[{ke.ModName}] {ke.Key} ({kc})", GUILayout.Height(40f)))
                                {
                                    TriggerVirtualKeyFromUi(kc);
                                }
                            }
                        }
                        GUILayout.Space(8f);
                    }
                }

                GUILayout.Label("<b>=== Function & Utility Keys (F1 - F12, Esc, Tab, Enter, Space, BackQuote) ===</b>");
                int cols = 4;
                for (int i = 0; i < _quickFunctionKeys.Length; i += cols)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = 0; j < cols && (i + j) < _quickFunctionKeys.Length; j++)
                    {
                        var k = _quickFunctionKeys[i + j];
                        if (GUILayout.Button(k.ToString(), GUILayout.Height(42f), GUILayout.Width(160f)))
                        {
                            TriggerVirtualKeyFromUi(k);
                        }
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(6f);
                GUILayout.Label("<b>=== Modifier Hold Toggles & Arrow Keys ===</b>");
                GUILayout.BeginHorizontal();
                foreach (var modKey in _modifierKeys)
                {
                    bool held = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(modKey);
                    string lbl = held ? $"[HELD] {modKey}" : modKey.ToString();
                    if (GUILayout.Button(lbl, held ? _styleTabBtnActive : _styleActionBtn, GUILayout.Height(40f), GUILayout.Width(155f)))
                    {
                        BepInEx.MobileVirtualInput.SetKeyHeld(modKey, !held);
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                foreach (var navKey in _navigationKeys)
                {
                    if (GUILayout.Button(navKey.ToString(), GUILayout.Height(40f), GUILayout.Width(130f)))
                    {
                        TriggerVirtualKeyFromUi(navKey);
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_uGuiKeyHoldMode ? "Key Mode: HOLD TOGGLE (Tap to Hold/Release)" : "Key Mode: SINGLE TAP PULSE (1-Frame Clean)", _uGuiKeyHoldMode ? _styleTabBtnActive : _styleActionBtn, GUILayout.Height(38f), GUILayout.Width(360f)))
                {
                    _uGuiKeyHoldMode = !_uGuiKeyHoldMode;
                }
                if (GUILayout.Button("Release All Held Keys", _styleActionBtn, GUILayout.Height(38f), GUILayout.Width(220f)))
                {
                    foreach (var nk in _numberKeys0To9) BepInEx.MobileVirtualInput.SetKeyHeld(nk, false);
                    foreach (var lk in _letterKeysAToZ) BepInEx.MobileVirtualInput.SetKeyHeld(lk, false);
                    foreach (var mk in _modifierKeys) BepInEx.MobileVirtualInput.SetKeyHeld(mk, false);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4f);
                GUILayout.Label("<b>=== Number Keys (0 - 9) [Alpha0-9 + Keypad0-9] ===</b>");
                int numCols = 5;
                for (int i = 0; i < _numberKeys0To9.Length; i += numCols)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = 0; j < numCols && (i + j) < _numberKeys0To9.Length; j++)
                    {
                        var k = _numberKeys0To9[i + j];
                        int digit = (int)(k - KeyCode.Alpha0);
                        bool held = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(k);
                        string disp = held ? $"[{digit}] (HELD)" : digit.ToString();
                        if (GUILayout.Button(disp, held ? _styleTabBtnActive : _styleActionBtn, GUILayout.Height(40f), GUILayout.Width(125f)))
                        {
                            TriggerVirtualKeyFromUi(k);
                        }
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(4f);
                GUILayout.Label("<b>=== Letter Keys (A - Z) [All 26 Keys] ===</b>");
                int letCols = 6;
                for (int i = 0; i < _letterKeysAToZ.Length; i += letCols)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = 0; j < letCols && (i + j) < _letterKeysAToZ.Length; j++)
                    {
                        var k = _letterKeysAToZ[i + j];
                        bool held = BepInEx.MobileVirtualInput.IsKeyManuallyHeld(k);
                        string disp = held ? $"[{k}] (HELD)" : k.ToString();
                        if (GUILayout.Button(disp, held ? _styleTabBtnActive : _styleActionBtn, GUILayout.Height(40f), GUILayout.Width(105f)))
                        {
                            TriggerVirtualKeyFromUi(k);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else if (_selectedModTab == "Configs")
            {
                lock (_discoveredConfigs)
                {
                    if (_discoveredConfigs.Count == 0)
                    {
                        GUILayout.Label("No configurable PC mod settings discovered yet.");
                    }
                    else
                    {
                        foreach (var group in _discoveredConfigs.GroupBy(c => c.ModName))
                        {
                            GUILayout.Label($"=== {group.Key} ===");
                            foreach (var item in group)
                            {
                                GUILayout.BeginHorizontal("box");
                                GUILayout.Label($"{item.Section}: {item.Key}", GUILayout.Width(360f));

                                var val = item.Entry.BoxedValue;
                                if (item.SettingType == typeof(bool) && val is bool bVal)
                                {
                                    if (GUILayout.Button(bVal ? "ON (Tap to Toggle)" : "OFF (Tap to Toggle)", GUILayout.Height(34f)))
                                    {
                                        item.Entry.BoxedValue = !bVal;
                                    }
                                }
                                else if (item.SettingType != null && item.SettingType.IsEnum && val != null)
                                {
                                    if (GUILayout.Button(val.ToString(), GUILayout.Height(34f)))
                                    {
                                        var vals = Enum.GetValues(item.SettingType);
                                        int idx = Array.IndexOf(vals, val);
                                        int nextIdx = (idx + 1) % vals.Length;
                                        item.Entry.BoxedValue = vals.GetValue(nextIdx);
                                    }
                                }
                                else if (item.SettingType == typeof(int) && val is int iVal)
                                {
                                    if (GUILayout.Button("-", GUILayout.Width(44f), GUILayout.Height(34f))) item.Entry.BoxedValue = iVal - 1;
                                    GUILayout.Label(iVal.ToString(), GUILayout.Width(60f));
                                    if (GUILayout.Button("+", GUILayout.Width(44f), GUILayout.Height(34f))) item.Entry.BoxedValue = iVal + 1;
                                }
                                else
                                {
                                    GUILayout.Label(val?.ToString() ?? "null");
                                }

                                GUILayout.EndHorizontal();
                            }
                            GUILayout.Space(10f);
                        }
                    }
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                string profLabel = RuntimeHybridCLRAdapterBuilder.EnableHookProfiler ? "Profiler: ON (Stop)" : "Profiler: OFF (Start)";
                if (GUILayout.Button(profLabel, GUILayout.Width(190f), GUILayout.Height(36f)))
                {
                    RuntimeHybridCLRAdapterBuilder.EnableHookProfiler = !RuntimeHybridCLRAdapterBuilder.EnableHookProfiler;
                }
                if (GUILayout.Button("Reset Profiler", GUILayout.Width(130f), GUILayout.Height(36f)))
                {
                    RuntimeHybridCLRAdapterBuilder.ResetHookProfiler();
                }
                if (GUILayout.Button("Dump Inv State", GUILayout.Width(135f), GUILayout.Height(36f)))
                {
                    try
                    {
                        var sb = new StringBuilder();
                        sb.Append("[InvDiag] ");
                        var pls = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
                        sb.Append($"PaneLists={pls?.Length ?? 0}; ");
                        if (pls != null)
                        {
                            foreach (var pl in pls)
                            {
                                if (pl == null || pl.gameObject == null || !pl.gameObject.scene.IsValid()) continue;
                                string fgAlphaStr = "none";
                                try
                                {
                                    var fgType = HarmonyLib.AccessTools.TypeByName("TeamCherry.NestedFadeGroup.NestedFadeGroupBase") ?? HarmonyLib.AccessTools.TypeByName("NestedFadeGroupBase");
                                    var fgComp = fgType != null ? pl.GetComponentInParent(fgType) : null;
                                    var aProp = fgComp != null ? fgType.GetProperty("AlphaSelf", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                                    if (aProp != null) fgAlphaStr = Convert.ToSingle(aProp.GetValue(fgComp, null)).ToString("F2");
                                }
                                catch { }
                                sb.Append($"PL('{pl.gameObject.name}', act={pl.gameObject.activeInHierarchy}, layer={pl.gameObject.layer}, pos={pl.transform.position}, fgAlpha={fgAlphaStr}); ");
                            }
                        }
                        var sp = GameObject.Find("Inventory ScreenPlane");
                        if (sp != null)
                        {
                            var r = sp.GetComponent<Renderer>();
                            sb.Append($"ScreenPlane(act={sp.activeInHierarchy}, layer={sp.layer}, rendEn={(r != null ? r.enabled.ToString() : "none")}, mat={(r != null && r.sharedMaterial != null ? r.sharedMaterial.shader?.name : "none")}); ");
                        }
                        var gc = GameCameras.instance;
                        if (gc != null && gc.hudCamera != null)
                        {
                            var hCam = gc.hudCamera.GetComponent<Camera>();
                            sb.Append($"HudCam(act={gc.hudCamera.gameObject.activeInHierarchy}, camEn={(hCam != null ? hCam.enabled.ToString() : "none")}, mask={(hCam != null ? "0x" + hCam.cullingMask.ToString("X") : "none")}); ");
                        }
                        SilksongMobileAPIPlugin.LogDiagnosticInfo(sb.ToString());
                        SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                        _copyStatusMessage = "Dumped Inventory state to ModLog.log!";
                    }
                    catch (Exception ex) { _copyStatusMessage = "InvDiag error: " + ex.Message; }
                }
                string vramLabel = AssetHelperBridge.MaxModTextureDimension <= 0 ? "VRAM Cap: OFF" : $"VRAM Cap: {AssetHelperBridge.MaxModTextureDimension}px";
                if (GUILayout.Button(vramLabel, GUILayout.Width(150f), GUILayout.Height(36f)))
                {
                    AssetHelperBridge.MaxModTextureDimension = AssetHelperBridge.MaxModTextureDimension == 2048 ? 4096 : (AssetHelperBridge.MaxModTextureDimension == 4096 ? 0 : 2048);
                }
                if (GUILayout.Button("Copy Log to Clipboard", GUILayout.Height(36f)))
                {
                    try
                    {
                        string fullText;
                        lock (_logLock)
                        {
                            fullText = string.Join("\n", _inMemoryLogLines);
                        }
                        GUIUtility.systemCopyBuffer = fullText;
                        _copyStatusMessage = $"Copied {_inMemoryLogLines.Count} log lines to clipboard!";
                    }
                    catch (Exception ex)
                    {
                        _copyStatusMessage = $"Copy failed: {ex.Message}";
                    }
                }
                if (!string.IsNullOrEmpty(_copyStatusMessage))
                {
                    GUILayout.Label(_copyStatusMessage);
                }
                GUILayout.EndHorizontal();

                GUILayout.Label($"<color=#88ddff>[Mobile Bridges] Shaders Auto-Repaired: {AssetHelperBridge.RepairedShaderCount} | Oversized Textures Downscaled: {AssetHelperBridge.DownscaledTextureCount} | Save Slot Dir: slot_{HarmonyBridge.CurrentSaveSlotIndex}</color>");
                if (RuntimeHybridCLRAdapterBuilder.EnableHookProfiler)
                {
                    GUILayout.Space(4f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("<b>=== Live Per-Mod Hook CPU Profiler ===</b>");
                    string slowestMod = RuntimeHybridCLRAdapterBuilder.GetSlowestProfiledModName();
                    if (!string.IsNullOrEmpty(slowestMod))
                    {
                        if (GUILayout.Button($"Pause Slowest ({slowestMod})", GUILayout.Width(240f), GUILayout.Height(30f)))
                        {
                            RuntimeHybridCLRAdapterBuilder.DisabledModHookOwners.Add(slowestMod);
                        }
                    }
                    if (RuntimeHybridCLRAdapterBuilder.DisabledModHookOwners.Count > 0)
                    {
                        if (GUILayout.Button($"Resume All ({RuntimeHybridCLRAdapterBuilder.DisabledModHookOwners.Count})", GUILayout.Width(150f), GUILayout.Height(30f)))
                        {
                            RuntimeHybridCLRAdapterBuilder.DisabledModHookOwners.Clear();
                        }
                    }
                    GUILayout.EndHorizontal();
                    var profLines = RuntimeHybridCLRAdapterBuilder.GetHookProfilerSummaryLines();
                    for (int pIdx = 0; pIdx < profLines.Count; pIdx++)
                    {
                        GUILayout.Label(profLines[pIdx]);
                    }
                    GUILayout.Space(6f);
                }

                GUILayout.Space(6f);
                lock (_logLock)
                {
                    for (int i = 0; i < _inMemoryLogLines.Count; i++)
                    {
                        GUILayout.Label(_inMemoryLogLines[i]);
                    }
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 28f));
        }

        // ======================================================================
        // Structured Multi-Section Diagnostic Logging -> ModLog.log ONLY
        // ======================================================================
        private static DateTime _mogLogStartTime = DateTime.Now;
        private static readonly List<string> _secModLoading = new List<string>(128);
        private static readonly List<string> _secHookErrors = new List<string>(128);
        private static readonly List<string> _secModRuntimeExceptions = new List<string>(128);
        private static readonly List<string> _secFsmAndAssets = new List<string>(128);
        private static readonly List<string> _secApiCatches = new List<string>(128);
        private static readonly List<string> _secUnityAndCrashes = new List<string>(128);
        private static readonly List<string> _secTimeline = new List<string>(512);
        private static volatile bool _mogLogDirty;
        private static bool _asyncLogWorkerStarted;
        private static readonly System.Threading.AutoResetEvent _asyncLogWake = new System.Threading.AutoResetEvent(false);

        private static void InitDiagnosticLog()
        {
            _mogLogStartTime = DateTime.Now;
            try
            {
                // Ensure ONLY 2 log files exist in BepInEx/: LogOutput.log and ModLog.log
                if (!string.IsNullOrEmpty(Paths.BepInExRootPath) && Directory.Exists(Paths.BepInExRootPath))
                {
                    foreach (var pattern in new[] { "PCMods_Diag_v*.log", "SilksongMobileAPI_PCMods.log", "Crash.log", "MogLog", "ModLog.log" })
                    {
                        foreach (var oldLog in Directory.GetFiles(Paths.BepInExRootPath, pattern, SearchOption.TopDirectoryOnly))
                        {
                            if (!string.Equals(oldLog, DiagnosticLogPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(oldLog); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                lock (_logLock)
                {
                    _inMemoryLogLines.Clear();
                    _secModLoading.Clear();
                    _secHookErrors.Clear();
                    _secModRuntimeExceptions.Clear();
                    _secFsmAndAssets.Clear();
                    _secApiCatches.Clear();
                    _secUnityAndCrashes.Clear();
                    _secTimeline.Clear();
                    _inMemoryLogLines.Add("Silksong Mobile PC Mod Compatibility API v2.9.0 (ModLog.log)");
                    _inMemoryLogLines.Add($"Started: {_mogLogStartTime:yyyy-MM-dd HH:mm:ss}");
                    _mogLogDirty = true;
                }
                WriteStructuredModLogToDisk(new System.Text.StringBuilder(8192));
            }
            catch { }
        }

        private static void EnsureAsyncLogWorkerStarted()
        {
            if (_asyncLogWorkerStarted) return;
            _asyncLogWorkerStarted = true;
            try
            {
                var th = new System.Threading.Thread(AsyncLogWriterLoop)
                {
                    Name = "SilksongMobileAPI_ModLogWriter",
                    IsBackground = true
                };
                th.Start();
                Application.quitting += FlushAsyncDiagnosticLogs;
            }
            catch { }
        }

        private void OnApplicationQuit()
        {
            FlushAsyncDiagnosticLogs();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus) FlushAsyncDiagnosticLogs();
        }

        private static void AsyncLogWriterLoop()
        {
            var sb = new System.Text.StringBuilder(16384);
            while (true)
            {
                _asyncLogWake.WaitOne(350);
                if (_mogLogDirty)
                {
                    WriteStructuredModLogToDisk(sb);
                }
            }
        }

        public static void FlushAsyncDiagnosticLogs()
        {
            try
            {
                WriteStructuredModLogToDisk(new System.Text.StringBuilder(16384));
            }
            catch { }
        }

        private static void WriteSectionBlock(System.Text.StringBuilder sb, string title, List<string> items, string emptyText)
        {
            sb.AppendLine("==============================================================================");
            sb.AppendLine($"  {title} ({items.Count} entries)");
            sb.AppendLine("==============================================================================");
            if (items.Count == 0)
            {
                sb.AppendLine($"  {emptyText}");
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    sb.AppendLine(items[i]);
                }
            }
            sb.AppendLine();
        }

        private static void WriteStructuredModLogToDisk(System.Text.StringBuilder sb)
        {
            if (string.IsNullOrEmpty(DiagnosticLogPath)) return;
            lock (_logLock)
            {
                _mogLogDirty = false;
                sb.Clear();
                sb.AppendLine("##############################################################################");
                sb.AppendLine("#                        MODLOG - IRIS v2.9.6 REPORT                         #");
                sb.AppendLine("##############################################################################");
                sb.AppendLine();
                sb.AppendLine("==============================================================================");
                sb.AppendLine("  [SECTION 1: SYSTEM & API OVERVIEW]");
                sb.AppendLine("==============================================================================");
                sb.AppendLine($"  Started           : {_mogLogStartTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  Last Updated      : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"  BepInEx Root Path : {Paths.BepInExRootPath}");
                sb.AppendLine($"  Loaded PC Mods    : [{string.Join(", ", _loadedModNames)}]");
                sb.AppendLine($"  Shaders Repaired  : {AssetHelperBridge.RepairedShaderCount}");
                sb.AppendLine($"  VRAM Downscaled   : {AssetHelperBridge.DownscaledTextureCount} (Cap: {AssetHelperBridge.MaxModTextureDimension}px)");
                sb.AppendLine($"  Active Save Slot  : slot_{HarmonyBridge.CurrentSaveSlotIndex}");
                sb.AppendLine($"  Error Summary     : HookErrors={_secHookErrors.Count} | ModRuntimeExceptions={_secModRuntimeExceptions.Count} | FsmAssetWarnings={_secFsmAndAssets.Count} | ApiCatches={_secApiCatches.Count} | UnityCrashes={_secUnityAndCrashes.Count}");
                sb.AppendLine();

                WriteSectionBlock(sb, "[SECTION 2: MOD LOADING, CECIL REWRITER & LOCALIZATION]", _secModLoading, "No mod loading issues recorded.");
                WriteSectionBlock(sb, "[SECTION 3: HARMONY, MONODETOUR & NATIVE HOOK ERRORS]", _secHookErrors, "Zero hook installation or parameter mismatch errors.");
                WriteSectionBlock(sb, "[SECTION 4: RUNTIME MOD EXCEPTIONS (PREFIX / POSTFIX / MONOBEHAVIOUR)]", _secModRuntimeExceptions, "Zero runtime exceptions thrown by mods.");
                WriteSectionBlock(sb, "[SECTION 5: PLAYMAKER FSM, ASSETHELPER & SHADER/VRAM REPORT]", _secFsmAndAssets, "Zero PlayMaker FSM or AssetHelper extraction warnings.");
                WriteSectionBlock(sb, "[SECTION 6: API INTERNAL CATCHES & FALLBACK DIAGNOSTICS]", _secApiCatches, "Zero internal API fallback catches.");
                WriteSectionBlock(sb, "[SECTION 7: UNITY ENGINE ERRORS, ASSERTIONS & UNHANDLED CRASHES]", _secUnityAndCrashes, "Zero Unity engine errors or unhandled exceptions.");
                WriteSectionBlock(sb, "[SECTION 8: FULL CHRONOLOGICAL DIAGNOSTIC TIMELINE]", _secTimeline, "No timeline entries yet.");

                try
                {
                    File.WriteAllText(DiagnosticLogPath, sb.ToString());
                }
                catch { }
            }
        }

        public static void RecordCrashOrThreadedError(string category, string message, string stackTrace)
        {
            string entry = string.IsNullOrEmpty(stackTrace)
                ? $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}"
                : $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}\n{stackTrace.TrimEnd()}";
            lock (_logLock)
            {
                if (_secUnityAndCrashes.Count < 250)
                    _secUnityAndCrashes.Add(entry);
                if (_secTimeline.Count < 800)
                    _secTimeline.Add(entry);
                if (_inMemoryLogLines.Count < 400)
                    _inMemoryLogLines.Add(entry);
                _mogLogDirty = true;
            }
            _asyncLogWake.Set();
        }

        private static void AppendToDiagnosticFile(string level, string msg)
        {
            try
            {
                EnsureAsyncLogWorkerStarted();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
                lock (_logLock)
                {
                    if (_inMemoryLogLines.Count < 400)
                        _inMemoryLogLines.Add(line);
                    if (_secTimeline.Count < 800)
                        _secTimeline.Add(line);

                    if (level == "UNITY_EX" || level == "UNITY_ERR")
                    {
                        if (_secUnityAndCrashes.Count < 250) _secUnityAndCrashes.Add(line);
                    }
                    else if (level == "API_WARN")
                    {
                        if (_secApiCatches.Count < 250) _secApiCatches.Add(line);
                    }
                    else if (msg.IndexOf("[PCMod Error", StringComparison.Ordinal) >= 0)
                    {
                        if (msg.IndexOf("PatchAll", StringComparison.Ordinal) >= 0 || msg.IndexOf("InstallNativeHook", StringComparison.Ordinal) >= 0)
                        {
                            if (_secHookErrors.Count < 250) _secHookErrors.Add(line);
                        }
                        else
                        {
                            if (_secModRuntimeExceptions.Count < 250) _secModRuntimeExceptions.Add(line);
                        }
                    }
                    else if (msg.IndexOf("[AdapterBuilder]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[CecilRuntimeRewriter]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[HarmonyBridge]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[MonoDetour]", StringComparison.Ordinal) >= 0)
                    {
                        if (level == "WARN" || level == "ERROR" || msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (_secHookErrors.Count < 250) _secHookErrors.Add(line);
                        }
                        else if (_secModLoading.Count < 300)
                        {
                            _secModLoading.Add(line);
                        }
                    }
                    else if (msg.IndexOf("[AssetHelperBridge]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[PlayMakerHybridCLRBridge]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[SceneBreadcrumb]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[UnifiedFsmUtil]", StringComparison.Ordinal) >= 0)
                    {
                        if (_secFsmAndAssets.Count < 250) _secFsmAndAssets.Add(line);
                    }
                    else if (msg.IndexOf("[PCMod:", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("Loading PC Mod DLL:", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("Inspecting PC Mod:", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[PreOrganize]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("[I18NBridge]", StringComparison.Ordinal) >= 0 ||
                             msg.IndexOf("scan complete", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (_secModLoading.Count < 300) _secModLoading.Add(line);
                    }
                    else if (level == "ERROR" || level == "WARN")
                    {
                        if (_secModRuntimeExceptions.Count < 250) _secModRuntimeExceptions.Add(line);
                    }

                    _mogLogDirty = true;
                }
                if (level == "ERROR" || level == "WARN" || level == "UNITY_EX" || level == "API_WARN")
                    _asyncLogWake.Set();
            }
            catch { }
        }

        public static void LogDiagnosticInfo(string msg)
        {
            StaticLogger?.LogInfo(msg);
            AppendToDiagnosticFile("INFO", msg);
        }

        public static void LogDiagnosticWarn(string msg)
        {
            StaticLogger?.LogWarning(msg);
            AppendToDiagnosticFile("WARN", msg);
        }

        public static void LogDiagnosticError(string msg)
        {
            StaticLogger?.LogError(msg);
            AppendToDiagnosticFile("ERROR", msg);
        }

        private static bool _crashHooksInstalled;
        private static int _unityErrorLogCount;

        public static void InstallCrashAndLogcatCapture()
        {
            if (_crashHooksInstalled) return;
            _crashHooksInstalled = true;
            try
            {
                Application.logMessageReceived -= OnUnityLogMessageReceived;
                Application.logMessageReceived += OnUnityLogMessageReceived;
            }
            catch (System.Exception __apiEx_49) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L3061", __apiEx_49); }
        }

        private static void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)
                return;
            if (string.IsNullOrEmpty(condition))
                return;
            if (condition.IndexOf("[SilksongMobileAPI]", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("Silksong Mobile PC Mod Compatibility API", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("Cannot set the parent of the GameObject", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("Sprite not found attached to tk2dSpriteAnimator", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("Could not parse crest id", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("Could not load crest ", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("MatchVisualsWithConfigs", StringComparison.Ordinal) >= 0 ||
                condition.IndexOf("get_fsm: Fsm not initialized", StringComparison.Ordinal) >= 0)
                return;
            if ((AssetHelperBridge.IsExtractingPharloomsGloryScene || AssetHelperBridge.IsAnyRepackerBusy()) &&
                condition.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0)
                return;

            int n = System.Threading.Interlocked.Increment(ref _unityErrorLogCount);
            if (n <= 120)
            {
                AppendToDiagnosticFile(
                    type == LogType.Exception ? "UNITY_EX" : "UNITY_ERR",
                    $"{condition}\n{stackTrace}".Trim());
            }
        }

        [ThreadStatic]
        private static bool _inLogApiCatch;

        public static void LogApiCatch(string module, string site, Exception ex)
        {
            if (ex == null || _inLogApiCatch) return;
            _inLogApiCatch = true;
            try
            {
                var actual = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                string siteKey = $"API::{module}::{site}::{actual.GetType().Name}";
                int count;
                lock (_errorCountBySite)
                {
                    _errorCountBySite.TryGetValue(siteKey, out count);
                    count++;
                    _errorCountBySite[siteKey] = count;
                }
                int maxPerSite = (CfgEnableVerboseApiLogging == null || CfgEnableVerboseApiLogging.Value) ? 12 : 4;
                if (count <= maxPerSite)
                {
                    string msg = count == 1
                        ? $"[API][{module}][{site}] Caught {actual.GetType().Name}: {actual.Message}\n{actual.StackTrace}"
                        : $"[API][{module}][{site}] (#{count}) Caught {actual.GetType().Name}: {actual.Message}";
                    Log?.LogWarning(msg);
                    AppendToDiagnosticFile("API_WARN", msg);
                }
            }
            catch
            {
            }
            finally
            {
                _inLogApiCatch = false;
            }
        }

        public static void LogApiTrace(string module, string message)
        {
            if (CfgEnableVerboseApiLogging != null && !CfgEnableVerboseApiLogging.Value)
                return;
            string formatted = $"[API][{module}] {message}";
            Log?.LogInfo(formatted);
            AppendToDiagnosticFile("TRACE", formatted);
        }

        public static void LogModException(string modName, string context, Exception ex)
        {
            if (ex == null) return;
            var actual = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
            string siteKey = $"{modName}::{context}::{actual.GetType().Name}";
            int count;
            lock (_errorCountBySite)
            {
                _errorCountBySite.TryGetValue(siteKey, out count);
                count++;
                _errorCountBySite[siteKey] = count;
            }
            if (count <= 10)
            {
                string formatted = $"[PCMod Error in '{modName}' at {context}] (#{count}):\n{actual}";
                LogDiagnosticError(formatted);
            }
        }
    }

    public sealed class ModConsoleIMGUIRenderer : MonoBehaviour
    {
        private void OnGUI()
        {
            SilksongMobileAPIPlugin.Instance?.RenderModConsoleIMGUI();
        }
    }

    /// <summary>
    /// Ultra-lightweight pure uGUI Preloader Screen (integrated from FasterUI v2.5).
    /// Zero OnGUI, zero per-frame allocations, single canvas batch, smooth 60 FPS animation.
    /// </summary>
    public class ModPreloaderScreen : MonoBehaviour
    {
        public static ModPreloaderScreen Instance { get; private set; }
        public static bool IsActive { get; private set; }
        public static bool IsOperationMode { get; set; }
        public static float Progress { get; set; }

        private static string _statusText = "Initializing IRIS...";
        public static string StatusText
        {
            get => _statusText;
            set
            {
                string next = value ?? "";
                if (!string.Equals(_statusText, next, StringComparison.Ordinal))
                {
                    _statusText = next;
                    AdvanceRandomIrisMessage();
                }
            }
        }

        public static float FadeAlpha { get; private set; } = 1f;

        // 40 Unique Loading Messages (10 User Messages + 30 Silksong/Gaming Messages — No Leading Numbers)
        public static readonly string[] IrisLoadingMessages = new string[]
        {
            "Riding BEEG YOSHI",
            "8-3 /// THIRD DISINTEGRATION LOOP",
            "Inviting Zote In Silksong",
            "Checking For Any Kiss Mods",
            "Waking Up Unity's Particle System (Piano noises)",
            "Finding Maidens For The User",
            "Doing The Groal Runback Please Wait 2 Hours",
            "Finishing Loading Before GTA VI",
            "Getting The Yee-Yee Ass Haircut From The Barber",
            "Introducing User To Free And Open Source Operating System Linux",
            "Polishing Hornet's Needle To A Mirror Shine",
            "Tuning The Needolin Strings In Pharloom",
            "Teaching Sherma A Brand New Bell Song",
            "Bribing Shakra For A Map Marker",
            "Counting Every Single Rosary Bead Twice",
            "Asking Team Cherry For The Release Date Again",
            "Taking Off The Clown Makeup One Last Time",
            "Sweeping Up Shell Shards Across Greymoor",
            "Oiling The Gears Inside The Cogwork Core",
            "Practicing Stylish Parries Against Lace",
            "Waking Up The Bell Beast For Fast Travel",
            "Hiding Extra Fleas In Hard-To-Reach Corners",
            "Spooling Fresh Silk For The Next Boss Fight",
            "Brewing Fleabrew At The Halfway Home",
            "Sharpening Every Crest On The Workbench",
            "Politely Asking The Last Judge To Calm Down",
            "Download More RAM? No, Just Optimizing IL2CPP",
            "Warming Up The Bench In Bonebottom",
            "Untangling Silk Threads Inside The Citadel",
            "Convincing Trobbio To Tone Down The Fireworks",
            "Teaching The Moss Mother How To Dodge",
            "Locking The Doors To Deep Docks",
            "Checking Behind Every Waterfall For Secret Rooms",
            "Calibrating 60 FPS Silk Physics",
            "Feeding Shell Shards To The Forge Daughter",
            "Looking For A Bench That Doesn't Cost Rosaries",
            "Pre-Warming The Boss Arena Spotlights",
            "Translating Ancient Weaver Tablets",
            "Making Sure Hornet Says SHAW At Maximum Volume",
            "Opening The Golden Gates Of The Citadel",
            "This Compatability Layer Is Sponsored By Raid Shadow Legends",
            "Delivering Mods To The Mask Maker"
        };

        private static int _currentMessageIndex = -1;
        private static int _completedStepCounter = 0;
        private static readonly System.Random _msgRng = new System.Random();

        public static string CurrentIrisMessage
        {
            get
            {
                if (_currentMessageIndex < 0 || _currentMessageIndex >= IrisLoadingMessages.Length)
                {
                    _currentMessageIndex = _msgRng.Next(0, IrisLoadingMessages.Length);
                }
                return IrisLoadingMessages[_currentMessageIndex];
            }
        }

        public static void AdvanceRandomIrisMessage()
        {
            int count = IrisLoadingMessages.Length;
            if (count <= 1)
            {
                _currentMessageIndex = 0;
                return;
            }
            int next = _msgRng.Next(0, count - 1);
            if (next >= _currentMessageIndex) next++;
            _currentMessageIndex = next;
            _completedStepCounter++;
            if (Instance != null && Instance._centerMessageTextComp != null)
            {
                Instance._centerMessageTextComp.text = IrisLoadingMessages[_currentMessageIndex];
            }
        }

        private CanvasGroup _canvasGroup;
        private RectTransform _fillBarRect;
        private UnityEngine.UI.Text _topVersionTextComp;
        private UnityEngine.UI.Text _topPctTextComp;
        private UnityEngine.UI.Text _centerBrandTextComp;
        private UnityEngine.UI.Text _centerMessageTextComp;
        private UnityEngine.UI.Text _bottomStatusTextComp;
        private UnityEngine.UI.Text _bottomStepBadgeTextComp;

        private float _barMaxWidth = 1920f;
        private int _lastDisplayedPct = -1;
        private string _lastStatusStr = "";
        private int _lastDisplayedMsgIdx = -1;
        private bool _fadingOut;

        private static Sprite _whiteSprite;
        private static Font _cachedFont;

        private static Sprite GetWhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            tex.Apply(false, true);
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
            return _whiteSprite;
        }

        private static Font GetUiFont()
        {
            if (_cachedFont != null) return _cachedFont;
            _cachedFont = HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _cachedFont;
        }

        private void Awake()
        {
            Instance = this;
            IsActive = true;
            FadeAlpha = 1f;
            if (_currentMessageIndex < 0)
                AdvanceRandomIrisMessage();
            if (!IsOperationMode)
            {
                Progress = 0.05f;
                StatusText = "Initializing IRIS...";
            }
            BuildCanvasHierarchy();
        }

        private void Start()
        {
            if (!IsOperationMode)
            {
                StartCoroutine(RunPreloader());
            }
        }

        public static void ShowOperationOverlay(string title, string subtitle, string status, float progress = 0.08f)
        {
            try
            {
                // Never show loading screen for PatchWork-related tasks
                if ((!string.IsNullOrEmpty(title) && title.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(subtitle) && subtitle.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(status) && status.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return;
                }

                IsOperationMode = true;
                IsActive = true;
                FadeAlpha = 1f;
                Progress = Mathf.Clamp01(progress);
                if (!string.IsNullOrEmpty(status))
                    StatusText = status;
                else
                    AdvanceRandomIrisMessage();

                if (Instance == null)
                {
                    var go = new GameObject("IRIS_ModPreloader");
                    DontDestroyOnLoad(go);
                    Instance = go.AddComponent<ModPreloaderScreen>();
                }
                else
                {
                    Instance.StopAllCoroutines();
                    Instance._fadingOut = false;
                    if (Instance._canvasGroup != null)
                        Instance._canvasGroup.alpha = 1f;
                    Instance.gameObject.SetActive(true);
                }

                Instance.RefreshStealthRibbonTexts();
            }
            catch (System.Exception __apiEx_50) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L3311", __apiEx_50); }
        }

        public static void UpdateOperationOverlay(string status, float progress)
        {
            try
            {
                if (!string.IsNullOrEmpty(status) && status.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;
                Progress = Mathf.Clamp01(progress);
                if (!string.IsNullOrEmpty(status))
                    StatusText = status;
                if (Instance != null)
                    Instance.RefreshStealthRibbonTexts();
            }
            catch (System.Exception __apiEx_51) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L3327", __apiEx_51); }
        }

        public static void FinishOperationOverlay(string finalStatus, float holdSeconds = 0.35f)
        {
            try
            {
                Progress = 1.0f;
                if (!string.IsNullOrEmpty(finalStatus))
                    StatusText = finalStatus;
                if (Instance != null && Instance.gameObject != null && Instance.gameObject.activeInHierarchy)
                {
                    Instance.RefreshStealthRibbonTexts();
                    Instance.StopAllCoroutines();
                    Instance.StartCoroutine(Instance.CoFadeOutOperationOverlay(holdSeconds));
                }
                else
                {
                    IsActive = false;
                    IsOperationMode = false;
                }
            }
            catch (System.Exception __apiEx_52) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L3350", __apiEx_52); }
        }

        private IEnumerator CoFadeOutOperationOverlay(float holdSeconds)
        {
            _fadingOut = false;
            FadeAlpha = 1f;
            if (_canvasGroup != null) _canvasGroup.alpha = 1f;
            yield return new WaitForSecondsRealtime(Mathf.Max(0.15f, holdSeconds));
            _fadingOut = true;
            while (FadeAlpha > 0.01f)
            {
                FadeAlpha -= Time.unscaledDeltaTime * 2.8f;
                if (_canvasGroup != null)
                    _canvasGroup.alpha = Mathf.Clamp01(FadeAlpha);
                yield return null;
            }
            IsActive = false;
            IsOperationMode = false;
            Destroy(gameObject);
        }

        // =====================================================================
        // Style 04: Stealth Cinema Ribbon (Pure Flat Dark #060608 / #0D0D11, Zero Gradients)
        // =====================================================================
        private void BuildCanvasHierarchy()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            canvas.pixelPerfect = false;

            var scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 1f;
            _canvasGroup.blocksRaycasts = true;
            _canvasGroup.interactable = false;

            var rootRect = GetComponent<RectTransform>();

            // Colors (Stealth Cinema Ribbon — Flat Dark Mode, Zero Gradients)
            Color colBackdrop = new Color32(6, 6, 8, 255);      // #060608
            Color colStripBg = new Color32(13, 13, 17, 255);   // #0D0D11
            Color colBorder = new Color32(34, 34, 44, 255);    // #22222C
            Color colTrack = new Color32(23, 23, 31, 255);     // #17171F
            Color colPrimary = new Color32(236, 236, 239, 255);// #ECECEF
            Color colMuted = new Color32(154, 154, 168, 255);  // #9A9AA8
            Color colDim = new Color32(110, 110, 128, 255);    // #6E6E80
            Color colBadgeBg = new Color32(19, 19, 26, 255);   // #13131A

            // 1. Pure Flat Dark Fullscreen Backdrop (#060608)
            CreatePanel(rootRect, "CinemaBackdrop", Vector2.zero, new Vector2(2400f, 1400f), colBackdrop);

            // 2. Top Letterbox Strip (#0D0D11, height 72px) + 1px Bottom Border (#22222C)
            var topStrip = CreatePanel(rootRect, "TopLetterboxStrip", new Vector2(0f, 504f), new Vector2(1920f, 72f), colStripBg).rectTransform;
            CreatePanel(topStrip, "TopStripBorder", new Vector2(0f, -36f), new Vector2(1920f, 1.5f), colBorder);

            // Left Square Indicator + "IRIS v2.9.6"
            CreatePanel(topStrip, "TopSquareDot", new Vector2(-896f, 0f), new Vector2(10f, 10f), colPrimary);
            _topVersionTextComp = CreateLabel(
                topStrip,
                "TopVersionLabel",
                new Vector2(-420f, 0f),
                new Vector2(920f, 40f),
                "IRIS v2.9.6",
                20,
                FontStyle.Bold,
                colPrimary,
                TextAnchor.MiddleLeft);

            // Right Percentage Counter in Top Strip
            _topPctTextComp = CreateLabel(
                topStrip,
                "TopPctLabel",
                new Vector2(700f, 0f),
                new Vector2(400f, 40f),
                "5%",
                22,
                FontStyle.Bold,
                colPrimary,
                TextAnchor.MiddleRight);

            // 3. Center Cinema Space — "IRIS / <Random Message>"
            _centerBrandTextComp = CreateLabel(
                rootRect,
                "CenterIrisBrand",
                new Vector2(0f, 34f),
                new Vector2(1600f, 56f),
                "IRIS",
                42,
                FontStyle.Bold,
                colPrimary,
                TextAnchor.MiddleCenter);

            CreatePanel(rootRect, "CenterDivider", new Vector2(0f, -4f), new Vector2(96f, 2f), colBorder);

            _centerMessageTextComp = CreateLabel(
                rootRect,
                "CenterRandomMessage",
                new Vector2(0f, -42f),
                new Vector2(1720f, 48f),
                CurrentIrisMessage,
                25,
                FontStyle.Normal,
                colMuted,
                TextAnchor.MiddleCenter);

            // 4. Bottom Letterbox Strip (#0D0D11, height 84px) + 3px Razor-Sharp Edge-to-Edge Progress Line (#ECECEF)
            var bottomStrip = CreatePanel(rootRect, "BottomLetterboxStrip", new Vector2(0f, -498f), new Vector2(1920f, 84f), colStripBg).rectTransform;
            var barTrack = CreatePanel(bottomStrip, "BottomProgressTrack", new Vector2(0f, 40.5f), new Vector2(1920f, 3.5f), colTrack).rectTransform;

            var fillImg = CreatePanel(barTrack, "BottomProgressFill", Vector2.zero, new Vector2(0f, 3.5f), colPrimary);
            _fillBarRect = fillImg.rectTransform;
            _fillBarRect.anchorMin = new Vector2(0f, 0.5f);
            _fillBarRect.anchorMax = new Vector2(0f, 0.5f);
            _fillBarRect.pivot = new Vector2(0f, 0.5f);
            _fillBarRect.anchoredPosition = Vector2.zero;

            _bottomStatusTextComp = CreateLabel(
                bottomStrip,
                "BottomStatusLabel",
                new Vector2(-200f, -2f),
                new Vector2(1400f, 40f),
                StatusText,
                19,
                FontStyle.Normal,
                colMuted,
                TextAnchor.MiddleLeft);

            var badgeBox = CreatePanel(bottomStrip, "BottomStepBadge", new Vector2(810f, -2f), new Vector2(184f, 36f), colBadgeBg).rectTransform;
            _bottomStepBadgeTextComp = CreateLabel(
                badgeBox,
                "BottomStepBadgeText",
                Vector2.zero,
                new Vector2(176f, 32f),
                "IRIS v2.9.6",
                15,
                FontStyle.Bold,
                colDim,
                TextAnchor.MiddleCenter);

            RefreshStealthRibbonTexts();
        }

        private void RefreshStealthRibbonTexts()
        {
            float clampedProg = Mathf.Clamp01(Progress);
            int pct = Mathf.RoundToInt(clampedProg * 100f);
            if (_fillBarRect != null)
                _fillBarRect.sizeDelta = new Vector2(_barMaxWidth * clampedProg, 3.5f);
            if (_topPctTextComp != null)
                _topPctTextComp.text = pct.ToString() + "%";
            if (_centerMessageTextComp != null)
                _centerMessageTextComp.text = CurrentIrisMessage;
            if (_bottomStatusTextComp != null)
                _bottomStatusTextComp.text = StatusText;
            if (_bottomStepBadgeTextComp != null)
                _bottomStepBadgeTextComp.text = pct >= 100 ? "READY" : ("STEP " + Math.Max(1, _completedStepCounter).ToString());
        }

        private static UnityEngine.UI.Image CreatePanel(Transform parent, string name, Vector2 anchoredPos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.sprite = GetWhiteSprite();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static UnityEngine.UI.Text CreateLabel(Transform parent, string name, Vector2 anchoredPos, Vector2 size, string text, int fontSize, FontStyle style, Color color, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var txt = go.GetComponent<UnityEngine.UI.Text>();
            txt.font = GetUiFont();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.alignment = align;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.color = color;
            txt.raycastTarget = false;
            return txt;
        }

        private float _watchdogElapsed;
        private bool _isActivelyPreloading;

        private void Update()
        {
            if (!IsActive && !_fadingOut) return;

            _watchdogElapsed += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            if (_isActivelyPreloading || AssetHelperBridge.IsExtractingPharloomsGloryScene || AssetHelperBridge.IsAnyRepackerBusy())
            {
                if (_watchdogElapsed > 25f) _watchdogElapsed = 25f;
                string extSc = AssetHelperBridge.PharloomsGloryCurrentExtractingScene;
                if (!string.IsNullOrEmpty(extSc))
                {
                    StatusText = "Pre-caching Scene Assets (" + extSc + ")...";
                }
                int totalSc = Math.Max(1, AssetHelperBridge.PharloomsGloryScenesTotal);
                int doneSc = Mathf.Clamp(AssetHelperBridge.PharloomsGloryScenesDone, 0, totalSc);
                Progress = Mathf.Max(Progress, Mathf.Lerp(0.70f, 0.96f, doneSc / (float)totalSc));
            }
            if (_watchdogElapsed > 58.0f && !_fadingOut)
            {
                Progress = 1.0f;
                StatusText = "IRIS Ready!";
                _fadingOut = true;
            }
            if (_watchdogElapsed > 60.0f)
            {
                TrySetBackgroundLoadingPriority(ThreadPriority.Normal);
                IsActive = false;
                SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
                Destroy(gameObject);
                return;
            }

            float clampedProg = Mathf.Clamp01(Progress);
            float fillW = _barMaxWidth * clampedProg;
            if (_fillBarRect != null)
                _fillBarRect.sizeDelta = new Vector2(fillW, 3.5f);

            int pct = Mathf.RoundToInt(clampedProg * 100f);
            if (pct != _lastDisplayedPct || !ReferenceEquals(StatusText, _lastStatusStr) || _currentMessageIndex != _lastDisplayedMsgIdx)
            {
                _lastDisplayedPct = pct;
                _lastStatusStr = StatusText;
                _lastDisplayedMsgIdx = _currentMessageIndex;
                RefreshStealthRibbonTexts();
            }
        }

        private static void TrySetBackgroundLoadingPriority(ThreadPriority prio)
        {
            try
            {
                var prop = typeof(Application).GetProperty("backgroundLoadingPriority", BindingFlags.Static | BindingFlags.Public);
                prop?.GetSetMethod()?.Invoke(null, new object[] { prio });
            }
            catch (System.Exception __apiEx_53) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("SilksongMobileAPIPlugin", "L3583", __apiEx_53); }
        }

        private static IEnumerator SafeRunSubCoroutine(string stepName, Func<IEnumerator> factory)
        {
            IEnumerator inner = null;
            try { inner = factory?.Invoke(); }
            catch (Exception ex) { SilksongMobileAPIPlugin.LogApiCatch("FastPreloaderUI", stepName + ".Create", ex); }
            if (inner == null) yield break;

            int maxSteps = 240;
            while (maxSteps-- > 0)
            {
                bool hasNext = false;
                object cur = null;
                try
                {
                    hasNext = inner.MoveNext();
                    if (hasNext) cur = inner.Current;
                }
                catch (Exception ex)
                {
                    SilksongMobileAPIPlugin.LogApiCatch("FastPreloaderUI", stepName + ".MoveNext", ex);
                    break;
                }
                if (!hasNext) break;
                yield return cur;
            }
        }

        private IEnumerator RunPreloader()
        {
            SilksongMobileAPIPlugin.LogDiagnosticInfo("[FastPreloaderUI] Started Stealth Cinema Ribbon 60 FPS preloading sequence (IRIS v2.9.6)...");
            TrySetBackgroundLoadingPriority(ThreadPriority.High);
            yield return null;

            StatusText = "Configuring Pharloom Boss & Arena Bridges...";
            for (int i = 0; i < 6; i++)
            {
                Progress = Mathf.Lerp(0.08f, 0.32f, (i + 1) / 6f);
                yield return null;
            }
            try { AssetHelperBridge.EnsureBonemasterAlwaysHisEnabled(); } catch { }
            AdvanceRandomIrisMessage();
            yield return null;

            StatusText = "Preparing Mod Hooks & Asset Bridges...";
            for (int i = 0; i < 6; i++)
            {
                Progress = Mathf.Lerp(0.32f, 0.55f, (i + 1) / 6f);
                yield return null;
            }

            StatusText = "Synchronizing AssetHelper & Localization Tables...";
            try { HarmonyBridge.SyncModLocalizationIntoGame(); } catch { }
            AdvanceRandomIrisMessage();
            for (int i = 0; i < 6; i++)
            {
                Progress = Mathf.Lerp(0.78f, 0.92f, (i + 1) / 6f);
                yield return null;
            }

            StatusText = "Awaiting Title Scene for Batch Asset Extraction...";
            int waitMenu = 0;
            while (waitMenu++ < 1200)
            {
                if (_watchdogElapsed > 15f) _watchdogElapsed = 15f;
                var sc = SceneManager.GetActiveScene();
                if (sc.IsValid() &&
                    (string.Equals(sc.name, "Menu_Title", StringComparison.OrdinalIgnoreCase) ||
                     (HeroController.instance != null && GameManager.instance != null)))
                {
                    break;
                }
                Progress = Mathf.Lerp(0.65f, 0.72f, Mathf.Clamp01(waitMenu / 600f));
                yield return null;
            }
            AdvanceRandomIrisMessage();

            _isActivelyPreloading = true;
            StatusText = "Pre-caching Boss & Arena Scene Templates...";
            Progress = 0.72f;
            bool prevAudioPause = AudioListener.pause;
            try { AudioListener.pause = true; } catch { }
            yield return StartCoroutine(AssetHelperBridge.RunPreloaderPharloomsGloryBatchExtract());
            AdvanceRandomIrisMessage();
            yield return SafeRunSubCoroutine("CoEnsureBonemasterShelfPreloaded", () => AssetHelperBridge.CoEnsureBonemasterShelfPreloaded(forcePreload: true));
            AdvanceRandomIrisMessage();
            yield return SafeRunSubCoroutine("CoPreloadRevoltWidowTemplate", AssetHelperBridge.CoPreloadRevoltWidowTemplate);
            AdvanceRandomIrisMessage();
            StatusText = "Pre-caching Weaver Crest & White Skill Bundles...";
            Progress = 0.90f;
            yield return SafeRunSubCoroutine("CoWarmupWeaverCrestAbilities", AssetHelperBridge.CoWarmupWeaverCrestAbilities);
            AdvanceRandomIrisMessage();
            try { AudioListener.pause = prevAudioPause; } catch { }
            _isActivelyPreloading = false;

            StatusText = "IRIS Ready!";
            Progress = 1.0f;
            yield return new WaitForSecondsRealtime(0.12f);

            _fadingOut = true;
            while (FadeAlpha > 0.01f)
            {
                FadeAlpha -= Time.unscaledDeltaTime * 3.0f;
                if (_canvasGroup != null)
                    _canvasGroup.alpha = Mathf.Clamp01(FadeAlpha);
                yield return null;
            }

            TrySetBackgroundLoadingPriority(ThreadPriority.Normal);
            IsActive = false;
            SilksongMobileAPIPlugin.LogDiagnosticInfo("[FastPreloaderUI] Stealth Cinema Ribbon preloader completed smoothly (0 OnGUI allocations).");
            SilksongMobileAPIPlugin.FlushAsyncDiagnosticLogs();
            Destroy(gameObject);
        }

        private static void TryForcePreloaderGarbageCollection()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false, false);
            }
            catch { }
        }

        private void OnDestroy()
        {
            IsActive = false;
            IsOperationMode = false;
            if (Instance == this) Instance = null;
        }
    }

    [HarmonyPatch(typeof(UIManager), "UIGoToModListMenu")]
    public static class UIManagerModListHook
    {
        public static bool Prefix()
        {
            NativeModMenu.OpenInstalledModsScreen();
            return false;
        }
    }
}
