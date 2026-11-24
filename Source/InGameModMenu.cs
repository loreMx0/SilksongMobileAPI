using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using GlobalEnums;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SilksongMobileAPI
{
    // =========================================================================
    // Data Models
    // =========================================================================
    public sealed class InstalledModItem
    {
        public string FolderName;
        public string DisplayName;
        public string Version = "1.0.0";
        public string Author = "Unknown";
        public string Description = "";
        public string DirectoryPath;
        public string PrimaryDllPath;
        public bool IsEnabled = true;
        public bool IsCore = false;
        public long SizeBytes = 0;
        public bool PendingDeleteConfirm = false;
    }

    public sealed class OnlineModCatalogItem
    {
        public string PackageName;
        public string FullName;
        public string Author;
        public string Version;
        public string DisplayTitle;
        public string Description;
        public string DownloadUrl;
        public bool IsCompatible = true;
        public string CompatibilityReason = "";
        public bool RequiresAdaptation = false;
        public string AdaptationDetails = "";
        public bool IsInstalled = false;
        public float DownloadProgress = 0f;
        public string InstallStatus = "Ready"; // "Ready", "Downloading", "Extracting", "Installed", "Incompatible", "Failed"
        public string ErrorMessage = "";
        public int FileSizeBytes = 0;
        public List<string> Dependencies = new List<string>();
    }

    // =========================================================================
    // InGameModManager: Scans device mods, handles downloads, extraction & adaptations
    // =========================================================================
    public class InGameModManager : MonoBehaviour
    {
        public static InGameModManager Instance { get; private set; }

        public readonly List<InstalledModItem> InstalledMods = new List<InstalledModItem>();
        public readonly List<OnlineModCatalogItem> OnlineCatalog = new List<OnlineModCatalogItem>();

        public bool IsScanning { get; private set; }
        public bool IsFetchingStore { get; private set; }
        public bool IsDownloading { get; private set; }
        public string GlobalStatusMessage = "";
        public int TotalOnlineModsFound = 0;
        public bool ShowOnlyCompatibleMods = true;

        private static readonly HashSet<string> CoreFolderGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "00_IRIS",
            "IRIS",
            "00_SilksongMobileAPI",
            "SilksongMobileAPI"
        };

        private static readonly HashSet<string> KnownPcAssetBundleMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KnightInSilksong",
            "shownyoung-KnightInSilksong",
            "Knight_In_Silksong",
            "ForgottenKingdoms",
            "ForgottenKingdoms-ForgottenKingdoms",
            "Forgotten_Kingdoms",
            "BepInExPack_Silksong",
            "silksong_modding-BepInExPack_Silksong",
            "Architect",
            "cometcake575-Architect",
            "Build_Your_Own_Bench",
            "BuildYourOwnBench",
            "OrionBonnie-Build_Your_Own_Bench"
        };

        public static bool IsArchitectOrDependentPackage(string name, string fullName, IEnumerable<string> dependencies, string description = null)
        {
            // HkCore & HkBossLoader are removed from Install Mods List ONLY (still work if placed in BepInEx/plugins/)
            if (SilksongMobileAPIPlugin.IsHkCoreOrBossLoaderIdentifier(name) ||
                SilksongMobileAPIPlugin.IsHkCoreOrBossLoaderIdentifier(fullName))
                return true;
            if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(name) ||
                SilksongMobileAPIPlugin.IsExcludedModIdentifier(fullName) ||
                SilksongMobileAPIPlugin.IsStitchworkSkinOrAssetPackName(name) ||
                SilksongMobileAPIPlugin.IsStitchworkSkinOrAssetPackName(fullName) ||
                SilksongMobileAPIPlugin.IsSsmpOrAddonIdentifier(name) ||
                SilksongMobileAPIPlugin.IsSsmpOrAddonIdentifier(fullName))
                return true;
            if (dependencies != null)
            {
                foreach (var dep in dependencies)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(dep) ||
                        SilksongMobileAPIPlugin.IsStitchworkSkinOrAssetPackName(dep) ||
                        SilksongMobileAPIPlugin.IsSsmpOrAddonIdentifier(dep) ||
                        dep.IndexOf("-Architect-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dep.EndsWith("-Architect", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dep, "Architect", StringComparison.OrdinalIgnoreCase) ||
                        dep.IndexOf("com.cometcake575.architect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dep.IndexOf("Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dep.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dep.IndexOf("SSMP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dep.IndexOf("BasicEnemySync", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        private bool _storeCatalogRequested;

        public static void Initialize(GameObject parent)
        {
            if (Instance == null && parent != null)
            {
                Instance = parent.AddComponent<InGameModManager>();
                Instance.InitFallbackCatalog();
                Instance.ScanInstalledMods();
                // Lazy RAM optimization: do NOT parse or download the 15 MB Thunderstore JSON on startup!
                // Only load the online catalog when the user actually opens 'Install Mods'.
            }
        }

        public void EnsureStoreCatalogLoaded()
        {
            if (_storeCatalogRequested || IsFetchingStore) return;
            _storeCatalogRequested = true;
            StartCoroutine(FetchThunderstoreCatalogRoutine());
        }

        public void UninstallMod(InstalledModItem mod) => DeleteMod(mod);
        public void InitCatalog() => InitFallbackCatalog();
        public void StartInstallOnlineMod(OnlineModCatalogItem item) => InstallOnlineMod(item);

        private void Awake()
        {
            Instance = this;
        }

        public void ScanInstalledMods()
        {
            var prevPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < InstalledMods.Count; i++)
            {
                if (InstalledMods[i] != null && InstalledMods[i].PendingDeleteConfirm && !string.IsNullOrEmpty(InstalledMods[i].FolderName))
                    prevPending.Add(InstalledMods[i].FolderName);
            }

            InstalledMods.Clear();
            string pluginsDir = Paths.PluginPath;
            if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir))
            {
                try { pluginsDir = Path.Combine(Paths.BepInExRootPath, "plugins"); } catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L172", __apiEx_1); }
            }
            if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir)) return;

            try
            {
                var subDirs = Directory.GetDirectories(pluginsDir);
                foreach (var dir in subDirs)
                {
                    try
                    {
                        string folderName = Path.GetFileName(dir) ?? "";
                        if (string.IsNullOrEmpty(folderName) || folderName.StartsWith(".", StringComparison.Ordinal))
                            continue;
                        if (SilksongMobileAPIPlugin.IsArchitectDependentModDir(dir))
                            continue;
                        if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(folderName) ||
                            string.Equals(folderName, "Stitchwork", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, "StitchworkWiki", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, "PatchWork", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, "Patchwork", StringComparison.OrdinalIgnoreCase))
                            continue;

                        EnsureCreditsFileForModDir(dir);
                        var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                            .Where(f => f.IndexOf(".cecil_cache", StringComparison.OrdinalIgnoreCase) < 0)
                            .ToArray();
                        var disabledDllFiles = Directory.GetFiles(dir, "*.dll.disabled*", SearchOption.AllDirectories)
                            .Where(f => f.IndexOf(".cecil_cache", StringComparison.OrdinalIgnoreCase) < 0)
                            .ToArray();

                        if (dllFiles.Length == 0 && disabledDllFiles.Length == 0) continue;

                        bool isCore = CoreFolderGuids.Contains(folderName);
                        bool hasDisabledTxt = File.Exists(Path.Combine(dir, "disabled.txt"));
                        bool isEnabled = dllFiles.Length > 0 && !hasDisabledTxt;
                        string primaryDll = dllFiles.Length > 0 ? dllFiles[0] : disabledDllFiles[0];

                        long totalBytes = 0;
                        try
                        {
                            foreach (var f in dllFiles) totalBytes += new FileInfo(f).Length;
                            foreach (var f in disabledDllFiles) totalBytes += new FileInfo(f).Length;
                        }
                        catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L209", __apiEx_2); }

                        string displayName = CleanModDisplayName(folderName);
                        string version = "1.0.0";
                        string author = "Unknown";
                        string desc = "";

                        string manifestPath = Path.Combine(dir, "manifest.json");
                        if (File.Exists(manifestPath))
                        {
                            try
                            {
                                string json = File.ReadAllText(manifestPath);
                                var nameMatch = System.Text.RegularExpressions.Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                                var verMatch = System.Text.RegularExpressions.Regex.Match(json, "\"version_number\"\\s*:\\s*\"([^\"]+)\"");
                                var descMatch = System.Text.RegularExpressions.Regex.Match(json, "\"description\"\\s*:\\s*\"([^\"]+)\"");
                                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                                    displayName = nameMatch.Groups[1].Value.Replace('_', ' ').Trim();
                                if (verMatch.Success && !string.IsNullOrWhiteSpace(verMatch.Groups[1].Value))
                                    version = verMatch.Groups[1].Value.Trim();
                                if (descMatch.Success)
                                    desc = descMatch.Groups[1].Value;
                            }
                            catch (System.Exception __apiEx_3) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L232", __apiEx_3); }
                        }

                        InstalledMods.Add(new InstalledModItem
                        {
                            FolderName = folderName,
                            DisplayName = displayName,
                            Version = version,
                            Author = author,
                            Description = desc,
                            DirectoryPath = dir,
                            PrimaryDllPath = primaryDll,
                            IsEnabled = isEnabled,
                            IsCore = isCore,
                            SizeBytes = totalBytes,
                            PendingDeleteConfirm = prevPending.Contains(folderName)
                        });
                    }
                    catch (System.Exception __apiEx_4) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L250", __apiEx_4); }
                }

                InstalledMods.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                UpdateInstalledFlags();
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Error scanning installed mods: {ex.Message}");
            }
        }

        private static string CleanModDisplayName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Unknown Mod";
            var parts = raw.Split(new char[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[parts.Length - 1].Length > 0 && char.IsDigit(parts[parts.Length - 1][0]))
            {
                return string.Join(" ", parts, 1, parts.Length - 2).Replace('_', ' ').Trim();
            }
            if (parts.Length == 2)
            {
                if (parts[1].Length > 0 && char.IsDigit(parts[1][0]))
                    return parts[0].Replace('_', ' ').Trim();
                return parts[1].Replace('_', ' ').Trim();
            }
            return raw.Replace('_', ' ').Trim();
        }

        public void UpdateInstalledFlags()
        {
            foreach (var online in OnlineCatalog)
            {
                online.IsInstalled = InstalledMods.Any(im =>
                    im.FolderName.IndexOf(online.PackageName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    im.DisplayName.IndexOf(online.PackageName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (!string.IsNullOrEmpty(online.FullName) && im.FolderName.IndexOf(online.FullName, StringComparison.OrdinalIgnoreCase) >= 0));
                
                if (online.IsInstalled)
                {
                    online.InstallStatus = "Installed";
                }
            }
        }

        public void ToggleMod(InstalledModItem mod)
        {
            if (mod.IsCore)
            {
                GlobalStatusMessage = "Cannot disable core SilksongMobileAPI!";
                return;
            }

            try
            {
                if (mod.IsEnabled)
                {
                    var dlls = Directory.GetFiles(mod.DirectoryPath, "*.dll", SearchOption.AllDirectories);
                    foreach (var dll in dlls)
                    {
                        string target = dll + ".disabled";
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(dll, target);
                    }
                    mod.IsEnabled = false;
                    SilksongMobileAPIPlugin.DeactivateLoadedModRuntime(mod.DisplayName ?? mod.FolderName, mod.DirectoryPath);
                    GlobalStatusMessage = $"Disabled '{mod.DisplayName}' (runtime hooks detached; restart recommended for full unload).";
                }
                else
                {
                    var disabled = Directory.GetFiles(mod.DirectoryPath, "*.dll.disabled", SearchOption.AllDirectories);
                    foreach (var d in disabled)
                    {
                        string target = d.Substring(0, d.Length - ".disabled".Length);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(d, target);
                    }
                    mod.IsEnabled = true;
                    GlobalStatusMessage = $"Enabled '{mod.DisplayName}'.";
                }
                ScanInstalledMods();
            }
            catch (Exception ex)
            {
                GlobalStatusMessage = $"Failed to toggle mod: {ex.Message}";
                SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Error toggling mod: {ex}");
            }
        }

        public void DeleteMod(InstalledModItem mod)
        {
            if (mod == null || mod.IsCore)
            {
                GlobalStatusMessage = "Cannot delete core SilksongMobileAPI!";
                return;
            }

            try
            {
                ModPreloaderScreen.ShowOperationOverlay(
                    "UNINSTALLING MOD PACKAGE",
                    $"Removing {mod.DisplayName} from Pharloom Mod Ecosystem...",
                    $"Removing {mod.DisplayName}...",
                    0.45f);

                SilksongMobileAPIPlugin.DeactivateLoadedModRuntime(mod.DisplayName ?? mod.FolderName, mod.DirectoryPath);
                if (Directory.Exists(mod.DirectoryPath))
                {
                    Directory.Delete(mod.DirectoryPath, true);
                    GlobalStatusMessage = $"Deleted '{mod.DisplayName}' (runtime hooks detached).";
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Uninstalled mod '{mod.DisplayName}' ({mod.DirectoryPath}).");
                }
                ScanInstalledMods();
                ModPreloaderScreen.FinishOperationOverlay($"Uninstalled {mod.DisplayName}!", 0.45f);
            }
            catch (Exception ex)
            {
                GlobalStatusMessage = $"Failed to delete mod: {ex.Message}";
                SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Error deleting mod: {ex}");
                ModPreloaderScreen.FinishOperationOverlay($"Uninstall Failed: {ex.Message}", 0.55f);
            }
        }

        // =====================================================================
        // Thunderstore Catalog Fetching (400+ Mods)
        // =====================================================================
        public IEnumerator FetchThunderstoreCatalogRoutine()
        {
            IsFetchingStore = true;
            string cachePath = Path.Combine(Application.persistentDataPath, "thunderstore_silksong_catalog.json");
            string endpoint = "https://thunderstore.io/c/hollow-knight-silksong/api/v1/package/";

            // 1. Try load from local persistent cache first if available
            if (File.Exists(cachePath) && OnlineCatalog.Count <= 10)
            {
                try
                {
                    string cachedJson = File.ReadAllText(cachePath);
                    ParseThunderstoreCatalogJson(cachedJson);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Loaded {OnlineCatalog.Count} mods from local Thunderstore cache.");
                }
                catch (System.Exception __apiEx_5) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L389", __apiEx_5); }
            }

            // 2. Query Thunderstore API for full live package list
            using (UnityWebRequest req = UnityWebRequest.Get(endpoint))
            {
                req.timeout = 20;
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;

                if (req.result == UnityWebRequest.Result.Success)
                {
                    string rawJson = req.downloadHandler.text;
                    if (!string.IsNullOrEmpty(rawJson) && rawJson.Length > 100)
                    {
                        try
                        {
                            File.WriteAllText(cachePath, rawJson);
                        }
                        catch (System.Exception __apiEx_6) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L408", __apiEx_6); }

                        ParseThunderstoreCatalogJson(rawJson);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Successfully retrieved {OnlineCatalog.Count} live mods from Thunderstore!");
                    }
                }
                else
                {
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] Thunderstore online fetch notice: {req.error} (Using local catalog).");
                }
            }

            IsFetchingStore = false;
            UpdateInstalledFlags();
        }

        private void ParseThunderstoreCatalogJson(string json)
        {
            try
            {
                var jArray = JArray.Parse(json);
                if (jArray == null || jArray.Count == 0) return;

                List<OnlineModCatalogItem> parsed = new List<OnlineModCatalogItem>();

                foreach (JObject pkg in jArray)
                {
                    try
                    {
                        string name = pkg.Value<string>("name") ?? "";
                        string owner = pkg.Value<string>("owner") ?? "";
                        string fullName = pkg.Value<string>("full_name") ?? $"{owner}-{name}";
                        bool isDeprecated = pkg.Value<bool>("is_deprecated");

                        JArray versions = pkg.Value<JArray>("versions");
                        if (versions == null || versions.Count == 0) continue;

                        JObject latest = (JObject)versions[0];
                        string ver = latest.Value<string>("version_number") ?? "1.0.0";
                        string desc = latest.Value<string>("description") ?? "";
                        string downloadUrl = latest.Value<string>("download_url") ?? "";
                        int fileSize = latest.Value<int>("file_size");

                        List<string> depList = new List<string>();
                        JArray depsArr = latest.Value<JArray>("dependencies");
                        if (depsArr != null)
                        {
                            foreach (var dTok in depsArr)
                            {
                                string dStr = dTok?.ToString();
                                if (!string.IsNullOrEmpty(dStr))
                                    depList.Add(dStr);
                            }
                        }

                        // Filter out ONLY Architect (+ dependents), Stitchwork (+ Hornet skins), SSMP (+ addons), and HkCore/HkBossLoader (store-only)
                        if (IsArchitectOrDependentPackage(name, fullName, depList, desc))
                            continue;

                        // Compatibility filter
                        bool isCompatible = true;
                        string compatReason = "Fully compatible mobile mod.";
                        bool reqAdapt = false;
                        string adaptDetails = "";

                        if (isDeprecated)
                        {
                            isCompatible = false;
                            compatReason = "Deprecated by author.";
                        }
                        else if (KnownPcAssetBundleMods.Contains(name) || KnownPcAssetBundleMods.Contains(fullName))
                        {
                            isCompatible = false;
                            compatReason = "Blocked: PC-only AssetBundle / Windows Doorstop package.";
                        }
                        else if (name.IndexOf("Doorstop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("ReShade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("x86_64", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("DirectX 11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("winhttp.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isCompatible = false;
                            compatReason = "Blocked: Requires Windows native x86_64 / DirectX binary.";
                        }
                        else if (desc.IndexOf("assetbundle", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                 desc.IndexOf("mobile", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            reqAdapt = true;
                            adaptDetails = "Runtime Android AssetBundle & Scene Repacker enabled.";
                        }
                        else if (desc.IndexOf("cecil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("monomod", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            reqAdapt = true;
                            adaptDetails = "Stripped PC prepatcher; runtime HybridCLR bridged.";
                        }

                        // Rebellious Second Sentinel mobile adaptation
                        if (name.IndexOf("SecondSentinel", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            reqAdapt = true;
                            adaptDetails = "FSM lock + HybridCLR reflection adaptation.";
                        }

                        // Never display blocked or incompatible mods anywhere in the UI
                        if (!isCompatible)
                            continue;

                        parsed.Add(new OnlineModCatalogItem
                        {
                            PackageName = name,
                            FullName = fullName,
                            Author = owner,
                            Version = ver,
                            DisplayTitle = name.Replace('_', ' '),
                            Description = desc,
                            DownloadUrl = downloadUrl,
                            IsCompatible = isCompatible,
                            CompatibilityReason = compatReason,
                            RequiresAdaptation = reqAdapt,
                            AdaptationDetails = adaptDetails,
                            FileSizeBytes = fileSize,
                            Dependencies = depList,
                            InstallStatus = isCompatible ? "Ready" : "Incompatible"
                        });
                    }
                    catch (System.Exception __apiEx_7) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L530", __apiEx_7); }
                }

                if (parsed.Count > 0)
                {
                    parsed.Sort((a, b) =>
                    {
                        int cmp = string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
                        if (cmp != 0) return cmp;
                        return string.Compare(a.PackageName, b.PackageName, StringComparison.OrdinalIgnoreCase);
                    });
                    OnlineCatalog.Clear();
                    OnlineCatalog.AddRange(parsed);
                    TotalOnlineModsFound = parsed.Count;
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Error parsing Thunderstore catalog: {ex.Message}");
            }
        }

        private void InitFallbackCatalog()
        {
            if (OnlineCatalog.Count > 0) return;

            OnlineCatalog.Add(new OnlineModCatalogItem
            {
                PackageName = "AuricAegis",
                FullName = "AstrumNova-AuricAegis",
                Author = "AstrumNova",
                Version = "1.1.2",
                DisplayTitle = "Auric Aegis",
                Description = "Custom boss encounter and gameplay overhaul by AstrumNova (explicit unrestricted permission).",
                DownloadUrl = "https://thunderstore.io/package/download/AstrumNova/AuricAegis/1.1.2/",
                IsCompatible = true,
                CompatibilityReason = "Verified open permission. Full IRIS support.",
                RequiresAdaptation = false
            });

            OnlineCatalog.Add(new OnlineModCatalogItem
            {
                PackageName = "KarmelitaPrime",
                FullName = "MicheliniDev-KarmelitaPrime",
                Author = "MicheliniDev",
                Version = "1.0.0",
                DisplayTitle = "Karmelita Prime",
                Description = "Enhanced Skarrsinger Karmelita boss battle (MIT License).",
                DownloadUrl = "https://thunderstore.io/package/download/MicheliniDev/KarmelitaPrime/1.0.0/",
                IsCompatible = true,
                CompatibilityReason = "MIT Licensed. Full Harmony & FSM support.",
                RequiresAdaptation = false
            });

            OnlineCatalog.Add(new OnlineModCatalogItem
            {
                PackageName = "LostSinner",
                FullName = "JngoCreates-LostSinner",
                Author = "JngoCreates",
                Version = "1.0.0",
                DisplayTitle = "Lost Sinner",
                Description = "Custom Act 3 boss fight (MIT License).",
                DownloadUrl = "https://thunderstore.io/package/download/JngoCreates/LostSinner/1.0.0/",
                IsCompatible = true,
                CompatibilityReason = "MIT Licensed. Full Harmony & FSM support.",
                RequiresAdaptation = false
            });

            OnlineCatalog.Add(new OnlineModCatalogItem
            {
                PackageName = "DoubleEnemiesMod",
                FullName = "adwamogus-DoubleEnemiesMod",
                Author = "adwamogus",
                Version = "1.0.0",
                DisplayTitle = "Double Enemies Mod",
                Description = "Doubles enemy spawns across Pharloom for extra challenge (MIT License).",
                DownloadUrl = "https://thunderstore.io/package/download/adwamogus/DoubleEnemiesMod/1.0.0/",
                IsCompatible = true,
                CompatibilityReason = "MIT Licensed. Lightweight gameplay mod.",
                RequiresAdaptation = false
            });

            OnlineCatalog.Add(new OnlineModCatalogItem
            {
                PackageName = "FastFastTravel",
                FullName = "Clazex-FastFastTravel",
                Author = "Clazex",
                Version = "1.0.0",
                DisplayTitle = "Fast Fast Travel",
                Description = "Speeds up fast travel transitions (MIT License).",
                DownloadUrl = "https://thunderstore.io/package/download/Clazex/FastFastTravel/1.0.0/",
                IsCompatible = true,
                CompatibilityReason = "MIT Licensed. Lightweight QoL mod.",
                RequiresAdaptation = false
            });

            OnlineCatalog.Sort((a, b) => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================================
        // Pure-C# ZIP Extractor via GZipStream + Dependency Auto-Resolver
        // =====================================================================
        private static int ReadUInt16LE(byte[] buf, int offset)
        {
            return buf[offset] | (buf[offset + 1] << 8);
        }

        private static uint ReadUInt32LE(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static void WriteUInt32LE(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
            buf[offset + 2] = (byte)((val >> 16) & 0xFF);
            buf[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static bool IsManagedDotNetAssembly(string dllPath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(dllPath);
                if (data == null || data.Length < 256) return false;
                if (data[0] != 0x4D || data[1] != 0x5A) return false; // "MZ"
                int peOffset = BitConverter.ToInt32(data, 0x3C);
                if (peOffset < 0 || peOffset + 128 >= data.Length) return false;
                if (data[peOffset] != 0x50 || data[peOffset + 1] != 0x45 || data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
                    return false; // "PE\0\0"
                ushort magic = BitConverter.ToUInt16(data, peOffset + 24);
                int dataDirOffset = peOffset + 24 + (magic == 0x20B ? 112 : 96);
                int cliHeaderOffset = dataDirOffset + (14 * 8);
                if (cliHeaderOffset + 8 > data.Length) return false;
                uint cliRva = BitConverter.ToUInt32(data, cliHeaderOffset);
                uint cliSize = BitConverter.ToUInt32(data, cliHeaderOffset + 4);
                return cliRva != 0 && cliSize >= 0x48;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidateExtractedModCompatibility(string targetDir, out string failReason)
        {
            failReason = "";
            try
            {
                var allDlls = Directory.GetFiles(targetDir, "*.dll", SearchOption.AllDirectories);
                if (allDlls.Length == 0)
                {
                    return true;
                }
                int managedCount = 0;
                int nativeCount = 0;
                foreach (var dll in allDlls)
                {
                    if (IsManagedDotNetAssembly(dll))
                    {
                        managedCount++;
                    }
                    else
                    {
                        nativeCount++;
                        try { File.Delete(dll); } catch (System.Exception __apiEx_8) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L693", __apiEx_8); }
                    }
                }
                if (managedCount == 0 && nativeCount > 0)
                {
                    failReason = "Incompatible: Package contains only native Windows x86_64 C/C++ DLLs.";
                    return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        public static void ExtractZipBytesSafe(byte[] zipBytes, string targetDir)
        {
            if (zipBytes == null || zipBytes.Length < 22)
                throw new InvalidDataException("Invalid or empty zip archive.");

            int eocdPos = -1;
            int minSearch = Math.Max(0, zipBytes.Length - 65558);
            for (int i = zipBytes.Length - 22; i >= minSearch; i--)
            {
                if (zipBytes[i] == 0x50 && zipBytes[i + 1] == 0x4B && zipBytes[i + 2] == 0x05 && zipBytes[i + 3] == 0x06)
                {
                    eocdPos = i;
                    break;
                }
            }
            if (eocdPos < 0)
                throw new InvalidDataException("ZIP End of Central Directory not found.");

            int totalEntries = ReadUInt16LE(zipBytes, eocdPos + 10);
            int cdOffset = (int)ReadUInt32LE(zipBytes, eocdPos + 16);
            int pos = cdOffset;

            for (int idx = 0; idx < totalEntries; idx++)
            {
                if (pos + 46 > zipBytes.Length) break;
                uint sig = ReadUInt32LE(zipBytes, pos);
                if (sig != 0x02014B50u) break;

                int method = ReadUInt16LE(zipBytes, pos + 10);
                uint crc32 = ReadUInt32LE(zipBytes, pos + 16);
                int compSize = (int)ReadUInt32LE(zipBytes, pos + 20);
                int uncompSize = (int)ReadUInt32LE(zipBytes, pos + 24);
                int fnLen = ReadUInt16LE(zipBytes, pos + 28);
                int extraLen = ReadUInt16LE(zipBytes, pos + 30);
                int commentLen = ReadUInt16LE(zipBytes, pos + 32);
                int lho = (int)ReadUInt32LE(zipBytes, pos + 42);

                string entryName = Encoding.UTF8.GetString(zipBytes, pos + 46, fnLen);
                pos += 46 + fnLen + extraLen + commentLen;

                string normName = entryName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(normName) || normName.EndsWith("/"))
                    continue;

                if (normName.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase))
                    normName = normName.Substring("BepInEx/plugins/".Length);
                else if (normName.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                    normName = normName.Substring("plugins/".Length);

                normName = normName.TrimStart('/');
                if (string.IsNullOrEmpty(normName) || normName.Contains(".."))
                    continue;

                if (lho + 30 > zipBytes.Length) continue;
                int lFnLen = ReadUInt16LE(zipBytes, lho + 26);
                int lExtraLen = ReadUInt16LE(zipBytes, lho + 28);
                int dataStart = lho + 30 + lFnLen + lExtraLen;
                if (dataStart + compSize > zipBytes.Length) continue;

                string outPath = Path.Combine(targetDir, normName.Replace('/', Path.DirectorySeparatorChar));
                string parentDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    Directory.CreateDirectory(parentDir);

                if (uncompSize == 0 || compSize == 0)
                {
                    File.WriteAllBytes(outPath, Array.Empty<byte>());
                    continue;
                }

                if (method == 0)
                {
                    byte[] raw = new byte[compSize];
                    Buffer.BlockCopy(zipBytes, dataStart, raw, 0, compSize);
                    File.WriteAllBytes(outPath, raw);
                }
                else if (method == 8)
                {
                    byte[] gzWrapped = new byte[10 + compSize + 8];
                    gzWrapped[0] = 0x1F;
                    gzWrapped[1] = 0x8B;
                    gzWrapped[2] = 0x08;
                    Buffer.BlockCopy(zipBytes, dataStart, gzWrapped, 10, compSize);
                    WriteUInt32LE(gzWrapped, 10 + compSize, crc32);
                    WriteUInt32LE(gzWrapped, 10 + compSize + 4, (uint)uncompSize);

                    using (var msIn = new MemoryStream(gzWrapped, writable: false))
                    using (var gz = new GZipStream(msIn, CompressionMode.Decompress))
                    using (var fsOut = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        gz.CopyTo(fsOut);
                    }
                }
                else
                {
                    throw new NotSupportedException($"Unsupported ZIP compression method: {method}");
                }
            }
        }

        private static bool IsBuiltInOrNativeMobileDependency(string depName)
        {
            if (string.IsNullOrEmpty(depName)) return true;
            string n = depName.Trim();
            if (n.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Prepatcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("I18N", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("MonoMod", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("ConfigurationManager", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (n.IndexOf("AssetHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("FsmUtil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Stitchwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Patchwork", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Native mobile AssetHelper & Silksong.FsmUtil are pre-installed and bridged by SilksongMobileAPI;
                // never overwrite them with PC binaries.
                return true;
            }
            return false;
        }

        private void CollectMissingDependencies(OnlineModCatalogItem target, List<OnlineModCatalogItem> queue, HashSet<string> visited)
        {
            if (target == null || target.Dependencies == null || target.Dependencies.Count == 0) return;

            foreach (string rawDep in target.Dependencies)
            {
                if (string.IsNullOrEmpty(rawDep)) continue;
                var parts = rawDep.Split(new char[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string depAuthor = parts[0];
                string depPkgName = parts.Length >= 3
                    ? string.Join("-", parts, 1, parts.Length - 2)
                    : parts[1];
                string depKey = $"{depAuthor}-{depPkgName}";

                if (!visited.Add(depKey)) continue;

                if (IsBuiltInOrNativeMobileDependency(depPkgName) || IsBuiltInOrNativeMobileDependency(depKey))
                {
                    SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Dependency '{rawDep}' is natively provided by SilksongMobileAPI / Mobile runtime.");
                    continue;
                }

                // Check if already installed on device
                var existing = InstalledMods.FirstOrDefault(im =>
                    im.FolderName.IndexOf(depPkgName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    im.DisplayName.IndexOf(depPkgName, StringComparison.OrdinalIgnoreCase) >= 0);

                if (existing != null)
                {
                    if (!existing.IsEnabled)
                    {
                        ToggleMod(existing);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Auto-enabled installed dependency '{existing.DisplayName}'.");
                    }
                    else
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Dependency '{depPkgName}' is already installed and active.");
                    }
                    continue;
                }

                // Find in Thunderstore OnlineCatalog
                var catalogDep = OnlineCatalog.FirstOrDefault(c =>
                    c.IsCompatible &&
                    (string.Equals(c.PackageName, depPkgName, StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(c.Author, depAuthor, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(c.PackageName, depPkgName, StringComparison.OrdinalIgnoreCase))));

                if (catalogDep != null && !queue.Contains(catalogDep))
                {
                    CollectMissingDependencies(catalogDep, queue, visited);
                    if (!queue.Contains(catalogDep))
                    {
                        queue.Add(catalogDep);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Queued missing dependency '{catalogDep.Author}-{catalogDep.PackageName}' (v{catalogDep.Version}) for auto-install.");
                    }
                }
            }
        }

        public void InstallOnlineMod(OnlineModCatalogItem item, Action<float, string> onProgress = null, Action<bool, string> onComplete = null)
        {
            if (item == null || !item.IsCompatible) return;
            try
            {
                MonoBehaviour runner = (SilksongMobileAPIPlugin.Instance != null && SilksongMobileAPIPlugin.Instance.isActiveAndEnabled)
                    ? (MonoBehaviour)SilksongMobileAPIPlugin.Instance
                    : (MonoBehaviour)this;
                runner.StartCoroutine(InstallOnlineModRoutine(item, onProgress, onComplete));
            }
            catch (Exception ex)
            {
                item.InstallStatus = "Failed";
                item.ErrorMessage = ex.Message;
                SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Failed to start install coroutine for {item.PackageName}: {ex}");
                onComplete?.Invoke(false, ex.Message);
            }
        }

        private IEnumerator InstallOnlineModRoutine(OnlineModCatalogItem item, Action<float, string> onProgress, Action<bool, string> onComplete)
        {
            item.InstallStatus = "Downloading";
            item.DownloadProgress = 0.05f;
            item.ErrorMessage = "";
            GlobalStatusMessage = $"Preparing {item.DisplayTitle}...";
            ModPreloaderScreen.ShowOperationOverlay(
                "INSTALLING MOD PACKAGE",
                $"Downloading & adapting {item.DisplayTitle} for Android...",
                $"Preparing {item.DisplayTitle}...",
                0.05f);
            onProgress?.Invoke(0.05f, "Checking Deps");
            yield return null;

            ScanInstalledMods();
            var installQueue = new List<OnlineModCatalogItem>();
            var visitedDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                CollectMissingDependencies(item, installQueue, visitedDeps);
            }
            catch (Exception depEx)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] Dependency check notice: {depEx.Message}");
            }
            installQueue.Add(item);

            for (int qIdx = 0; qIdx < installQueue.Count; qIdx++)
            {
                var curPkg = installQueue[qIdx];
                bool isDep = (curPkg != item);
                string prefixStatus = isDep ? $"Dep {curPkg.PackageName}" : "Downloading";

                curPkg.InstallStatus = "Downloading";
                curPkg.DownloadProgress = 0.10f;
                curPkg.ErrorMessage = "";
                onProgress?.Invoke(0.10f, prefixStatus);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Starting download of '{curPkg.Author}-{curPkg.PackageName}' (v{curPkg.Version}) [isDep={isDep}]");

                byte[] zipBytes = null;
                string downloadError = "";

                string[] candidateUrls = new string[]
                {
                    $"https://gcdn.thunderstore.io/live/repository/packages/{curPkg.Author}-{curPkg.PackageName}-{curPkg.Version}.zip",
                    curPkg.DownloadUrl,
                    $"https://thunderstore.io/package/download/{curPkg.Author}/{curPkg.PackageName}/{curPkg.Version}/"
                };

                for (int u = 0; u < candidateUrls.Length; u++)
                {
                    string url = candidateUrls[u];
                    if (string.IsNullOrEmpty(url)) continue;

                    using (UnityWebRequest req = UnityWebRequest.Get(url))
                    {
                        req.timeout = 45;
                        var op = req.SendWebRequest();

                        while (!op.isDone)
                        {
                            float p = Mathf.Clamp(req.downloadProgress, 0.10f, 0.90f);
                            curPkg.DownloadProgress = p;
                            item.DownloadProgress = p;
                            ModPreloaderScreen.UpdateOperationOverlay($"Downloading {curPkg.DisplayTitle}...", Mathf.Lerp(0.10f, 0.72f, req.downloadProgress));
                            onProgress?.Invoke(p, prefixStatus);
                            yield return null;
                        }

                        if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null)
                        {
                            byte[] rawData = req.downloadHandler.data;
                            if (rawData != null && rawData.Length > 64)
                            {
                                zipBytes = rawData;
                                downloadError = "";
                                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Downloaded '{curPkg.PackageName}' ({zipBytes.Length} bytes) from {url}");
                                break;
                            }
                        }

                        downloadError = req.error ?? "Network error";
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] Download attempt failed from {url}: {downloadError}");
                    }
                }

                if (zipBytes == null || zipBytes.Length == 0)
                {
                    curPkg.InstallStatus = "Failed";
                    curPkg.ErrorMessage = downloadError;
                    if (isDep)
                    {
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] Optional/dependency package '{curPkg.PackageName}' download failed ({downloadError}); continuing with main mod.");
                        continue;
                    }
                    item.InstallStatus = "Failed";
                    item.ErrorMessage = downloadError;
                    GlobalStatusMessage = $"Download failed: {downloadError}";
                    ModPreloaderScreen.FinishOperationOverlay($"Download Failed: {downloadError}", 0.60f);
                    onComplete?.Invoke(false, downloadError);
                    yield break;
                }

                curPkg.InstallStatus = "Extracting";
                item.InstallStatus = "Extracting";
                ModPreloaderScreen.UpdateOperationOverlay($"Extracting {curPkg.DisplayTitle}...", 0.80f);
                onProgress?.Invoke(0.95f, isDep ? $"Extracting {curPkg.PackageName}" : "Extracting");
                GlobalStatusMessage = $"Extracting {curPkg.DisplayTitle}...";
                yield return null;

                string targetDir = Path.Combine(Paths.PluginPath, $"{curPkg.Author}-{curPkg.PackageName}-{curPkg.Version}");
                bool extractSuccess = false;
                string extractError = "";

                try
                {
                    Directory.CreateDirectory(Paths.PluginPath);
                    if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                    Directory.CreateDirectory(targetDir);

                    ExtractZipBytesSafe(zipBytes, targetDir);
                    if (!ValidateExtractedModCompatibility(targetDir, out string compatErr))
                    {
                        try { Directory.Delete(targetDir, true); } catch (System.Exception __apiEx_9) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1037", __apiEx_9); }
                        curPkg.IsCompatible = false;
                        curPkg.CompatibilityReason = compatErr;
                        curPkg.InstallStatus = "Incompatible";
                        extractError = compatErr;
                        extractSuccess = false;
                    }
                    else
                    {
                        WriteModCreditsFile(targetDir, curPkg.DisplayTitle, curPkg.Author);
                        SilksongMobileAPIPlugin.PreOrganizeTopLevelPcModDlls();
                        extractSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    extractError = ex.Message;
                    SilksongMobileAPIPlugin.LogDiagnosticError($"[ModManager] Extraction error for {curPkg.PackageName}: {ex}");
                }

                if (!extractSuccess)
                {
                    curPkg.InstallStatus = "Failed";
                    curPkg.ErrorMessage = extractError;
                    if (isDep) continue;
                    item.InstallStatus = "Failed";
                    item.ErrorMessage = extractError;
                    GlobalStatusMessage = $"Extract failed: {extractError}";
                    ModPreloaderScreen.FinishOperationOverlay($"Extract Failed: {extractError}", 0.60f);
                    onComplete?.Invoke(false, extractError);
                    yield break;
                }

                curPkg.InstallStatus = "Adapting";
                item.InstallStatus = "Adapting";
                ModPreloaderScreen.UpdateOperationOverlay($"Adapting & Hot-Loading {curPkg.DisplayTitle}...", 0.92f);
                onProgress?.Invoke(0.98f, isDep ? $"Loading {curPkg.PackageName}" : "Loading...");
                yield return null;

                bool hotLoaded = SilksongMobileAPIPlugin.HotLoadInstalledModDirectory(targetDir);
                curPkg.InstallStatus = "Installed";
                curPkg.IsInstalled = true;
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Installed '{curPkg.DisplayTitle}' into {targetDir} (hotLoaded={hotLoaded}).");
            }

            item.InstallStatus = "Installed";
            item.IsInstalled = true;
            GlobalStatusMessage = $"Installed & activated {item.DisplayTitle}!";
            ScanInstalledMods();
            ModPreloaderScreen.FinishOperationOverlay($"Installed {item.DisplayTitle}!", 0.45f);
            onComplete?.Invoke(true, null);
        }

        public static void EnsureCreditsFileForModDir(string modDir)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir)) return;
            try
            {
                string folderName = Path.GetFileName(modDir) ?? "";
                if (string.IsNullOrEmpty(folderName) ||
                    folderName.StartsWith(".", StringComparison.Ordinal) ||
                    folderName.StartsWith("00_", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(folderName, "SilksongMobileAPI", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(folderName, "IRIS", StringComparison.OrdinalIgnoreCase))
                    return;

                string creditsPath = Path.Combine(modDir, "credits.txt");
                if (File.Exists(creditsPath)) return;

                // Only generate credits.txt for directories containing a mod manifest or plugin DLL
                string manifestPath = Path.Combine(modDir, "manifest.json");
                bool hasManifest = File.Exists(manifestPath);
                bool hasDll = hasManifest || Directory.GetFiles(modDir, "*.dll", SearchOption.AllDirectories).Length > 0;
                if (!hasDll) return;

                string modName = "";
                string creator = "";

                if (hasManifest)
                {
                    try
                    {
                        var jo = JObject.Parse(File.ReadAllText(manifestPath));
                        modName = jo.Value<string>("name") ?? "";
                        creator = jo.Value<string>("author") ?? jo.Value<string>("owner") ?? "";
                    }
                    catch { }
                }

                // Fallback: parse Thunderstore folder naming convention "Author-ModName-1.0.0"
                string cleanFolder = System.Text.RegularExpressions.Regex.Replace(folderName, @"-\d+\.\d+.*$", "");
                int dashIdx = cleanFolder.IndexOf('-');
                if (dashIdx > 0 && dashIdx + 1 < cleanFolder.Length)
                {
                    if (string.IsNullOrWhiteSpace(creator))
                        creator = cleanFolder.Substring(0, dashIdx).Trim();
                    if (string.IsNullOrWhiteSpace(modName))
                        modName = cleanFolder.Substring(dashIdx + 1).Trim();
                }

                if (string.IsNullOrWhiteSpace(modName))
                    modName = cleanFolder.Replace('_', ' ').Trim();
                else
                    modName = modName.Replace('_', ' ').Trim();

                if (string.IsNullOrWhiteSpace(creator))
                    creator = "the mod creator";

                WriteModCreditsFile(modDir, modName, creator);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] EnsureCreditsFileForModDir notice for '{modDir}': {ex.Message}");
            }
        }

        public static void WriteModCreditsFile(string targetDir, string modName, string creator)
        {
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;
            try
            {
                string safeMod = string.IsNullOrWhiteSpace(modName) ? Path.GetFileName(targetDir) : modName.Trim();
                string safeCreator = string.IsNullOrWhiteSpace(creator) ? "the mod creator" : creator.Trim();
                string creditsText =
                    "(This is just an auto generated text file generated by IRIS thanking the creator of the mod)\n" +
                    safeMod + "\n" +
                    "Thanks " + safeCreator + " For this amazing mod,we are in the knowledge that this mod is intended for the pc version of silksong,however most people are not privileged to have a great pc or even have a pc at their house, Forcing them to rely on Mobile ports of the game.I respect the work of Team Cherry and your work and contribution to the community.If your mod was on the downloading list or downloaded it will be due to these reasons:\n" +
                    "1. Permission to allowed to be ported\n" +
                    "2. MIT or GPL license \n" +
                    "3. Personally asked for permission by someone from porting community.\n" +
                    "\n" +
                    "If you don't like your mod to be associated with the mobile port you can file a takedown request (at discord id loremx0) we will Edit the modding list and update the api to never work with your mod without breaking other mods. \n" +
                    "Again we thank you for the wonderful mod " + safeMod + "\n" +
                    "\n" +
                    "Regards,\n" +
                    "Beeg yoshi\n";
                File.WriteAllText(Path.Combine(targetDir, "credits.txt"), creditsText, System.Text.Encoding.UTF8);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[ModManager] Generated credits.txt for '{safeMod}' by '{safeCreator}'.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[ModManager] Failed to write credits.txt in '{targetDir}': {ex.Message}");
            }
        }
    }

    // =========================================================================
    // ModMenuResources: Resolves Vanilla Silksong Fonts, Animators & Scrollbar Sprites
    // =========================================================================
    public static class ModMenuResources
    {
        public static Font TrajanRegular { get; private set; }
        public static Font TrajanBold { get; private set; }
        public static Font Perpetua { get; private set; }

        public static RuntimeAnimatorController MenuTopFleurAnimator { get; private set; }
        public static RuntimeAnimatorController MenuCursorAnimator { get; private set; }
        public static RuntimeAnimatorController MenuButtonFlashAnimator { get; private set; }
        public static RuntimeAnimatorController TextHideShowAnimator { get; private set; }

        public static Sprite ScrollbarHandleSprite { get; private set; }
        public static Sprite ScrollbarBackgroundSprite { get; private set; }

        private static bool _initialized;
        private static Sprite _fallbackLineSprite;
        private static Sprite _fallbackHandleSprite;

        public static void EnsureLoaded(UIManager ui)
        {
            if (_initialized && TrajanBold != null && ScrollbarHandleSprite != null) return;
            _initialized = true;

            try
            {
                foreach (var anim in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
                {
                    if (anim == null) continue;
                    switch (anim.name)
                    {
                        case "Menu Animate In Out":
                            MenuTopFleurAnimator = anim;
                            break;
                        case "Menu Fleur":
                            MenuCursorAnimator = anim;
                            break;
                        case "Menu Flash Effect":
                            MenuButtonFlashAnimator = anim;
                            break;
                        case "TextHideShow":
                            TextHideShowAnimator = anim;
                            break;
                    }
                }
            }
            catch (System.Exception __apiEx_10) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1138", __apiEx_10); }

            try
            {
                foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (font == null) continue;
                    switch (font.name)
                    {
                        case "TrajanPro-Regular":
                            TrajanRegular = font;
                            break;
                        case "TrajanPro-Bold":
                            TrajanBold = font;
                            break;
                        case "Perpetua":
                            Perpetua = font;
                            break;
                    }
                }
            }
            catch (System.Exception __apiEx_11) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1159", __apiEx_11); }

            try
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null) continue;
                    if (sp.name == "scrollbar_fleur_new" || sp.name.IndexOf("scrollbar_fleur", StringComparison.OrdinalIgnoreCase) >= 0)
                        ScrollbarHandleSprite = sp;
                    else if (sp.name == "scrollbar_single" || sp.name.IndexOf("scrollbar_single", StringComparison.OrdinalIgnoreCase) >= 0)
                        ScrollbarBackgroundSprite = sp;
                }
            }
            catch (System.Exception __apiEx_12) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1172", __apiEx_12); }

            // Extract directly from UIManager screens if any resource wasn't in FindObjectsOfTypeAll
            try
            {
                if (ui == null) ui = UIManager.instance;
                if (ui != null)
                {
                    if (ui.extrasMenuScreen != null)
                    {
                        if (MenuTopFleurAnimator == null && ui.extrasMenuScreen.topFleur != null)
                            MenuTopFleurAnimator = ui.extrasMenuScreen.topFleur.runtimeAnimatorController;

                        var sampleBtn = ui.extrasMenuScreen.GetComponentInChildren<MenuButton>(true);
                        if (sampleBtn != null)
                        {
                            if (MenuCursorAnimator == null && sampleBtn.leftCursor != null)
                                MenuCursorAnimator = sampleBtn.leftCursor.runtimeAnimatorController;
                            if (MenuButtonFlashAnimator == null && sampleBtn.flashEffect != null)
                                MenuButtonFlashAnimator = sampleBtn.flashEffect.runtimeAnimatorController;
                            var btnTxt = sampleBtn.GetComponentInChildren<Text>(true);
                            if (btnTxt != null && btnTxt.font != null)
                            {
                                if (TrajanBold == null) TrajanBold = btnTxt.font;
                                if (TrajanRegular == null) TrajanRegular = btnTxt.font;
                            }
                        }
                    }

                    if (ui.achievementsMenuScreen != null)
                    {
                        var achSb = ui.achievementsMenuScreen.GetComponentInChildren<Scrollbar>(true);
                        if (achSb != null)
                        {
                            var sbh = achSb.GetComponentInChildren<ScrollBarHandle>(true);
                            if (sbh != null && ScrollbarHandleSprite == null)
                            {
                                var img = sbh.GetComponent<Image>();
                                if (img != null) ScrollbarHandleSprite = img.sprite;
                            }
                            var bgTr = achSb.transform.Find("Background");
                            if (bgTr != null && ScrollbarBackgroundSprite == null)
                            {
                                var bgImg = bgTr.GetComponent<Image>();
                                if (bgImg != null) ScrollbarBackgroundSprite = bgImg.sprite;
                            }
                        }
                    }
                }
            }
            catch (System.Exception __apiEx_13) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1222", __apiEx_13); }

            if (TrajanBold == null) TrajanBold = HarmonyBridge.GetFallbackFont();
            if (TrajanRegular == null) TrajanRegular = TrajanBold;
            if (Perpetua == null) Perpetua = TrajanRegular;

            if (ScrollbarBackgroundSprite == null)
            {
                if (_fallbackLineSprite == null)
                {
                    var tex = new Texture2D(4, 16, TextureFormat.RGBA32, false);
                    var cols = new Color32[64];
                    for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(230, 225, 215, 190);
                    tex.SetPixels32(cols);
                    tex.Apply();
                    tex.hideFlags = HideFlags.HideAndDontSave;
                    _fallbackLineSprite = Sprite.Create(tex, new Rect(0, 0, 4, 16), new Vector2(0.5f, 0.5f));
                }
                ScrollbarBackgroundSprite = _fallbackLineSprite;
            }

            if (ScrollbarHandleSprite == null)
            {
                if (_fallbackHandleSprite == null)
                {
                    var tex = new Texture2D(24, 40, TextureFormat.RGBA32, false);
                    var cols = new Color32[24 * 40];
                    for (int y = 0; y < 40; y++)
                    {
                        float dy = Mathf.Abs(y - 20f) / 20f;
                        int halfW = Mathf.Max(2, Mathf.RoundToInt((1f - dy * 0.8f) * 10f));
                        for (int x = 0; x < 24; x++)
                        {
                            bool inside = Mathf.Abs(x - 12) <= halfW;
                            cols[y * 24 + x] = inside ? new Color32(245, 240, 230, 255) : new Color32(0, 0, 0, 0);
                        }
                    }
                    tex.SetPixels32(cols);
                    tex.Apply();
                    tex.hideFlags = HideFlags.HideAndDontSave;
                    _fallbackHandleSprite = Sprite.Create(tex, new Rect(0, 0, 24, 40), new Vector2(0.5f, 0.5f));
                }
                ScrollbarHandleSprite = _fallbackHandleSprite;
            }
        }
    }

    // =========================================================================
    // EmptyRaycastGraphic: Zero-vertex Graphic for touch/mouse hitboxes with NO white quad
    // =========================================================================
    public sealed class EmptyRaycastGraphic : Graphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
        }
    }

    // =========================================================================
    // ModScrollPaneSelector: Scrolls ScrollRect when navigating via Controller/Keys
    // =========================================================================
    public sealed class ModScrollPaneSelector : MonoBehaviour, ISelectHandler
    {
        public RectTransform PaneRect;
        public RectTransform MaskRect;
        public Scrollbar Scrollbar;

        public void OnSelect(BaseEventData eventData)
        {
            if (!(eventData is AxisEventData) || PaneRect == null || MaskRect == null || Scrollbar == null) return;
            try
            {
                var rt = GetComponent<RectTransform>();
                if (rt == null) return;
                float contentH = PaneRect.sizeDelta.y;
                float maskH = MaskRect.rect.height;
                if (contentH <= maskH + 1f) return;

                float itemY = -rt.anchoredPosition.y;
                float targetNormalized = 1f - Mathf.Clamp01((itemY - maskH * 0.35f) / (contentH - maskH));
                Scrollbar.value = targetNormalized;
            }
            catch (System.Exception __apiEx_14) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1304", __apiEx_14); }
        }
    }

    // =========================================================================
    // ModHorizontalOptionController: Native Horizontal Option (Left/Right/Click)
    // =========================================================================
    public sealed class ModHorizontalOptionController : MonoBehaviour, IMoveHandler
    {
        public string[] Options = Array.Empty<string>();
        public int SelectedIndex;
        public Text ValueText;
        public Action<int> OnOptionChanged;

        public void Step(int delta)
        {
            if (Options == null || Options.Length == 0) return;
            SelectedIndex = (SelectedIndex + delta + Options.Length) % Options.Length;
            RefreshText();
            try { OnOptionChanged?.Invoke(SelectedIndex); } catch (System.Exception __apiEx_15) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1323", __apiEx_15); }
        }

        public void RefreshText()
        {
            if (ValueText != null && Options != null && Options.Length > 0)
            {
                int idx = Mathf.Clamp(SelectedIndex, 0, Options.Length - 1);
                ValueText.text = Options[idx];
            }
        }

        public void OnMove(AxisEventData eventData)
        {
            if (eventData == null) return;
            if (eventData.moveDir == MoveDirection.Left)
            {
                Step(-1);
                eventData.Use();
            }
            else if (eventData.moveDir == MoveDirection.Right)
            {
                Step(1);
                eventData.Use();
            }
        }
    }

    // =========================================================================
    // NativeModMenu: Hollow Knight Modding API Style Native Menu UI
    // =========================================================================
    public static class NativeModMenu
    {
        private static UIManager _ui;
        private static GameObject _buttonTemplate;

        private static MenuScreen _mainModScreen;
        private static MenuScreen _installModsScreen;
        private static MenuScreen _uninstallModsScreen;
        private static MenuScreen _manageModsScreen;
        private static MenuScreen _advancedScreen;

        public static MenuScreen CurrentCustomScreen { get; private set; }
        public static bool HasInjectedMainMenu { get; set; }

        private static MainMenuState _previousMenuState = MainMenuState.MAIN_MENU;
        private static MenuScreen _previousScreen = null;
        private static bool _openedFromPauseMenu = false;

        // Search / Filter & Pagination states for Install, Manage, and Uninstall screens
        private static int _installFilterMode = 0; // 0 = Mobile-Ready, 1 = All Mods, 2 = Bosses & Combat, 3 = Crests & Charms, 4 = Skins & Visuals
        private static int _installPageIndex = 0;
        private const int InstallPageSize = 20;
        private static readonly string[] InstallFilterLabels = new[]
        {
            "Search / Filter: ALL COMPATIBLE",
            "Search / Filter: BOSSES & ARENAS",
            "Search / Filter: CRESTS & SKILLS",
            "Search / Filter: SKINS & VISUALS"
        };

        private static string _searchQuery = "";
        private static TouchScreenKeyboard _activeSearchKeyboard;
        private static bool _isPollingSearchKeyboard;

        private static void OpenVirtualKeyboardForModSearch(Action onSearchUpdated)
        {
            try
            {
                _activeSearchKeyboard = TouchScreenKeyboard.Open(
                    _searchQuery ?? "",
                    TouchScreenKeyboardType.Default,
                    false,
                    false,
                    false,
                    false,
                    "Search mods by name or author...");
                if (!_isPollingSearchKeyboard && SilksongMobileAPIPlugin.Instance != null)
                {
                    SilksongMobileAPIPlugin.Instance.StartCoroutine(CoPollModSearchKeyboard(onSearchUpdated));
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NativeModMenu] TouchScreenKeyboard error: {ex.Message}");
            }
        }

        private static IEnumerator CoPollModSearchKeyboard(Action onSearchUpdated)
        {
            _isPollingSearchKeyboard = true;
            string lastText = _searchQuery ?? "";
            while (_activeSearchKeyboard != null)
            {
                string currentText = _activeSearchKeyboard.text ?? "";
                if (!string.Equals(currentText, lastText, StringComparison.Ordinal))
                {
                    lastText = currentText;
                    _searchQuery = currentText.Trim();
                    _installPageIndex = 0;
                }
                var status = _activeSearchKeyboard.status;
                if (status == TouchScreenKeyboard.Status.Done ||
                    status == TouchScreenKeyboard.Status.Canceled ||
                    status == TouchScreenKeyboard.Status.LostFocus ||
                    !_activeSearchKeyboard.active)
                {
                    _searchQuery = (_activeSearchKeyboard.text ?? "").Trim();
                    _activeSearchKeyboard = null;
                    _installPageIndex = 0;
                    try { onSearchUpdated?.Invoke(); } catch (System.Exception __apiEx_16) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L1434", __apiEx_16); }
                    break;
                }
                yield return null;
            }
            _isPollingSearchKeyboard = false;
        }

        private static bool MatchesModSearchQuery(string title, string author, string description)
        {
            if (string.IsNullOrWhiteSpace(_searchQuery)) return true;
            string q = _searchQuery.Trim();
            if (!string.IsNullOrEmpty(title) && title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(author) && author.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(description) && description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        private static int _manageFilterMode = 0; // 0 = All, 1 = Enabled Only, 2 = Disabled Only
        private static readonly string[] ManageFilterLabels = new[]
        {
            "Search for Mods: ALL INSTALLED",
            "Search for Mods: ENABLED ONLY",
            "Search for Mods: DISABLED ONLY"
        };
        private static int _syncRateIndex = 0; // 0 = 1 DAY, 1 = STARTUP, 2 = MANUAL
        private static int _preloadRateIndex = 0; // 0 = HIGH, 1 = LOW

        private static Type _entryType;
        private static FieldInfo _entriesField;
        private static FieldInfo _selectableField;

        private static void InitReflection()
        {
            if (_entryType == null)
            {
                _entryType = AccessTools.Inner(typeof(MenuButtonList), "Entry");
                _entriesField = AccessTools.Field(typeof(MenuButtonList), "entries");
                if (_entryType != null)
                {
                    _selectableField = AccessTools.Field(_entryType, "selectable");
                }
            }
        }

        public static void Inject(UIManager ui)
        {
            try
            {
                if (ui == null) ui = UIManager.instance;
                if (ui == null || ui.mainMenuButtons == null) return;

                _ui = ui;
                HasInjectedMainMenu = true;
                InitReflection();
                ModMenuResources.EnsureLoaded(ui);

                if (ui.extrasMenuScreen != null && _buttonTemplate == null)
                {
                    Transform cred = ((Component)ui.extrasMenuScreen).transform.Find("Content/Credits");
                    if (cred != null)
                    {
                        _buttonTemplate = UnityEngine.Object.Instantiate(cred.gameObject);
                        _buttonTemplate.name = "CleanModMenuButtonTemplate";
                        _buttonTemplate.SetActive(false);
                        UnityEngine.Object.DontDestroyOnLoad(_buttonTemplate);

                        StripButtonVisualArtifacts(_buttonTemplate);
                        var mb = _buttonTemplate.GetComponentInChildren<MenuButton>(true);
                        if (mb != null)
                        {
                            mb.transition = Selectable.Transition.None;
                            mb.targetGraphic = null;
                            mb.flashEffect = null;
                            mb.selectHighlight = null;
                            mb.leftCursor = null;
                            mb.rightCursor = null;
                            mb.descriptionText = null;
                            mb.cancelAction = CancelAction.DoNothing;
                            mb.customGoBack = null;
                            mb.hasCancelEventTrigger = false;
                            mb.eventTrigger = null;
                            if (mb.OnSubmitPressed != null) mb.OnSubmitPressed.RemoveAllListeners();
                            mb.OnSubmitPressed = new UnityEvent();
                        }
                    }
                }

                Transform existing = ((Component)ui.mainMenuButtons).transform.Find("ModsButton");
                if (existing != null)
                {
                    var existingBtn = existing.GetComponent<MenuButton>();
                    if (existingBtn != null)
                    {
                        existingBtn.OnSubmitPressed = new UnityEvent();
                        existingBtn.OnSubmitPressed.AddListener(() => OpenMainModMenuScreen());
                    }
                    return;
                }

                MainMenuOptions mm = ui.mainMenuButtons;
                if (mm == null || mm.optionsButton == null) return;
                GameObject btnObj = UnityEngine.Object.Instantiate(mm.optionsButton.gameObject, ((Component)mm).transform, false);
                btnObj.name = "ModsButton";

                Transform bossRushBtn = ((Component)mm).transform.Find("BossRushButton");
                int sibling = (bossRushBtn != null) ? bossRushBtn.GetSiblingIndex() + 1 : mm.optionsButton.transform.GetSiblingIndex() + 1;
                btnObj.transform.SetSiblingIndex(sibling);

                foreach (var loc in btnObj.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                    UnityEngine.Object.DestroyImmediate(loc);
                foreach (var trg in btnObj.GetComponentsInChildren<EventTrigger>(true))
                    UnityEngine.Object.DestroyImmediate(trg);
                foreach (var comp in btnObj.GetComponentsInChildren<Component>(true))
                {
                    if (comp != null && comp.GetType().Name.Contains("Fsm"))
                        UnityEngine.Object.DestroyImmediate(comp);
                }

                MenuButton btn = btnObj.GetComponent<MenuButton>();
                btn.cancelAction = CancelAction.DoNothing;
                btn.customGoBack = null;
                btn.hasCancelEventTrigger = false;
                btn.eventTrigger = null;
                if (btn.OnSubmitPressed != null) btn.OnSubmitPressed.RemoveAllListeners();
                btn.OnSubmitPressed = new UnityEvent();
                btn.OnSubmitPressed.AddListener(() => OpenMainModMenuScreen());

                Text txt = btnObj.GetComponentInChildren<Text>(true);
                if (txt != null) txt.text = "Mods";

                MenuButtonList list = ((Component)mm).GetComponent<MenuButtonList>();
                if (list != null && _entriesField != null && _selectableField != null && _entryType != null)
                {
                    Array oldEntries = (Array)_entriesField.GetValue(list);
                    List<object> entryList = new List<object>();
                    if (oldEntries != null)
                    {
                        foreach (var e in oldEntries) entryList.Add(e);
                    }
                    object newEntry = Activator.CreateInstance(_entryType, true);
                    _selectableField.SetValue(newEntry, btn);
                    int idx = Mathf.Min(sibling, entryList.Count);
                    entryList.Insert(idx, newEntry);

                    Array newArr = Array.CreateInstance(_entryType, entryList.Count);
                    for (int i = 0; i < entryList.Count; i++) newArr.SetValue(entryList[i], i);
                    _entriesField.SetValue(list, newArr);
                    list.SetupActive();
                }

                SilksongMobileAPIPlugin.LogDiagnosticInfo("[NativeModMenu] Injected native 'Mods' button into Main Menu successfully.");
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[NativeModMenu] Error injecting native mods button: {ex}");
            }
        }

        private static bool IsSubModScreen(MenuScreen screen)
        {
            return screen != null &&
                   (screen == _installModsScreen ||
                    screen == _uninstallModsScreen ||
                    screen == _manageModsScreen ||
                    screen == _advancedScreen);
        }

        // Alias kept for compatibility with UIManagerModListHook and callers
        public static void OpenInstalledModsScreen() => OpenMainModMenuScreen();
        public static void OpenStoreScreen() => OpenInstallModsScreen();

        // =====================================================================
        // Screen 1: Main MODS Hub Screen (Matches "mod menu when opened.jpg")
        // =====================================================================
        public static void OpenMainModMenuScreen()
        {
            if (_ui == null && UIManager.instance != null)
                Inject(UIManager.instance);
            if (_ui == null || _ui.extrasMenuScreen == null) return;
            ModMenuResources.EnsureLoaded(_ui);

            if (CurrentCustomScreen == null || (!IsSubModScreen(CurrentCustomScreen) && CurrentCustomScreen != _mainModScreen))
            {
                _previousMenuState = _ui.menuState;
                bool isGameplay = (GameManager.instance != null && !GameManager.instance.IsMenuScene())
                                  || _ui.menuState == MainMenuState.PAUSE_MENU
                                  || _ui.menuState == MainMenuState.OPTIONS_MENU;
                _openedFromPauseMenu = isGameplay && (_ui.mainMenuScreen == null || !_ui.mainMenuScreen.gameObject.activeInHierarchy);

                if (_ui.optionsMenuScreen != null && _ui.optionsMenuScreen.gameObject.activeInHierarchy)
                {
                    _previousScreen = _ui.optionsMenuScreen;
                    _previousMenuState = MainMenuState.OPTIONS_MENU;
                }
                else if (_ui.pauseMenuScreen != null && _ui.pauseMenuScreen.gameObject.activeInHierarchy)
                {
                    _previousScreen = _ui.pauseMenuScreen;
                    _previousMenuState = MainMenuState.PAUSE_MENU;
                }
                else
                {
                    _previousScreen = null;
                }
            }

            BuildMainModMenuScreen();
            TransitionTo(_mainModScreen);
        }

        private static MenuScreen CreateCleanMenuScreenClone(string screenName, Action onBackPressed)
        {
            GameObject screenObj = UnityEngine.Object.Instantiate(_ui.extrasMenuScreen.gameObject, _ui.extrasMenuScreen.transform.parent, false);
            screenObj.name = screenName;
            screenObj.SetActive(false);
            var screen = screenObj.GetComponent<MenuScreen>();

            foreach (var comp in screenObj.GetComponentsInChildren<Component>(true))
            {
                if (comp != null && (comp.GetType().Name.Contains("Fsm") || comp.GetType().Name.Contains("Trigger")))
                {
                    if (!(comp is MenuScreen) && !(comp is MenuButtonList) && !(comp is CanvasGroup) && !(comp is Animator))
                    {
                        UnityEngine.Object.DestroyImmediate(comp);
                    }
                }
            }

            if (screen.backButton != null)
            {
                foreach (var loc in screen.backButton.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                    UnityEngine.Object.DestroyImmediate(loc);
                foreach (var trg in screen.backButton.GetComponentsInChildren<EventTrigger>(true))
                    UnityEngine.Object.DestroyImmediate(trg);
                foreach (var comp in screen.backButton.GetComponentsInChildren<Component>(true))
                    if (comp != null && comp.GetType().Name.Contains("Fsm")) UnityEngine.Object.DestroyImmediate(comp);

                screen.backButton.cancelAction = CancelAction.DoNothing;
                screen.backButton.hasCancelEventTrigger = false;
                screen.backButton.eventTrigger = null;
                if (screen.backButton.OnSubmitPressed != null)
                    screen.backButton.OnSubmitPressed.RemoveAllListeners();
                screen.backButton.OnSubmitPressed = new UnityEvent();
                if (onBackPressed != null)
                {
                    screen.backButton.OnSubmitPressed.AddListener(() => onBackPressed());
                }
                var backTxt = screen.backButton.GetComponentInChildren<Text>(true);
                if (backTxt != null) backTxt.text = "BACK";

                var backEt = screen.backButton.gameObject.GetComponent<EventTrigger>() ?? screen.backButton.gameObject.AddComponent<EventTrigger>();
                backEt.triggers = new List<EventTrigger.Entry>();
                var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                clickEntry.callback.AddListener((_) => onBackPressed?.Invoke());
                backEt.triggers.Add(clickEntry);
            }

            return screen;
        }

        private static void BuildMainModMenuScreen()
        {
            if (_mainModScreen == null)
            {
                _mainModScreen = CreateCleanMenuScreenClone("NativeMainModMenuScreen", () => CloseModMenu());
            }

            SetScreenTitle(_mainModScreen, "MODS");

            Transform contentTrans = _mainModScreen.transform.Find("Content");
            if (contentTrans == null) return;
            for (int i = contentTrans.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(contentTrans.GetChild(i).gameObject);

            RectTransform contentRect = contentTrans.GetComponent<RectTransform>();
            if (contentRect != null)
            {
                contentRect.anchorMin = new Vector2(0.15f, 0.14f);
                contentRect.anchorMax = new Vector2(0.85f, 0.80f);
                contentRect.pivot = new Vector2(0.5f, 0.5f);
                contentRect.anchoredPosition = Vector2.zero;
                contentRect.sizeDelta = Vector2.zero;
            }
            VerticalLayoutGroup vlg = contentTrans.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.spacing = 18f;
                vlg.childAlignment = TextAnchor.MiddleCenter;
                vlg.childControlHeight = false;
                vlg.childControlWidth = false;
                vlg.childForceExpandHeight = false;
                vlg.childForceExpandWidth = false;
            }

            List<Selectable> selectables = new List<Selectable>();

            selectables.Add(CreateRowButton(contentTrans, "MOD KEYS, CONFIGS & PROFILE", () => SilksongMobileAPIPlugin.Instance?.OpenModPanel("Profile"), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "INSTALL MODS", () => OpenInstallModsScreen(), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "UNINSTALL MODS", () => OpenUninstallModsScreen(), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "MANAGE MODS", () => OpenManageModsScreen(), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "IMPORT MOD", () => InvokeBepInExImportMod(), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "IMPORT SAVE", () => InvokeBepInExImportSave(), 34, 62f));
            selectables.Add(CreateRowButton(contentTrans, "ADVANCED", () => OpenAdvancedScreen(), 34, 62f));

            if (_mainModScreen.backButton != null)
            {
                _mainModScreen.backButton.gameObject.SetActive(true);
                selectables.Add(_mainModScreen.backButton);
            }

            WireScreenButtons(_mainModScreen, selectables);
        }

        // =====================================================================
        // Native ScrollPane Builder (Matches ScrollPane.cs + Achievements Scrollbar)
        // =====================================================================
        private static void BuildScrollableScreenLayout(
            MenuScreen screen,
            string title,
            float itemRowHeight,
            int itemCount,
            out RectTransform scrollPaneRt,
            out Scrollbar scrollbarComp,
            out RectTransform controlPaneRt)
        {
            SetScreenTitle(screen, title);

            // Hide default backButton since we place Search + Back in the ControlPane matching HK Modding API screenshots
            if (screen.backButton != null)
                screen.backButton.gameObject.SetActive(false);

            Transform contentTrans = screen.transform.Find("Content");
            for (int i = contentTrans.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(contentTrans.GetChild(i).gameObject);

            VerticalLayoutGroup oldVlg = contentTrans.GetComponent<VerticalLayoutGroup>();
            if (oldVlg != null) oldVlg.enabled = false;

            RectTransform contentRect = contentTrans.GetComponent<RectTransform>();
            if (contentRect != null)
            {
                contentRect.anchorMin = new Vector2(0f, 0f);
                contentRect.anchorMax = new Vector2(1f, 1f);
                contentRect.pivot = new Vector2(0.5f, 0.5f);
                contentRect.anchoredPosition = Vector2.zero;
                contentRect.sizeDelta = Vector2.zero;
            }

            // 1. ScrollMask (viewport) — uses RectMask2D + EmptyRaycastGraphic (zero stencil/white quad artifacts)
            var scrollMask = new GameObject("ScrollMask");
            scrollMask.transform.SetParent(contentTrans, false);
            var scrollMaskRt = scrollMask.AddComponent<RectTransform>();
            scrollMaskRt.anchorMin = new Vector2(0.12f, 0.23f);
            scrollMaskRt.anchorMax = new Vector2(0.86f, 0.81f);
            scrollMaskRt.pivot = new Vector2(0.5f, 0.5f);
            scrollMaskRt.anchoredPosition = Vector2.zero;
            scrollMaskRt.sizeDelta = Vector2.zero;
            scrollMask.AddComponent<CanvasRenderer>();
            var maskHitbox = scrollMask.AddComponent<EmptyRaycastGraphic>();
            maskHitbox.raycastTarget = true;
            scrollMask.AddComponent<RectMask2D>();

            // 2. ScrollingPane (content)
            var scrollPane = new GameObject("ScrollingPane");
            scrollPane.transform.SetParent(scrollMask.transform, false);
            scrollPaneRt = scrollPane.AddComponent<RectTransform>();
            scrollPaneRt.anchorMin = new Vector2(0f, 1f);
            scrollPaneRt.anchorMax = new Vector2(1f, 1f);
            scrollPaneRt.pivot = new Vector2(0.5f, 1f);
            scrollPaneRt.anchoredPosition = Vector2.zero;
            float totalHeight = Mathf.Max(520f, itemCount * itemRowHeight + 24f);
            scrollPaneRt.sizeDelta = new Vector2(0f, totalHeight);
            scrollPane.AddComponent<CanvasRenderer>();

            var paneVlg = scrollPane.AddComponent<VerticalLayoutGroup>();
            paneVlg.spacing = 10f;
            paneVlg.padding = new RectOffset(16, 28, 8, 12);
            paneVlg.childAlignment = TextAnchor.UpperCenter;
            paneVlg.childControlHeight = false;
            paneVlg.childControlWidth = false;
            paneVlg.childForceExpandHeight = false;
            paneVlg.childForceExpandWidth = false;

            // 3. Native Right-Side Scrollbar (matching ScrollPane.cs & Achievements Menu)
            var scrollbarGo = new GameObject("Scrollbar");
            scrollbarGo.transform.SetParent(contentTrans, false);
            var scrollbarRt = scrollbarGo.AddComponent<RectTransform>();
            scrollbarRt.anchorMin = new Vector2(0.88f, 0.23f);
            scrollbarRt.anchorMax = new Vector2(0.88f, 0.81f);
            scrollbarRt.pivot = new Vector2(0.5f, 0.5f);
            scrollbarRt.anchoredPosition = Vector2.zero;
            scrollbarRt.sizeDelta = new Vector2(38f, 0f);
            scrollbarGo.AddComponent<CanvasRenderer>();

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(scrollbarGo.transform, false);
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0.5f, 0f);
            bgRt.anchorMax = new Vector2(0.5f, 1f);
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.anchoredPosition = Vector2.zero;
            bgRt.sizeDelta = new Vector2(5f, 0f);
            bgGo.AddComponent<CanvasRenderer>();
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = ModMenuResources.ScrollbarBackgroundSprite;
            bgImg.color = new Color(1f, 1f, 1f, 0.85f);

            var slidingArea = new GameObject("Sliding Area");
            slidingArea.transform.SetParent(scrollbarGo.transform, false);
            var slidingRt = slidingArea.AddComponent<RectTransform>();
            slidingRt.anchorMin = Vector2.zero;
            slidingRt.anchorMax = Vector2.one;
            slidingRt.pivot = new Vector2(0.5f, 0.5f);
            slidingRt.anchoredPosition = Vector2.zero;
            slidingRt.sizeDelta = new Vector2(-10f, -20f);

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(slidingArea.transform, false);
            var handleRt = handleGo.AddComponent<RectTransform>();
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.pivot = new Vector2(0.5f, 0.5f);
            handleRt.anchoredPosition = Vector2.zero;
            handleRt.sizeDelta = new Vector2(36f, 0f);
            handleGo.AddComponent<CanvasRenderer>();

            var handleFleur = new GameObject("TopFleur");
            handleFleur.transform.SetParent(handleGo.transform, false);
            var handleFleurRt = handleFleur.AddComponent<RectTransform>();
            handleFleurRt.anchorMin = new Vector2(0.5f, 0.5f);
            handleFleurRt.anchorMax = new Vector2(0.5f, 0.5f);
            handleFleurRt.pivot = new Vector2(0.5f, 0.5f);
            handleFleurRt.anchoredPosition = Vector2.zero;
            handleFleurRt.sizeDelta = new Vector2(38f, 68f);
            handleFleur.AddComponent<CanvasRenderer>();
            var handleImg = handleFleur.AddComponent<Image>();
            handleImg.sprite = ModMenuResources.ScrollbarHandleSprite;

            scrollbarComp = scrollbarGo.AddComponent<Scrollbar>();
            scrollbarComp.direction = Scrollbar.Direction.BottomToTop;
            scrollbarComp.numberOfSteps = 0;
            scrollbarComp.handleRect = handleRt;
            scrollbarComp.targetGraphic = handleImg;
            scrollbarComp.size = Mathf.Clamp(520f / Mathf.Max(520f, totalHeight), 0.1f, 1f);
            scrollbarComp.value = 1f;

            var sbh = handleFleur.AddComponent<ScrollBarHandle>();
            sbh.scrollBar = scrollbarComp;

            var scrollRect = scrollMask.AddComponent<ScrollRect>();
            scrollRect.viewport = scrollMaskRt;
            scrollRect.content = scrollPaneRt;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 35f;
            scrollRect.verticalScrollbar = scrollbarComp;

            // 4. Bottom ControlPane (for "Search for Mods" and "BACK")
            var ctrlGo = new GameObject("ControlPane");
            ctrlGo.transform.SetParent(contentTrans, false);
            controlPaneRt = ctrlGo.AddComponent<RectTransform>();
            controlPaneRt.anchorMin = new Vector2(0.15f, 0.04f);
            controlPaneRt.anchorMax = new Vector2(0.85f, 0.21f);
            controlPaneRt.pivot = new Vector2(0.5f, 0.5f);
            controlPaneRt.anchoredPosition = Vector2.zero;
            controlPaneRt.sizeDelta = Vector2.zero;

            var ctrlVlg = ctrlGo.AddComponent<VerticalLayoutGroup>();
            ctrlVlg.spacing = 10f;
            ctrlVlg.childAlignment = TextAnchor.MiddleCenter;
            ctrlVlg.childControlHeight = false;
            ctrlVlg.childControlWidth = false;
            ctrlVlg.childForceExpandHeight = false;
            ctrlVlg.childForceExpandWidth = false;
        }

        // =====================================================================
        // Screen 2: INSTALL MODS Screen (Matches "install mods preview with native game ui and scrollbar.jpg")
        // =====================================================================
        public static void OpenInstallModsScreen()
        {
            if (_ui == null || _ui.extrasMenuScreen == null) return;
            _searchQuery = "";
            _installPageIndex = 0;
            InGameModManager.Instance?.EnsureStoreCatalogLoaded();
            BuildInstallModsScreen();
            TransitionTo(_installModsScreen);
        }

        private static List<OnlineModCatalogItem> GetFilteredInstallCatalog()
        {
            var all = InGameModManager.Instance.OnlineCatalog
                .Where(c => MatchesModSearchQuery(c.DisplayTitle, c.Author, c.Description))
                .ToList();
            switch (_installFilterMode)
            {
                case 0: // All Compatible
                    return all.Where(c => c.IsCompatible).ToList();
                case 1: // Unfiltered
                    return all.ToList();
                case 2: // Bosses & Arenas
                    return all.Where(c => c.IsCompatible &&
                        (c.DisplayTitle.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Judge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Bonemaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Pantheon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Sentinel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (c.Description != null && c.Description.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
                case 3: // Crests & Skills
                    return all.Where(c => c.IsCompatible &&
                        (c.DisplayTitle.IndexOf("Crest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Skill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Needle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Charm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (c.Description != null && c.Description.IndexOf("crest", StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
                case 4: // Skins & Visuals
                    return all.Where(c => c.IsCompatible &&
                        (c.DisplayTitle.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Glory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.DisplayTitle.IndexOf("Style", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                default:
                    return all.Where(c => c.IsCompatible).ToList();
            }
        }

        private static void BuildInstallModsScreen()
        {
            if (_installModsScreen == null)
            {
                _installModsScreen = CreateCleanMenuScreenClone("NativeInstallModsScreen", () => OpenMainModMenuScreen());
            }

            var catalog = GetFilteredInstallCatalog();
            int totalPages = Math.Max(1, (catalog.Count + InstallPageSize - 1) / InstallPageSize);
            if (_installPageIndex >= totalPages) _installPageIndex = totalPages - 1;
            if (_installPageIndex < 0) _installPageIndex = 0;

            var pageItems = catalog.Skip(_installPageIndex * InstallPageSize).Take(InstallPageSize).ToList();

            BuildScrollableScreenLayout(
                _installModsScreen,
                $"INSTALL MODS ({_installPageIndex + 1}/{totalPages})",
                76f,
                Math.Max(1, pageItems.Count + (totalPages > 1 ? 2 : 0)),
                out var scrollPaneRt,
                out var scrollbar,
                out var controlPaneRt);

            var maskRt = scrollPaneRt.parent as RectTransform;
            List<Selectable> selectables = new List<Selectable>();

            if (catalog.Count == 0)
            {
                string emptyMsg = InGameModManager.Instance.IsFetchingStore
                    ? "Fetching catalog from Thunderstore..."
                    : "No matching mods in current filter";
                var emptyBtn = CreateRowButton(scrollPaneRt, emptyMsg, null, 28, 66f);
                if (emptyBtn != null) selectables.Add(emptyBtn);
            }
            else
            {
                if (totalPages > 1)
                {
                    var prevNextTopBtn = CreateRowButton(
                        scrollPaneRt,
                        $"<color=#FFD877>[ PAGE {_installPageIndex + 1} OF {totalPages} — TAP FOR NEXT PAGE ({catalog.Count} MODS) ]</color>",
                        () =>
                        {
                            _installPageIndex = (_installPageIndex + 1) % totalPages;
                            BuildInstallModsScreen();
                        },
                        26,
                        56f);
                    if (prevNextTopBtn != null)
                    {
                        AttachScrollSelector(prevNextTopBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(prevNextTopBtn);
                    }
                }

                foreach (var item in pageItems)
                {
                    string statusTag;
                    if (item.IsInstalled)
                        statusTag = "<color=#77FF99>[INSTALLED]</color>";
                    else if (!item.IsCompatible)
                        statusTag = "<color=#FF6666>[PC BUNDLE]</color>";
                    else if (item.InstallStatus == "Downloading")
                        statusTag = $"<color=#66EEFF>[DOWNLOADING {(int)(item.DownloadProgress * 100)}%]</color>";
                    else if (item.InstallStatus == "Extracting" || item.InstallStatus == "Adapting")
                        statusTag = $"<color=#FFEE66>[{item.InstallStatus.ToUpperInvariant()}...]</color>";
                    else if (item.InstallStatus == "Failed")
                        statusTag = "<color=#FF6666>[RETRY]</color>";
                    else
                        statusTag = $"v{item.Version} by {item.Author}";

                    string rowLabel = $"{item.DisplayTitle}  <size=22>{statusTag}</size>";
                    OnlineModCatalogItem capturedItem = item;
                    Text rowTextRef = null;

                    MenuButton itemBtn = CreateRowButton(scrollPaneRt, rowLabel, () =>
                    {
                        if (capturedItem.IsInstalled || !capturedItem.IsCompatible) return;
                        if (capturedItem.InstallStatus == "Downloading" || capturedItem.InstallStatus == "Extracting" || capturedItem.InstallStatus == "Adapting")
                            return;

                        capturedItem.InstallStatus = "Downloading";
                        capturedItem.DownloadProgress = 0.05f;
                        if (rowTextRef != null)
                        {
                            rowTextRef.text = $"{capturedItem.DisplayTitle}  <size=22><color=#66EEFF>[STARTING...]</color></size>";
                        }
                        InGameModManager.Instance.InstallOnlineMod(
                            capturedItem,
                            onProgress: (prog, st) =>
                            {
                                if (rowTextRef != null && CurrentCustomScreen == _installModsScreen)
                                {
                                    rowTextRef.text = $"{capturedItem.DisplayTitle}  <size=22><color=#66EEFF>[{st.ToUpperInvariant()} {(int)(prog * 100)}%]</color></size>";
                                }
                            },
                            onComplete: (ok, err) =>
                            {
                                if (CurrentCustomScreen == _installModsScreen)
                                {
                                    BuildInstallModsScreen();
                                }
                            });
                    }, out rowTextRef, 30, 66f, TextAnchor.MiddleLeft);

                    if (itemBtn != null)
                    {
                        AttachScrollSelector(itemBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(itemBtn);
                    }
                }
            }

            if (totalPages > 1 && pageItems.Count > 0)
            {
                var prevBtn = CreateRowButton(
                    scrollPaneRt,
                    $"<color=#99EEFF>[ << PREVIOUS PAGE ({((_installPageIndex - 1 + totalPages) % totalPages) + 1}/{totalPages}) ]</color>",
                    () =>
                    {
                        _installPageIndex = (_installPageIndex - 1 + totalPages) % totalPages;
                        BuildInstallModsScreen();
                    },
                    26,
                    56f);
                if (prevBtn != null)
                {
                    AttachScrollSelector(prevBtn, scrollPaneRt, maskRt, scrollbar);
                    selectables.Add(prevBtn);
                }
            }

            string queryDisplay = string.IsNullOrWhiteSpace(_searchQuery) ? "All (Tap to Type)" : $"'{_searchQuery}' (Tap to Edit)";
            string filterShort = InstallFilterLabels[_installFilterMode % InstallFilterLabels.Length].Replace("Search: ", "");
            string searchLabel = $"Search Mods: {queryDisplay}  <size=21>[{filterShort}]</size>";
            var searchBtn = CreateRowButton(controlPaneRt, searchLabel, () =>
            {
                OpenVirtualKeyboardForModSearch(() => BuildInstallModsScreen());
            }, 28, 50f);
            if (searchBtn != null) selectables.Add(searchBtn);

            var backBtn = CreateRowButton(controlPaneRt, "BACK", () => OpenMainModMenuScreen(), 34, 54f);
            if (backBtn != null) selectables.Add(backBtn);

            WireScreenButtons(_installModsScreen, selectables);
        }

        // =====================================================================
        // Screen 3: MANAGE MODS Screen (Matches "manage mods when opened.jpg")
        // =====================================================================
        public static void OpenManageModsScreen()
        {
            if (_ui == null || _ui.extrasMenuScreen == null) return;
            _searchQuery = "";
            BuildManageModsScreen();
            TransitionTo(_manageModsScreen);
        }

        private static void BuildManageModsScreen()
        {
            if (_manageModsScreen == null)
            {
                _manageModsScreen = CreateCleanMenuScreenClone("NativeManageModsScreen", () => OpenMainModMenuScreen());
            }

            InGameModManager.Instance.ScanInstalledMods();
            var allMods = InGameModManager.Instance.InstalledMods;
            List<InstalledModItem> mods;
            if (_manageFilterMode == 1)
                mods = allMods.Where(m => m.IsEnabled).ToList();
            else if (_manageFilterMode == 2)
                mods = allMods.Where(m => !m.IsEnabled).ToList();
            else
                mods = allMods.ToList();
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                mods = mods.Where(m => MatchesModSearchQuery(m.DisplayName, m.FolderName, m.Description)).ToList();
            }

            BuildScrollableScreenLayout(
                _manageModsScreen,
                "MANAGE MODS",
                94f,
                Math.Max(1, mods.Count),
                out var scrollPaneRt,
                out var scrollbar,
                out var controlPaneRt);

            var maskRt = scrollPaneRt.parent as RectTransform;
            List<Selectable> selectables = new List<Selectable>();

            if (mods.Count == 0)
            {
                var emptyBtn = CreateRowButton(scrollPaneRt, "No mods matching current filter", null, 28, 70f);
                if (emptyBtn != null) selectables.Add(emptyBtn);
            }
            else
            {
                foreach (var mod in mods)
                {
                    InstalledModItem capturedMod = mod;
                    string subDesc = $"v{mod.Version}" + (mod.IsCore ? " (Core API)" : "");
                    MenuButton horizBtn = CreateHorizontalOptionRow(
                        scrollPaneRt,
                        mod.DisplayName,
                        subDesc,
                        new[] { "OFF", "ON" },
                        mod.IsEnabled ? 1 : 0,
                        newIdx =>
                        {
                            bool wantEnabled = newIdx == 1;
                            if (capturedMod.IsEnabled != wantEnabled)
                            {
                                InGameModManager.Instance.ToggleMod(capturedMod);
                            }
                        });
                    if (horizBtn != null)
                    {
                        AttachScrollSelector(horizBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(horizBtn);
                    }
                }
            }

            string queryDisplay = string.IsNullOrWhiteSpace(_searchQuery) ? "All (Tap to Type)" : $"'{_searchQuery}' (Tap to Edit)";
            string searchLabel = $"Search Mods: {queryDisplay}";
            var searchBtn = CreateRowButton(controlPaneRt, searchLabel, () =>
            {
                OpenVirtualKeyboardForModSearch(() => BuildManageModsScreen());
            }, 28, 50f);
            if (searchBtn != null) selectables.Add(searchBtn);

            var backBtn = CreateRowButton(controlPaneRt, "BACK", () => OpenMainModMenuScreen(), 34, 54f);
            if (backBtn != null) selectables.Add(backBtn);

            WireScreenButtons(_manageModsScreen, selectables);
        }

        // =====================================================================
        // Screen 4: UNINSTALL MODS Screen (Matches "delete mods when opened.jpg")
        // =====================================================================
        public static void OpenUninstallModsScreen()
        {
            if (_ui == null || _ui.extrasMenuScreen == null) return;
            _searchQuery = "";
            _installPageIndex = 0;
            InGameModManager.Instance?.ScanInstalledMods();
            if (InGameModManager.Instance != null)
            {
                foreach (var m in InGameModManager.Instance.InstalledMods)
                    if (m != null) m.PendingDeleteConfirm = false;
            }
            BuildUninstallModsScreen();
            TransitionTo(_uninstallModsScreen);
        }

        private static void BuildUninstallModsScreen()
        {
            if (_uninstallModsScreen == null)
            {
                _uninstallModsScreen = CreateCleanMenuScreenClone("NativeUninstallModsScreen", () => OpenMainModMenuScreen());
            }

            if (InGameModManager.Instance != null && InGameModManager.Instance.InstalledMods.Count == 0)
            {
                InGameModManager.Instance.ScanInstalledMods();
            }

            var allDeletable = InGameModManager.Instance != null
                ? InGameModManager.Instance.InstalledMods.Where(m => m != null && !m.IsCore).ToList()
                : new List<InstalledModItem>();

            var deletableMods = allDeletable
                .Where(m => MatchesModSearchQuery(m.DisplayName, m.FolderName, m.Description))
                .ToList();

            if (deletableMods.Count == 0 && !string.IsNullOrWhiteSpace(_searchQuery) && allDeletable.Count > 0)
            {
                _searchQuery = "";
                deletableMods = allDeletable;
            }

            int totalPages = Math.Max(1, (deletableMods.Count + InstallPageSize - 1) / InstallPageSize);
            if (_installPageIndex >= totalPages) _installPageIndex = totalPages - 1;
            if (_installPageIndex < 0) _installPageIndex = 0;

            var pageItems = deletableMods.Skip(_installPageIndex * InstallPageSize).Take(InstallPageSize).ToList();

            BuildScrollableScreenLayout(
                _uninstallModsScreen,
                totalPages > 1 ? $"UNINSTALL MODS ({_installPageIndex + 1}/{totalPages})" : $"UNINSTALL MODS ({deletableMods.Count})",
                76f,
                Math.Max(1, pageItems.Count + (totalPages > 1 ? 2 : 0)),
                out var scrollPaneRt,
                out var scrollbar,
                out var controlPaneRt);

            var maskRt = scrollPaneRt.parent as RectTransform;
            List<Selectable> selectables = new List<Selectable>();

            if (deletableMods.Count == 0)
            {
                var emptyBtn = CreateRowButton(scrollPaneRt, "No removable mods found", null, 28, 66f);
                if (emptyBtn != null) selectables.Add(emptyBtn);
            }
            else
            {
                if (totalPages > 1)
                {
                    var prevNextTopBtn = CreateRowButton(
                        scrollPaneRt,
                        $"<color=#FFD877>[ PAGE {_installPageIndex + 1} OF {totalPages} — TAP FOR NEXT PAGE ({deletableMods.Count} MODS) ]</color>",
                        () =>
                        {
                            _installPageIndex = (_installPageIndex + 1) % totalPages;
                            BuildUninstallModsScreen();
                        },
                        26,
                        56f);
                    if (prevNextTopBtn != null)
                    {
                        AttachScrollSelector(prevNextTopBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(prevNextTopBtn);
                    }
                }

                foreach (var mod in pageItems)
                {
                    InstalledModItem capturedMod = mod;
                    long kb = Math.Max(1L, mod.SizeBytes / 1024L);
                    string label = mod.PendingDeleteConfirm
                        ? $"<color=#FF5555>CONFIRM UNINSTALL: {mod.DisplayName}? (Tap Again)</color>"
                        : $"{mod.DisplayName}  <size=22><color=#FFAA88>[UNINSTALL]</color> v{mod.Version} ({kb} KB)</size>";

                    Text rowTextRef = null;
                    MenuButton delBtn = CreateRowButton(scrollPaneRt, label, () =>
                    {
                        if (!capturedMod.PendingDeleteConfirm)
                        {
                            foreach (var m in allDeletable) m.PendingDeleteConfirm = false;
                            capturedMod.PendingDeleteConfirm = true;
                            if (rowTextRef != null)
                                rowTextRef.text = $"<color=#FF5555>CONFIRM UNINSTALL: {capturedMod.DisplayName}? (Tap Again)</color>";
                            else
                                BuildUninstallModsScreen();
                        }
                        else
                        {
                            capturedMod.PendingDeleteConfirm = false;
                            InGameModManager.Instance.DeleteMod(capturedMod);
                            BuildUninstallModsScreen();
                        }
                    }, out rowTextRef, 30, 66f, TextAnchor.MiddleLeft);

                    if (delBtn != null)
                    {
                        AttachScrollSelector(delBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(delBtn);
                    }
                }

                if (totalPages > 1 && pageItems.Count > 0)
                {
                    var prevBtn = CreateRowButton(
                        scrollPaneRt,
                        $"<color=#99EEFF>[ << PREVIOUS PAGE ({((_installPageIndex - 1 + totalPages) % totalPages) + 1}/{totalPages}) ]</color>",
                        () =>
                        {
                            _installPageIndex = (_installPageIndex - 1 + totalPages) % totalPages;
                            BuildUninstallModsScreen();
                        },
                        26,
                        56f);
                    if (prevBtn != null)
                    {
                        AttachScrollSelector(prevBtn, scrollPaneRt, maskRt, scrollbar);
                        selectables.Add(prevBtn);
                    }
                }
            }

            string queryDisplay = string.IsNullOrWhiteSpace(_searchQuery) ? "All (Tap to Type)" : $"'{_searchQuery}' (Tap to Edit)";
            var searchBtn = CreateRowButton(controlPaneRt, $"Search Mods: {queryDisplay}", () =>
            {
                OpenVirtualKeyboardForModSearch(() => BuildUninstallModsScreen());
            }, 28, 50f);
            if (searchBtn != null) selectables.Add(searchBtn);

            var backBtn = CreateRowButton(controlPaneRt, "BACK", () => OpenMainModMenuScreen(), 34, 54f);
            if (backBtn != null) selectables.Add(backBtn);

            WireScreenButtons(_uninstallModsScreen, selectables);
        }

        // =====================================================================
        // Screen 5: ADVANCED OPTIONS Screen (Matches "advanced option preview.jpg")
        // =====================================================================
        public static void OpenAdvancedScreen()
        {
            if (_ui == null || _ui.extrasMenuScreen == null) return;
            BuildAdvancedScreen();
            TransitionTo(_advancedScreen);
        }

        private static void BuildAdvancedScreen()
        {
            if (_advancedScreen == null)
            {
                _advancedScreen = CreateCleanMenuScreenClone("NativeAdvancedModScreen", () => OpenMainModMenuScreen());
            }

            BuildScrollableScreenLayout(
                _advancedScreen,
                "ADVANCED OPTIONS",
                88f,
                9,
                out var scrollPaneRt,
                out var scrollbar,
                out var controlPaneRt);

            var maskRt = scrollPaneRt.parent as RectTransform;
            List<Selectable> selectables = new List<Selectable>();

            var syncRow = CreateHorizontalOptionRow(
                scrollPaneRt,
                "MOD LIST SYNCHRONIZATION:",
                "Catalog refresh interval",
                new[] { "1 DAY", "STARTUP", "MANUAL" },
                _syncRateIndex,
                idx => _syncRateIndex = idx);
            if (syncRow != null) { AttachScrollSelector(syncRow, scrollPaneRt, maskRt, scrollbar); selectables.Add(syncRow); }

            var compatRow = CreateHorizontalOptionRow(
                scrollPaneRt,
                "COMPATIBILITY FILTER:",
                "Hide PC-only AssetBundle mods in catalog",
                new[] { "OFF", "ON" },
                InGameModManager.Instance.ShowOnlyCompatibleMods ? 1 : 0,
                idx => InGameModManager.Instance.ShowOnlyCompatibleMods = (idx == 1));
            if (compatRow != null) { AttachScrollSelector(compatRow, scrollPaneRt, maskRt, scrollbar); selectables.Add(compatRow); }

            var logRow = CreateHorizontalOptionRow(
                scrollPaneRt,
                "COMBAT LOG SUPPRESSION:",
                "Prevent mid-combat disk I/O lag spikes",
                new[] { "OFF", "ON" },
                HarmonyBridge.SuppressVerboseCombatLogs ? 1 : 0,
                idx => HarmonyBridge.SuppressVerboseCombatLogs = (idx == 1));
            if (logRow != null) { AttachScrollSelector(logRow, scrollPaneRt, maskRt, scrollbar); selectables.Add(logRow); }

            var preloadRow = CreateHorizontalOptionRow(
                scrollPaneRt,
                "PRELOADING RATE:",
                "Arena & template warmup speed",
                new[] { "HIGH", "LOW" },
                _preloadRateIndex,
                idx => _preloadRateIndex = idx);
            if (preloadRow != null) { AttachScrollSelector(preloadRow, scrollPaneRt, maskRt, scrollbar); selectables.Add(preloadRow); }

            var silkRow = CreateHorizontalOptionRow(
                scrollPaneRt,
                "FREE WEAVER SILK ATTACKS:",
                "Zero silk drain on Weaver Crest attacks",
                new[] { "OFF", "ON" },
                HarmonyBridge.FreeWeaverAttacksNoSilkDrain ? 1 : 0,
                idx => HarmonyBridge.FreeWeaverAttacksNoSilkDrain = (idx == 1));
            if (silkRow != null) { AttachScrollSelector(silkRow, scrollPaneRt, maskRt, scrollbar); selectables.Add(silkRow); }

            var keysBtn = CreateRowButton(scrollPaneRt, "MOD KEYS & CONFIGS (HOTKEYS / SETTINGS)", () =>
            {
                CloseModMenu();
                SilksongMobileAPIPlugin.Instance?.OpenModPanel("Hotkeys");
            }, 30, 66f);
            if (keysBtn != null) { AttachScrollSelector(keysBtn, scrollPaneRt, maskRt, scrollbar); selectables.Add(keysBtn); }

            var unlockBtn = CreateRowButton(scrollPaneRt, "UNLOCK ALL CREST SLOTS & SKILLS", () =>
            {
                SilksongMobileAPIPlugin.UnlockAllCrestSlotsAndModSkills();
            }, 30, 66f);
            if (unlockBtn != null) { AttachScrollSelector(unlockBtn, scrollPaneRt, maskRt, scrollbar); selectables.Add(unlockBtn); }

            var backupBtn = CreateRowButton(scrollPaneRt, "BACKUP SAVE", () => InvokeBepInExExportSave(), 30, 66f);
            if (backupBtn != null) { AttachScrollSelector(backupBtn, scrollPaneRt, maskRt, scrollbar); selectables.Add(backupBtn); }

            var resetBtn = CreateRowButton(scrollPaneRt, "RESET DEFAULTS", () =>
            {
                _syncRateIndex = 0;
                _preloadRateIndex = 0;
                InGameModManager.Instance.ShowOnlyCompatibleMods = true;
                HarmonyBridge.SuppressVerboseCombatLogs = true;
                HarmonyBridge.FreeWeaverAttacksNoSilkDrain = false;
                BuildAdvancedScreen();
            }, 30, 66f);
            if (resetBtn != null) { AttachScrollSelector(resetBtn, scrollPaneRt, maskRt, scrollbar); selectables.Add(resetBtn); }

            var backBtn = CreateRowButton(controlPaneRt, "BACK", () => OpenMainModMenuScreen(), 34, 54f);
            if (backBtn != null) selectables.Add(backBtn);

            WireScreenButtons(_advancedScreen, selectables);
        }

        // =====================================================================
        // Native Import / Export Actions (BepInExLoader Integration)
        // =====================================================================
        private static void InvokeBepInExImportMod()
        {
            try
            {
                var loaderType = AccessTools.TypeByName("BepInEx.BepInExLoader");
                var importMi = loaderType?.GetMethod("ImportPlugin", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (importMi != null)
                {
                    importMi.Invoke(null, null);
                    if (SilksongMobileAPIPlugin.Instance != null)
                        SilksongMobileAPIPlugin.Instance.StartCoroutine(CoRescanAfterImport());
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NativeModMenu] ImportPlugin notice: {ex.Message}");
            }
        }

        private static void InvokeImportSkin()
        {
            try
            {
                string stitchSkinsDir = Path.Combine(Paths.PluginPath, "Stitchwork", "Skins");
                Directory.CreateDirectory(stitchSkinsDir);
                InvokeBepInExImportMod();
            }
            catch (System.Exception __apiEx_17) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L2492", __apiEx_17); }
        }

        private static void InvokeBepInExImportSave()
        {
            try
            {
                var loaderType = AccessTools.TypeByName("BepInEx.BepInExLoader");
                var importSaveMi = loaderType?.GetMethod("ImportSaveData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                importSaveMi?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NativeModMenu] ImportSaveData notice: {ex.Message}");
            }
        }

        private static void InvokeBepInExExportSave()
        {
            try
            {
                var loaderType = AccessTools.TypeByName("BepInEx.BepInExLoader");
                var exportSaveMi = loaderType?.GetMethod("ExportSaveData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (exportSaveMi != null)
                {
                    string saveDir = Paths.SaveDataPath ?? Application.persistentDataPath;
                    if (Directory.Exists(saveDir))
                    {
                        var datFiles = Directory.GetFiles(saveDir, "user*.dat", SearchOption.AllDirectories);
                        if (datFiles.Length > 0)
                        {
                            exportSaveMi.Invoke(null, new object[] { datFiles[0] });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[NativeModMenu] ExportSaveData notice: {ex.Message}");
            }
        }

        private static IEnumerator CoRescanAfterImport()
        {
            float[] waits = new float[] { 2.0f, 3.0f, 5.0f };
            for (int i = 0; i < waits.Length; i++)
            {
                yield return new WaitForSecondsRealtime(waits[i]);
                try
                {
                    SilksongMobileAPIPlugin.PreOrganizeTopLevelPcModDlls();
                    InGameModManager.Instance?.ScanInstalledMods();
                }
                catch (System.Exception __apiEx_18) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L2542", __apiEx_18); }
            }
        }

        // =====================================================================
        // UI Row Builders (MenuButton & HorizontalOption Matching HK Modding API)
        // =====================================================================
        private static void StripButtonVisualArtifacts(GameObject obj)
        {
            if (obj == null) return;
            foreach (var loc in obj.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                UnityEngine.Object.DestroyImmediate(loc);
            foreach (var trg in obj.GetComponentsInChildren<EventTrigger>(true))
                UnityEngine.Object.DestroyImmediate(trg);
            foreach (var b in obj.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                UnityEngine.Object.DestroyImmediate(b);
            foreach (var anim in obj.GetComponentsInChildren<Animator>(true))
                UnityEngine.Object.DestroyImmediate(anim);
            foreach (var img in obj.GetComponentsInChildren<Image>(true))
                UnityEngine.Object.DestroyImmediate(img);
            foreach (var raw in obj.GetComponentsInChildren<RawImage>(true))
                UnityEngine.Object.DestroyImmediate(raw);
            foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>(true))
                UnityEngine.Object.DestroyImmediate(sr);
            foreach (var mr in obj.GetComponentsInChildren<MeshRenderer>(true))
                UnityEngine.Object.DestroyImmediate(mr);
            foreach (var comp in obj.GetComponentsInChildren<Component>(true))
            {
                if (comp != null && (comp.GetType().Name.Contains("Fsm") || comp.GetType().Name.Contains("Trigger")))
                    UnityEngine.Object.DestroyImmediate(comp);
            }
            for (int i = obj.transform.childCount - 1; i >= 0; i--)
            {
                var ch = obj.transform.GetChild(i);
                if (ch != null && ch.GetComponentInChildren<Text>(true) == null)
                {
                    UnityEngine.Object.DestroyImmediate(ch.gameObject);
                }
            }
        }

        private static void AttachScrollSelector(Selectable sel, RectTransform paneRt, RectTransform maskRt, Scrollbar sb)
        {
            if (sel == null || paneRt == null || maskRt == null || sb == null) return;
            var sps = sel.gameObject.GetComponent<ModScrollPaneSelector>() ?? sel.gameObject.AddComponent<ModScrollPaneSelector>();
            sps.PaneRect = paneRt;
            sps.MaskRect = maskRt;
            sps.Scrollbar = sb;
        }

        private static MenuButton CreateHorizontalOptionRow(
            Transform parent,
            string label,
            string description,
            string[] options,
            int initialIndex,
            Action<int> onChanged)
        {
            if (_buttonTemplate == null || parent == null) return null;

            GameObject obj = UnityEngine.Object.Instantiate(_buttonTemplate, parent, false);
            obj.name = "HorizOpt_" + label;
            obj.SetActive(true);

            StripButtonVisualArtifacts(obj);

            RectTransform rt = obj.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(1050f, 82f);
            }

            // Left Label (using existing template Text)
            Text labelTxt = obj.GetComponentInChildren<Text>(true);
            if (labelTxt != null)
            {
                labelTxt.text = label;
                labelTxt.font = ModMenuResources.TrajanBold ?? labelTxt.font;
                labelTxt.fontSize = 31;
                labelTxt.alignment = TextAnchor.MiddleLeft;
                labelTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
                labelTxt.verticalOverflow = VerticalWrapMode.Overflow;
                var lrt = labelTxt.rectTransform;
                if (lrt != null)
                {
                    lrt.anchorMin = new Vector2(0f, 0.30f);
                    lrt.anchorMax = new Vector2(0.75f, 1f);
                    lrt.anchoredPosition = new Vector2(24f, 0f);
                    lrt.sizeDelta = Vector2.zero;
                }
            }

            // Subtitle Description underneath (matching "v1.0.0" in manage mods when opened.jpg)
            if (!string.IsNullOrEmpty(description))
            {
                var descGo = new GameObject("Description");
                descGo.transform.SetParent(obj.transform, false);
                var descRt = descGo.AddComponent<RectTransform>();
                descRt.anchorMin = new Vector2(0f, 0f);
                descRt.anchorMax = new Vector2(0.75f, 0.36f);
                descRt.anchoredPosition = new Vector2(44f, 0f);
                descRt.sizeDelta = Vector2.zero;
                descGo.AddComponent<CanvasRenderer>();
                var descTxt = descGo.AddComponent<Text>();
                descTxt.font = ModMenuResources.Perpetua ?? ModMenuResources.TrajanRegular;
                descTxt.fontSize = 22;
                descTxt.alignment = TextAnchor.UpperLeft;
                descTxt.color = new Color(0.82f, 0.80f, 0.78f, 0.92f);
                descTxt.text = description;
                descTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
                descTxt.verticalOverflow = VerticalWrapMode.Overflow;
            }

            // Right Value Text (ON / OFF)
            var valGo = new GameObject("OptionValueText");
            valGo.transform.SetParent(obj.transform, false);
            var valRt = valGo.AddComponent<RectTransform>();
            valRt.anchorMin = new Vector2(0.70f, 0.20f);
            valRt.anchorMax = new Vector2(1f, 0.95f);
            valRt.anchoredPosition = new Vector2(-24f, 0f);
            valRt.sizeDelta = Vector2.zero;
            valGo.AddComponent<CanvasRenderer>();
            var valTxt = valGo.AddComponent<Text>();
            valTxt.font = ModMenuResources.TrajanRegular ?? ModMenuResources.TrajanBold;
            valTxt.fontSize = 31;
            valTxt.alignment = TextAnchor.MiddleRight;
            valTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
            valTxt.verticalOverflow = VerticalWrapMode.Overflow;

            var ctrl = obj.AddComponent<ModHorizontalOptionController>();
            ctrl.Options = options ?? new[] { "OFF", "ON" };
            ctrl.SelectedIndex = Mathf.Clamp(initialIndex, 0, ctrl.Options.Length - 1);
            ctrl.ValueText = valTxt;
            ctrl.OnOptionChanged = onChanged;
            ctrl.RefreshText();

            int lastClickFrame = -1;
            Action stepAction = () =>
            {
                if (Time.frameCount == lastClickFrame) return;
                lastClickFrame = Time.frameCount;
                ctrl.Step(1);
            };

            MenuButton btn = obj.GetComponentInChildren<MenuButton>(true);
            if (btn != null)
            {
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = null;
                btn.flashEffect = null;
                btn.selectHighlight = null;
                btn.leftCursor = null;
                btn.rightCursor = null;
                btn.descriptionText = null;
                btn.cancelAction = CancelAction.DoNothing;
                btn.customGoBack = null;
                btn.hasCancelEventTrigger = false;
                btn.eventTrigger = null;
                if (btn.OnSubmitPressed != null) btn.OnSubmitPressed.RemoveAllListeners();
                btn.OnSubmitPressed = new UnityEvent();
                btn.OnSubmitPressed.AddListener(() => stepAction());
            }

            var rootHitbox = obj.GetComponent<EmptyRaycastGraphic>() ?? obj.AddComponent<EmptyRaycastGraphic>();
            rootHitbox.raycastTarget = true;

            var et = obj.GetComponent<EventTrigger>() ?? obj.AddComponent<EventTrigger>();
            et.triggers = new List<EventTrigger.Entry>();
            var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            clickEntry.callback.AddListener((data) =>
            {
                if (data is PointerEventData ped && ped.dragging) return;
                stepAction();
            });
            et.triggers.Add(clickEntry);

            return btn;
        }

        private static MenuButton CreateRowButton(
            Transform parent,
            string label,
            Action onClick,
            out Text textComponent,
            int fontSize = 30,
            float rowHeight = 56f,
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            textComponent = null;
            if (_buttonTemplate == null || parent == null) return null;

            GameObject obj = UnityEngine.Object.Instantiate(_buttonTemplate, parent, false);
            obj.name = "Row_" + label;
            obj.SetActive(true);

            StripButtonVisualArtifacts(obj);

            int lastClickFrame = -1;
            Action invokeOncePerFrame = () =>
            {
                if (onClick == null) return;
                if (Time.frameCount == lastClickFrame) return;
                lastClickFrame = Time.frameCount;
                onClick();
            };

            MenuButton btn = obj.GetComponentInChildren<MenuButton>(true);
            if (btn != null)
            {
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = null;
                btn.flashEffect = null;
                btn.selectHighlight = null;
                btn.leftCursor = null;
                btn.rightCursor = null;
                btn.descriptionText = null;
                btn.cancelAction = CancelAction.DoNothing;
                btn.customGoBack = null;
                btn.hasCancelEventTrigger = false;
                btn.eventTrigger = null;
                if (btn.OnSubmitPressed != null) btn.OnSubmitPressed.RemoveAllListeners();
                btn.OnSubmitPressed = new UnityEvent();
                if (onClick != null)
                    btn.OnSubmitPressed.AddListener(() => invokeOncePerFrame());
            }

            var rootHitbox = obj.GetComponent<EmptyRaycastGraphic>() ?? obj.AddComponent<EmptyRaycastGraphic>();
            rootHitbox.raycastTarget = true;

            if (onClick != null)
            {
                var et = obj.GetComponent<EventTrigger>() ?? obj.AddComponent<EventTrigger>();
                et.triggers = new List<EventTrigger.Entry>();
                var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                clickEntry.callback.AddListener((data) =>
                {
                    if (data is PointerEventData ped && ped.dragging) return;
                    invokeOncePerFrame();
                });
                et.triggers.Add(clickEntry);
            }

            textComponent = obj.GetComponentInChildren<Text>(true);
            if (textComponent != null)
            {
                textComponent.text = label;
                textComponent.font = ModMenuResources.TrajanBold ?? textComponent.font;
                textComponent.supportRichText = true;
                textComponent.fontSize = fontSize;
                textComponent.alignment = align;
                textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
                textComponent.verticalOverflow = VerticalWrapMode.Overflow;
                textComponent.raycastTarget = true;
                var trt = textComponent.rectTransform;
                if (trt != null)
                {
                    trt.anchorMin = Vector2.zero;
                    trt.anchorMax = Vector2.one;
                    trt.sizeDelta = Vector2.zero;
                    trt.anchoredPosition = align == TextAnchor.MiddleLeft ? new Vector2(24f, 0f) : Vector2.zero;
                }
            }

            RectTransform rt = obj.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(1050f, rowHeight);
            }

            return btn;
        }

        private static MenuButton CreateRowButton(Transform parent, string label, Action onClick, int fontSize = 30, float rowHeight = 56f)
        {
            return CreateRowButton(parent, label, onClick, out _, fontSize, rowHeight, TextAnchor.MiddleCenter);
        }

        private static void SetScreenTitle(MenuScreen screen, string title)
        {
            if (screen == null) return;
            Transform titleTrans = screen.transform.Find("Title");
            if (titleTrans != null)
            {
                foreach (var loc in titleTrans.GetComponentsInChildren<AutoLocalizeTextUI>(true))
                    UnityEngine.Object.DestroyImmediate(loc);
                Text txt = titleTrans.GetComponent<Text>();
                if (txt != null)
                {
                    txt.text = title;
                    txt.font = ModMenuResources.TrajanBold ?? txt.font;
                    txt.fontSize = 44;
                    txt.alignment = TextAnchor.MiddleCenter;
                }
                RectTransform rt = titleTrans.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.1f, 0.88f);
                    rt.anchorMax = new Vector2(0.9f, 0.97f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = Vector2.zero;
                }
            }
        }

        private static void WireScreenButtons(MenuScreen screen, List<Selectable> selectables)
        {
            if (screen == null || selectables == null) return;
            InitReflection();
            MenuButtonList list = screen.GetComponent<MenuButtonList>();
            if (list == null || _entriesField == null || _selectableField == null || _entryType == null) return;

            var validSelectables = selectables.Where(s => s != null).ToList();

            // Set explicit vertical navigation chain (like ChainedNavGraph in HK Modding API)
            for (int i = 0; i < validSelectables.Count; i++)
            {
                var cur = validSelectables[i];
                var prev = validSelectables[(i - 1 + validSelectables.Count) % validSelectables.Count];
                var next = validSelectables[(i + 1) % validSelectables.Count];
                cur.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = prev,
                    selectOnDown = next
                };
            }

            Array newArr = Array.CreateInstance(_entryType, validSelectables.Count);
            for (int i = 0; i < validSelectables.Count; i++)
            {
                object entry = Activator.CreateInstance(_entryType, true);
                _selectableField.SetValue(entry, validSelectables[i]);
                newArr.SetValue(entry, i);
            }

            _entriesField.SetValue(list, newArr);
            if (validSelectables.Count > 0)
            {
                screen.defaultHighlight = validSelectables[0];
            }
            try
            {
                list.ClearLastSelected();
                list.SetupActive();
                if (validSelectables.Count == 0)
                    list.enabled = false;
                else
                    list.enabled = true;
            }
            catch (System.Exception __apiEx_19) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L2890", __apiEx_19); }
        }

        private static void TransitionTo(MenuScreen targetScreen)
        {
            if (_ui == null || targetScreen == null) return;
            _ui.StartCoroutine(TransitionRoutine(targetScreen));
        }

        private static IEnumerator TransitionRoutine(MenuScreen targetScreen)
        {
            if (_ui.gameTitle == null)
            {
                var logo = GameObject.Find("LogoTitle");
                if (logo != null) _ui.gameTitle = logo.GetComponent<SpriteRenderer>();
            }

            if (CurrentCustomScreen != null && CurrentCustomScreen.gameObject.activeInHierarchy)
            {
                yield return _ui.StartCoroutine(_ui.HideMenu(CurrentCustomScreen, true));
            }
            else if (_previousScreen != null && _previousScreen.gameObject.activeInHierarchy)
            {
                yield return _ui.StartCoroutine(_ui.HideMenu(_previousScreen, true));
            }
            else if (_ui.menuState == MainMenuState.MAIN_MENU)
            {
                if (_ui.gameTitle != null)
                {
                    _ui.StartCoroutine(_ui.FadeOutSprite(_ui.gameTitle));
                }
                if (_ui.subtitleFSM != null)
                {
                    _ui.subtitleFSM.SendEvent("FADE OUT");
                }
                if (_ui.mainMenuScreen != null && _ui.mainMenuScreen.gameObject.activeInHierarchy)
                {
                    yield return _ui.StartCoroutine(_ui.FadeOutCanvasGroup(_ui.mainMenuScreen, true, false));
                }
            }

            if (_ui.gameTitle != null)
            {
                _ui.gameTitle.color = new Color(_ui.gameTitle.color.r, _ui.gameTitle.color.g, _ui.gameTitle.color.b, 0f);
            }

            _ui.menuState = (MainMenuState)16978;
            CurrentCustomScreen = targetScreen;
            yield return _ui.StartCoroutine(_ui.ShowMenu(targetScreen));
        }

        public static void CloseModMenu()
        {
            if (_ui == null) return;
            _ui.StartCoroutine(CloseModMenuRoutine());
        }

        public static void CloseToMainMenu()
        {
            CloseModMenu();
        }

        private static IEnumerator CloseModMenuRoutine()
        {
            if (CurrentCustomScreen != null)
            {
                if (CurrentCustomScreen.gameObject.activeInHierarchy)
                {
                    yield return _ui.StartCoroutine(_ui.HideMenu(CurrentCustomScreen, true));
                }
                else
                {
                    CurrentCustomScreen.gameObject.SetActive(false);
                }
                CurrentCustomScreen = null;
            }

            if (_previousScreen != null && _previousScreen.gameObject != null)
            {
                _ui.menuState = _previousMenuState;
                yield return _ui.StartCoroutine(_ui.ShowMenu(_previousScreen));
            }
            else if (_openedFromPauseMenu)
            {
                _ui.menuState = MainMenuState.PAUSE_MENU;
                if (_ui.pauseMenuScreen != null)
                {
                    yield return _ui.StartCoroutine(_ui.ShowMenu(_ui.pauseMenuScreen));
                }
                else
                {
                    _ui.UIGoToPauseMenu();
                }
            }
            else
            {
                if (_ui.gameTitle == null)
                {
                    var logo = GameObject.Find("LogoTitle");
                    if (logo != null) _ui.gameTitle = logo.GetComponent<SpriteRenderer>();
                }
                if (_ui.gameTitle != null)
                {
                    _ui.StartCoroutine(_ui.FadeInSprite(_ui.gameTitle));
                }
                if (_ui.subtitleFSM != null)
                {
                    _ui.subtitleFSM.SendEvent("FADE IN");
                }
                _ui.menuState = MainMenuState.MAIN_MENU;
                yield return _ui.StartCoroutine(_ui.GoToMainMenu());
            }
        }

        public static bool HandleGoBack(UIManager ui)
        {
            if (IsSubModScreen(CurrentCustomScreen))
            {
                OpenMainModMenuScreen();
                return true;
            }
            if (CurrentCustomScreen == _mainModScreen)
            {
                CloseModMenu();
                return true;
            }
            return false;
        }
    }

    // =========================================================================
    // Harmony Hooks: Injects Native Menu & Protects Boss Rush History
    // =========================================================================
    [HarmonyPatch(typeof(UIManager), "Start")]
    internal static class UIManagerStartNativeMenuPatch
    {
        private static void Postfix(UIManager __instance)
        {
            NativeModMenu.Inject(__instance);
        }
    }

    [HarmonyPatch(typeof(UIManager), "UIGoBack")]
    internal static class UIManagerNativeGoBackPatch
    {
        private static bool Prefix(UIManager __instance, ref bool __result)
        {
            if (NativeModMenu.CurrentCustomScreen != null && NativeModMenu.CurrentCustomScreen.gameObject.activeInHierarchy)
            {
                __result = NativeModMenu.HandleGoBack(__instance);
                return false;
            }
            return true;
        }
    }

    // =========================================================================
    // Crash Guard: Fixes Boss Rush Run History & Native Menu Highlight SIGSEGV
    // =========================================================================
    [HarmonyPatch(typeof(MenuScreen), nameof(MenuScreen.HighlightDefault))]
    public static class MenuScreenHighlightDefaultCrashGuard
    {
        public static bool Prefix(MenuScreen __instance)
        {
            try
            {
                if (__instance == null || EventSystem.current == null || EventSystem.current.currentSelectedGameObject != null)
                    return false;

                Selectable highlight = __instance.defaultHighlight;
                if (highlight == null) return false;

                Selectable first = highlight.GetFirstInteractable();
                if (first == null) return false;

                UIManager.HighlightSelectableNoSound(first);

                foreach (Transform item in highlight.transform)
                {
                    if (item == null) continue;
                    if (!item.gameObject.activeInHierarchy)
                    {
                        Animator inactiveAnim = item.GetComponent<Animator>();
                        if (inactiveAnim != null)
                        {
                            UnityEngine.Object.Destroy(inactiveAnim);
                        }
                        continue;
                    }

                    Animator anim = item.GetComponent<Animator>();
                    if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
                    {
                        try
                        {
                            if (anim.HasParameter(MenuScreen._hidePropId)) anim.ResetTrigger(MenuScreen._hidePropId);
                            if (anim.HasParameter(MenuScreen._showPropId)) anim.SetTrigger(MenuScreen._showPropId);
                        }
                        catch (System.Exception __apiEx_20) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L3088", __apiEx_20); }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[HighlightDefaultGuard] Safely handled: {ex.Message}");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(UIManager), "ShowMenu")]
    public static class UIManagerShowMenuCrashGuard
    {
        public static void Prefix(MenuScreen menu)
        {
            if (menu == null) return;
            try
            {
                if (menu.topFleur != null && (menu.topFleur.runtimeAnimatorController == null || !menu.topFleur.isActiveAndEnabled))
                {
                    menu.topFleur = null;
                }
                if (menu.bottomFleur != null && (menu.bottomFleur.runtimeAnimatorController == null || !menu.bottomFleur.isActiveAndEnabled))
                {
                    menu.bottomFleur = null;
                }
            }
            catch (System.Exception __apiEx_21) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("InGameModMenu", "L3118", __apiEx_21); }
        }
    }

    // =========================================================================
    // IRIS Profile System (.jsonc) + 3-Second Hold-to-Download Missing Mods
    // =========================================================================
    public sealed class IrisProfileModEntry
    {
        public string PackageId = "";
        public string Author = "";
        public string PackageName = "";
        public string DisplayName = "";
        public string Version = "1.0.0";
        public bool Enabled = true;
        public List<string> Dependencies = new List<string>();
    }

    public sealed class IrisUpdateBlinkBehavior : MonoBehaviour
    {
        private UnityEngine.UI.Image _img;
        private UnityEngine.UI.Text _txt;
        private string _normalText;
        private string _alertText;
        private Color _normalCol;
        private Color _blinkA;
        private Color _blinkB;

        public void Init(UnityEngine.UI.Image img, UnityEngine.UI.Text txt, string normalText, string alertText, Color normalCol, Color blinkA, Color blinkB)
        {
            _img = img;
            _txt = txt;
            _normalText = normalText;
            _alertText = alertText;
            _normalCol = normalCol;
            _blinkA = blinkA;
            _blinkB = blinkB;
        }

        private void Update()
        {
            if (_img == null) return;
            bool shouldBlink = IrisRemoteUpdater.IsUpdateAvailable && !IrisRemoteUpdater.UpdateAppliedPendingRestart;
            if (shouldBlink)
            {
                float t = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5.5f);
                _img.color = Color.Lerp(_blinkB, _blinkA, t);
                if (_txt != null && !string.IsNullOrEmpty(_alertText) && _txt.text != _alertText)
                    _txt.text = _alertText;
            }
            else
            {
                if (_img.color != _normalCol) _img.color = _normalCol;
                if (_txt != null && !string.IsNullOrEmpty(_normalText) && _txt.text != _normalText)
                    _txt.text = _normalText;
            }
        }
    }

    public static class IrisRemoteUpdater
    {
        public const string CurrentVersionText = "2.9.8.52";
        private const string DefaultManifestUrl = "https://raw.githubusercontent.com/loreMx0/SilksongMobileAPI/main/00_IRIS/plugin.json";

        public static bool IsChecking { get; private set; }
        public static bool IsDownloading { get; private set; }
        public static bool IsUpdateAvailable { get; private set; }
        public static bool UpdateAppliedPendingRestart { get; private set; }
        public static string RemoteVersionText { get; private set; } = "";
        public static string StatusMessage { get; private set; } = $"IRIS v{CurrentVersionText} (Not checked yet)";
        private static bool _startupCheckScheduled;

        public static string GetStatusBannerText()
        {
            if (UpdateAppliedPendingRestart)
                return $"[UPDATED] IRIS v{RemoteVersionText} installed — restart the game to activate!";
            if (IsDownloading)
                return StatusMessage;
            if (IsUpdateAvailable)
                return $"[UPDATE AVAILABLE] IRIS v{CurrentVersionText} -> v{RemoteVersionText} ready to install!";
            if (IsChecking)
                return $"Checking for IRIS updates (current: v{CurrentVersionText})...";
            return StatusMessage;
        }

        public static void ScheduleStartupCheck()
        {
            if (_startupCheckScheduled) return;
            _startupCheckScheduled = true;
            if (SilksongMobileAPIPlugin.Instance != null)
            {
                SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDelayedStartupCheck());
            }
        }

        private static IEnumerator CoDelayedStartupCheck()
        {
            yield return new WaitForSecondsRealtime(3.0f);
            StartCheckForUpdates(null);
        }

        public static void StartCheckForUpdates(Action onDone)
        {
            if (IsChecking || IsDownloading) return;
            if (SilksongMobileAPIPlugin.Instance == null) return;
            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoCheckForUpdates(onDone));
        }

        public static void StartDownloadAndApplyUpdate(Action onDone)
        {
            if (IsDownloading) return;
            if (SilksongMobileAPIPlugin.Instance == null) return;
            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDownloadAndApplyUpdate(onDone));
        }

        private static string GetManifestUrl()
        {
            string cfgUrl = SilksongMobileAPIPlugin.CfgRemoteManifestUrl?.Value;
            return !string.IsNullOrWhiteSpace(cfgUrl) ? cfgUrl.Trim() : DefaultManifestUrl;
        }

        private static string GetDllUrlFromManifest(string manifestUrl)
        {
            int slash = manifestUrl.LastIndexOf('/');
            if (slash > 8)
                return manifestUrl.Substring(0, slash + 1) + "IRIS.dll";
            return "https://raw.githubusercontent.com/loreMx0/SilksongMobileAPI/main/00_IRIS/IRIS.dll";
        }

        private static bool TryParseVersionString(string text, out Version parsed, out string cleanText)
        {
            parsed = null;
            cleanText = "";
            if (string.IsNullOrEmpty(text)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"v?(\d+\.\d+\.\d+(?:\.\d+)?)");
            if (!m.Success) return false;
            cleanText = m.Groups[1].Value;
            return Version.TryParse(cleanText, out parsed);
        }

        private static string ExtractJsonNameField(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"Name\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static IEnumerator CoCheckForUpdates(Action onDone)
        {
            IsChecking = true;
            StatusMessage = "Checking GitHub for IRIS updates...";
            string url = GetManifestUrl() + "?t=" + DateTime.UtcNow.Ticks;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 12;
                req.SetRequestHeader("Cache-Control", "no-cache");
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;

                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null)
                {
                    string json = req.downloadHandler.text ?? "";
                    string nameVal = ExtractJsonNameField(json);
                    if (TryParseVersionString(nameVal, out Version remoteVer, out string remoteClean) &&
                        TryParseVersionString(CurrentVersionText, out Version localVer, out _))
                    {
                        RemoteVersionText = remoteClean;
                        if (remoteVer > localVer)
                        {
                            IsUpdateAvailable = true;
                            StatusMessage = $"IRIS update available: v{CurrentVersionText} -> v{remoteClean}";
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[RemoteUpdater] Update available: v{CurrentVersionText} -> v{remoteClean}.");
                            TryShowMenuUpdateToastIfNeeded();
                        }
                        else
                        {
                            IsUpdateAvailable = false;
                            StatusMessage = $"IRIS v{CurrentVersionText} is up to date (latest: v{remoteClean}).";
                            SilksongMobileAPIPlugin.LogDiagnosticInfo($"[RemoteUpdater] Up to date (local=v{CurrentVersionText}, remote=v{remoteClean}).");
                        }
                    }
                    else
                    {
                        StatusMessage = $"IRIS v{CurrentVersionText} (Could not parse remote version)";
                        SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RemoteUpdater] Could not parse version from manifest Name='{nameVal}'.");
                    }
                }
                else
                {
                    StatusMessage = $"IRIS v{CurrentVersionText} (Offline / check failed)";
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RemoteUpdater] Update check skipped/failed: {req.error}");
                }
            }
            IsChecking = false;
            try { onDone?.Invoke(); } catch { }
        }

        private static string ResolveIrisPluginDir()
        {
            try
            {
                string loc = typeof(SilksongMobileAPIPlugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return dir;
                }
            }
            catch { }
            try
            {
                string fallback = Path.Combine(BepInEx.Paths.PluginPath, "00_IRIS");
                if (Directory.Exists(fallback)) return fallback;
            }
            catch { }
            return null;
        }

        private static GameObject _updatingOverlayGo;
        private static UnityEngine.UI.Text _updatingTitleText;
        private static UnityEngine.UI.Text _updatingDetailText;
        private static UnityEngine.UI.Image _updatingFillImg;
        private static GameObject _updatingCloseBtnGo;

        private static GameObject _menuToastGo;
        private static bool _menuToastShownThisVisit;
        private static bool _menuToastRunning;

        public static void OnSceneLoadedForToast(string sceneName)
        {
            if (string.Equals(sceneName, "Menu_Title", StringComparison.OrdinalIgnoreCase))
            {
                _menuToastShownThisVisit = false;
                TryShowMenuUpdateToastIfNeeded();
            }
            else
            {
                _menuToastShownThisVisit = false;
                DestroyMenuToastImmediate();
            }
        }

        public static void TryShowMenuUpdateToastIfNeeded()
        {
            if (!IsUpdateAvailable || UpdateAppliedPendingRestart || _menuToastShownThisVisit || _menuToastRunning)
                return;
            var activeSc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!activeSc.IsValid() || !string.Equals(activeSc.name, "Menu_Title", StringComparison.OrdinalIgnoreCase))
                return;
            if (SilksongMobileAPIPlugin.Instance == null) return;
            _menuToastShownThisVisit = true;
            SilksongMobileAPIPlugin.Instance.StartCoroutine(CoShowMenuUpdateToast());
        }

        private static void DestroyMenuToastImmediate()
        {
            if (_menuToastGo != null)
            {
                try { UnityEngine.Object.Destroy(_menuToastGo); } catch { }
                _menuToastGo = null;
            }
            _menuToastRunning = false;
        }

        private static IEnumerator CoShowMenuUpdateToast()
        {
            DestroyMenuToastImmediate();
            _menuToastRunning = true;
            try
            {
                _menuToastGo = new GameObject("IRIS_MenuUpdateToastCanvas");
                UnityEngine.Object.DontDestroyOnLoad(_menuToastGo);
                var canvas = _menuToastGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32765;
                var scaler = _menuToastGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var borderGo = new GameObject("ToastBorder", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                borderGo.transform.SetParent(_menuToastGo.transform, false);
                var brt = borderGo.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(0.5f, 1f);
                brt.anchorMax = new Vector2(0.5f, 1f);
                brt.pivot = new Vector2(0.5f, 1f);
                brt.anchoredPosition = new Vector2(0f, -26f);
                brt.sizeDelta = new Vector2(880f, 70f);
                borderGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.96f, 0.64f, 0.14f, 0.98f);

                var bgGo = new GameObject("ToastBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                bgGo.transform.SetParent(borderGo.transform, false);
                var bgRt = bgGo.GetComponent<RectTransform>();
                bgRt.anchorMin = Vector2.zero;
                bgRt.anchorMax = Vector2.one;
                bgRt.offsetMin = new Vector2(3f, 3f);
                bgRt.offsetMax = new Vector2(-3f, -3f);
                bgGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.07f, 0.11f, 0.96f);

                var txtGo = new GameObject("ToastText", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
                txtGo.transform.SetParent(bgGo.transform, false);
                var trt = txtGo.GetComponent<RectTransform>();
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(16f, 4f);
                trt.offsetMax = new Vector2(-16f, -4f);
                var txt = txtGo.GetComponent<UnityEngine.UI.Text>();
                txt.font = HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                txt.fontSize = 24;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = new Color(1f, 0.88f, 0.45f, 1f);
                txt.text = $"IRIS UPDATE AVAILABLE: v{RemoteVersionText}  —  Open Profile to Update";
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("IrisRemoteUpdater", "ShowMenuToast", ex);
                DestroyMenuToastImmediate();
                yield break;
            }

            var myToast = _menuToastGo;
            float elapsed = 0f;
            while (elapsed < 3.0f && _menuToastGo == myToast)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                elapsed += 0.25f;
                var curSc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!curSc.IsValid() || !string.Equals(curSc.name, "Menu_Title", StringComparison.OrdinalIgnoreCase) || UpdateAppliedPendingRestart)
                    break;
            }
            if (_menuToastGo == myToast) DestroyMenuToastImmediate();
        }

        private static void ShowUpdatingOverlay(string title, string detail, float progress, bool showClose, Color? titleColor = null)
        {
            try
            {
                if (_updatingOverlayGo == null)
                {
                    _updatingOverlayGo = new GameObject("IRIS_UpdatingOverlayCanvas");
                    UnityEngine.Object.DontDestroyOnLoad(_updatingOverlayGo);
                    var canvas = _updatingOverlayGo.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 32766;
                    var scaler = _updatingOverlayGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                    scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(1920f, 1080f);
                    scaler.matchWidthOrHeight = 0.5f;
                    _updatingOverlayGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                    var dimGo = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                    dimGo.transform.SetParent(_updatingOverlayGo.transform, false);
                    var drt = dimGo.GetComponent<RectTransform>();
                    drt.anchorMin = Vector2.zero;
                    drt.anchorMax = Vector2.one;
                    drt.offsetMin = Vector2.zero;
                    drt.offsetMax = Vector2.zero;
                    dimGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.03f, 0.03f, 0.05f, 0.94f);

                    var cardGo = new GameObject("Card", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                    cardGo.transform.SetParent(dimGo.transform, false);
                    var crt = cardGo.GetComponent<RectTransform>();
                    crt.anchorMin = new Vector2(0.5f, 0.5f);
                    crt.anchorMax = new Vector2(0.5f, 0.5f);
                    crt.pivot = new Vector2(0.5f, 0.5f);
                    crt.sizeDelta = new Vector2(960f, 360f);
                    cardGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.09f, 0.08f, 0.13f, 0.98f);

                    var font = HarmonyBridge.GetFallbackFont() ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

                    var tGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
                    tGo.transform.SetParent(cardGo.transform, false);
                    var trt = tGo.GetComponent<RectTransform>();
                    trt.anchoredPosition = new Vector2(0f, 105f);
                    trt.sizeDelta = new Vector2(880f, 64f);
                    _updatingTitleText = tGo.GetComponent<UnityEngine.UI.Text>();
                    _updatingTitleText.font = font;
                    _updatingTitleText.fontSize = 36;
                    _updatingTitleText.fontStyle = FontStyle.Bold;
                    _updatingTitleText.alignment = TextAnchor.MiddleCenter;

                    var dGo = new GameObject("Detail", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
                    dGo.transform.SetParent(cardGo.transform, false);
                    var dtrt = dGo.GetComponent<RectTransform>();
                    dtrt.anchoredPosition = new Vector2(0f, 28f);
                    dtrt.sizeDelta = new Vector2(880f, 52f);
                    _updatingDetailText = dGo.GetComponent<UnityEngine.UI.Text>();
                    _updatingDetailText.font = font;
                    _updatingDetailText.fontSize = 22;
                    _updatingDetailText.alignment = TextAnchor.MiddleCenter;
                    _updatingDetailText.color = new Color(0.86f, 0.90f, 0.96f, 1f);

                    var barBgGo = new GameObject("BarBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                    barBgGo.transform.SetParent(cardGo.transform, false);
                    var bbrt = barBgGo.GetComponent<RectTransform>();
                    bbrt.anchoredPosition = new Vector2(0f, -38f);
                    bbrt.sizeDelta = new Vector2(780f, 28f);
                    barBgGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.18f, 0.18f, 0.24f, 1f);

                    var barFillGo = new GameObject("BarFill", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                    barFillGo.transform.SetParent(barBgGo.transform, false);
                    var bfrt = barFillGo.GetComponent<RectTransform>();
                    bfrt.anchorMin = new Vector2(0f, 0f);
                    bfrt.anchorMax = new Vector2(0f, 1f);
                    bfrt.pivot = new Vector2(0f, 0.5f);
                    bfrt.offsetMin = Vector2.zero;
                    bfrt.offsetMax = Vector2.zero;
                    _updatingFillImg = barFillGo.GetComponent<UnityEngine.UI.Image>();
                    _updatingFillImg.color = new Color(0.95f, 0.65f, 0.18f, 1f);

                    _updatingCloseBtnGo = new GameObject("CloseUpdateBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                    _updatingCloseBtnGo.transform.SetParent(cardGo.transform, false);
                    var cbrt = _updatingCloseBtnGo.GetComponent<RectTransform>();
                    cbrt.anchoredPosition = new Vector2(0f, -118f);
                    cbrt.sizeDelta = new Vector2(280f, 54f);
                    _updatingCloseBtnGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.22f, 0.34f, 0.46f, 0.98f);
                    var cBtn = _updatingCloseBtnGo.GetComponent<UnityEngine.UI.Button>();
                    cBtn.onClick.AddListener(HideUpdatingOverlay);
                    var cTxtGo = new GameObject("CloseTxt", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
                    cTxtGo.transform.SetParent(_updatingCloseBtnGo.transform, false);
                    var ctrt = cTxtGo.GetComponent<RectTransform>();
                    ctrt.anchorMin = Vector2.zero;
                    ctrt.anchorMax = Vector2.one;
                    ctrt.offsetMin = Vector2.zero;
                    ctrt.offsetMax = Vector2.zero;
                    var cTxt = cTxtGo.GetComponent<UnityEngine.UI.Text>();
                    cTxt.font = font;
                    cTxt.fontSize = 22;
                    cTxt.fontStyle = FontStyle.Bold;
                    cTxt.alignment = TextAnchor.MiddleCenter;
                    cTxt.color = Color.white;
                    cTxt.text = "Close";
                }

                if (_updatingTitleText != null)
                {
                    _updatingTitleText.text = title;
                    _updatingTitleText.color = titleColor ?? new Color(0.98f, 0.84f, 0.42f, 1f);
                }
                if (_updatingDetailText != null) _updatingDetailText.text = detail;
                if (_updatingFillImg != null)
                {
                    var frt = _updatingFillImg.rectTransform;
                    frt.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
                }
                if (_updatingCloseBtnGo != null) _updatingCloseBtnGo.SetActive(showClose);
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogApiCatch("IrisRemoteUpdater", "ShowUpdatingOverlay", ex);
                if (_updatingCloseBtnGo == null) HideUpdatingOverlay();
            }
        }

        private static void HideUpdatingOverlay()
        {
            if (_updatingOverlayGo != null)
            {
                try { UnityEngine.Object.Destroy(_updatingOverlayGo); } catch { }
                _updatingOverlayGo = null;
                _updatingTitleText = null;
                _updatingDetailText = null;
                _updatingFillImg = null;
                _updatingCloseBtnGo = null;
            }
        }

        private static IEnumerator CoDownloadAndApplyUpdate(Action onDone)
        {
            IsDownloading = true;
            DestroyMenuToastImmediate();
            StatusMessage = $"Downloading IRIS v{RemoteVersionText}...";
            ShowUpdatingOverlay("UPDATING IRIS...", StatusMessage, 0.05f, false);
            string irisDir = ResolveIrisPluginDir();
            if (string.IsNullOrEmpty(irisDir))
            {
                StatusMessage = "Update failed: could not locate 00_IRIS plugin folder.";
                ShowUpdatingOverlay("IRIS UPDATE FAILED", StatusMessage, 0f, true, new Color(1f, 0.45f, 0.45f, 1f));
                IsDownloading = false;
                try { onDone?.Invoke(); } catch { }
                yield break;
            }

            string manifestUrl = GetManifestUrl();
            string dllUrl = GetDllUrlFromManifest(manifestUrl) + "?t=" + DateTime.UtcNow.Ticks;
            byte[] dllBytes = null;
            using (UnityWebRequest req = UnityWebRequest.Get(dllUrl))
            {
                req.timeout = 30;
                req.SetRequestHeader("Cache-Control", "no-cache");
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    int pct = Mathf.Clamp(Mathf.RoundToInt(req.downloadProgress * 100f), 0, 100);
                    StatusMessage = $"Downloading IRIS v{RemoteVersionText}... ({pct}%)";
                    ShowUpdatingOverlay("UPDATING IRIS...", StatusMessage, 0.05f + 0.75f * Mathf.Clamp01(req.downloadProgress), false);
                    yield return null;
                }
                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null)
                {
                    dllBytes = req.downloadHandler.data;
                }
                else
                {
                    StatusMessage = $"Download failed: {req.error}";
                    ShowUpdatingOverlay("IRIS UPDATE FAILED", StatusMessage, 0f, true, new Color(1f, 0.45f, 0.45f, 1f));
                    SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RemoteUpdater] IRIS.dll download failed: {req.error}");
                    IsDownloading = false;
                    try { onDone?.Invoke(); } catch { }
                    yield break;
                }
            }

            if (dllBytes == null || dllBytes.Length < 50000 || dllBytes[0] != 0x4D || dllBytes[1] != 0x5A)
            {
                StatusMessage = $"Update aborted: downloaded IRIS.dll is invalid ({dllBytes?.Length ?? 0} bytes).";
                ShowUpdatingOverlay("IRIS UPDATE FAILED", StatusMessage, 0f, true, new Color(1f, 0.45f, 0.45f, 1f));
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[RemoteUpdater] Invalid IRIS.dll payload ({dllBytes?.Length ?? 0} bytes).");
                IsDownloading = false;
                try { onDone?.Invoke(); } catch { }
                yield break;
            }

            string manifestJson = null;
            using (UnityWebRequest mReq = UnityWebRequest.Get(manifestUrl + "?t=" + DateTime.UtcNow.Ticks))
            {
                mReq.timeout = 12;
                var mop = mReq.SendWebRequest();
                while (!mop.isDone) yield return null;
                if (mReq.result == UnityWebRequest.Result.Success && mReq.downloadHandler != null)
                {
                    string txt = mReq.downloadHandler.text ?? "";
                    if (txt.Contains("\"EntryDLL\"")) manifestJson = txt;
                }
            }

            try
            {
                string dllPath = Path.Combine(irisDir, "IRIS.dll");
                string tmpPath = Path.Combine(irisDir, "IRIS.dll.tmp");
                string bakPath = Path.Combine(irisDir, "IRIS.dll.bak");
                File.WriteAllBytes(tmpPath, dllBytes);
                if (File.Exists(dllPath))
                {
                    try { File.Copy(dllPath, bakPath, true); } catch { }
                    File.Delete(dllPath);
                }
                File.Move(tmpPath, dllPath);
                if (!string.IsNullOrEmpty(manifestJson))
                {
                    try { File.WriteAllText(Path.Combine(irisDir, "plugin.json"), manifestJson); } catch { }
                }
                UpdateAppliedPendingRestart = true;
                IsUpdateAvailable = false;
                StatusMessage = $"Updated to IRIS v{RemoteVersionText} ({dllBytes.Length / 1024} KB)! Restart game to apply.";
                ShowUpdatingOverlay("IRIS UPDATE COMPLETE", StatusMessage, 1f, true, new Color(0.45f, 0.98f, 0.62f, 1f));
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"[RemoteUpdater] Successfully updated {dllPath} to v{RemoteVersionText} ({dllBytes.Length} bytes).");
            }
            catch (Exception ex)
            {
                string dllPathRb = Path.Combine(irisDir, "IRIS.dll");
                string bakPathRb = Path.Combine(irisDir, "IRIS.dll.bak");
                if (!File.Exists(dllPathRb) && File.Exists(bakPathRb))
                {
                    try { File.Copy(bakPathRb, dllPathRb, true); } catch { }
                }
                StatusMessage = $"Install error: {ex.Message}";
                ShowUpdatingOverlay("IRIS UPDATE FAILED", StatusMessage, 0f, true, new Color(1f, 0.45f, 0.45f, 1f));
                SilksongMobileAPIPlugin.LogApiCatch("IrisRemoteUpdater", "ApplyUpdate", ex);
            }

            IsDownloading = false;
            try { onDone?.Invoke(); } catch { }
        }
    }

    public sealed class IrisProfileHoldDownloadBehavior : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public RectTransform FillRect;
        public float MaxWidth = 960f;
        public Text LabelText;
        public int MissingCount;
        public Action OnHoldCompleted;

        private bool _isHolding;
        private bool _triggered;
        private float _holdSeconds;
        private const float RequiredHoldDuration = 3.0f;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (IrisProfileManager.IsDownloadingMissingMods) return;
            _isHolding = true;
            _triggered = false;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _isHolding = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _isHolding = false;
        }

        private void Update()
        {
            if (IrisProfileManager.IsDownloadingMissingMods)
            {
                if (FillRect != null)
                    FillRect.sizeDelta = new Vector2(MaxWidth * Mathf.Clamp01(IrisProfileManager.DownloadOverallProgress), 0f);
                if (LabelText != null)
                    LabelText.text = $"DOWNLOADING MISSING MODS... ({IrisProfileManager.DownloadStatusText})";
                return;
            }

            if (_isHolding && !_triggered)
            {
                _holdSeconds += Time.unscaledDeltaTime;
                float ratio = Mathf.Clamp01(_holdSeconds / RequiredHoldDuration);
                if (FillRect != null)
                    FillRect.sizeDelta = new Vector2(MaxWidth * ratio, 0f);
                if (LabelText != null)
                    LabelText.text = $"HOLDING TO DOWNLOAD {MissingCount} MISSING MOD(S)... ({_holdSeconds:0.0}s / 3.0s — {Mathf.RoundToInt(ratio * 100f)}%)";

                if (_holdSeconds >= RequiredHoldDuration)
                {
                    _triggered = true;
                    _isHolding = false;
                    if (FillRect != null)
                        FillRect.sizeDelta = new Vector2(MaxWidth, 0f);
                    if (LabelText != null)
                        LabelText.text = "STARTING DOWNLOAD OF MISSING MODS & DEPENDENCIES...";
                    try { OnHoldCompleted?.Invoke(); } catch { }
                }
            }
            else if (!_triggered && _holdSeconds > 0f)
            {
                _holdSeconds = Mathf.Max(0f, _holdSeconds - Time.unscaledDeltaTime * 2.5f);
                float ratio = Mathf.Clamp01(_holdSeconds / RequiredHoldDuration);
                if (FillRect != null)
                    FillRect.sizeDelta = new Vector2(MaxWidth * ratio, 0f);
                if (_holdSeconds <= 0.001f && LabelText != null)
                    LabelText.text = $"HOLD FOR 3 SECONDS TO DOWNLOAD {MissingCount} MISSING MOD(S) + DEPENDENCIES";
            }
        }
    }

    public static class IrisProfileManager
    {
        public static string CurrentProfileName = "Pharloom Wanderer";
        public static readonly List<IrisProfileModEntry> ProfileMods = new List<IrisProfileModEntry>();
        public static string LastStatusMessage = "Profile ready.";
        public static bool IsDownloadingMissingMods { get; private set; }
        public static float DownloadOverallProgress { get; private set; }
        public static string DownloadStatusText { get; private set; } = "";

        private static bool _loaded;
        private static float _imguiHoldTimer;

        public static string ProfileFilePath
        {
            get
            {
                string rootDir = Paths.BepInExRootPath;
                if (string.IsNullOrEmpty(rootDir))
                {
                    rootDir = Application.persistentDataPath;
                }
                if (!Directory.Exists(rootDir))
                {
                    try { Directory.CreateDirectory(rootDir); } catch { }
                }
                return Path.Combine(rootDir, "iris_profile.jsonc");
            }
        }

        public static string ConfigMirrorProfileFilePath
        {
            get
            {
                string cfgDir = Paths.ConfigPath;
                if (string.IsNullOrEmpty(cfgDir))
                {
                    cfgDir = Path.Combine(Paths.BepInExRootPath ?? Application.persistentDataPath, "config");
                }
                if (!Directory.Exists(cfgDir))
                {
                    try { Directory.CreateDirectory(cfgDir); } catch { }
                }
                return Path.Combine(cfgDir, "iris_profile.jsonc");
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            if (File.Exists(ProfileFilePath))
            {
                LoadProfileFromFile(ProfileFilePath);
                SaveProfileToFile(ProfileFilePath);
            }
            else if (File.Exists(ConfigMirrorProfileFilePath))
            {
                LoadProfileFromFile(ConfigMirrorProfileFilePath);
                SaveProfileToFile(ProfileFilePath);
            }
            else
            {
                SyncDeviceModsToProfile();
            }
        }

        public static void SetProfileName(string name)
        {
            EnsureLoaded();
            if (!string.IsNullOrWhiteSpace(name))
            {
                CurrentProfileName = name.Trim();
                SaveProfileToFile(ProfileFilePath);
                LastStatusMessage = $"Updated profile name to '{CurrentProfileName}'.";
            }
        }

        public static void SyncDeviceModsToProfile()
        {
            try
            {
                var mgr = InGameModManager.Instance;
                mgr?.ScanInstalledMods();
                ProfileMods.Clear();

                if (mgr != null)
                {
                    foreach (var im in mgr.InstalledMods)
                    {
                        if (im == null || im.IsCore) continue;
                        if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(im.FolderName) ||
                            SilksongMobileAPIPlugin.IsExcludedModIdentifier(im.DisplayName))
                            continue;

                        ParseAuthorAndPackage(im.FolderName, im.DisplayName, out string author, out string pkgName);
                        var deps = ReadManifestDependencies(im.DirectoryPath);

                        ProfileMods.Add(new IrisProfileModEntry
                        {
                            PackageId = !string.IsNullOrEmpty(author) ? $"{author}-{pkgName}" : im.FolderName,
                            Author = author,
                            PackageName = pkgName,
                            DisplayName = im.DisplayName ?? pkgName,
                            Version = im.Version ?? "1.0.0",
                            Enabled = im.IsEnabled,
                            Dependencies = deps
                        });
                    }
                }

                SaveProfileToFile(ProfileFilePath);
                LastStatusMessage = $"Synced {ProfileMods.Count} installed mod(s) to iris_profile.jsonc.";
            }
            catch (Exception ex)
            {
                LastStatusMessage = $"Profile sync error: {ex.Message}";
            }
        }

        private static void ParseAuthorAndPackage(string folderName, string displayName, out string author, out string pkgName)
        {
            author = "";
            pkgName = displayName ?? folderName ?? "UnknownMod";
            if (string.IsNullOrEmpty(folderName)) return;

            var parts = folderName.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && char.IsDigit(parts[parts.Length - 1][0]))
            {
                author = parts[0];
                pkgName = string.Join("-", parts, 1, parts.Length - 2);
            }
            else if (parts.Length == 2)
            {
                if (char.IsDigit(parts[1][0]))
                {
                    pkgName = parts[0];
                }
                else
                {
                    author = parts[0];
                    pkgName = parts[1];
                }
            }
            else
            {
                pkgName = folderName;
            }
        }

        private static List<string> ReadManifestDependencies(string modDir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(modDir)) return list;
            try
            {
                string mf = Path.Combine(modDir, "manifest.json");
                if (File.Exists(mf))
                {
                    var jo = JObject.Parse(File.ReadAllText(mf));
                    var deps = jo["dependencies"] as JArray;
                    if (deps != null)
                    {
                        foreach (var d in deps)
                        {
                            string s = d?.ToString();
                            if (!string.IsNullOrEmpty(s))
                                list.Add(s);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static void SaveProfileToFile(string path)
        {
            try
            {
                var root = new JObject
                {
                    ["profileName"] = CurrentProfileName ?? "Pharloom Wanderer",
                    ["irisVersion"] = "2.9.6",
                    ["updatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };

                var modsArr = new JArray();
                foreach (var pm in ProfileMods)
                {
                    var depsArr = new JArray();
                    if (pm.Dependencies != null)
                    {
                        foreach (var d in pm.Dependencies)
                            depsArr.Add(d);
                    }
                    modsArr.Add(new JObject
                    {
                        ["packageId"] = pm.PackageId ?? "",
                        ["author"] = pm.Author ?? "",
                        ["packageName"] = pm.PackageName ?? "",
                        ["displayName"] = pm.DisplayName ?? "",
                        ["version"] = pm.Version ?? "1.0.0",
                        ["enabled"] = pm.Enabled,
                        ["dependencies"] = depsArr
                    });
                }
                root["mods"] = modsArr;

                var sb = new StringBuilder();
                sb.AppendLine("// =============================================================================");
                sb.AppendLine("// IRIS v2.9.6 Mod Profile Configuration (.jsonc)");
                sb.AppendLine("// Contains user profile name, synced mods, and dependency manifests.");
                sb.AppendLine("// =============================================================================");
                sb.AppendLine(root.ToString(Formatting.Indented));

                string content = sb.ToString();
                File.WriteAllText(path, content, Encoding.UTF8);
                try
                {
                    string rootPath = ProfileFilePath;
                    if (!string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(rootPath, content, Encoding.UTF8);
                }
                catch { }
                try
                {
                    string cfgMirror = ConfigMirrorProfileFilePath;
                    if (!string.Equals(path, cfgMirror, StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(cfgMirror, content, Encoding.UTF8);
                }
                catch { }
                try
                {
                    string dlDir = "/storage/emulated/0/Download";
                    if (Directory.Exists(dlDir))
                        File.WriteAllText(Path.Combine(dlDir, "iris_profile.jsonc"), content, Encoding.UTF8);
                }
                catch { }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[Profile] Failed to save profile to {path}: {ex.Message}");
            }
        }

        public static bool LoadProfileFromFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return false;

                // Strip // comments for .jsonc compatibility
                var cleanLines = new List<string>();
                using (var sr = new StringReader(raw))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        cleanLines.Add(line);
                    }
                }
                string cleanBody = string.Join("\n", cleanLines).Trim();

                ProfileMods.Clear();
                if (cleanBody.StartsWith("{", StringComparison.Ordinal))
                {
                    var root = JObject.Parse(cleanBody);
                    string pName = root.Value<string>("profileName");
                    if (!string.IsNullOrWhiteSpace(pName))
                        CurrentProfileName = pName.Trim();

                    var modsArr = root["mods"] as JArray;
                    if (modsArr != null)
                    {
                        foreach (var tok in modsArr)
                        {
                            if (tok is JObject mo)
                            {
                                string pkgId = mo.Value<string>("packageId") ?? "";
                                string author = mo.Value<string>("author") ?? "";
                                string pkgName = mo.Value<string>("packageName") ?? "";
                                string disp = mo.Value<string>("displayName") ?? pkgName;
                                string ver = mo.Value<string>("version") ?? "1.0.0";
                                bool en = mo["enabled"] != null ? mo.Value<bool>("enabled") : true;

                                if (string.IsNullOrEmpty(pkgName) && !string.IsNullOrEmpty(pkgId))
                                {
                                    ParseAuthorAndPackage(pkgId, disp, out author, out pkgName);
                                }
                                if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(pkgName) ||
                                    SilksongMobileAPIPlugin.IsExcludedModIdentifier(pkgId))
                                    continue;

                                var depList = new List<string>();
                                if (mo["dependencies"] is JArray da)
                                {
                                    foreach (var dt in da)
                                    {
                                        string ds = dt?.ToString();
                                        if (!string.IsNullOrEmpty(ds)) depList.Add(ds);
                                    }
                                }

                                ProfileMods.Add(new IrisProfileModEntry
                                {
                                    PackageId = !string.IsNullOrEmpty(pkgId) ? pkgId : (!string.IsNullOrEmpty(author) ? $"{author}-{pkgName}" : pkgName),
                                    Author = author,
                                    PackageName = pkgName,
                                    DisplayName = !string.IsNullOrEmpty(disp) ? disp : pkgName,
                                    Version = ver,
                                    Enabled = en,
                                    Dependencies = depList
                                });
                            }
                        }
                    }
                }
                else
                {
                    // Fallback plain .txt / .cfg line parser (Name=... and one mod per line)
                    foreach (var l in cleanLines)
                    {
                        string t = l.Trim();
                        if (string.IsNullOrEmpty(t)) continue;
                        if (t.StartsWith("ProfileName=", StringComparison.OrdinalIgnoreCase) ||
                            t.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = t.IndexOf('=');
                            if (eq >= 0 && eq + 1 < t.Length)
                                CurrentProfileName = t.Substring(eq + 1).Trim();
                            continue;
                        }
                        ParseAuthorAndPackage(t, t, out string auth, out string pkg);
                        if (SilksongMobileAPIPlugin.IsExcludedModIdentifier(pkg)) continue;
                        ProfileMods.Add(new IrisProfileModEntry
                        {
                            PackageId = t,
                            Author = auth,
                            PackageName = pkg,
                            DisplayName = pkg.Replace('_', ' '),
                            Version = "1.0.0",
                            Enabled = true
                        });
                    }
                }

                _loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                LastStatusMessage = $"Failed to parse profile: {ex.Message}";
                return false;
            }
        }

        public static bool MatchesInstalledMod(IrisProfileModEntry pm, InstalledModItem im)
        {
            if (pm == null || im == null) return false;
            string pName = (pm.PackageName ?? "").Replace(" ", "").Replace("_", "");
            string pDisp = (pm.DisplayName ?? "").Replace(" ", "").Replace("_", "");
            string iFold = (im.FolderName ?? "").Replace(" ", "").Replace("_", "");
            string iDisp = (im.DisplayName ?? "").Replace(" ", "").Replace("_", "");

            if (!string.IsNullOrEmpty(pName) &&
                (iFold.IndexOf(pName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 iDisp.IndexOf(pName, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            if (!string.IsNullOrEmpty(pDisp) &&
                (iFold.IndexOf(pDisp, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 iDisp.IndexOf(pDisp, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        public static void ComputeSyncState(
            out bool isSynced,
            out List<IrisProfileModEntry> missingFromDevice,
            out List<InstalledModItem> extraOnDevice,
            out List<string> unifiedRows)
        {
            EnsureLoaded();
            missingFromDevice = new List<IrisProfileModEntry>();
            extraOnDevice = new List<InstalledModItem>();
            unifiedRows = new List<string>();

            var mgr = InGameModManager.Instance;
            mgr?.ScanInstalledMods();
            var installed = mgr != null
                ? mgr.InstalledMods.Where(m => m != null && !m.IsCore &&
                    !SilksongMobileAPIPlugin.IsExcludedModIdentifier(m.FolderName) &&
                    !SilksongMobileAPIPlugin.IsExcludedModIdentifier(m.DisplayName)).ToList()
                : new List<InstalledModItem>();

            var matchedInstalled = new HashSet<InstalledModItem>();

            foreach (var pm in ProfileMods)
            {
                var match = installed.FirstOrDefault(im => MatchesInstalledMod(pm, im));
                if (match != null)
                {
                    matchedInstalled.Add(match);
                    unifiedRows.Add($"[SYNCED]  {pm.DisplayName} (v{match.Version}) — Installed & Matched");
                }
                else
                {
                    missingFromDevice.Add(pm);
                    unifiedRows.Add($"[MISSING ON DEVICE]  {pm.DisplayName} (v{pm.Version}) — Listed in profile, not installed");
                }
            }

            foreach (var im in installed)
            {
                if (!matchedInstalled.Contains(im))
                {
                    extraOnDevice.Add(im);
                    unifiedRows.Add($"[EXTRA ON DEVICE]  {im.DisplayName} (v{im.Version}) — Installed on phone, not in profile");
                }
            }

            isSynced = (missingFromDevice.Count == 0 && extraOnDevice.Count == 0);
        }

        public static void ExportProfileWithFileManager(Action<string> onDone = null)
        {
            try
            {
                EnsureLoaded();
                SaveProfileToFile(ProfileFilePath);

                // Also copy a convenience backup to /storage/emulated/0/Download/iris_profile.jsonc if accessible
                try
                {
                    string dlDir = "/storage/emulated/0/Download";
                    if (Directory.Exists(dlDir))
                    {
                        File.Copy(ProfileFilePath, Path.Combine(dlDir, "iris_profile.jsonc"), true);
                    }
                }
                catch { }

                // Open native Android File Manager via NativeFilePicker.ExportFile or BepInExLoader.ExportSaveData
                var pickerType = AccessTools.TypeByName("NativeFilePicker");
                if (pickerType != null)
                {
                    var exportMi = pickerType.GetMethod("ExportFile", BindingFlags.Static | BindingFlags.Public);
                    var cbType = pickerType.GetNestedType("FilesExportedCallback", BindingFlags.Public);
                    if (exportMi != null && cbType != null)
                    {
                        Action<bool> handler = ok =>
                        {
                            LastStatusMessage = ok
                                ? $"Exported '{ Path.GetFileName(ProfileFilePath) }' via File Manager!"
                                : $"Saved profile to {ProfileFilePath}";
                            onDone?.Invoke(LastStatusMessage);
                        };
                        var del = Delegate.CreateDelegate(cbType, handler.Target, handler.Method, false);
                        exportMi.Invoke(null, new object[] { ProfileFilePath, del });
                        return;
                    }
                }

                var loaderType = AccessTools.TypeByName("BepInEx.BepInExLoader");
                var exportSaveMi = loaderType?.GetMethod("ExportSaveData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (exportSaveMi != null)
                {
                    exportSaveMi.Invoke(null, new object[] { ProfileFilePath });
                    LastStatusMessage = $"Opened File Manager to export {Path.GetFileName(ProfileFilePath)}.";
                    onDone?.Invoke(LastStatusMessage);
                    return;
                }

                LastStatusMessage = $"Saved profile to {ProfileFilePath}.";
                onDone?.Invoke(LastStatusMessage);
            }
            catch (Exception ex)
            {
                LastStatusMessage = $"Export notice: {ex.Message} (Saved at {ProfileFilePath})";
                onDone?.Invoke(LastStatusMessage);
            }
        }

        public static void ImportProfileWithFileManager(Action<string> onDone = null)
        {
            try
            {
                var pickerType = AccessTools.TypeByName("NativeFilePicker");
                if (pickerType != null)
                {
                    var pickMi = pickerType.GetMethod("PickFile", BindingFlags.Static | BindingFlags.Public);
                    var cbType = pickerType.GetNestedType("FilePickedCallback", BindingFlags.Public);
                    if (pickMi != null && cbType != null)
                    {
                        Action<string> handler = pickedPath =>
                        {
                            if (string.IsNullOrEmpty(pickedPath) || !File.Exists(pickedPath))
                            {
                                LastStatusMessage = "Profile import cancelled.";
                                onDone?.Invoke(LastStatusMessage);
                                return;
                            }
                            if (LoadProfileFromFile(pickedPath))
                            {
                                SaveProfileToFile(ProfileFilePath);
                                LastStatusMessage = $"Imported profile '{CurrentProfileName}' ({ProfileMods.Count} mods) overwriting active profile!";
                            }
                            onDone?.Invoke(LastStatusMessage);
                        };
                        var del = Delegate.CreateDelegate(cbType, handler.Target, handler.Method, false);
                        pickMi.Invoke(null, new object[] { del, new string[] { "*/*" } });
                        return;
                    }
                }

                // Fallback: Check /storage/emulated/0/Download/iris_profile.jsonc
                string dlCandidate = "/storage/emulated/0/Download/iris_profile.jsonc";
                if (File.Exists(dlCandidate) && LoadProfileFromFile(dlCandidate))
                {
                    SaveProfileToFile(ProfileFilePath);
                    LastStatusMessage = $"Imported profile '{CurrentProfileName}' from Download/iris_profile.jsonc!";
                    onDone?.Invoke(LastStatusMessage);
                    return;
                }

                LastStatusMessage = "Could not open native file picker; place iris_profile.jsonc in Download or BepInEx/config/.";
                onDone?.Invoke(LastStatusMessage);
            }
            catch (Exception ex)
            {
                LastStatusMessage = $"Import error: {ex.Message}";
                onDone?.Invoke(LastStatusMessage);
            }
        }

        public static void StartDownloadMissingMods(Action onCompleted = null)
        {
            if (IsDownloadingMissingMods) return;
            if (SilksongMobileAPIPlugin.Instance != null)
            {
                SilksongMobileAPIPlugin.Instance.StartCoroutine(CoDownloadMissingModsRoutine(onCompleted));
            }
        }

        private static IEnumerator CoDownloadMissingModsRoutine(Action onCompleted)
        {
            IsDownloadingMissingMods = true;
            DownloadOverallProgress = 0.05f;
            DownloadStatusText = "Loading Thunderstore catalog...";
            LastStatusMessage = DownloadStatusText;

            var mgr = InGameModManager.Instance;
            if (mgr == null)
            {
                IsDownloadingMissingMods = false;
                yield break;
            }

            if (mgr.OnlineCatalog.Count <= 10)
            {
                yield return mgr.StartCoroutine(mgr.FetchThunderstoreCatalogRoutine());
            }

            ComputeSyncState(out _, out var missingList, out _, out _);
            if (missingList.Count == 0)
            {
                IsDownloadingMissingMods = false;
                DownloadOverallProgress = 1f;
                LastStatusMessage = "All profile mods are already installed!";
                onCompleted?.Invoke();
                yield break;
            }

            int total = missingList.Count;
            int succeeded = 0;

            for (int i = 0; i < total; i++)
            {
                var missing = missingList[i];
                DownloadStatusText = $"{i + 1}/{total}: {missing.DisplayName}";
                DownloadOverallProgress = (float)i / total;

                // Match in OnlineCatalog by PackageName / Author / DisplayName
                var catalogMatch = mgr.OnlineCatalog.FirstOrDefault(c =>
                    c.IsCompatible &&
                    (string.Equals(c.PackageName, missing.PackageName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.FullName, missing.PackageId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.DisplayTitle, missing.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                     c.PackageName.IndexOf(missing.PackageName, StringComparison.OrdinalIgnoreCase) >= 0));

                if (catalogMatch == null && !string.IsNullOrEmpty(missing.Author) && !string.IsNullOrEmpty(missing.PackageName))
                {
                    // Construct direct catalog item from profile entry metadata + dependencies so it downloads even if catalog search missed
                    catalogMatch = new OnlineModCatalogItem
                    {
                        Author = missing.Author,
                        PackageName = missing.PackageName,
                        FullName = $"{missing.Author}-{missing.PackageName}",
                        DisplayTitle = missing.DisplayName ?? missing.PackageName,
                        Version = !string.IsNullOrEmpty(missing.Version) ? missing.Version : "1.0.0",
                        DownloadUrl = $"https://thunderstore.io/package/download/{missing.Author}/{missing.PackageName}/{missing.Version}/",
                        IsCompatible = true,
                        Dependencies = missing.Dependencies ?? new List<string>()
                    };
                }

                if (catalogMatch != null)
                {
                    bool finishedOne = false;
                    bool okOne = false;
                    mgr.InstallOnlineMod(
                        catalogMatch,
                        onProgress: (p, st) =>
                        {
                            DownloadOverallProgress = ((float)i + Mathf.Clamp01(p)) / total;
                            DownloadStatusText = $"{i + 1}/{total}: {missing.DisplayName} ({st} {(int)(p * 100)}%)";
                        },
                        onComplete: (ok, err) =>
                        {
                            okOne = ok;
                            finishedOne = true;
                        });

                    while (!finishedOne)
                        yield return null;

                    if (okOne) succeeded++;
                }
            }

            mgr.ScanInstalledMods();
            IsDownloadingMissingMods = false;
            DownloadOverallProgress = 1f;
            DownloadStatusText = "Complete!";
            LastStatusMessage = $"Downloaded & installed {succeeded}/{total} missing profile mod(s) + dependencies!";
            onCompleted?.Invoke();
        }

        public static void DrawIMGUIProfileTab()
        {
            EnsureLoaded();
            ComputeSyncState(out bool isSynced, out var missingFromDevice, out var extraOnDevice, out var unifiedRows);

            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Profile Name:</b>", GUILayout.Width(120f));
            string editedName = GUILayout.TextField(CurrentProfileName ?? "Pharloom Wanderer", GUILayout.Width(320f), GUILayout.Height(32f));
            if (!string.Equals(editedName, CurrentProfileName, StringComparison.Ordinal))
            {
                CurrentProfileName = editedName;
            }
            if (GUILayout.Button("Save Name", GUILayout.Width(110f), GUILayout.Height(32f)))
            {
                SetProfileName(CurrentProfileName);
            }
            if (GUILayout.Button("Sync Device -> Profile", GUILayout.Width(190f), GUILayout.Height(32f)))
            {
                SyncDeviceModsToProfile();
            }
            if (GUILayout.Button("Export Profile", GUILayout.Width(135f), GUILayout.Height(32f)))
            {
                ExportProfileWithFileManager();
            }
            if (GUILayout.Button("Import Profile", GUILayout.Width(135f), GUILayout.Height(32f)))
            {
                ImportProfileWithFileManager();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal("box");
            GUILayout.Label($"<b>{IrisRemoteUpdater.GetStatusBannerText()}</b>");
            Color _prevUpdCol = GUI.color;
            if (IrisRemoteUpdater.IsUpdateAvailable && !IrisRemoteUpdater.UpdateAppliedPendingRestart)
                GUI.color = (Mathf.Sin(Time.unscaledTime * 6f) > 0f) ? new Color(1f, 0.78f, 0.25f, 1f) : new Color(0.75f, 0.45f, 0.15f, 1f);
            string _imUpdBtn = IrisRemoteUpdater.IsDownloading
                ? "Updating..."
                : (IrisRemoteUpdater.UpdateAppliedPendingRestart
                    ? "Restart Game"
                    : (IrisRemoteUpdater.IsUpdateAvailable
                        ? $"Update -> v{IrisRemoteUpdater.RemoteVersionText}"
                        : (IrisRemoteUpdater.IsChecking ? "Checking..." : "Check API Update")));
            if (GUILayout.Button(_imUpdBtn, GUILayout.Width(210f), GUILayout.Height(30f)))
            {
                if (!IrisRemoteUpdater.IsDownloading && !IrisRemoteUpdater.IsChecking && !IrisRemoteUpdater.UpdateAppliedPendingRestart)
                {
                    if (IrisRemoteUpdater.IsUpdateAvailable)
                        IrisRemoteUpdater.StartDownloadAndApplyUpdate(null);
                    else
                        IrisRemoteUpdater.StartCheckForUpdates(null);
                }
            }
            GUI.color = _prevUpdCol;
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label($"<size=12><color=#99ccff><b>Stored at:</b> {ProfileFilePath}  (Mirror: {ConfigMirrorProfileFilePath})</color></size>");
            GUILayout.Space(4f);
            string syncBanner = isSynced
                ? $"<color=#55ff88><b>[SYNCED]</b> Mods on device match profile '{CurrentProfileName}' ({ProfileMods.Count} mods).</color>"
                : $"<color=#ffaa44><b>[NOT SYNCED]</b> Mods not synced with profile (Missing: {missingFromDevice.Count} | Extra on phone: {extraOnDevice.Count}).</color>";
            GUILayout.Label(syncBanner);

            if (!string.IsNullOrEmpty(LastStatusMessage))
            {
                GUILayout.Label($"<color=#88ddff>{LastStatusMessage}</color>");
            }

            if (missingFromDevice.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label($"<color=#ffdd66><b>Missing {missingFromDevice.Count} mods from the profile — do you want to download them?</b></color>");
                float ratio = Mathf.Clamp01(_imguiHoldTimer / 3.0f);
                string holdLbl = IsDownloadingMissingMods
                    ? $"Downloading Missing Mods... ({DownloadStatusText})"
                    : (_imguiHoldTimer > 0f
                        ? $"HOLDING... {_imguiHoldTimer:0.0}s / 3.0s [{new string('█', Mathf.RoundToInt(ratio * 20)).PadRight(20, '░')}] ({Mathf.RoundToInt(ratio * 100f)}%)"
                        : $"HOLD FOR 3 SECONDS TO DOWNLOAD {missingFromDevice.Count} MISSING MOD(S) + DEPENDENCIES");

                bool held = GUILayout.RepeatButton(holdLbl, GUILayout.Height(42f));
                if (held && !IsDownloadingMissingMods)
                {
                    _imguiHoldTimer += Time.unscaledDeltaTime;
                    if (_imguiHoldTimer >= 3.0f)
                    {
                        _imguiHoldTimer = 0f;
                        StartDownloadMissingMods();
                    }
                }
                else if (Event.current.type == EventType.Repaint && _imguiHoldTimer > 0f)
                {
                    _imguiHoldTimer = Mathf.Max(0f, _imguiHoldTimer - Time.unscaledDeltaTime * 2.0f);
                }
            }
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            foreach (var row in unifiedRows)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label(row);
                GUILayout.EndVertical();
            }
        }
    }
}
