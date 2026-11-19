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
                    var pluginSubDirs = Directory.GetDirectories(subPlugins);
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
            LogDiagnosticInfo($"IRIS v2.9.6.17 scan complete. Loaded PC Mod assemblies: [{string.Join(", ", _loadedModNames)}]");
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
            "HkCore",
            "HkBossLoader",
            "SsBossLoader",
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
