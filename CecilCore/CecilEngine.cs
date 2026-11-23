using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SilksongMobileAPI.CecilCore
{
    public static class CecilEngine
    {
        private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        private static MethodInfo _logInfoMethod;
        private static MethodInfo _logWarnMethod;
        private static MethodInfo _registerResourcesMethod;
        private static MethodInfo _registerPatchInfoMethod;
        private static Type _extractedInfoType;
        private static Type _harmonyBridgeType;
        private static Type _adapterBuilderType;
        private static Type _assetHelperBridgeType;
        private static Type _mobileVirtualInputType;
        private static Assembly _mainApiAssembly;
        private static bool _boundMainApi;
        private static int _nextFieldRefId = 1;

        private static readonly Dictionary<string, string> _runtimeRenamedAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void EnsureBoundMainApi()
        {
            if (_boundMainApi && _harmonyBridgeType != null && _mainApiAssembly != null) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "IRIS" || asm.GetName().Name == "SilksongMobileAPI")
                {
                    _mainApiAssembly = asm;
                    var pluginType = asm.GetType("SilksongMobileAPI.SilksongMobileAPIPlugin", false);
                    _harmonyBridgeType = asm.GetType("SilksongMobileAPI.HarmonyBridge", false);
                    _adapterBuilderType = asm.GetType("SilksongMobileAPI.RuntimeHybridCLRAdapterBuilder", false);
                    _assetHelperBridgeType = asm.GetType("SilksongMobileAPI.AssetHelperBridge", false);
                    _extractedInfoType = asm.GetType("SilksongMobileAPI.ExtractedHarmonyPatchInfo", false);
                    _mobileVirtualInputType = asm.GetType("BepInEx.MobileVirtualInput", false);

                    if (pluginType != null)
                    {
                        _logInfoMethod = pluginType.GetMethod("LogDiagnosticInfo", BindingFlags.Static | BindingFlags.Public);
                        _logWarnMethod = pluginType.GetMethod("LogDiagnosticWarn", BindingFlags.Static | BindingFlags.Public);
                    }
                    if (_harmonyBridgeType != null)
                    {
                        _registerResourcesMethod = _harmonyBridgeType.GetMethod("RegisterEmbeddedResources", BindingFlags.Static | BindingFlags.Public);
                        _registerPatchInfoMethod = _harmonyBridgeType.GetMethod("RegisterExtractedPatchInfo", BindingFlags.Static | BindingFlags.Public);
                    }
                    _boundMainApi = true;
                    break;
                }
            }
        }

        private static void LogInfo(string msg)
        {
            EnsureBoundMainApi();
            _logInfoMethod?.Invoke(null, new object[] { msg });
        }

        private static void LogWarn(string msg)
        {
            EnsureBoundMainApi();
            _logWarnMethod?.Invoke(null, new object[] { msg });
        }

        private static bool IsAssemblyNameAlreadyInAppDomain(string asmShortName)
        {
            if (string.IsNullOrEmpty(asmShortName)) return false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(asm.GetName().Name, asmShortName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static ulong ComputeFnv1a64(byte[] data)
        {
            ulong hash = 14695981039346656037UL;
            if (data == null) return hash;
            int step = data.Length > 262144 ? 4 : 1;
            for (int i = 0; i < data.Length; i += step)
            {
                hash ^= data[i];
                hash *= 1099511628211UL;
            }
            hash ^= (ulong)data.Length;
            hash *= 1099511628211UL;
            return hash;
        }

        private static bool TryLoadFromCecilDiskCache(
            string cachePrefix,
            string shortModName,
            bool alreadyInAppDomain,
            out byte[] cachedRewrittenBytes)
        {
            cachedRewrittenBytes = null;
            try
            {
                string mainPath = cachePrefix + ".rewritten.bin";
                if (!File.Exists(mainPath)) return false;

                string shimPath = cachePrefix + ".shim.bin";
                if (File.Exists(shimPath))
                {
                    byte[] shimBytes = File.ReadAllBytes(shimPath);
                    if (shimBytes != null && shimBytes.Length > 64)
                    {
                        Assembly.Load(shimBytes);
                    }
                }

                string resPath = cachePrefix + ".res.bin";
                if (File.Exists(resPath) && _registerResourcesMethod != null)
                {
                    using (var fs = new FileStream(resPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var br = new BinaryReader(fs, Encoding.UTF8))
                    {
                        string resModName = br.ReadString();
                        int count = br.ReadInt32();
                        var dict = new Dictionary<string, byte[]>(count, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < count; i++)
                        {
                            string rKey = br.ReadString();
                            int rLen = br.ReadInt32();
                            byte[] rData = br.ReadBytes(rLen);
                            dict[rKey] = rData;
                        }
                        if (dict.Count > 0)
                        {
                            _registerResourcesMethod.Invoke(null, new object[] { resModName, dict });
                        }
                    }
                }

                if (alreadyInAppDomain && !string.IsNullOrEmpty(shortModName))
                {
                    lock (_runtimeRenamedAssemblies)
                    {
                        _runtimeRenamedAssemblies[shortModName] = shortModName + "_MobileRt";
                    }
                }

                cachedRewrittenBytes = File.ReadAllBytes(mainPath);
                if (cachedRewrittenBytes != null && cachedRewrittenBytes.Length > 64)
                {
                    LogInfo($"[PCMod:{shortModName}] Loaded Cecil-rewritten assembly from disk cache ({cachedRewrittenBytes.Length} bytes).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogWarn($"[PCMod:{shortModName}] Disk cache read notice (falling back to live Cecil rewrite): {ex.Message}");
            }
            cachedRewrittenBytes = null;
            return false;
        }

        private static void SaveToCecilDiskCache(
            string cacheDir,
            string cachePrefix,
            string modName,
            byte[] rewrittenBytes,
            byte[] shimBytes,
            Dictionary<string, byte[]> extractedResources)
        {
            if (string.IsNullOrEmpty(cacheDir) || string.IsNullOrEmpty(cachePrefix) || rewrittenBytes == null)
                return;
            try
            {
                if (!Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                if (shimBytes != null && shimBytes.Length > 0)
                {
                    File.WriteAllBytes(cachePrefix + ".shim.bin", shimBytes);
                }

                if (extractedResources != null && extractedResources.Count > 0)
                {
                    using (var fs = new FileStream(cachePrefix + ".res.bin", FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                    {
                        bw.Write(modName ?? "");
                        bw.Write(extractedResources.Count);
                        foreach (var kv in extractedResources)
                        {
                            bw.Write(kv.Key ?? "");
                            byte[] val = kv.Value ?? Array.Empty<byte>();
                            bw.Write(val.Length);
                            bw.Write(val);
                        }
                    }
                }

                File.WriteAllBytes(cachePrefix + ".rewritten.bin", rewrittenBytes);
            }
            catch (Exception ex)
            {
                LogWarn($"[PCMod:{modName}] Disk cache write notice: {ex.Message}");
            }
        }

        public static byte[] RewritePCModAssembly(byte[] rawBytes, string dllPath)
        {
            EnsureBoundMainApi();
            if (rawBytes == null || rawBytes.Length == 0) return rawBytes;

            string shortFileName = !string.IsNullOrEmpty(dllPath) ? Path.GetFileNameWithoutExtension(dllPath) : "UnknownMod";
            bool preAlreadyLoaded = IsAssemblyNameAlreadyInAppDomain(shortFileName);
            string cacheDir = null;
            string cachePrefix = null;
            if (!string.IsNullOrEmpty(dllPath))
            {
                try
                {
                    string parentDir = Path.GetDirectoryName(dllPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        try
                        {
                            foreach (var oldName in new[] { ".cecil_cache_v2428", ".cecil_cache_v2429", ".cecil_cache_v2430", ".cecil_cache_v2432", ".cecil_cache_v250", ".cecil_cache_v251", ".cecil_cache_v251a", ".cecil_cache_v252", ".cecil_cache_v255", ".cecil_cache_v257", ".cecil_cache_v258", ".cecil_cache_v259", ".cecil_cache_v260", ".cecil_cache_v261", ".cecil_cache_v262", ".cecil_cache_v263", ".cecil_cache_v264", ".cecil_cache_v265", ".cecil_cache_v266", ".cecil_cache_v267", ".cecil_cache_v268", ".cecil_cache_v269", ".cecil_cache_v275", ".cecil_cache_v280", ".cecil_cache_v281", ".cecil_cache_v282", ".cecil_cache_v283", ".cecil_cache_v284", ".cecil_cache_v285", ".cecil_cache_v286", ".cecil_cache_v287", ".cecil_cache_v289", ".cecil_cache_v290", ".cecil_cache_v292", ".cecil_cache_v293", ".cecil_cache_v294", ".cecil_cache_v295", ".cecil_cache_v296", ".cecil_cache_v2961", ".cecil_cache_v2962", ".cecil_cache_v2963", ".cecil_cache_v2964", ".cecil_cache_v2965", ".cecil_cache_v2966", ".cecil_cache_v2967", ".cecil_cache_v2968", ".cecil_cache_v29614", ".cecil_cache_v29615", ".cecil_cache_v29618", ".cecil_cache_v29620", ".cecil_cache_v29625", ".cecil_cache_v29626", ".cecil_cache_v2983", ".cecil_cache_v2984", ".cecil_cache_v2986" })
                            {
                                if (oldName == ".cecil_cache_v29839") continue;
                                string oldP = Path.Combine(parentDir, oldName);
                                if (Directory.Exists(oldP)) Directory.Delete(oldP, true);
                            }
                        }
                        catch { }
                        cacheDir = Path.Combine(parentDir, ".cecil_cache_v29839");
                        ulong fnv = ComputeFnv1a64(rawBytes);
                        string modeTag = preAlreadyLoaded ? "rt" : "std";
                        cachePrefix = Path.Combine(cacheDir, $"{shortFileName}_{rawBytes.Length}_{fnv:X16}_{modeTag}");
                        if (TryLoadFromCecilDiskCache(cachePrefix, shortFileName, preAlreadyLoaded, out byte[] cachedBytes))
                        {
                            return cachedBytes;
                        }
                    }
                }
                catch { }
            }

            using (var msIn = new MemoryStream(rawBytes, writable: false))
            {
                var readerParams = new ReaderParameters { ReadWrite = false, InMemory = true, ReadingMode = ReadingMode.Deferred };
                var asmDef = AssemblyDefinition.ReadAssembly(msIn, readerParams);
                var mod = asmDef.MainModule;
                string origModName = asmDef.Name.Name ?? shortFileName;

                if (IsAssemblyNameAlreadyInAppDomain(origModName))
                {
                    string renamed = origModName + "_MobileRt";
                    asmDef.Name.Name = renamed;
                    mod.Name = renamed + ".dll";
                    lock (_runtimeRenamedAssemblies)
                    {
                        _runtimeRenamedAssemblies[origModName] = renamed;
                    }
                    LogInfo($"[PCMod:{origModName}] Bypassing HybridCLR Phase-1 cache by naming rewritten in-memory assembly '{renamed}'.");
                }

                string modName = asmDef.Name.Name ?? origModName;

                LogInfo($"==================== [Inspecting PC Mod: {origModName} v{asmDef.Name.Version}] ====================");

                // 1. Extract Embedded Resources so Assembly.GetManifestResourceStream works on Android HybridCLR
                Dictionary<string, byte[]> extractedResources = ExtractAndRegisterEmbeddedResources(modName, mod);

                LogInfo($"[PCMod:{origModName}] Initial AssemblyRefs: [{string.Join(", ", mod.AssemblyReferences.Select(r => $"{r.Name} ({r.Version})"))}]");

                // 2. Redirect AssemblyReferences to match Mobile runtime versions
                foreach (var aref in mod.AssemblyReferences.ToArray())
                {
                    string oldName = aref.Name ?? "";
                    string oldVer = aref.Version?.ToString() ?? "";

                    lock (_runtimeRenamedAssemblies)
                    {
                        if (_runtimeRenamedAssemblies.TryGetValue(oldName, out var renamedDep))
                        {
                            aref.Name = renamedDep;
                            LogInfo($"[PCMod:{origModName}] Redirected dependency '{oldName}' -> '{renamedDep}'");
                            continue;
                        }
                    }

                    if (oldName.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        oldName.Equals("0Harmony20", StringComparison.OrdinalIgnoreCase) ||
                        oldName.Equals("HarmonyX", StringComparison.OrdinalIgnoreCase) ||
                        oldName.Equals("BepInEx.Harmony", StringComparison.OrdinalIgnoreCase) ||
                        oldName.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                        oldName.Equals("BepInEx.Core", StringComparison.OrdinalIgnoreCase))
                    {
                        aref.Name = "BepInEx";
                        aref.Version = new Version(0, 0, 0, 0);
                        aref.PublicKeyToken = Array.Empty<byte>();
                        LogInfo($"[PCMod:{origModName}] Redirected assembly ref '{oldName} ({oldVer})' -> 'BepInEx (0.0.0.0)'");
                    }
                    else if (oldName.Equals("Silksong.AssetHelper", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("AssetHelper", StringComparison.OrdinalIgnoreCase))
                    {
                        aref.Name = "AssetHelper";
                        aref.Version = new Version(1, 3, 2, 1);
                        aref.PublicKeyToken = Array.Empty<byte>();
                    }
                    else if (oldName.Equals("Silksong.FsmUtil", StringComparison.OrdinalIgnoreCase))
                    {
                        aref.Name = "IRIS";
                        aref.Version = new Version(1, 0, 0, 0);
                        aref.PublicKeyToken = Array.Empty<byte>();
                        LogInfo($"[PCMod:{origModName}] Redirected assembly ref 'Silksong.FsmUtil ({oldVer})' -> 'IRIS (1.0.0.0)'");
                    }
                    else if (oldName.Equals("Silksong.ModMenu", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("MonoMod.Utils", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("MonoMod.RuntimeDetour", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("MonoDetour", StringComparison.OrdinalIgnoreCase) ||
                             oldName.StartsWith("0.com.github.MonoDetour", StringComparison.OrdinalIgnoreCase) ||
                             oldName.StartsWith("com.github.MonoDetour", StringComparison.OrdinalIgnoreCase) ||
                             oldName.StartsWith("MonoDetour.", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("com.rlabrecque.steamworks.net", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("Steamworks.NET", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("BepInEx.Preloader", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("PrepatcherPlugin", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("Silksong.PrepatcherPlugin", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("UnityHelper", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("Silksong.UnityHelper", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("GenericVariableExtension", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("CanvasUtil", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("WishUtil", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("DebugMod", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("SkongGamemodes", StringComparison.OrdinalIgnoreCase) ||
                             oldName.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase))
                    {
                        aref.Name = "IRIS";
                        aref.Version = new Version(1, 0, 0, 0);
                        aref.PublicKeyToken = Array.Empty<byte>();
                        LogInfo($"[PCMod:{origModName}] Redirected assembly ref '{oldName}' -> 'IRIS (1.0.0.0)'");
                    }
                    else if (oldName.Equals("UnityEngine.TextCoreFontEngineModule", StringComparison.OrdinalIgnoreCase))
                    {
                        aref.Version = new Version(0, 0, 0, 0);
                        aref.PublicKeyToken = Array.Empty<byte>();
                    }
                    else if (oldName.Equals("Mono.Cecil", StringComparison.OrdinalIgnoreCase))
                    {
                        var loadedCecilName = typeof(ModuleDefinition).Assembly.GetName();
                        aref.Version = loadedCecilName.Version ?? new Version(0, 10, 4, 0);
                        aref.PublicKeyToken = loadedCecilName.GetPublicKeyToken() ?? Array.Empty<byte>();
                        LogInfo($"[PCMod:{origModName}] Normalized Mono.Cecil assembly ref ({oldVer}) -> ({aref.Version})");
                    }
                }

                var mobileApiRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "IRIS");
                if (mobileApiRef == null)
                {
                    mobileApiRef = new AssemblyNameReference("IRIS", new Version(1, 0, 0, 0));
                    mod.AssemblyReferences.Add(mobileApiRef);
                }

                // 3. Stub out [HarmonyILManipulator], [HarmonyTranspiler], and <Transpiler>d__ state machines
                StubOutILManipulatorsAndTranspilers(origModName, mod, mobileApiRef);

                // 3b. Bridge fork-only FsmUtil methods to the public mobile API, and remove
                // newer System.IO.EnumerationOptions-dependent bodies unsupported by the game runtime.
                int fsmUtilCompatCalls = RewriteFsmUtilCompatibilityCalls(origModName, mod, mobileApiRef);
                int enumerationMethodsStubbed = StubMethodsUsingUnsupportedEnumerationOptions(origModName, mod);

                // 3c. Purge all structural MonoDetour.Cil metadata, not only direct method parameters.
                int monoDetourRefsSanitized = SanitizeUnsupportedMonoDetourCilMetadata(origModName, mod);

                // 3d. Inline static HarmonyLib.FieldRef<T, F> / FieldRef<F> fields directly into native ldflda / ldsflda instructions
                int inlinedFieldRefs = InlineStaticFieldRefAccesses(origModName, mod);

                // 4. Redirect TypeReferences AND MemberReferences for shimmed BepInEx / HarmonyLib / ModMenu / MonoMod types
                foreach (var tref in mod.GetTypeReferences())
                {
                    RedirectTypeReferenceIfShimmed(tref, mobileApiRef);
                }
                foreach (var mref in mod.GetMemberReferences())
                {
                    if (mref.DeclaringType != null)
                        SanitizeTypeSpecAndSignature(mref.DeclaringType, mobileApiRef);
                    if (mref is FieldReference fref)
                        SanitizeTypeSpecAndSignature(fref.FieldType, mobileApiRef);
                    else if (mref is MethodReference methodRef)
                    {
                        SanitizeTypeSpecAndSignature(methodRef.ReturnType, mobileApiRef);
                        foreach (var p in methodRef.Parameters)
                            SanitizeTypeSpecAndSignature(p.ParameterType, mobileApiRef);
                    }
                }

                // 4b. Apply targeted IL2CPP & mobile mod bug sanitizers before extracting patches
                SanitizeModSpecificBugs(origModName, mod, mobileApiRef);

                // 5. Extract [HarmonyPatch] custom attributes via raw ECMA-335 blob (ca.GetBlob())
                int extractedPatchCount = ExtractAndEmbedHarmonyPatches(modName, mod, mobileApiRef);

                // 6. Rewrite all IL method bodies
                int rewrittenCalls = RewriteAllMethodBodies(origModName, mod, mobileApiRef);

                // 7. Verify all TypeReferences against AppDomain & auto-synthesize any missing external types
                int autoShimmedTypes = VerifyAndAutoShimTypeReferences(origModName, mod, mobileApiRef, out byte[] shimBytes);

                LogInfo($"[PCMod:{origModName}] Rewriting complete ({rawBytes.Length} bytes, {extractedPatchCount} [HarmonyPatch] attributes extracted, {rewrittenCalls} IL call bridges, {fsmUtilCompatCalls} FsmUtil compatibility call redirects, {enumerationMethodsStubbed} EnumerationOptions-dependent methods stubbed, {monoDetourRefsSanitized} MonoDetour.Cil members sanitized, {autoShimmedTypes} auto-shimmed types).");

                using (var msOut = new MemoryStream())
                {
                    asmDef.Write(msOut);
                    byte[] outBytes = msOut.ToArray();
                    if (!string.IsNullOrEmpty(cachePrefix))
                    {
                        SaveToCecilDiskCache(cacheDir, cachePrefix, modName, outBytes, shimBytes, extractedResources);
                    }
                    return outBytes;
                }
            }
        }


        private sealed class InlinedFieldRefTarget
        {
            public FieldReference TargetField;
            public bool IsStatic;
        }

        private static FieldReference ResolveTargetFieldForInlineFieldRef(ModuleDefinition mod, TypeReference targetTypeRef, TypeReference fieldTypeRef, string fieldName, bool isStatic)
        {
            if (mod == null || targetTypeRef == null || string.IsNullOrEmpty(fieldName)) return null;
            try
            {
                string rtFullName = (targetTypeRef.FullName ?? "").Replace('/', '+');
                var rtType = ResolveRuntimeType(rtFullName);
                for (var cur = rtType; cur != null; cur = cur.BaseType)
                {
                    var flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    var fi = cur.GetField(fieldName, flags);
                    if (fi != null)
                    {
                        return mod.ImportReference(fi);
                    }
                }
            }
            catch { }

            if (fieldTypeRef == null) return null;
            return new FieldReference(fieldName, mod.ImportReference(fieldTypeRef), mod.ImportReference(targetTypeRef));
        }

        private static int InlineStaticFieldRefAccesses(string modName, ModuleDefinition mod)
        {
            if (mod == null) return 0;
            var allTypes = GetAllTypesRecursive(mod.Types).ToArray();
            var fieldMap = new Dictionary<string, InlinedFieldRefTarget>(StringComparer.Ordinal);

            foreach (var td in allTypes)
            {
                var cctor = td.Methods.FirstOrDefault(m => m.Name == ".cctor" && m.HasBody);
                if (cctor == null) continue;
                var instrs = cctor.Body.Instructions;

                for (int i = 0; i < instrs.Count; i++)
                {
                    if (instrs[i].OpCode != OpCodes.Stsfld || !(instrs[i].Operand is FieldReference stFieldRef))
                        continue;

                    var fd = td.Fields.FirstOrDefault(f => f.Name == stFieldRef.Name && f.IsStatic);
                    if (fd == null) continue;
                    string ftName = fd.FieldType?.FullName ?? "";
                    if (ftName.IndexOf("HarmonyLib.AccessTools/FieldRef", StringComparison.Ordinal) < 0 &&
                        ftName.IndexOf("HarmonyLib.AccessTools+FieldRef", StringComparison.Ordinal) < 0 &&
                        ftName.IndexOf("FieldRef`", StringComparison.Ordinal) < 0)
                        continue;

                    // Pattern 1: ldstr "fieldName" -> call AccessTools::FieldRefAccess<T, F>(string) -> stsfld fd
                    if (i >= 2 &&
                        instrs[i - 2].OpCode == OpCodes.Ldstr &&
                        instrs[i - 2].Operand is string instFieldName &&
                        (instrs[i - 1].OpCode == OpCodes.Call || instrs[i - 1].OpCode == OpCodes.Callvirt) &&
                        instrs[i - 1].Operand is GenericInstanceMethod gim &&
                        gim.Name == "FieldRefAccess" &&
                        gim.GenericArguments.Count == 2)
                    {
                        var targetField = ResolveTargetFieldForInlineFieldRef(mod, gim.GenericArguments[0], gim.GenericArguments[1], instFieldName, false);
                        if (targetField != null)
                        {
                            string key = $"{td.FullName}::{fd.Name}";
                            fieldMap[key] = new InlinedFieldRefTarget { TargetField = targetField, IsStatic = false };
                            fd.FieldType = mod.TypeSystem.Object;
                            instrs[i - 2].OpCode = OpCodes.Nop;
                            instrs[i - 2].Operand = null;
                            instrs[i - 1].OpCode = OpCodes.Nop;
                            instrs[i - 1].Operand = null;
                            instrs[i].OpCode = OpCodes.Nop;
                            instrs[i].Operand = null;
                        }
                    }
                    // Pattern 2: ldtoken T -> call GetTypeFromHandle -> ldstr "fieldName" -> call AccessTools::Field -> call StaticFieldRefAccess<F>(FieldInfo) -> stsfld fd
                    else if (i >= 5 &&
                             instrs[i - 5].OpCode == OpCodes.Ldtoken &&
                             instrs[i - 5].Operand is TypeReference statTargetType &&
                             instrs[i - 3].OpCode == OpCodes.Ldstr &&
                             instrs[i - 3].Operand is string statFieldName &&
                             (instrs[i - 1].OpCode == OpCodes.Call || instrs[i - 1].OpCode == OpCodes.Callvirt) &&
                             instrs[i - 1].Operand is GenericInstanceMethod sgim &&
                             sgim.Name == "StaticFieldRefAccess" &&
                             sgim.GenericArguments.Count == 1)
                    {
                        var targetField = ResolveTargetFieldForInlineFieldRef(mod, statTargetType, sgim.GenericArguments[0], statFieldName, true);
                        if (targetField != null)
                        {
                            string key = $"{td.FullName}::{fd.Name}";
                            fieldMap[key] = new InlinedFieldRefTarget { TargetField = targetField, IsStatic = true };
                            fd.FieldType = mod.TypeSystem.Object;
                            for (int k = i - 5; k <= i; k++)
                            {
                                instrs[k].OpCode = OpCodes.Nop;
                                instrs[k].Operand = null;
                            }
                        }
                    }
                }
            }

            if (fieldMap.Count == 0) return 0;

            int inlinedCallSites = 0;
            foreach (var td in allTypes)
            {
                foreach (var md in td.Methods)
                {
                    if (!md.HasBody) continue;
                    var instrs = md.Body.Instructions;
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        if (instrs[i].OpCode != OpCodes.Ldsfld || !(instrs[i].Operand is FieldReference ldsFieldRef))
                            continue;
                        string key = $"{ldsFieldRef.DeclaringType?.FullName}::{ldsFieldRef.Name}";
                        if (!fieldMap.TryGetValue(key, out var target))
                            continue;

                        if (target.IsStatic)
                        {
                            // Static FieldRef<F>: ldsfld fd -> callvirt FieldRef<F>::Invoke()
                            if (i + 1 < instrs.Count &&
                                (instrs[i + 1].OpCode == OpCodes.Callvirt || instrs[i + 1].OpCode == OpCodes.Call) &&
                                instrs[i + 1].Operand is MethodReference invMr &&
                                invMr.Name == "Invoke")
                            {
                                instrs[i].OpCode = OpCodes.Nop;
                                instrs[i].Operand = null;
                                instrs[i + 1].OpCode = OpCodes.Ldsflda;
                                instrs[i + 1].Operand = target.TargetField;
                                inlinedCallSites++;
                            }
                        }
                        else
                        {
                            // Instance FieldRef<T, F>: ldsfld fd -> <push instance (1..7 instrs)> -> callvirt FieldRef<T, F>::Invoke(T)
                            for (int j = i + 1; j < Math.Min(instrs.Count, i + 9); j++)
                            {
                                if ((instrs[j].OpCode == OpCodes.Callvirt || instrs[j].OpCode == OpCodes.Call) &&
                                    instrs[j].Operand is MethodReference invMr &&
                                    invMr.Name == "Invoke" &&
                                    invMr.Parameters.Count == 1)
                                {
                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    instrs[j].OpCode = OpCodes.Ldflda;
                                    instrs[j].Operand = target.TargetField;
                                    inlinedCallSites++;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (inlinedCallSites > 0)
            {
                LogInfo($"[PCMod:{modName}] Inlined {fieldMap.Count} static FieldRef definitions across {inlinedCallSites} call sites into direct native ldflda/ldsflda instructions.");
            }
            return inlinedCallSites;
        }

        private static int RewriteFsmUtilCompatibilityCalls(string modName, ModuleDefinition mod, AssemblyNameReference mobileApiRef)
        {
            if (mod == null || mobileApiRef == null) return 0;
            int rewritten = 0;
            var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef, false);

            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                foreach (var md in td.Methods)
                {
                    if (!md.HasBody) continue;
                    foreach (var ins in md.Body.Instructions)
                    {
                        if (!(ins.Operand is MethodReference source) || source.DeclaringType == null ||
                            !string.Equals(source.DeclaringType.FullName, "Silksong.FsmUtil.FsmUtil", StringComparison.Ordinal) ||
                            source.HasThis || source.ReturnType == null || source.ReturnType.FullName != "System.Void" ||
                            source.Parameters.Count != 1)
                            continue;

                        string helperName = null;
                        string parameterType = source.Parameters[0].ParameterType?.FullName ?? "";
                        if (source.Name == "EnsureStateLoaded" && parameterType == "HutongGames.PlayMaker.FsmState")
                            helperName = "EnsureSingleStateLoadedAndLocked";
                        else if (source.Name == "EnsureFsmLoaded" && parameterType == "PlayMakerFSM")
                            helperName = "EnsurePlayMakerFsmPreprocessedAndLoaded";
                        else if (source.Name == "EnsureFsmLoaded" && parameterType == "HutongGames.PlayMaker.Fsm")
                            helperName = "EnsureFsmPreprocessedOnce";
                        else if (source.Name == "LockState" && parameterType == "HutongGames.PlayMaker.FsmState")
                            helperName = "LockStateSafe";

                        if (helperName == null) continue;
                        var target = new MethodReference(helperName, mod.TypeSystem.Void, bridgeType)
                        {
                            HasThis = false,
                            CallingConvention = MethodCallingConvention.Default
                        };
                        target.Parameters.Add(new ParameterDefinition(mod.ImportReference(source.Parameters[0].ParameterType)));
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = target;
                        rewritten++;
                    }
                }
            }

            if (rewritten > 0)
                LogInfo($"[PCMod:{modName}] Redirected {rewritten} calls to FsmUtil fork-only Ensure*/LockState methods into PlayMakerHybridCLRBridge compatibility helpers.");
            return rewritten;
        }

        private static bool ReferencesEnumerationOptions(TypeReference type)
        {
            if (type == null) return false;
            string name = type.FullName ?? "";
            return name.IndexOf("System.IO.EnumerationOptions", StringComparison.Ordinal) >= 0;
        }

        private static bool OperandUsesEnumerationOptions(object operand)
        {
            if (operand == null) return false;
            if (operand is TypeReference tr && ReferencesEnumerationOptions(tr)) return true;
            if (operand is FieldReference fr && (ReferencesEnumerationOptions(fr.DeclaringType) || ReferencesEnumerationOptions(fr.FieldType))) return true;
            if (operand is MethodReference mr)
            {
                if (ReferencesEnumerationOptions(mr.DeclaringType) || ReferencesEnumerationOptions(mr.ReturnType)) return true;
                if (mr.Parameters.Any(p => ReferencesEnumerationOptions(p.ParameterType))) return true;
                if (mr.GenericParameters.Any(gp => gp.Constraints.Any(c => ReferencesEnumerationOptions(c.ConstraintType)))) return true;
            }
            return (operand.ToString() ?? "").IndexOf("System.IO.EnumerationOptions", StringComparison.Ordinal) >= 0;
        }

        private static void StubMethodWithDefaultReturn(MethodDefinition method)
        {
            if (method == null || !method.HasBody) return;
            method.Body.Instructions.Clear();
            method.Body.Variables.Clear();
            method.Body.ExceptionHandlers.Clear();
            method.Body.InitLocals = false;
            var il = method.Body.GetILProcessor();
            string ret = method.ReturnType?.FullName ?? "System.Void";
            if (ret == "System.Void")
            {
                il.Emit(OpCodes.Ret);
            }
            else if (ret == "System.String")
            {
                il.Emit(OpCodes.Ldstr, "");
                il.Emit(OpCodes.Ret);
            }
            else if (ret == "System.Boolean" || ret == "System.Int32" || ret == "System.UInt32" ||
                     ret == "System.Int16" || ret == "System.UInt16" || ret == "System.Byte" ||
                     ret == "System.SByte" || ret == "System.Int64" || ret == "System.UInt64")
            {
                if (ret == "System.Int64" || ret == "System.UInt64")
                    il.Emit(OpCodes.Ldc_I8, 0L);
                else
                    il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
            }
            else if (ret == "System.Single")
            {
                il.Emit(OpCodes.Ldc_R4, 0f);
                il.Emit(OpCodes.Ret);
            }
            else if (ret == "System.Double")
            {
                il.Emit(OpCodes.Ldc_R8, 0d);
                il.Emit(OpCodes.Ret);
            }
            else if (method.ReturnType.IsValueType || method.ReturnType is GenericParameter)
            {
                method.Body.InitLocals = true;
                var local = new VariableDefinition(method.ReturnType);
                method.Body.Variables.Add(local);
                il.Emit(OpCodes.Ldloca, local);
                il.Emit(OpCodes.Initobj, method.ReturnType);
                il.Emit(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Ret);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
        }

        private static int StubMethodsUsingUnsupportedEnumerationOptions(string modName, ModuleDefinition mod)
        {
            if (mod == null) return 0;
            var mobileApiRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "IRIS");
            if (mobileApiRef == null) return 0;
            var compatType = new TypeReference("SilksongMobileAPI", "EnumerationOptionsCompat", mod, mobileApiRef, false);
            var enumOptsType = new TypeReference("SilksongMobileAPI.IO", "EnumerationOptions", mod, mobileApiRef, false);
            var matchCasingType = new TypeReference("SilksongMobileAPI.IO", "MatchCasing", mod, mobileApiRef, true);
            var matchTypeType = new TypeReference("SilksongMobileAPI.IO", "MatchType", mod, mobileApiRef, true);
            int count = 0;

            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                foreach (var md in td.Methods)
                {
                    if (!md.HasBody) continue;
                    foreach (var v in md.Body.Variables)
                    {
                        if (v.VariableType == null) continue;
                        if (v.VariableType.FullName == "System.IO.EnumerationOptions")
                            v.VariableType = enumOptsType;
                        else if (v.VariableType.FullName == "System.IO.MatchCasing")
                            v.VariableType = matchCasingType;
                        else if (v.VariableType.FullName == "System.IO.MatchType")
                            v.VariableType = matchTypeType;
                    }
                    var instrs = md.Body.Instructions;
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        if (ins.Operand is MethodReference mr && mr.DeclaringType != null)
                        {
                            if (mr.DeclaringType.FullName == "System.IO.EnumerationOptions")
                            {
                                mr.DeclaringType = enumOptsType;
                                if (mr.ReturnType != null)
                                {
                                    if (mr.ReturnType.FullName == "System.IO.MatchCasing") mr.ReturnType = matchCasingType;
                                    else if (mr.ReturnType.FullName == "System.IO.MatchType") mr.ReturnType = matchTypeType;
                                }
                                for (int pi = 0; pi < mr.Parameters.Count; pi++)
                                {
                                    var pt = mr.Parameters[pi].ParameterType;
                                    if (pt != null)
                                    {
                                        if (pt.FullName == "System.IO.MatchCasing") mr.Parameters[pi].ParameterType = matchCasingType;
                                        else if (pt.FullName == "System.IO.MatchType") mr.Parameters[pi].ParameterType = matchTypeType;
                                    }
                                }
                                count++;
                            }
                            else if (mr.DeclaringType.FullName == "System.IO.Directory" &&
                                     mr.Parameters.Count == 3 &&
                                     mr.Parameters[2].ParameterType.FullName == "System.IO.EnumerationOptions")
                            {
                                string helperName = null;
                                if (mr.Name == "GetFiles") helperName = "Directory_GetFiles";
                                else if (mr.Name == "EnumerateFiles") helperName = "Directory_EnumerateFiles";
                                else if (mr.Name == "GetDirectories") helperName = "Directory_GetDirectories";
                                else if (mr.Name == "EnumerateDirectories") helperName = "Directory_EnumerateDirectories";
                                else if (mr.Name == "GetFileSystemEntries") helperName = "Directory_GetFileSystemEntries";
                                else if (mr.Name == "EnumerateFileSystemEntries") helperName = "Directory_EnumerateFileSystemEntries";

                                if (helperName != null)
                                {
                                    var target = new MethodReference(helperName, mr.ReturnType, compatType) { HasThis = false };
                                    target.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
                                    target.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
                                    target.Parameters.Add(new ParameterDefinition(enumOptsType));
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = target;
                                    count++;
                                }
                            }
                        }
                    }
                }
            }
            if (count > 0)
                LogInfo($"[PCMod:{modName}] Bridged {count} System.IO.EnumerationOptions reference(s) to SilksongMobileAPI.EnumerationOptionsCompat.");
            return count;
        }

        private static bool IsMonoDetourCilReference(TypeReference type)
        {
            if (type == null) return false;
            string fullName = type.FullName ?? "";
            return fullName.IndexOf("MonoDetour.Cil.", StringComparison.Ordinal) >= 0 ||
                   fullName.StartsWith("MonoDetour.Cil", StringComparison.Ordinal);
        }

        private static bool OperandUsesMonoDetourCil(object operand)
        {
            if (operand == null) return false;
            if (operand is TypeReference tr && IsMonoDetourCilReference(tr)) return true;
            if (operand is FieldReference fr && (IsMonoDetourCilReference(fr.DeclaringType) || IsMonoDetourCilReference(fr.FieldType))) return true;
            if (operand is MethodReference mr)
            {
                if (IsMonoDetourCilReference(mr.DeclaringType) || IsMonoDetourCilReference(mr.ReturnType)) return true;
                if (mr.Parameters.Any(p => IsMonoDetourCilReference(p.ParameterType))) return true;
                if (mr.GenericParameters.Any(gp => gp.Constraints.Any(c => IsMonoDetourCilReference(c.ConstraintType)))) return true;
            }
            return (operand.ToString() ?? "").IndexOf("MonoDetour.Cil.", StringComparison.Ordinal) >= 0;
        }

        private static void RemoveMonoDetourCilAttributes(Mono.Cecil.ICustomAttributeProvider provider)
        {
            if (provider == null || !provider.HasCustomAttributes) return;
            for (int i = provider.CustomAttributes.Count - 1; i >= 0; i--)
            {
                var attribute = provider.CustomAttributes[i];
                if (attribute != null && IsMonoDetourCilReference(attribute.AttributeType))
                    provider.CustomAttributes.RemoveAt(i);
            }
        }

        private static int SanitizeUnsupportedMonoDetourCilMetadata(string modName, ModuleDefinition mod)
        {
            int count = 0;
            RemoveMonoDetourCilAttributes(mod);
            if (mod.Assembly != null) RemoveMonoDetourCilAttributes(mod.Assembly);

            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                RemoveMonoDetourCilAttributes(td);
                if (IsMonoDetourCilReference(td.BaseType))
                {
                    td.BaseType = mod.TypeSystem.Object;
                    count++;
                }
                for (int i = td.Interfaces.Count - 1; i >= 0; i--)
                {
                    if (IsMonoDetourCilReference(td.Interfaces[i].InterfaceType))
                    {
                        td.Interfaces.RemoveAt(i);
                        count++;
                    }
                }
                foreach (var gp in td.GenericParameters)
                {
                    RemoveMonoDetourCilAttributes(gp);
                    for (int i = gp.Constraints.Count - 1; i >= 0; i--)
                        if (IsMonoDetourCilReference(gp.Constraints[i].ConstraintType)) gp.Constraints.RemoveAt(i);
                }

                foreach (var fd in td.Fields)
                {
                    RemoveMonoDetourCilAttributes(fd);
                    if (IsMonoDetourCilReference(fd.FieldType)) { fd.FieldType = mod.TypeSystem.Object; count++; }
                }
                foreach (var pd in td.Properties)
                {
                    RemoveMonoDetourCilAttributes(pd);
                    if (IsMonoDetourCilReference(pd.PropertyType)) { pd.PropertyType = mod.TypeSystem.Object; count++; }
                }
                foreach (var ed in td.Events)
                {
                    RemoveMonoDetourCilAttributes(ed);
                    if (IsMonoDetourCilReference(ed.EventType)) { ed.EventType = mod.TypeSystem.Object; count++; }
                }
                foreach (var md in td.Methods)
                {
                    RemoveMonoDetourCilAttributes(md);
                    RemoveMonoDetourCilAttributes(md.MethodReturnType);
                    foreach (var gp in md.GenericParameters)
                    {
                        RemoveMonoDetourCilAttributes(gp);
                        for (int i = gp.Constraints.Count - 1; i >= 0; i--)
                            if (IsMonoDetourCilReference(gp.Constraints[i].ConstraintType)) gp.Constraints.RemoveAt(i);
                    }
                    foreach (var parameter in md.Parameters) RemoveMonoDetourCilAttributes(parameter);

                    bool badSignature = IsMonoDetourCilReference(md.ReturnType) ||
                                        md.Parameters.Any(parameter => IsMonoDetourCilReference(parameter.ParameterType));
                    if (IsMonoDetourCilReference(md.ReturnType)) md.ReturnType = mod.TypeSystem.Object;
                    foreach (var parameter in md.Parameters)
                        if (IsMonoDetourCilReference(parameter.ParameterType)) parameter.ParameterType = mod.TypeSystem.Object;

                    bool badBody = md.HasBody && (md.Body.Variables.Any(v => IsMonoDetourCilReference(v.VariableType)) ||
                                                   md.Body.Instructions.Any(ins => OperandUsesMonoDetourCil(ins.Operand)));
                    if (badSignature || badBody)
                    {
                        StubMethodWithDefaultReturn(md);
                        count++;
                    }
                }
            }

            if (count > 0)
                LogInfo($"[PCMod:{modName}] Sanitized {count} MonoDetour.Cil signatures, metadata items, or method bodies unsupported by HybridCLR.");
            return count;
        }


        private static void SanitizeModSpecificBugs(string origModName, ModuleDefinition mod, AssemblyNameReference mobileApiRef)
        {
            var bridgeTypeRef = new TypeReference("SilksongMobileAPI", "HarmonyBridge", mod, mobileApiRef);

            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                // A1. RebalancedCrests.Core.EnemyHitContext: eliminate System.Nullable<HitInstance> (un-AOT'd 104-byte generic struct)
                if (td.FullName == "RebalancedCrests.Core.EnemyHitContext")
                {
                    td.CustomAttributes.Clear();
                    foreach (var fd in td.Fields)
                    {
                        fd.FieldType = mod.TypeSystem.Object;
                    }
                    foreach (var prop in td.Properties)
                    {
                        prop.PropertyType = mod.TypeSystem.Object;
                    }
                    foreach (var md in td.Methods)
                    {
                        md.CustomAttributes.Clear();
                        md.Parameters.Clear();
                        md.ReturnType = mod.TypeSystem.Void;
                        if (md.HasBody)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            md.Body.GetILProcessor().Emit(OpCodes.Ret);
                        }
                    }
                    LogInfo($"[PCMod:{origModName}] Sanitized EnemyHitContext Nullable<HitInstance> state on IL2CPP.");
                }

                // A2. RebalancedCrests.Crests.Reaper.ReaperPayoutPatch::OverridePayout: replace EnemyHitContext.get_Current Nullable<HitInstance>
                else if (td.FullName == "RebalancedCrests.Crests.Reaper.ReaperPayoutPatch")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "OverridePayout" && md.HasBody)
                        {
                            var bridgeOverride = new MethodReference("RebalancedCrests_OverrideReaperPayout", mod.TypeSystem.Boolean, bridgeTypeRef)
                            {
                                HasThis = false
                            };
                            if (md.Parameters.Count >= 2)
                            {
                                bridgeOverride.Parameters.Add(new ParameterDefinition(md.Parameters[0].ParameterType));
                                bridgeOverride.Parameters.Add(new ParameterDefinition(md.Parameters[1].ParameterType));
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                md.Body.Instructions.Clear();
                                var il = md.Body.GetILProcessor();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, bridgeOverride);
                                il.Emit(OpCodes.Ret);
                                LogInfo($"[PCMod:{origModName}] Bridged ReaperPayoutPatch.OverridePayout without Nullable<HitInstance>.");
                            }
                        }
                    }
                }
                // A3. RebalancedCrests.Crests.Wanderer.SingleSwipeNeedleStrikeTweak:
                //     - Ensure CreateSwipe wakes & initializes _swipe and _swipeSlash
                //     - Ensure BaseChargedClip falls back safely if GetClipByName returns null
                else if (td.FullName == "RebalancedCrests.Crests.Wanderer.SingleSwipeNeedleStrikeTweak")
                {
                    FieldDefinition swipeField = td.Fields.FirstOrDefault(f => f.Name == "_swipe");
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "CreateSwipe" && md.HasBody && swipeField != null)
                        {
                            var initMethod = new MethodReference("EnsureCustomNailSlashColliderAndAnimReady", mod.TypeSystem.Void, bridgeTypeRef)
                            {
                                HasThis = false
                            };
                            initMethod.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                            var ins = md.Body.Instructions;
                            for (int i = ins.Count - 1; i >= 0; i--)
                            {
                                if (ins[i].OpCode == OpCodes.Ret)
                                {
                                    var retIns = ins[i];
                                    var il = md.Body.GetILProcessor();
                                    il.InsertBefore(retIns, il.Create(OpCodes.Ldarg_0));
                                    il.InsertBefore(retIns, il.Create(OpCodes.Ldfld, swipeField));
                                    il.InsertBefore(retIns, il.Create(OpCodes.Call, initMethod));
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Injected Wanderer Swipe initialization into SingleSwipeNeedleStrikeTweak.CreateSwipe.");
                        }
                        else if (md.Name == "BaseChargedClip" && md.HasBody)
                        {
                            // Ensure if __instance.animator.GetClipByName(clipName) returns null, BaseChargedClip returns true (run original) instead of false with null __result!
                            var safeClipMethod = new MethodReference("RebalancedCrests_SafeBaseChargedClip", mod.TypeSystem.Boolean, bridgeTypeRef)
                            {
                                HasThis = false
                            };
                            if (md.Parameters.Count == 3)
                            {
                                safeClipMethod.Parameters.Add(new ParameterDefinition(md.Parameters[0].ParameterType));
                                safeClipMethod.Parameters.Add(new ParameterDefinition(md.Parameters[1].ParameterType));
                                safeClipMethod.Parameters.Add(new ParameterDefinition(md.Parameters[2].ParameterType));
                                var ins = md.Body.Instructions;
                                // Replace the tail starting at ldarg.2 (where it sets __result and returns false)
                                for (int i = 0; i < ins.Count; i++)
                                {
                                    if (ins[i].OpCode == OpCodes.Ldarg_2 && i + 5 < ins.Count && ins[i + 5].OpCode == OpCodes.Stind_Ref)
                                    {
                                        ins[i].OpCode = OpCodes.Ldarg_0;
                                        ins[i].Operand = null;
                                        ins[i + 1].OpCode = OpCodes.Ldarg_1;
                                        ins[i + 1].Operand = null;
                                        ins[i + 2].OpCode = OpCodes.Ldarg_2;
                                        ins[i + 2].Operand = null;
                                        ins[i + 3].OpCode = OpCodes.Call;
                                        ins[i + 3].Operand = safeClipMethod;
                                        ins[i + 4].OpCode = OpCodes.Ret;
                                        ins[i + 4].Operand = null;
                                        ins[i + 5].OpCode = OpCodes.Nop;
                                        ins[i + 5].Operand = null;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                // A4. RebalancedCrests SingleSwipeNeedleStrikeTweak+<>c__DisplayClass42_0::<Apply>b__4:
                //     Ensure _swipeSlash and parent _swipe are active & initialized right before _swipeSlash.StartSlash()!
                else if (td.Name == "<>c__DisplayClass42_0" && td.DeclaringType?.FullName == "RebalancedCrests.Crests.Wanderer.SingleSwipeNeedleStrikeTweak")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "<Apply>b__4" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Callvirt || ins[i].OpCode == OpCodes.Call) &&
                                    ins[i].Operand is MethodReference mr && mr.Name == "StartSlash")
                                {
                                    var initAndStart = new MethodReference("RebalancedCrests_SafeStartSwipeSlash", mod.TypeSystem.Void, bridgeTypeRef)
                                    {
                                        HasThis = false
                                    };
                                    initAndStart.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                                    ins[i].OpCode = OpCodes.Call;
                                    ins[i].Operand = initAndStart;
                                    LogInfo($"[PCMod:{origModName}] Bridged SingleSwipeNeedleStrikeTweak.<Apply>b__4 StartSlash -> RebalancedCrests_SafeStartSwipeSlash.");
                                }
                            }
                        }
                    }
                }
                // B1. SSMP.Api.Addon.AddonLoader::GetAssemblyPaths: return Array.Empty<string>() so AddonLoader never calls Assembly.LoadFrom on raw disk DLLs
                else if (td.FullName == "SSMP.Api.Addon.AddonLoader")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "GetAssemblyPaths" && md.HasBody)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Newarr, mod.TypeSystem.String);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Patched SSMP AddonLoader.GetAssemblyPaths to use mobile managed addon loader.");
                        }
                    }
                }
                // B2. SSMP.Ui.Component.InputComponent::.ctor: set shouldActivateOnSelect = true so Android TouchScreenKeyboard opens on tap
                else if (td.FullName == "SSMP.Ui.Component.InputComponent")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == ".ctor" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 1; i < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Callvirt || ins[i].OpCode == OpCodes.Call) &&
                                    ins[i].Operand is MethodReference mr && mr.Name == "set_shouldActivateOnSelect" &&
                                    ins[i - 1].OpCode == OpCodes.Ldc_I4_0)
                                {
                                    ins[i - 1].OpCode = OpCodes.Ldc_I4_1;
                                    LogInfo($"[PCMod:{origModName}] Enabled Android TouchScreenKeyboard auto-open on SSMP InputComponent.");
                                }
                            }
                        }
                    }
                }
                // D1. SuSul WeaverCrest (0.1.4): Fix 4th slot (SlotIndex == 2) relocking & unequipping after leaving bench
                else if (td.FullName == "WeaverCrestMod.WeaverCrestRegistry")
                {
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "IsMemorySlot")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Unlocked WeaverCrest SlotIndex 2 (IsMemorySlot -> false).");
                        }
                        else if (md.Name == "IsValidEquippedTool")
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 2 < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ldarg_0 &&
                                    ins[i + 1].OpCode == OpCodes.Ldc_I4_2 &&
                                    (ins[i + 2].OpCode == OpCodes.Beq_S || ins[i + 2].OpCode == OpCodes.Beq))
                                {
                                    ins[i].OpCode = OpCodes.Nop;
                                    ins[i].Operand = null;
                                    ins[i + 1].OpCode = OpCodes.Nop;
                                    ins[i + 1].Operand = null;
                                    ins[i + 2].OpCode = OpCodes.Nop;
                                    ins[i + 2].Operand = null;
                                    LogInfo($"[PCMod:{origModName}] Allowed equipped tools in WeaverCrest SlotIndex 2 (IsValidEquippedTool).");
                                    break;
                                }
                            }
                        }
                        else if (md.Name == "BuildSlots")
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 1 < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ldc_I4_1 &&
                                    ins[i + 1].OpCode == OpCodes.Stfld &&
                                    ins[i + 1].Operand is FieldReference fr && fr.Name == "IsLocked")
                                {
                                    ins[i].OpCode = OpCodes.Ldc_I4_0;
                                    LogInfo($"[PCMod:{origModName}] Patched WeaverCrest BuildSlots SlotInfo[2].IsLocked -> false.");
                                }
                            }
                        }
                        else if (md.Name == "EnsureUnlocked")
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 5 < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Ldloc_S || ins[i].OpCode == OpCodes.Ldloc) &&
                                    ins[i + 1].OpCode == OpCodes.Ldc_I4_2 &&
                                    ins[i + 2].OpCode == OpCodes.Ceq &&
                                    ins[i + 3].OpCode == OpCodes.Ldc_I4_0 &&
                                    ins[i + 4].OpCode == OpCodes.Ceq &&
                                    ins[i + 5].OpCode == OpCodes.Stfld &&
                                    ins[i + 5].Operand is FieldReference fr && fr.Name == "IsUnlocked")
                                {
                                    ins[i].OpCode = OpCodes.Ldc_I4_1;
                                    ins[i].Operand = null;
                                    ins[i + 1].OpCode = OpCodes.Nop;
                                    ins[i + 1].Operand = null;
                                    ins[i + 2].OpCode = OpCodes.Nop;
                                    ins[i + 2].Operand = null;
                                    ins[i + 3].OpCode = OpCodes.Nop;
                                    ins[i + 3].Operand = null;
                                    ins[i + 4].OpCode = OpCodes.Nop;
                                    ins[i + 4].Operand = null;
                                    LogInfo($"[PCMod:{origModName}] Patched WeaverCrest EnsureUnlocked to keep SlotIndex 2 unlocked.");
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "WeaverCrestMod.WeaverMemorySlotSavedEquipPatch")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "Prefix" && md.HasBody)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            md.Body.GetILProcessor().Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Disabled WeaverMemorySlotSavedEquipPatch.Prefix so SlotIndex 2 keeps equipped tools after leaving bench.");
                        }
                    }
                }
                // E0. Stitchwork (0.9.1): Disable 272 MB Mono heap pre-allocation (PrewarmMonoHeap / PrewarmHeap / get_HeapReserveMB) to save mobile RAM!
                if (td.FullName.StartsWith("Stitchwork.", StringComparison.Ordinal))
                {
                    foreach (var md in td.Methods)
                    {
                        if ((md.Name == "PrewarmMonoHeap" || md.Name == "PrewarmHeap" || md.Name == "get_HeapReserveMB") && md.HasBody)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            if (md.ReturnType.FullName == "System.Int32" || md.ReturnType.FullName == "System.Boolean")
                                il.Emit(OpCodes.Ldc_I4_0);
                            else if (md.ReturnType.FullName == "System.Int64")
                            {
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Conv_I8);
                            }
                            else if (md.ReturnType.FullName != "System.Void")
                                il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Disabled Stitchwork 272 MB heap pre-allocation ({td.Name}.{md.Name}) to conserve mobile RAM.");
                        }
                    }
                }
                // E1. Stitchwork (0.9.1): Writable Android working root, safe DirectoryWatcher, built-in font, and touch-visible EdgeTabShell pull-tabs
                if (td.FullName == "Stitchwork.Plugin")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "ConfiguredRoot" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Call &&
                                    ins[i].Operand is MethodReference mr &&
                                    mr.Name == "get_dataPath" &&
                                    mr.DeclaringType?.FullName == "UnityEngine.Application")
                                {
                                    ins[i].Operand = new MethodReference("get_persistentDataPath", mod.TypeSystem.String, mr.DeclaringType) { HasThis = false };
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Redirected Stitchwork.Plugin.ConfiguredRoot dataPath -> persistentDataPath for writable Android storage.");
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.Watchers.DirectoryWatcher")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "Start" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 1 < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Newobj &&
                                    ins[i].Operand is MethodReference mr &&
                                    mr.DeclaringType?.FullName == "System.IO.DirectoryNotFoundException" &&
                                    ins[i + 1].OpCode == OpCodes.Throw)
                                {
                                    ins[i].OpCode = OpCodes.Pop;
                                    ins[i].Operand = null;
                                    ins[i + 1].OpCode = OpCodes.Ret;
                                    ins[i + 1].Operand = null;
                                    LogInfo($"[PCMod:{origModName}] Guarded Stitchwork DirectoryWatcher.Start against missing directory exceptions on Android.");
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.GUI.FabricUI")
                {
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "FindGameFont")
                        {
                            var getFallbackFontMi = _harmonyBridgeType?.GetMethod("GetFallbackFont", BindingFlags.Static | BindingFlags.Public);
                            if (getFallbackFontMi != null)
                            {
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                md.Body.Instructions.Clear();
                                var il = md.Body.GetILProcessor();
                                il.Emit(OpCodes.Call, mod.ImportReference(getFallbackFontMi));
                                il.Emit(OpCodes.Ret);
                                LogInfo($"[PCMod:{origModName}] Patched Stitchwork FabricUI.FindGameFont -> HarmonyBridge.GetFallbackFont() for visible mobile fonts.");
                            }
                        }
                        else if ((md.Name == "MakeEventSystemCurrent" || md.Name == "RestoreEventSystem" || md.Name == "EnsureEventSystem") &&
                                 md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Stubbed Stitchwork FabricUI.{md.Name} so Silksong's native EventSystem handles touch input.");
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.GUI.EdgeTabShell")
                {
                    var attachTabBtnMi = _harmonyBridgeType?.GetMethod("AttachStitchworkTabButton", BindingFlags.Static | BindingFlags.Public);
                    var invokeActivateSafeMi = _harmonyBridgeType?.GetMethod("InvokeStitchworkTabActivateSafe", BindingFlags.Static | BindingFlags.Public);

                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "get_Compact" && md.ReturnType.FullName == "System.Boolean")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Patched Stitchwork EdgeTabShell.get_Compact -> false so all top tabs display full text labels.");
                        }
                        else if (md.Name == "get_Revealed" && md.ReturnType.FullName == "System.Boolean")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Ret);
                        }
                        else if (md.Name == "Hide" && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ret);
                        }
                        else if (md.Name == "Build")
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 1 < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ldc_I4_1 &&
                                    ins[i + 1].OpCode == OpCodes.Call &&
                                    ins[i + 1].Operand is MethodReference mr &&
                                    mr.Name == "BuildTab")
                                {
                                    ins[i].OpCode = OpCodes.Ldc_I4_0;
                                    LogInfo($"[PCMod:{origModName}] Patched Stitchwork EdgeTabShell.Build so Settings tab (id=7) also displays its text label.");
                                }
                            }
                        }
                        else if (md.Name == "BuildTab" && attachTabBtnMi != null)
                        {
                            var il = md.Body.GetILProcessor();
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                                ExpandShortBranchIfNeeded(ins[i]);
                            var importedAttach = mod.ImportReference(attachTabBtnMi);
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ret)
                                {
                                    var callIns = il.Create(OpCodes.Call, importedAttach);
                                    il.InsertBefore(ins[i], callIns);
                                    i++;
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Wired native uGUI touch Button onto Stitchwork EdgeTabShell.BuildTab.");
                        }
                        else if (md.Name == "Tick")
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ldsfld &&
                                    ins[i].Operand is FieldReference fr &&
                                    fr.Name == "ShowDevHub")
                                {
                                    ins[i].OpCode = OpCodes.Ldc_I4_1;
                                    ins[i].Operand = null;
                                }
                                else if (ins[i].OpCode == OpCodes.Call &&
                                         ins[i].Operand is MethodReference mr &&
                                         mr.Name == "Activate" &&
                                         invokeActivateSafeMi != null)
                                {
                                    ins[i].Operand = mod.ImportReference(invokeActivateSafeMi);
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Patched Stitchwork EdgeTabShell.Tick for mobile touch activation.");
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.GUI.Kit.KitButton" || td.FullName == "Stitchwork.GUI.Kit.KitClickable")
                {
                    var attachKitBtnMi = _harmonyBridgeType?.GetMethod("AttachStitchworkKitButton", BindingFlags.Static | BindingFlags.Public);
                    if (attachKitBtnMi != null)
                    {
                        var importedAttachKit = mod.ImportReference(attachKitBtnMi);
                        if (td.Name == "KitButton")
                        {
                            foreach (var md in td.Methods)
                            {
                                if (md.Name == "Build" && md.HasBody)
                                {
                                    var il = md.Body.GetILProcessor();
                                    var ins = md.Body.Instructions;
                                    for (int i = 0; i < ins.Count; i++)
                                        ExpandShortBranchIfNeeded(ins[i]);
                                    for (int i = 0; i < ins.Count; i++)
                                    {
                                        if (ins[i].OpCode == OpCodes.Ret)
                                        {
                                            var retIns = ins[i];
                                            il.InsertBefore(retIns, il.Create(OpCodes.Ldarg_0));
                                            il.InsertBefore(retIns, il.Create(OpCodes.Call, importedAttachKit));
                                            i += 2;
                                        }
                                    }
                                }
                            }
                        }
                        else if (td.Name == "KitClickable" && !td.Methods.Any(m => m.Name == "OnEnable"))
                        {
                            var onEnableMd = new MethodDefinition(
                                "OnEnable",
                                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                                mod.TypeSystem.Void);
                            var il = onEnableMd.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Call, importedAttachKit);
                            il.Emit(OpCodes.Ret);
                            td.Methods.Add(onEnableMd);
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.GUI.Kit.KitLetterSpacing")
                {
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "Set")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ret);
                        }
                        else if (md.Name == "ModifyMesh" && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            md.Body.GetILProcessor().Emit(OpCodes.Ret);
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.Util.HealthCheck")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "ReadPinSuffix" && md.HasBody && md.ReturnType.FullName == "System.String")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldstr, "");
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Stubbed Stitchwork HealthCheck.ReadPinSuffix -> empty string (avoids stripped AssemblyMetadataAttribute.get_Key on Android).");
                        }
                    }
                }
                else if (td.FullName == "Stitchwork.Util.TexUtil")
                {
                    var rotProp = td.Properties.FirstOrDefault(p => p.Name == "RotateShaderUnsupported")?.SetMethod
                               ?? td.Methods.FirstOrDefault(m => m.Name == "set_RotateShaderUnsupported");
                    var chrProp = td.Properties.FirstOrDefault(p => p.Name == "ChromaLightUnsupported")?.SetMethod
                               ?? td.Methods.FirstOrDefault(m => m.Name == "set_ChromaLightUnsupported");
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "Initialize" && md.HasBody && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            if (rotProp != null)
                            {
                                il.Emit(OpCodes.Ldc_I4_1);
                                il.Emit(OpCodes.Call, rotProp);
                            }
                            if (chrProp != null)
                            {
                                il.Emit(OpCodes.Ldc_I4_1);
                                il.Emit(OpCodes.Call, chrProp);
                            }
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Patched Stitchwork TexUtil.Initialize to use CPU fallback without loading PC shader AssetBundle.");
                        }
                    }
                }
                else if (td.FullName == "PhantinelBoss.PhantinelBossPlugin")
                {
                    var harmonyField = td.Fields.FirstOrDefault(f => f.Name == "_harmony");
                    var patchesType = mod.GetType("PhantinelBoss.PhantomAndSentiPatches");
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "Awake" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            var il = md.Body.GetILProcessor();
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                                    ins[i].Operand is MethodReference mr && mr.Name == "add_activeSceneChanged")
                                {
                                    ins[i].OpCode = OpCodes.Pop;
                                    ins[i].Operand = null;
                                    LogInfo($"[PCMod:{origModName}] Neutralized SceneManager.activeSceneChanged in PhantinelBossPlugin.Awake without corrupting AssetHelper.RequestSceneAssets delegate.");
                                }
                            }
                        }
                        else if (md.Name == "EnsureRunner" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                                    ins[i].Operand is MethodReference mr && mr.Name == "add_activeSceneChanged")
                                {
                                    ins[i].OpCode = OpCodes.Pop;
                                    ins[i].Operand = null;
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Preserved EnsureRunner GameObject initialization in PhantinelBossPlugin.");
                        }
                        // Note: FsmState.LoadActions for PhantinelBoss is skipped safely inside RuntimeHybridCLRAdapterBuilder.RegisterOrUpdateHookChain
                        // without replacing ldstr "LoadActions" with ldnull (which caused AccessTools.Method(typeof(FsmState), null) to throw NullReferenceException).
                        if (md.Name == "OnSceneChange" && md.HasBody && harmonyField != null && patchesType != null)
                        {
                            // Bypass defeatedPhantom / wokeSongChevalier save-file gate so ForsakenSentinels always activates in Organ_01!
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Ldfld || ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                                    ins[i].Operand is MemberReference mr)
                                {
                                    if (mr.Name == "defeatedPhantom")
                                    {
                                        ins[i].OpCode = OpCodes.Pop;
                                        ins[i].Operand = null;
                                        md.Body.GetILProcessor().InsertAfter(ins[i], Instruction.Create(OpCodes.Ldc_I4_0));
                                        i++;
                                    }
                                    else if (mr.Name == "wokeSongChevalier")
                                    {
                                        ins[i].OpCode = OpCodes.Pop;
                                        ins[i].Operand = null;
                                        md.Body.GetILProcessor().InsertAfter(ins[i], Instruction.Create(OpCodes.Ldc_I4_1));
                                        i++;
                                    }
                                }
                            }
                            LogInfo($"[PCMod:{origModName}] Bypassed defeatedPhantom/wokeSongChevalier save-file gate in PhantinelBossPlugin.OnSceneChange.");
                        }
                    }
                }
                else if (td.FullName == "PhantinelBoss.PhantinelComponent")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "CoordinateHealth" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i + 2 < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ldstr &&
                                    ins[i].Operand as string == "WALL" &&
                                    (ins[i + 1].OpCode == OpCodes.Call || ins[i + 1].OpCode == OpCodes.Callvirt) &&
                                    ins[i + 2].OpCode == OpCodes.Stfld &&
                                    ins[i + 2].Operand is FieldReference fr &&
                                    fr.Name == "isFalse")
                                {
                                    ins[i].OpCode = OpCodes.Ldnull;
                                    ins[i].Operand = null;
                                    ins[i + 1].OpCode = OpCodes.Nop;
                                    ins[i + 1].Operand = null;
                                    LogInfo($"[PCMod:{origModName}] Cleared BoolTest.isFalse='WALL' in PhantinelComponent.CoordinateHealth so Second Sentinel moves freely in Phase 1.");
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "LostAndChained.Components.GMSMain" && _assetHelperBridgeType != null)
                {
                    var patchHandSafeMi = _assetHelperBridgeType.GetMethod("LostAndChained_PatchHandSafe", BindingFlags.Static | BindingFlags.Public);
                    if (patchHandSafeMi != null)
                    {
                        var importedPatchHand = mod.ImportReference(patchHandSafeMi);
                        foreach (var md in td.Methods)
                        {
                            if (md.Name == "PatchHand" && md.Parameters.Count == 1 && md.HasBody)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                md.Body.Variables.Clear();
                                md.Body.InitLocals = false;
                                md.Body.MaxStackSize = 4;
                                var il = md.Body.GetILProcessor();
                                il.Append(il.Create(OpCodes.Ldarg_0));
                                il.Append(il.Create(OpCodes.Ldarg_1));
                                il.Append(il.Create(OpCodes.Call, importedPatchHand));
                                il.Append(il.Create(OpCodes.Ret));
                                LogInfo($"[PCMod:{origModName}] Redirected LostAndChained.Components.GMSMain.PatchHand -> LostAndChained_PatchHandSafe (avoids HybridCLR AllocStackSlot overflow).");
                            }
                        }
                    }
                }
                else if (td.FullName == "PaleAutomaton.PaleAutomatonPlugin")
                {
                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "<Awake>b__30_0" && md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            var il = md.Body.GetILProcessor();
                            for (int k = 0; k < ins.Count; k++)
                                ExpandShortBranchIfNeeded(ins[k]);
                            for (int k = 0; k < ins.Count; k++)
                            {
                                if (ins[k].OpCode == OpCodes.Ldfld && ins[k].Operand is FieldReference pfr &&
                                    pfr.Name == "encounteredSongChevalierBoss")
                                {
                                    ins[k].OpCode = OpCodes.Pop;
                                    ins[k].Operand = null;
                                    il.InsertAfter(ins[k], il.Create(OpCodes.Ldc_I4_1));
                                    k++;
                                    LogInfo($"[PCMod:{origModName}] Forced PlayerData.encounteredSongChevalierBoss=true in PaleAutomaton.<Awake>b__30_0.");
                                }
                            }
                            if (_assetHelperBridgeType != null)
                            {
                                var safeDesMi = _assetHelperBridgeType.GetMethod("SafeDeactivateAndDestroyLater", BindingFlags.Static | BindingFlags.Public);
                                if (safeDesMi != null)
                                {
                                    var safeDesRef = mod.ImportReference(safeDesMi);
                                    for (int k = 0; k < ins.Count; k++)
                                    {
                                        if (ins[k].OpCode == OpCodes.Call && ins[k].Operand is MethodReference dmr &&
                                            dmr.Name == "Destroy" && dmr.DeclaringType != null && dmr.DeclaringType.FullName == "UnityEngine.Object" &&
                                            dmr.Parameters.Count == 1)
                                        {
                                            ins[k].Operand = safeDesRef;
                                        }
                                    }
                                }
                            }
                            int btIdx = -1;
                            int startCoroIdx = -1;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (btIdx < 0 && ins[i].OpCode == OpCodes.Ldstr && (ins[i].Operand as string) == "Black Thread States")
                                    btIdx = i;
                                if (btIdx >= 0 && ins[i].OpCode == OpCodes.Ldarg_0 &&
                                    i + 2 < ins.Count &&
                                    (ins[i + 1].OpCode == OpCodes.Call || ins[i + 1].OpCode == OpCodes.Callvirt) &&
                                    ins[i + 1].Operand is MethodReference pmr && pmr.Name == "PlaceHornet")
                                {
                                    // In `this.StartCoroutine(this.PlaceHornet())`, ins[i - 1] is the outer `ldarg.0` for `this.StartCoroutine(...)`
                                    // and ins[i] is the inner `ldarg.0` for `this.PlaceHornet()`. Jumping to ins[i] instead of ins[i - 1]
                                    // underflows the IL evaluation stack when StartCoroutine(IEnumerator) is called!
                                    startCoroIdx = (i > 0 && ins[i - 1].OpCode == OpCodes.Ldarg_0) ? (i - 1) : i;
                                    break;
                                }
                            }
                            if (btIdx >= 0 && startCoroIdx > btIdx && _assetHelperBridgeType != null)
                            {
                                var prepMi = _assetHelperBridgeType.GetMethod("PaleAutomaton_PrepareArborium11Objects", BindingFlags.Static | BindingFlags.Public);
                                if (prepMi != null)
                                {
                                    VariableDefinition goVar = null;
                                    for (int v = 0; v < md.Body.Variables.Count; v++)
                                    {
                                        var vd = md.Body.Variables[v];
                                        if (vd != null && vd.VariableType != null && vd.VariableType.FullName == "UnityEngine.GameObject")
                                            goVar = vd;
                                    }
                                    if (goVar != null)
                                    {
                                        ins[btIdx].OpCode = OpCodes.Ldloc;
                                        ins[btIdx].Operand = goVar;
                                    }
                                    else
                                    {
                                        ins[btIdx].OpCode = OpCodes.Ldnull;
                                        ins[btIdx].Operand = null;
                                    }
                                    il.InsertAfter(ins[btIdx], il.Create(OpCodes.Br, ins[startCoroIdx]));
                                    il.InsertAfter(ins[btIdx], il.Create(OpCodes.Call, mod.ImportReference(prepMi)));
                                    LogInfo($"[PCMod:{origModName}] Routed PaleAutomaton.<Awake>b__30_0 Arborium_11 scene object setup through stack-balanced PaleAutomaton_PrepareArborium11Objects -> PlaceHornet/SpawnSongKnight.");
                                }
                            }
                        }
                    }
                    foreach (var nt in td.NestedTypes)
                    {
                        if (nt.Name.StartsWith("<PlaceHornet>d__", StringComparison.Ordinal))
                        {
                            foreach (var nmd in nt.Methods)
                            {
                                if (nmd.Name == "MoveNext" && nmd.HasBody)
                                {
                                    var ins = nmd.Body.Instructions;
                                    var il = nmd.Body.GetILProcessor();
                                    for (int i = 0; i + 2 < ins.Count; i++)
                                    {
                                        if (ins[i].OpCode == OpCodes.Ldsfld &&
                                            ins[i].Operand is FieldReference fr &&
                                            fr.Name == "songKnight" &&
                                            ins[i + 1].OpCode == OpCodes.Ldc_I4_1 &&
                                            (ins[i + 2].OpCode == OpCodes.Call || ins[i + 2].OpCode == OpCodes.Callvirt) &&
                                            ins[i + 2].Operand is MethodReference gmr &&
                                            gmr.Name == "GetComponentsInChildren")
                                        {
                                            // Replace with AssetHelperBridge.GetComponentsInChildrenSafe<tk2dBaseSprite>(songKnight) or empty array if null
                                            for (int k = 0; k < ins.Count; k++)
                                                ExpandShortBranchIfNeeded(ins[k]);
                                            var afterCall = ins[i + 3];
                                            var notNullTarget = ins[i + 1];
                                            var emptyArrMethod = new GenericInstanceMethod(new MethodReference("Empty", new GenericParameter("T", new MethodReference("Empty", mod.TypeSystem.Void)), new TypeReference("System", "Array", mod, mod.TypeSystem.CoreLibrary)) { HasThis = false });
                                            if (gmr is GenericInstanceMethod origGim && origGim.GenericArguments.Count == 1)
                                            {
                                                var elemType = origGim.GenericArguments[0];
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Dup));
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Brtrue, notNullTarget));
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Pop));
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Ldc_I4_0));
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Newarr, elemType));
                                                il.InsertBefore(notNullTarget, il.Create(OpCodes.Br, afterCall));
                                                LogInfo($"[PCMod:{origModName}] Added forward-ordered null-guard for songKnight in PaleAutomaton.<PlaceHornet>d__.MoveNext.");
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "PaleAutomaton.Helpers" && _assetHelperBridgeType != null)
                {
                    var safeClampsMi = _assetHelperBridgeType.GetMethod("PaleAutomaton_SafeUpdateSaveHeroClamps", BindingFlags.Static | BindingFlags.Public);
                    if (safeClampsMi != null)
                    {
                        foreach (var md in td.Methods)
                        {
                            if (md.Name == "UpdateSaveHeroClamps" && md.Parameters.Count == 0 && md.HasBody)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                var il = md.Body.GetILProcessor();
                                il.Emit(OpCodes.Call, mod.ImportReference(safeClampsMi));
                                il.Emit(OpCodes.Ret);
                                LogInfo($"[PCMod:{origModName}] Redirected PaleAutomaton.Helpers.UpdateSaveHeroClamps -> PaleAutomaton_SafeUpdateSaveHeroClamps.");
                            }
                        }
                    }
                }
                else if (td.FullName == "FinalJudge" || td.Name == "FinalJudge")
                {
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "CacheBaselineContactDamagers" && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.GetILProcessor().Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Stubbed FinalJudge.CacheBaselineContactDamagers to prevent active Spin hitbox from being forced active forever.");
                        }
                        else if (md.Name == "OnFsmStateEntered" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var stopSpinMi = _assetHelperBridgeType.GetMethod("EnsureFinalJudgeSpinEffectsStopped", BindingFlags.Static | BindingFlags.Public);
                            if (stopSpinMi != null && md.Body.Instructions.Count > 0)
                            {
                                var il = md.Body.GetILProcessor();
                                var first = md.Body.Instructions[0];
                                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                                il.InsertBefore(first, il.Create(OpCodes.Call, mod.ImportReference(stopSpinMi)));
                                LogInfo($"[PCMod:{origModName}] Injected EnsureFinalJudgeSpinEffectsStopped into FinalJudge.OnFsmStateEntered.");
                            }
                        }
                    }
                }
                else if (td.FullName == "PharloomFishing.CatchFishPopup")
                {
                    var fishSpriteField = td.Fields.FirstOrDefault(f => f.Name == "_fishSprite");
                    if (fishSpriteField != null)
                    {
                        // 1. Synthesize public override Sprite GetPopupIcon() => this._fishSprite; (overrides SavedItem.GetPopupIcon for IL2CPP CollectableUIMsg.Spawn!)
                        if (!td.Methods.Any(m => m.Name == "GetPopupIcon" && m.Parameters.Count == 0))
                        {
                            var getPopupIconMd = new MethodDefinition(
                                "GetPopupIcon",
                                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.ReuseSlot,
                                fishSpriteField.FieldType);
                            var gil = getPopupIconMd.Body.GetILProcessor();
                            gil.Emit(OpCodes.Ldarg_0);
                            gil.Emit(OpCodes.Ldfld, fishSpriteField);
                            gil.Emit(OpCodes.Ret);
                            td.Methods.Add(getPopupIconMd);
                            LogInfo($"[PCMod:{origModName}] Synthesized CatchFishPopup.GetPopupIcon() override returning _fishSprite for IL2CPP CollectableUIMsg.");
                        }

                        // 2. Also call AssetHelperBridge.AssignCatchFishPopupSprite(msg, this._fishSprite) inside ShowPopup right after CollectableUIMsg.Spawn
                        var showPopupMd = td.Methods.FirstOrDefault(m => m.Name == "ShowPopup" && m.HasBody);
                        if (showPopupMd != null)
                        {
                            var bridgeType = new TypeReference("SilksongMobileAPI", "AssetHelperBridge", mod, mobileApiRef);
                            var assignSpriteMi = new MethodReference("AssignCatchFishPopupSprite", mod.TypeSystem.Void, bridgeType) { HasThis = false };
                            assignSpriteMi.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                            assignSpriteMi.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                            var importedAssign = mod.ImportReference(assignSpriteMi);

                            var ins = showPopupMd.Body.Instructions;
                            var il = showPopupMd.Body.GetILProcessor();
                            for (int i = 0; i + 1 < ins.Count; i++)
                            {
                                if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                                    ins[i].Operand is MethodReference mr &&
                                    mr.Name == "Spawn" &&
                                    ins[i + 1].OpCode == OpCodes.Stloc_0)
                                {
                                    for (int k = 0; k < ins.Count; k++)
                                        ExpandShortBranchIfNeeded(ins[k]);
                                    var afterStloc = ins[i + 2];
                                    il.InsertBefore(afterStloc, il.Create(OpCodes.Ldloc_0));
                                    il.InsertBefore(afterStloc, il.Create(OpCodes.Ldarg_0));
                                    il.InsertBefore(afterStloc, il.Create(OpCodes.Ldfld, fishSpriteField));
                                    il.InsertBefore(afterStloc, il.Create(OpCodes.Call, importedAssign));
                                    LogInfo($"[PCMod:{origModName}] Injected AssignCatchFishPopupSprite in CatchFishPopup.ShowPopup.");
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "PharloomFishing.DeactivateIfModDataFalse")
                {
                    foreach (var mName in new[] { "Start", "OnEnable", "ForceEvaluate" })
                    {
                        var mdPin = td.Methods.FirstOrDefault(m => m.Name == mName && m.HasBody);
                        if (mdPin != null)
                        {
                            mdPin.Body.Variables.Clear();
                            mdPin.Body.ExceptionHandlers.Clear();
                            mdPin.Body.Instructions.Clear();
                            mdPin.Body.GetILProcessor().Emit(OpCodes.Ret);
                        }
                    }
                    LogInfo($"[PCMod:{origModName}] Kept PharloomFishing MapPin active in DeactivateIfModDataFalse (Start/OnEnable/ForceEvaluate).");
                }
                else if (td.FullName == "PharloomFishing.SaveData")
                {
                    var ctorMd = td.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.HasBody);
                    if (ctorMd != null)
                    {
                        var ilSd = ctorMd.Body.GetILProcessor();
                        var retIns = ctorMd.Body.Instructions.LastOrDefault(x => x.OpCode == OpCodes.Ret);
                        if (retIns != null)
                        {
                            foreach (var fName in new[] { "HasFishingKit", "HasSeenFishingKit", "HasFishingSpotPins" })
                            {
                                var fDef = td.Fields.FirstOrDefault(f => f.Name == fName);
                                if (fDef != null)
                                {
                                    ilSd.InsertBefore(retIns, ilSd.Create(OpCodes.Ldarg_0));
                                    ilSd.InsertBefore(retIns, ilSd.Create(OpCodes.Ldc_I4_1));
                                    ilSd.InsertBefore(retIns, ilSd.Create(OpCodes.Stfld, fDef));
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "PharloomFishing.SaveManager")
                {
                    var cctorMd = td.Methods.FirstOrDefault(m => m.Name == ".cctor" && m.HasBody);
                    if (cctorMd != null)
                    {
                        // Replace DesktopPlatform cast in SaveManager..cctor with Android-safe initialization
                        var sdType = mod.Types.FirstOrDefault(t => t.FullName == "PharloomFishing.SaveData");
                        var sdCtor = sdType?.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                        var curSlotField = td.Fields.FirstOrDefault(f => f.Name == "CurrentSlotData");
                        var modSavePathField = td.Fields.FirstOrDefault(f => f.Name == "ModSavePath");
                        if (sdCtor != null && curSlotField != null)
                        {
                            cctorMd.Body.Variables.Clear();
                            cctorMd.Body.ExceptionHandlers.Clear();
                            cctorMd.Body.Instructions.Clear();
                            var ilSm = cctorMd.Body.GetILProcessor();
                            ilSm.Emit(OpCodes.Newobj, sdCtor);
                            ilSm.Emit(OpCodes.Stsfld, curSlotField);
                            if (modSavePathField != null)
                            {
                                var getPersPath = ResolveRuntimeType("UnityEngine.Application")?.GetMethod("get_persistentDataPath", BindingFlags.Static | BindingFlags.Public);
                                if (getPersPath != null)
                                {
                                    ilSm.Emit(OpCodes.Call, mod.ImportReference(getPersPath));
                                    ilSm.Emit(OpCodes.Stsfld, modSavePathField);
                                }
                            }
                            ilSm.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Rewrote PharloomFishing.SaveManager..cctor for Android Platform compatibility.");
                        }
                    }
                }
                else if (td.FullName == "WidowSceneLoader" ||
                         td.FullName == "SilkGoddess.AssetManager" ||
                         td.FullName == "AnySilkBoss.Source.Managers.AssetManager")
                {
                    foreach (var md in td.Methods)
                    {
                        if ((md.Name == ".cctor" || md.Name == "GetAssetRoot" || md.Name == "GetBundleRootFolder") && md.HasBody)
                        {
                            foreach (var ins in md.Body.Instructions)
                            {
                                if (ins.OpCode == OpCodes.Ldstr)
                                {
                                    string s = ins.Operand as string;
                                    if (s == "" || s == "StandaloneWindows64")
                                        ins.Operand = "Android";
                                }
                            }
                        }
                    }
                }
                else if (td.FullName == "CustomFont.FontAssetBuilder")
                {
                    var shimBuilderType = new TypeReference("CustomFont", "FontAssetBuilder", mod, mobileApiRef);
                    var buildMi = new MethodReference("Build", new TypeReference("TMProOld", "TMP_FontAsset", mod, mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Assembly-CSharp") ?? mod.TypeSystem.CoreLibrary), shimBuilderType) { HasThis = false };
                    buildMi.Parameters.Add(new ParameterDefinition(new TypeReference("UnityEngine", "Font", mod, mod.AssemblyReferences.FirstOrDefault(r => r.Name == "UnityEngine.TextRenderingModule") ?? mod.TypeSystem.CoreLibrary)));
                    buildMi.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
                    var getFontMi = td.Methods.FirstOrDefault(m => m.Name == "get_Font" && m.Parameters.Count == 0);
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "Create" && md.Parameters.Count == 0 && getFontMi != null)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var ilCf = md.Body.GetILProcessor();
                            ilCf.Emit(OpCodes.Ldarg_0);
                            ilCf.Emit(OpCodes.Call, getFontMi);
                            ilCf.Emit(OpCodes.Ldnull);
                            ilCf.Emit(OpCodes.Call, mod.ImportReference(buildMi));
                            ilCf.Emit(OpCodes.Ret);
                        }
                        else if (md.Name == "AddChars" || md.Name == "NewToOld" || md.Name.StartsWith("<Create>", StringComparison.Ordinal))
                        {
                            StubMethodWithDefaultReturn(md);
                        }
                    }
                    LogInfo($"[PCMod:{origModName}] Bridged CustomFont.FontAssetBuilder to mobile TMP_FontAsset fallback without stripped TextCore types.");
                }
                else if (td.FullName == "SSMP.Ui.UiManager")
                {
                    var screenRatioField = td.Fields.FirstOrDefault(f => f.Name == "ScreenHeightRatio");
                    var chatBoxField = td.Fields.FirstOrDefault(f => f.Name == "InternalChatBox");
                    var hbType = new TypeReference("SilksongMobileAPI", "HarmonyBridge", mod, mobileApiRef);
                    var cfgBtnRef = mod.ImportReference(new MethodReference("ConfigureSSMPMultiplayerButton", mod.TypeSystem.Void, hbType) { HasThis = false });
                    cfgBtnRef.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                    cfgBtnRef.Parameters.Add(new ParameterDefinition(mod.ImportReference(ResolveRuntimeType("UnityEngine.GameObject"))));

                    foreach (var md in td.Methods)
                    {
                        if (md.Name == ".cctor" && md.HasBody && screenRatioField != null)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldc_R4, 1.0f);
                            il.Emit(OpCodes.Stsfld, screenRatioField);
                            if (chatBoxField != null)
                            {
                                il.Emit(OpCodes.Ldnull);
                                il.Emit(OpCodes.Stsfld, chatBoxField);
                            }
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Normalized SSMP.Ui.UiManager.ScreenHeightRatio -> 1.0f for 1920x1080 CanvasScaler.");
                        }
                        else if (md.Name == "ConfigureButtonTriggers" && md.HasBody && md.Parameters.Count == 1)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldarg_1);
                            il.Emit(OpCodes.Call, cfgBtnRef);
                            il.Emit(OpCodes.Ret);
                        }
                    }
                }
                else if (td.FullName == "SSMP.Ui.ConnectInterface")
                {
                    var hbType = new TypeReference("SilksongMobileAPI", "HarmonyBridge", mod, mobileApiRef);
                    var fixLayoutRef = mod.ImportReference(new MethodReference("FixSSMPConnectInterfaceLayout", mod.TypeSystem.Void, hbType) { HasThis = false });
                    fixLayoutRef.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                    foreach (var md in td.Methods)
                    {
                        if ((md.Name == "FinalizeLayout" || md.Name == "SetMenuActive") && md.HasBody)
                        {
                            var il = md.Body.GetILProcessor();
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++) ExpandShortBranchIfNeeded(ins[i]);
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ret)
                                {
                                    ins[i].OpCode = OpCodes.Ldarg_0;
                                    ins[i].Operand = null;
                                    var callFix = il.Create(OpCodes.Call, fixLayoutRef);
                                    var retIns = il.Create(OpCodes.Ret);
                                    il.InsertAfter(ins[i], callFix);
                                    il.InsertAfter(callFix, retIns);
                                    i += 2;
                                }
                            }
                        }
                    }
                    var isMmsReadyField = td.Fields.FirstOrDefault(f => f.Name == "_isMatchmakingReady");
                    var isCheckingField = td.Fields.FirstOrDefault(f => f.Name == "_isCheckingMatchmakingVersion");
                    var isBlockedField = td.Fields.FirstOrDefault(f => f.Name == "_isMatchmakingVersionBlocked");
                    var activeTabField = td.Fields.FirstOrDefault(f => f.Name == "_activeTab");
                    var switchTabMi = td.Methods.FirstOrDefault(m => m.Name == "SwitchTab" && m.Parameters.Count == 1);
                    var onStartBtnMi = td.Methods.FirstOrDefault(m => m.Name == "OnStartButtonPressed" && m.Parameters.Count == 0);

                    foreach (var md in td.Methods)
                    {
                        if (md.Name == "BeginMatchmakingVersionCheck" && md.HasBody && isMmsReadyField != null)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            if (isCheckingField != null)
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Stfld, isCheckingField);
                            }
                            if (isBlockedField != null)
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Stfld, isBlockedField);
                            }
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Stfld, isMmsReadyField);
                            if (switchTabMi != null && activeTabField != null)
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, activeTabField);
                                il.Emit(OpCodes.Call, switchTabMi);
                            }
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Patched SSMP ConnectInterface.BeginMatchmakingVersionCheck -> ready & active on Android.");
                        }
                        else if (md.Name == "CreateMatchmakingLobbyWithConfig" && md.HasBody && onStartBtnMi != null)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Call, onStartBtnMi);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Routed SSMP CreateMatchmakingLobbyWithConfig -> OnStartButtonPressed for direct mobile hosting.");
                        }
                        else if (md.Name == "TryParsePort" && md.HasBody && md.Parameters.Count == 2)
                        {
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            md.Body.Instructions.Clear();
                            var il = md.Body.GetILProcessor();
                            var tryParseMi = ResolveRuntimeType("System.Int32")?.GetMethod("TryParse", new[] { typeof(string), typeof(int).MakeByRefType() });
                            var fallbackLabel = il.Create(OpCodes.Ldarg_1);
                            if (tryParseMi != null)
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(tryParseMi));
                                il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Ldind_I4);
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Ble_S, fallbackLabel);
                                il.Emit(OpCodes.Ldc_I4_1);
                                il.Emit(OpCodes.Ret);
                            }
                            il.Append(fallbackLabel);
                            il.Emit(OpCodes.Ldc_I4, 26950);
                            il.Emit(OpCodes.Stind_I4);
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Ret);
                            LogInfo($"[PCMod:{origModName}] Patched SSMP TryParsePort to default empty/invalid port to 26950 on Android.");
                        }
                    }
                }
                // B3. SsmpVoiceChat: prevent OpenALErrorWarning from destroying Main Menu UI and prevent x86_64 native dlopen crash on Android
                else if (td.FullName == "SsmpVoiceChat.VoiceChatMod" || td.FullName == "SsmpVoiceChat.Native.LibraryLoader" || td.FullName == "SsmpVoiceChat.VoiceChatClientAddon")
                {
                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        if (md.Name == "OpenALErrorWarning" || md.Name == "LoadNativeLibrary" || md.Name == "Awake" || (td.Name == "VoiceChatClientAddon" && md.Name == "Initialize"))
                        {
                            if (md.ReturnType.FullName == "System.Void")
                            {
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                md.Body.Instructions.Clear();
                                md.Body.GetILProcessor().Emit(OpCodes.Ret);
                                LogInfo($"[PCMod:{origModName}] Safely stubbed desktop-only native voice method {td.Name}.{md.Name} on Android.");
                            }
                        }
                    }
                }
                // B4. BouncyCastle.Cryptography: remove unused DotNetUtilities ECParameters/ECDsa/ECCurve/ECPoint methods (stripped from Android netstandard 2.0)
                else if (td.FullName == "Org.BouncyCastle.Security.DotNetUtilities")
                {
                    foreach (var md in td.Methods.ToArray())
                    {
                        if (md.Name == "GetECDsaKeyPair" || md.Name == "GetECDsaPublicKey" ||
                            md.Name == "GetECKeyPair" || md.Name == "GetECPublicKey" ||
                            md.Name == "GetECPoint" || md.Name == "GetX9ECParameters")
                        {
                            td.Methods.Remove(md);
                        }
                        else if (md.Name == "GetKeyPair" && md.Parameters.Count == 1 &&
                                 md.Parameters[0].ParameterType.FullName == "System.Security.Cryptography.AsymmetricAlgorithm" &&
                                 md.HasBody)
                        {
                            var ins = md.Body.Instructions;
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Isinst &&
                                    ins[i].Operand is TypeReference tr &&
                                    tr.FullName == "System.Security.Cryptography.ECDsa")
                                {
                                    ins[i].OpCode = OpCodes.Pop;
                                    ins[i].Operand = null;
                                    md.Body.GetILProcessor().InsertAfter(ins[i], Instruction.Create(OpCodes.Ldnull));
                                    i++;
                                }
                                else if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                                         ins[i].Operand is MethodReference mr &&
                                         mr.Name == "GetECDsaKeyPair")
                                {
                                    ins[i].OpCode = OpCodes.Pop;
                                    ins[i].Operand = null;
                                    md.Body.GetILProcessor().InsertAfter(ins[i], Instruction.Create(OpCodes.Ldnull));
                                    i++;
                                }
                            }
                        }
                    }
                    LogInfo($"[PCMod:{origModName}] Sanitized DotNetUtilities stripped ECParameters/ECDsa methods for Android runtime.");
                }
                // B5. SSMP: sanitize MmsUtilities ArrayBufferWriter<byte> & MemoryExtensions.AsSpan (absent from Android netstandard 2.0)
                else if (td.FullName == "SSMP.Networking.Matchmaking.Utilities.MmsUtilities")
                {
                    foreach (var md in td.Methods.ToArray())
                    {
                        if (md.Name == "AppendFrame")
                        {
                            td.Methods.Remove(md);
                        }
                    }
                    foreach (var nt in td.NestedTypes)
                    {
                        foreach (var fd in nt.Fields)
                        {
                            if (fd.FieldType != null && fd.FieldType.FullName.Contains("ArrayBufferWriter"))
                                fd.FieldType = mod.TypeSystem.Object;
                        }
                        foreach (var md in nt.Methods)
                        {
                            if (md.Name == "MoveNext" && md.HasBody)
                            {
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                md.Body.Instructions.Clear();
                                var il = md.Body.GetILProcessor();
                                if (md.ReturnType.FullName == "System.Boolean")
                                    il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Ret);
                            }
                        }
                    }
                    LogInfo($"[PCMod:{origModName}] Sanitized MmsUtilities ArrayBufferWriter<byte> for Android netstandard 2.0 runtime.");
                }
                // B6. RevoltAndDevotion: synchronize Widow & First Weaver wakeup and bundle pinning
                else if (td.FullName == "RevoltAndDevotion.RevoltAndDevotionPlugin")
                {
                    var bridgeType = new TypeReference("SilksongMobileAPI", "AssetHelperBridge", mod, mobileApiRef);
                    var syncMethod = mod.ImportReference(new MethodReference("EnsureRevoltAndDevotionWidowSynced", mod.TypeSystem.Void, bridgeType) { HasThis = false });
                    var resetMethod = mod.ImportReference(new MethodReference("ResetRevoltAndDevotionState", mod.TypeSystem.Void, bridgeType) { HasThis = false });

                    foreach (var md in td.Methods)
                    {
                        if (!md.HasBody) continue;
                        var il = md.Body.GetILProcessor();
                        var ins = md.Body.Instructions;
                        if (md.Name == "SetupWidow")
                        {
                            for (int i = 0; i < ins.Count; i++)
                                ExpandShortBranchIfNeeded(ins[i]);
                            for (int i = 0; i < ins.Count; i++)
                            {
                                if (ins[i].OpCode == OpCodes.Ret)
                                {
                                    ins[i].OpCode = OpCodes.Call;
                                    ins[i].Operand = syncMethod;
                                    il.InsertAfter(ins[i], il.Create(OpCodes.Ret));
                                    i++;
                                }
                            }
                        }
                        else if (md.Name == "LateUpdate" && ins.Count > 0)
                        {
                            for (int i = 0; i < ins.Count; i++)
                                ExpandShortBranchIfNeeded(ins[i]);
                            il.InsertBefore(ins[0], il.Create(OpCodes.Call, syncMethod));
                        }
                        else if (md.Name == "DeathReset" && ins.Count > 0)
                        {
                            for (int i = 0; i < ins.Count; i++)
                                ExpandShortBranchIfNeeded(ins[i]);
                            il.InsertBefore(ins[0], il.Create(OpCodes.Call, resetMethod));
                        }
                        else if (md.Name == "OnFsmStateEntered")
                        {
                            md.CustomAttributes.Clear();
                            md.Body.Instructions.Clear();
                            md.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                            LogInfo($"[PCMod:{origModName}] Neutralized global FsmState.OnEnter patch in RevoltAndDevotionPlugin to eliminate combat frame stutter.");
                        }
                    }
                    // Keep RevoltAndDevotionPlugin.<Start>d__ intact
                    LogInfo($"[PCMod:{origModName}] Injected RevoltAndDevotion Widow wakeup & state synchronization hooks.");
                }
            }
        }

        private static void StubOutILManipulatorsAndTranspilers(string modName, ModuleDefinition mod, AssemblyNameReference mobileApiRef)
        {
            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                // Check if td is a compiler-generated <Transpiler>d__ state machine implementing IEnumerator<CodeInstruction>
                bool isTranspilerStateMachine = td.Interfaces.Any(iface =>
                    iface.InterfaceType != null &&
                    (iface.InterfaceType.FullName.Contains("HarmonyLib.CodeInstruction") ||
                     iface.InterfaceType.FullName.Contains("MonoMod.Cil.") ||
                     iface.InterfaceType.FullName.Contains("MonoDetour.Cil.")));

                foreach (var fd in td.Fields.ToArray())
                {
                    string ftName = fd.FieldType?.FullName ?? "";
                    if (isTranspilerStateMachine &&
                        !fd.Name.StartsWith("<>1__state", StringComparison.Ordinal) &&
                        !fd.Name.StartsWith("<>2__current", StringComparison.Ordinal) &&
                        !fd.Name.StartsWith("<>l__initialThreadId", StringComparison.Ordinal))
                    {
                        fd.FieldType = mod.TypeSystem.Object;
                    }
                    else if (ftName.Contains("MonoMod.Cil.") || ftName.Contains("Mono.Cecil.Cil.") ||
                             ftName.Contains("MonoDetour.Cil."))
                    {
                        fd.FieldType = mod.TypeSystem.Object;
                    }
                }

                foreach (var pd in td.Properties.ToArray())
                {
                    string propertyType = pd.PropertyType?.FullName ?? "";
                    if (propertyType.Contains("MonoDetour.Cil."))
                        pd.PropertyType = mod.TypeSystem.Object;
                }

                foreach (var ed in td.Events.ToArray())
                {
                    string eventType = ed.EventType?.FullName ?? "";
                    if (eventType.Contains("MonoDetour.Cil."))
                        ed.EventType = mod.TypeSystem.Object;
                }

                foreach (var md in td.Methods.ToArray())
                {
                    bool usesMonoDetourCilSignature =
                        (md.ReturnType?.FullName ?? "").Contains("MonoDetour.Cil.") ||
                        md.Parameters.Any(p => (p.ParameterType?.FullName ?? "").Contains("MonoDetour.Cil."));

                    // MonoDetour's ILWeaver is unavailable on the Android HybridCLR runtime.
                    // Remove its signature types as well as the method body so the CLR does
                    // not try resolving a PC-only MonoDetour.Cil delegate while loading the mod.
                    if (usesMonoDetourCilSignature)
                    {
                        if ((md.ReturnType?.FullName ?? "").Contains("MonoDetour.Cil."))
                            md.ReturnType = mod.TypeSystem.Object;
                        foreach (var p in md.Parameters)
                        {
                            if ((p.ParameterType?.FullName ?? "").Contains("MonoDetour.Cil."))
                                p.ParameterType = mod.TypeSystem.Object;
                        }
                    }

                    if (!md.HasBody) continue;

                    bool isManip = usesMonoDetourCilSignature ||
                                   md.CustomAttributes.Any(ca => ca.AttributeType.Name == "HarmonyILManipulator") ||
                                   md.Parameters.Any(p => p.ParameterType.FullName.Contains("MonoMod.Cil.") ||
                                                          p.ParameterType.FullName.Contains("Mono.Cecil.Cil."));
                    bool isTranspiler = md.CustomAttributes.Any(ca => ca.AttributeType.Name == "HarmonyTranspiler") ||
                                        md.Parameters.Any(p => p.ParameterType.FullName.Contains("System.Reflection.Emit.ILGenerator") ||
                                                               p.ParameterType.FullName.Contains("HarmonyLib.CodeInstruction") ||
                                                               p.ParameterType.FullName.Contains("HarmonyLib.CodeMatcher")) ||
                                        md.ReturnType.FullName.Contains("HarmonyLib.CodeInstruction") ||
                                        md.ReturnType.FullName.Contains("HarmonyLib.CodeMatcher") ||
                                        (isTranspilerStateMachine && md.Name == "MoveNext");
                    bool isModMenuMethod = md.Name == "BuildCustomMenu" ||
                                           md.ReturnType.FullName.StartsWith("Silksong.ModMenu.", StringComparison.Ordinal) ||
                                           md.Parameters.Any(p => p.ParameterType.FullName.StartsWith("Silksong.ModMenu.", StringComparison.Ordinal)) ||
                                           (md.HasBody && md.Body.Variables.Any(v => v.VariableType.FullName.StartsWith("Silksong.ModMenu.", StringComparison.Ordinal) ||
                                                                                     v.VariableType.FullName.Contains("HarmonyLib.CodeMatcher")));

                    if (!isManip && !isTranspiler && !isModMenuMethod) continue;

                    for (int i = 0; i < md.Parameters.Count; i++)
                    {
                        var p = md.Parameters[i];
                        if (p.ParameterType.FullName == "MonoMod.Cil.ILContext")
                        {
                            p.ParameterType = new TypeReference("MonoMod.Cil", "ILContext", mod, mobileApiRef);
                        }
                        else if (p.ParameterType.FullName == "System.Reflection.Emit.ILGenerator")
                        {
                            p.ParameterType = new TypeReference("HarmonyLib", "ILGenerator", mod, mobileApiRef);
                        }
                    }

                    md.Body.Instructions.Clear();
                    md.Body.Variables.Clear();
                    md.Body.ExceptionHandlers.Clear();
                    var il = md.Body.GetILProcessor();
                    if (md.ReturnType.FullName == "System.Void")
                    {
                        il.Emit(OpCodes.Ret);
                    }
                    else if (md.ReturnType.FullName == "System.Boolean")
                    {
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                    }
                    else if (md.Parameters.Count > 0 && md.Parameters[0].ParameterType.FullName == md.ReturnType.FullName)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ret);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Ret);
                    }
                }
            }
        }

        private static Dictionary<string, byte[]> ExtractAndRegisterEmbeddedResources(string modName, ModuleDefinition mod)
        {
            if (!mod.HasResources || _registerResourcesMethod == null) return null;
            var dict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var res in mod.Resources)
            {
                if (res is EmbeddedResource er)
                {
                    try
                    {
                        dict[er.Name] = er.GetResourceData();
                    }
                    catch { }
                }
            }
            if (dict.Count > 0)
            {
                _registerResourcesMethod.Invoke(null, new object[] { modName, dict });
                if (modName != null && modName.IndexOf("WeaverCrest", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mod.Resources.Clear();
                }
                return dict;
            }
            return null;
        }

        private static bool IsShimmedTypeFullName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return false;
            switch (fullName)
            {
                case "BepInEx.BepInDependency":
                case "BepInEx.BepInDependency/DependencyFlags":
                case "BepInEx.DependencyFlags":
                case "BepInEx.BepInProcess":
                case "BepInEx.BepInIncompatibility":
                case "BepInEx.BepInAutoPluginAttribute":
                case "BepInEx.Preloader.Core.Patching.PatcherAutoPluginAttribute":
                case "BepInEx.IInputSystem":
                case "BepInEx.UnityInput":
                case "BepInEx.ThreadingHelper":
                case "BepInEx.Configuration.KeyboardShortcut":
                case "BepInEx.Bootstrap.Chainloader":
                case "BepInEx.Harmony.HarmonyWrapper":
                case "HarmonyLib.FieldRef`1":
                case "HarmonyLib.AccessTools/FieldRef`1":
                case "HarmonyLib.FieldRef`2":
                case "HarmonyLib.AccessTools/FieldRef`2":
                case "HarmonyLib.HarmonyReversePatchType":
                case "HarmonyLib.ExceptionBlockType":
                case "HarmonyLib.ExceptionBlock":
                case "HarmonyLib.Transpilers":
                case "HarmonyLib.PatchClassProcessor":
                case "HarmonyLib.Label":
                case "HarmonyLib.OpCode":
                case "HarmonyLib.OpCodes":
                case "HarmonyLib.ILGenerator":
                case "HarmonyLib.LocalBuilder":
                case "HarmonyLib.MethodType":
                case "HarmonyLib.ArgumentType":
                case "HarmonyLib.Priority":
                case "HarmonyLib.HarmonyTranspiler":
                case "HarmonyLib.HarmonyFinalizer":
                case "HarmonyLib.HarmonyILManipulator":
                case "HarmonyLib.HarmonyCleanup":
                case "HarmonyLib.HarmonyPrepare":
                case "HarmonyLib.HarmonyTargetMethod":
                case "HarmonyLib.HarmonyTargetMethods":
                case "HarmonyLib.HarmonyArgument":
                case "HarmonyLib.HarmonyPriority":
                case "HarmonyLib.HarmonyBefore":
                case "HarmonyLib.HarmonyAfter":
                case "HarmonyLib.HarmonyReversePatch":
                case "HarmonyLib.CodeInstruction":
                case "HarmonyLib.CodeInstructionExtensions":
                case "HarmonyLib.CodeMatch":
                case "HarmonyLib.CodeMatcher":
                case "HarmonyLib.Patch":
                case "HarmonyLib.Patches":
                case "HarmonyLib.PatchProcessor":
                case "HarmonyLib.Traverse":
                case "HarmonyLib.Traverse`1":
                case "Silksong.ModMenu.IModMenuCustomMenu":
                case "UnityEngine.Networking.DownloadedTextureParams":
                case "System.Index":
                case "System.Range":
                case "CustomFont.FontAssetBuilder":
                case "HarmonyLib.HarmonyWrapSafe":
                case "HarmonyLib.HarmonyPatchType":
                case "System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute":
                case "Unity.Mathematics.math":
                case "UnityStandardAssets.ImageEffects.FastNoise":
                case "TeamCherry.NestedFadeGroup.NestedFadeGroupTimedFader":
                case "HutongGames.PlayMaker.Actions.FadeNestedFadeGroup":
                case "NAudio.Wave.IWaveProvider":
                case "NAudio.Wave.MediaFoundationReader":
                case "NAudio.Wave.MediaFoundationEncoder":
                case "DanielSteginkUtils.Utilities.ClassIntegrations":
                case "DanielSteginkUtils.Utilities.NotchCosts":
                case "DanielSteginkUtils.ExternalFiles.GetAudioClip":
                case "DanielSteginkUtils.ExternalFiles.GetSprite":
                case "DanielSteginkUtils.Helpers.Needolin":
                case "DanielSteginkUtils.Helpers.DamageEnemy":
                case "DanielSteginkUtils.Helpers.GetEnemy":
                case "DanielSteginkUtils.Helpers.GetTools":
                case "DanielSteginkUtils.Utilities.Components":
                case "System.HashCode":
                case "HutongGames.PlayMaker.Actions.SuperJumpRaycast":
                case "InControl.BindingSourceRejectionType":
                case "InControl.InputDeviceClass":
                case "TMPro.SpriteAssetUtilities.TexturePacker_JsonArray":
                case "HarmonyLib.Tools.HarmonyFileLog":
                case "BepInEx.Bootstrap.TypeLoader":
                case "HutongGames.PlayMaker.Actions.FadeNestedFadeGroupV3":
                case "GenericVariableExtension.VariableExtensionsGeneric":
                case "DebugMod.Hitbox.HitboxRender":
                case "UnityEngine.UIElements.IEnumerableExtensions":
                    return true;
            }
            if (fullName.StartsWith("BepInExUtils.", StringComparison.Ordinal) ||
                fullName == "System.IO.EnumerationOptions" ||
                fullName == "System.IO.MatchCasing" ||
                fullName == "System.IO.MatchType" ||
                fullName == "Silksong.FsmUtil.FsmUtil" ||
                fullName.StartsWith("Silksong.FsmUtil.", StringComparison.Ordinal) ||
                fullName.StartsWith("Silksong.ModMenu.", StringComparison.Ordinal) ||
                fullName.StartsWith("MonoDetour.", StringComparison.Ordinal) ||
                fullName.StartsWith("MonoMod.Cil.", StringComparison.Ordinal) ||
                fullName.StartsWith("MonoMod.RuntimeDetour.", StringComparison.Ordinal) ||
                fullName.StartsWith("MonoMod.Utils.", StringComparison.Ordinal) ||
                fullName.StartsWith("Steamworks.", StringComparison.Ordinal) ||
                fullName.StartsWith("PrepatcherPlugin.", StringComparison.Ordinal) ||
                fullName.StartsWith("Silksong.UnityHelper.", StringComparison.Ordinal) ||
                fullName.StartsWith("System.IO.Enumeration.", StringComparison.Ordinal) ||
                fullName.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal) ||
                fullName.StartsWith("CanvasUtil.", StringComparison.Ordinal) ||
                fullName.StartsWith("WishUtil.", StringComparison.Ordinal) ||
                fullName.StartsWith("SkongGamemodes.", StringComparison.Ordinal) ||
                fullName.StartsWith("TMPro.SpriteAssetUtilities.TexturePacker_JsonArray", StringComparison.Ordinal) ||
                fullName == "UnityEngine.AssetBundleUnloadOperation" ||
                fullName == "UnityEngine.SpriteMask" ||
                fullName == "HarmonyLib.FieldRef`1")
                return true;
            return false;
        }

        private static void RedirectTypeReferenceIfShimmed(TypeReference tref, AssemblyNameReference mobileApiRef)
        {
            if (tref == null) return;
            if (tref.DeclaringType != null)
            {
                if (tref.FullName == "UnityEngine.UI.InputField/EndEditEvent")
                {
                    tref.Name = "SubmitEvent";
                    return;
                }
                if (tref.FullName == "BepInEx.BepInDependency/DependencyFlags")
                {
                    tref.DeclaringType = null;
                    tref.Namespace = "BepInEx";
                    tref.Name = "DependencyFlags";
                    tref.Scope = mobileApiRef;
                    return;
                }
                if (tref.FullName == "HarmonyLib.AccessTools/FieldRef`2" || tref.FullName == "HarmonyLib.AccessTools/FieldRef`1")
                {
                    string frName = tref.Name;
                    tref.DeclaringType = null;
                    tref.Namespace = "HarmonyLib";
                    tref.Name = frName;
                    tref.Scope = mobileApiRef;
                    return;
                }
                if (tref.FullName == "HarmonyLib.AccessTools/FieldRef`1")
                {
                    tref.DeclaringType = null;
                    tref.Namespace = "HarmonyLib";
                    tref.Name = "FieldRef`1";
                    tref.Scope = mobileApiRef;
                    return;
                }
                RedirectTypeReferenceIfShimmed(tref.DeclaringType, mobileApiRef);
                return;
            }

            if (tref.Namespace == "System.Reflection.Emit" &&
                (tref.Name == "Label" || tref.Name == "OpCode" || tref.Name == "OpCodes" ||
                 tref.Name == "ILGenerator" || tref.Name == "LocalBuilder"))
            {
                tref.Namespace = "HarmonyLib";
                tref.Scope = mobileApiRef;
                return;
            }

            if (tref.Namespace == "System" &&
                (tref.Name == "ValueTuple" || tref.Name == "ValueTuple`1" || tref.Name == "ValueTuple`2" ||
                 tref.Name == "ValueTuple`3" || tref.Name == "ValueTuple`4") &&
                (tref.Scope?.Name == "netstandard" || tref.Scope?.Name == "System.ValueTuple"))
            {
                if (typeof(object).Assembly.GetType(tref.FullName.Replace('/', '+'), false) != null)
                {
                    var mscorlibRef = tref.Module?.AssemblyReferences?.FirstOrDefault(r => r.Name == "mscorlib");
                    if (mscorlibRef == null && tref.Module != null)
                    {
                        mscorlibRef = new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0))
                        {
                            PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }
                        };
                        tref.Module.AssemblyReferences.Add(mscorlibRef);
                    }
                    else if (mscorlibRef != null && (mscorlibRef.PublicKeyToken == null || mscorlibRef.PublicKeyToken.Length == 0))
                    {
                        mscorlibRef.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                    }
                    if (mscorlibRef != null)
                    {
                        tref.Scope = mscorlibRef;
                        return;
                    }
                }
            }

            if (tref.Namespace == "InControl" || (tref.Namespace != null && tref.Namespace.StartsWith("InControl.", StringComparison.Ordinal)))
            {
                if (tref.Module != null)
                {
                    var inControlRef = tref.Module.AssemblyReferences.FirstOrDefault(r => r.Name == "InControl");
                    if (inControlRef == null)
                    {
                        inControlRef = new AssemblyNameReference("InControl", new Version(0, 0, 0, 0));
                        tref.Module.AssemblyReferences.Add(inControlRef);
                    }
                    tref.Scope = inControlRef;
                    return;
                }
            }

            if (IsShimmedTypeFullName(tref.FullName))
            {
                if (tref.FullName == "System.IO.EnumerationOptions" ||
                    tref.FullName == "System.IO.MatchCasing" ||
                    tref.FullName == "System.IO.MatchType")
                {
                    tref.Namespace = "SilksongMobileAPI.IO";
                }
                tref.Scope = mobileApiRef;
            }
        }

        private static int ExtractAndEmbedHarmonyPatches(string modName, ModuleDefinition mod, AssemblyNameReference mobileApiRef)
        {
            int count = 0;

            var metaAttrCtor = new MethodReference(
                ".ctor",
                mod.TypeSystem.Void,
                new TypeReference("SilksongMobileAPI", "MobileHarmonyPatchMetaAttribute", mod, mobileApiRef))
            {
                HasThis = true
            };
            metaAttrCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
            metaAttrCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
            metaAttrCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Int32));
            metaAttrCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));

            foreach (var td in GetAllTypesRecursive(mod.Types))
            {
                count += ExtractHarmonyPatchOnProvider(modName, $"{modName}::T:{td.FullName.Replace('/', '+')}", td, mod, metaAttrCtor);
                foreach (var md in td.Methods)
                {
                    count += ExtractHarmonyPatchOnProvider(modName, $"{modName}::M:{td.FullName.Replace('/', '+')}::{md.Name}", md, mod, metaAttrCtor);
                }
            }
            return count;
        }

        private static IEnumerable<TypeDefinition> GetAllTypesRecursive(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types.ToArray())
            {
                yield return t;
                if (t.HasNestedTypes)
                {
                    foreach (var nt in GetAllTypesRecursive(t.NestedTypes.ToArray()))
                        yield return nt;
                }
            }
        }

        private static int ExtractHarmonyPatchOnProvider(
            string modName,
            string memberKey,
            Mono.Cecil.ICustomAttributeProvider provider,
            ModuleDefinition mod,
            MethodReference metaAttrCtor)
        {
            if (!provider.HasCustomAttributes) return 0;
            int extracted = 0;

            if (memberKey.IndexOf("Awakened_Grand_Mother_Silk.Source.Patches.DebugPatches", StringComparison.Ordinal) >= 0)
            {
                foreach (var ca in provider.CustomAttributes.ToArray())
                {
                    if (ca.AttributeType.Name.StartsWith("Harmony", StringComparison.Ordinal))
                        provider.CustomAttributes.Remove(ca);
                }
                return 0;
            }
            foreach (var ca in provider.CustomAttributes.ToArray())
            {
                if (ca.AttributeType.Name != "HarmonyPatch") continue;

                string declTypeName = null;
                string methodName = null;
                int methodTypeInt = -1;
                var argTypeNames = new List<string>();

                try
                {
                    ParseHarmonyPatchFromBlob(ca, ref declTypeName, ref methodName, ref methodTypeInt, argTypeNames);
                }
                catch { }

                if (_extractedInfoType != null && _registerPatchInfoMethod != null)
                {
                    try
                    {
                        var infoObj = Activator.CreateInstance(_extractedInfoType);
                        _extractedInfoType.GetField("DeclaringTypeName")?.SetValue(infoObj, declTypeName);
                        _extractedInfoType.GetField("MethodName")?.SetValue(infoObj, methodName);
                        if (methodTypeInt >= 0)
                        {
                            var mtField = _extractedInfoType.GetField("PatchMethodType");
                            if (mtField != null)
                            {
                                var enumType = Nullable.GetUnderlyingType(mtField.FieldType);
                                if (enumType != null)
                                    mtField.SetValue(infoObj, Enum.ToObject(enumType, methodTypeInt));
                            }
                        }
                        if (argTypeNames.Count > 0)
                            _extractedInfoType.GetField("ArgumentTypeNames")?.SetValue(infoObj, argTypeNames.ToArray());

                        _registerPatchInfoMethod.Invoke(null, new object[] { memberKey, infoObj });
                    }
                    catch { }
                }

                try
                {
                    var metaCa = new CustomAttribute(metaAttrCtor);
                    metaCa.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, declTypeName ?? ""));
                    metaCa.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, methodName ?? ""));
                    metaCa.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.Int32, methodTypeInt));
                    metaCa.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, string.Join("|", argTypeNames)));
                    provider.CustomAttributes.Add(metaCa);
                }
                catch { }

                provider.CustomAttributes.Remove(ca);
                extracted++;
            }

            return extracted;
        }

        private static void ParseHarmonyPatchFromBlob(
            CustomAttribute ca,
            ref string declTypeName,
            ref string methodName,
            ref int methodTypeInt,
            List<string> argTypeNames)
        {
            byte[] blob = ca.GetBlob();
            if (blob == null || blob.Length < 4 || blob[0] != 1 || blob[1] != 0)
                return;

            int pos = 2;
            var ctorParams = ca.Constructor.Parameters;
            for (int i = 0; i < ctorParams.Count && pos < blob.Length; i++)
            {
                var p = ctorParams[i];
                string pFull = p.ParameterType.FullName ?? "";
                string pName = p.ParameterType.Name ?? "";

                if (pFull == "System.Type")
                {
                    string s = ReadSerString(blob, ref pos);
                    if (!string.IsNullOrEmpty(s))
                    {
                        string norm = NormalizeBlobTypeName(s);
                        if (declTypeName == null) declTypeName = norm;
                        else argTypeNames.Add(norm);
                    }
                }
                else if (pFull == "System.String")
                {
                    string s = ReadSerString(blob, ref pos);
                    if (!string.IsNullOrEmpty(s))
                    {
                        if (methodName == null) methodName = s;
                        else if (declTypeName == null) declTypeName = s;
                    }
                }
                else if (pName == "MethodType")
                {
                    if (pos + 4 <= blob.Length)
                    {
                        methodTypeInt = BitConverter.ToInt32(blob, pos);
                        pos += 4;
                    }
                }
                else if (pFull == "System.Type[]")
                {
                    if (pos + 4 <= blob.Length)
                    {
                        int count = BitConverter.ToInt32(blob, pos);
                        pos += 4;
                        if (count == 0)
                        {
                            argTypeNames.Add("__ZERO_ARGS__");
                        }
                        else if (count > 0 && count <= 64)
                        {
                            for (int k = 0; k < count && pos < blob.Length; k++)
                            {
                                string ts = ReadSerString(blob, ref pos);
                                if (!string.IsNullOrEmpty(ts))
                                    argTypeNames.Add(NormalizeBlobTypeName(ts));
                            }
                        }
                    }
                }
                else if (pName == "ArgumentType[]")
                {
                    if (pos + 4 <= blob.Length)
                    {
                        int count = BitConverter.ToInt32(blob, pos);
                        pos += 4;
                        if (count > 0 && count <= 64 && pos + count * 4 <= blob.Length)
                            pos += count * 4;
                    }
                }
            }
        }

        private static string NormalizeBlobTypeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            int comma = raw.IndexOf(',');
            string clean = comma > 0 ? raw.Substring(0, comma).Trim() : raw.Trim();
            return clean.Replace('/', '+');
        }

        private static string ReadSerString(byte[] blob, ref int pos)
        {
            if (pos >= blob.Length) return null;
            if (blob[pos] == 0xFF)
            {
                pos++;
                return null;
            }
            int len = ReadCompressedUInt32(blob, ref pos);
            if (len <= 0 || pos + len > blob.Length) return "";
            string str = Encoding.UTF8.GetString(blob, pos, len);
            pos += len;
            return str;
        }

        private static int ReadCompressedUInt32(byte[] blob, ref int pos)
        {
            byte b0 = blob[pos++];
            if ((b0 & 0x80) == 0)
                return b0;
            if ((b0 & 0xC0) == 0x80)
            {
                if (pos >= blob.Length) return 0;
                byte b1 = blob[pos++];
                return ((b0 & 0x3F) << 8) | b1;
            }
            if (pos + 2 >= blob.Length) return 0;
            byte c1 = blob[pos++];
            byte c2 = blob[pos++];
            byte c3 = blob[pos++];
            return ((b0 & 0x1F) << 24) | (c1 << 16) | (c2 << 8) | c3;
        }

        private static void SanitizeTypeSpecAndSignature(TypeReference tr, AssemblyNameReference mobileApiRef)
        {
            if (tr == null) return;
            if (tr is GenericInstanceType git)
            {
                RedirectTypeReferenceIfShimmed(git.ElementType, mobileApiRef);
                for (int i = 0; i < git.GenericArguments.Count; i++)
                {
                    SanitizeTypeSpecAndSignature(git.GenericArguments[i], mobileApiRef);
                }
                return;
            }
            if (tr is ByReferenceType brt)
            {
                SanitizeTypeSpecAndSignature(brt.ElementType, mobileApiRef);
                return;
            }
            if (tr is ArrayType at)
            {
                SanitizeTypeSpecAndSignature(at.ElementType, mobileApiRef);
                return;
            }
            RedirectTypeReferenceIfShimmed(tr, mobileApiRef);
        }

        private static int RewriteAllMethodBodies(string modName, ModuleDefinition mod, AssemblyNameReference mobileApiRef)
        {
            int count = 0;
            if (_harmonyBridgeType == null) return 0;

            foreach (var td in GetAllTypesRecursive(mod.Types).ToArray())
            {
                if (td.BaseType != null)
                    SanitizeTypeSpecAndSignature(td.BaseType, mobileApiRef);
                if (td.HasInterfaces)
                {
                    foreach (var iface in td.Interfaces.ToArray())
                        SanitizeTypeSpecAndSignature(iface.InterfaceType, mobileApiRef);
                }
                foreach (var fd in td.Fields.ToArray())
                {
                    SanitizeTypeSpecAndSignature(fd.FieldType, mobileApiRef);
                }
                foreach (var pd in td.Properties.ToArray())
                {
                    SanitizeTypeSpecAndSignature(pd.PropertyType, mobileApiRef);
                }

                foreach (var md in td.Methods.ToArray())
                {
                    SanitizeTypeSpecAndSignature(md.ReturnType, mobileApiRef);
                    foreach (var p in md.Parameters.ToArray())
                        SanitizeTypeSpecAndSignature(p.ParameterType, mobileApiRef);

                    if (!md.HasBody) continue;
                    foreach (var v in md.Body.Variables.ToArray())
                        SanitizeTypeSpecAndSignature(v.VariableType, mobileApiRef);

                    var il = md.Body.GetILProcessor();
                    var instrs = md.Body.Instructions;

                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        if (ins.Operand is TypeReference trOp)
                            SanitizeTypeSpecAndSignature(trOp, mobileApiRef);
                        if (ins.Operand is FieldReference frOp)
                        {
                            if (frOp.DeclaringType != null)
                            {
                                SanitizeTypeSpecAndSignature(frOp.DeclaringType, mobileApiRef);
                                if (frOp.DeclaringType.FullName == "ChangeFontByLanguage" && frOp.Name == "fontZH_TW")
                                {
                                    frOp.Name = "fontZH";
                                    count++;
                                }
                                if (ins.OpCode == OpCodes.Ldsfld && frOp.DeclaringType.FullName == "System.DateTime" && frOp.Name == "UnixEpoch" && _harmonyBridgeType != null)
                                {
                                    var m = _harmonyBridgeType.GetMethod("GetDateTimeUnixEpoch", BindingFlags.Static | BindingFlags.Public);
                                    if (m != null)
                                    {
                                        ins.OpCode = OpCodes.Call;
                                        ins.Operand = mod.ImportReference(m);
                                        count++;
                                        continue;
                                    }
                                }
                            }
                            SanitizeTypeSpecAndSignature(frOp.FieldType, mobileApiRef);
                        }

                        if (!(ins.Operand is MethodReference mr)) continue;

                        if (mr.DeclaringType != null)
                            SanitizeTypeSpecAndSignature(mr.DeclaringType, mobileApiRef);
                        SanitizeTypeSpecAndSignature(mr.ReturnType, mobileApiRef);
                        foreach (var mp in mr.Parameters)
                            SanitizeTypeSpecAndSignature(mp.ParameterType, mobileApiRef);
                        if (mr is GenericInstanceMethod gimOp)
                        {
                            for (int g = 0; g < gimOp.GenericArguments.Count; g++)
                                SanitizeTypeSpecAndSignature(gimOp.GenericArguments[g], mobileApiRef);
                        }

                        string declFull = mr.DeclaringType?.FullName ?? "";
                        string mName = mr.Name ?? "";

                        // 0. Redirect ManualLogSource.LogInfo / LogDebug to HarmonyBridge.LogModInfoFiltered / LogModDebugFiltered to eliminate per-frame combat disk I/O
                        if (declFull == "BepInEx.Logging.ManualLogSource" && mr.Parameters.Count == 1)
                        {
                            if (mName == "LogInfo" || mName == "LogMessage")
                            {
                                var m = _harmonyBridgeType.GetMethod("LogModInfoFiltered", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "LogDebug")
                            {
                                var m = _harmonyBridgeType.GetMethod("LogModDebugFiltered", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "LogWarning")
                            {
                                var m = _harmonyBridgeType.GetMethod("LogModWarnFiltered", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "LogError")
                            {
                                var m = _harmonyBridgeType.GetMethod("LogModErrorFiltered", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 0a. BepInEx.Paths.get_ProcessName & System.MemoryExtensions.SequenceEqual<T> bridges for Stitchwork
                        if (declFull == "BepInEx.Paths" && mName == "get_ProcessName" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetBepInExProcessName", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "System.MemoryExtensions" && (mName == "SequenceEqual" || mName == "IndexOf") && mr.Parameters.Count == 2)
                        {
                            ins.OpCode = OpCodes.Pop;
                            ins.Operand = null;
                            var pop2 = il.Create(OpCodes.Pop);
                            var pushRes = il.Create(mName == "SequenceEqual" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_M1);
                            il.InsertAfter(ins, pop2);
                            il.InsertAfter(pop2, pushRes);
                            i += 2;
                            count++;
                            continue;
                        }
                        if (declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAsset", StringComparison.Ordinal) &&
                            mName == "FromSceneAsset" && mr.Parameters.Count == 2)
                        {
                            var fixInPlaceMi = _assetHelperBridgeType?.GetMethod("FixManagedSceneAssetInPlace", BindingFlags.Static | BindingFlags.Public);
                            if (fixInPlaceMi != null)
                            {
                                var dupIns = il.Create(OpCodes.Dup);
                                var callFix = il.Create(OpCodes.Call, mod.ImportReference(fixInPlaceMi));
                                il.InsertAfter(ins, dupIns);
                                il.InsertAfter(dupIns, callFix);
                                i += 2;
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "Silksong.AssetHelper.Android.AndroidSceneAssetRepacker" &&
                            mName == "StartExtract" && mr.Parameters.Count == 3 && _assetHelperBridgeType != null)
                        {
                            var redirMi = _assetHelperBridgeType.GetMethod("AndroidSceneAssetRepacker_StartExtract_Redirect", BindingFlags.Static | BindingFlags.Public);
                            if (redirMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(redirMi);
                                count++;
                                continue;
                            }
                        }
                        if (modName.IndexOf("PharloomsGlory", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            declFull == "UnityEngine.SceneManagement.SceneManager" &&
                            mName == "GetActiveScene" && mr.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            var pgGetActMi = _assetHelperBridgeType.GetMethod("PharloomsGloryGetActiveScene", BindingFlags.Static | BindingFlags.Public);
                            if (pgGetActMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(pgGetActMi);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "Steamworks.SteamInput" && mName == "GetGlyphPNGForActionOrigin" && mr.Parameters.Count == 3 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("SteamInput_GetGlyphPNGForActionOrigin", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "System.Diagnostics.ProcessStartInfo" && mName == "get_ArgumentList" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("ProcessStartInfo_GetArgumentList", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "System.String" && mName == "op_Implicit" && mr.Parameters.Count == 1 &&
                            mr.ReturnType != null && mr.ReturnType.FullName.IndexOf("ReadOnlySpan", StringComparison.Ordinal) >= 0)
                        {
                            ins.OpCode = OpCodes.Nop;
                            ins.Operand = null;
                            count++;
                            continue;
                        }
                        if (declFull == "System.IO.Path" && mName == "Join" && mr.Parameters.Count == 2 &&
                            mr.Parameters[0].ParameterType.FullName.IndexOf("ReadOnlySpan", StringComparison.Ordinal) >= 0 &&
                            _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("PathJoinTwoStrings", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "Silksong.AssetHelper.ManagedAssets.CatalogKeys" &&
                            mName == "GetKeyForSceneAsset" && mr.Parameters.Count == 2)
                        {
                            var rewriteKeyMi = _assetHelperBridgeType?.GetMethod("RewriteSceneAssetAddressKey", BindingFlags.Static | BindingFlags.Public);
                            if (rewriteKeyMi != null)
                            {
                                var callFix = il.Create(OpCodes.Call, mod.ImportReference(rewriteKeyMi));
                                il.InsertAfter(ins, callFix);
                                i += 1;
                                count++;
                                continue;
                            }
                        }

                        // 0b. Stitchwork 0.9.1 bridges: UnityWebRequestTexture.GetTexture(string, DownloadedTextureParams) & ColorCorrectionCurves.UpdateMaterial()
                        if (declFull == "UnityEngine.Networking.UnityWebRequestTexture" && mName == "GetTexture" &&
                            mr.Parameters.Count == 2 && mr.Parameters[1].ParameterType.Name == "DownloadedTextureParams" &&
                            _assetHelperBridgeType != null)
                        {
                            var m = _assetHelperBridgeType.GetMethod("GetTextureWithParams", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "UnityStandardAssets.ImageEffects.ColorCorrectionCurves" && mName == "UpdateMaterial" && mr.Parameters.Count == 0)
                        {
                            mr.Name = "UpdateParameters";
                            count++;
                            continue;
                        }

                        // 1. new Harmony(string id) / new Harmony() -> HarmonyBridge.CreateHarmony
                        if (ins.OpCode == OpCodes.Newobj && declFull == "HarmonyLib.Harmony" && mName == ".ctor")
                        {
                            if (mr.Parameters.Count == 1)
                            {
                                var createHarmony = _harmonyBridgeType.GetMethod("CreateHarmony", BindingFlags.Static | BindingFlags.Public);
                                if (createHarmony != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(createHarmony);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mr.Parameters.Count == 0)
                            {
                                var createHarmony0 = _harmonyBridgeType.GetMethod("CreateHarmonyNoArgs", BindingFlags.Static | BindingFlags.Public);
                                if (createHarmony0 != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(createHarmony0);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 2. Harmony.get_Id -> HarmonyBridge.GetHarmonyId
                        if (declFull == "HarmonyLib.Harmony" && mName == "get_Id")
                        {
                            var m = _harmonyBridgeType.GetMethod("GetHarmonyId", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }

                        // 3. Harmony.PatchAll() / PatchAll(Assembly) / PatchAll(Type) / CreateAndPatchAll
                        if (declFull == "HarmonyLib.Harmony")
                        {
                            if (mName == "PatchAll" && mr.Parameters.Count == 0)
                            {
                                var mByMod = _harmonyBridgeType.GetMethod("PatchAllByModName", BindingFlags.Static | BindingFlags.Public);
                                if (mByMod != null)
                                {
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldstr, mod.Assembly?.Name?.Name ?? modName));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(mByMod);
                                    count++;
                                    continue;
                                }
                                var m = _harmonyBridgeType.GetMethod("PatchAllNoArgs", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "PatchAll" && mr.Parameters.Count == 1)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string targetHelper = p0 == "System.Type" ? "PatchAllType" : "PatchAllAssembly";
                                var m = _harmonyBridgeType.GetMethod(targetHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "CreateAndPatchAll" && mr.Parameters.Count >= 1)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string targetHelper = p0 == "System.Type" ? "CreateAndPatchAllType" : "CreateAndPatchAllAssembly";
                                var m = _harmonyBridgeType.GetMethod(targetHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null && mr.Parameters.Count == 2)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                                else if (m != null && mr.Parameters.Count == 1)
                                {
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldnull));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "Patch")
                            {
                                bool isMethodInfoOverload = mr.Parameters.Count >= 2 &&
                                    mr.Parameters[1].ParameterType != null &&
                                    mr.Parameters[1].ParameterType.FullName == "System.Reflection.MethodInfo";
                                if (isMethodInfoOverload)
                                {
                                    bool expectsMethodInfo = mr.ReturnType != null && mr.ReturnType.FullName == "System.Reflection.MethodInfo";
                                    string helperSuffix = expectsMethodInfo ? "MethodInfo" : "";
                                    string helperName = mr.Parameters.Count == 2 ? $"PatchMethodInfos2{helperSuffix}"
                                                      : mr.Parameters.Count == 3 ? $"PatchMethodInfos3{helperSuffix}"
                                                      : mr.Parameters.Count == 4 ? $"PatchMethodInfos4{helperSuffix}"
                                                      : mr.Parameters.Count == 5 ? $"PatchMethodInfos5{helperSuffix}" : null;
                                    if (helperName != null)
                                    {
                                        var m = _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                        if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                                    }
                                }
                                else if (mr.Parameters.Count == 6)
                                {
                                    var m = _harmonyBridgeType.GetMethod("PatchWithHarmonyMethods", BindingFlags.Static | BindingFlags.Public);
                                    if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                                }
                                else if (mr.Parameters.Count == 5)
                                {
                                    var m = _harmonyBridgeType.GetMethod("Patch5Args", BindingFlags.Static | BindingFlags.Public);
                                    if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                                }
                                else if (mr.Parameters.Count == 4)
                                {
                                    var m = _harmonyBridgeType.GetMethod("Patch4Args", BindingFlags.Static | BindingFlags.Public);
                                    if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                                }
                            }
                            else if (mName == "UnpatchSelf" && mr.Parameters.Count == 0)
                            {
                                var m = _harmonyBridgeType.GetMethod("UnpatchSelf", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if ((mName == "UnpatchAll" || mName == "UnpatchID") && mr.Parameters.Count == 1)
                            {
                                var m = _harmonyBridgeType.GetMethod("UnpatchId", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "CreateProcessor" && mr.Parameters.Count == 1)
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateProcessor", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetPatchedMethods" && mr.Parameters.Count == 0)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetPatchedMethods", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetAllPatchedMethods" && mr.Parameters.Count == 0)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetAllPatchedMethods", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetPatchInfo")
                            {
                                string helper = mr.HasThis ? "InstanceGetPatchInfo" : "GetPatchInfo";
                                var m = _harmonyBridgeType.GetMethod(helper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "CreateClassProcessor")
                            {
                                string helper = mr.Parameters.Count == 2 ? "CreateClassProcessorBool" : "CreateClassProcessor";
                                var m = _harmonyBridgeType.GetMethod(helper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }

                        // 4. AccessTools.EnumeratorMoveNext, MethodDelegate<T>, FieldRefAccess<T, F>, & StaticFieldRefAccess
                        if (declFull == "HarmonyLib.AccessTools")
                        {
                            if (mName == "EnumeratorMoveNext")
                            {
                                var m = _harmonyBridgeType.GetMethod("EnumeratorMoveNext", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "MethodDelegate" && mr is GenericInstanceMethod gimMd && gimMd.GenericArguments.Count == 1)
                            {
                                var openHelper = _harmonyBridgeType.GetMethod("AccessToolsMethodDelegate", BindingFlags.Static | BindingFlags.Public);
                                if (openHelper != null)
                                {
                                    var ginst = new GenericInstanceMethod(mod.ImportReference(openHelper));
                                    ginst.GenericArguments.Add(gimMd.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "FieldRefAccess" && mr is GenericInstanceMethod gimFr)
                            {
                                if (gimFr.GenericArguments.Count == 2)
                                {
                                    string helperName = null;
                                    if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                                        helperName = "FieldRefAccess";
                                    else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.FieldInfo")
                                        helperName = "FieldRefAccessByField";
                                    else if (mr.Parameters.Count == 2 && mr.Parameters[1].ParameterType.FullName == "System.String")
                                        helperName = "FieldRefAccessInstance";

                                    var openHelper = helperName != null ? _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public) : null;
                                    if (openHelper != null)
                                    {
                                        var importedOpen = mod.ImportReference(openHelper);
                                        var ginst = new GenericInstanceMethod(importedOpen);
                                        ginst.GenericArguments.Add(gimFr.GenericArguments[0]);
                                        ginst.GenericArguments.Add(gimFr.GenericArguments[1]);
                                        ins.OpCode = OpCodes.Call;
                                        ins.Operand = ginst;
                                        count++;
                                        continue;
                                    }
                                }
                                else if (gimFr.GenericArguments.Count == 1)
                                {
                                    string helperName = null;
                                    if (mr.Parameters.Count == 2 && mr.Parameters[0].ParameterType.FullName == "System.Type" && mr.Parameters[1].ParameterType.FullName == "System.String")
                                        helperName = "FieldRefAccessByTypeAndName";
                                    else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.FieldInfo")
                                        helperName = "FieldRefAccessByFieldObject";
                                    else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                                        helperName = "StaticFieldRefAccessByName";

                                    var openHelper = helperName != null ? _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public) : null;
                                    if (openHelper != null)
                                    {
                                        var importedOpen = mod.ImportReference(openHelper);
                                        var ginst = new GenericInstanceMethod(importedOpen);
                                        ginst.GenericArguments.Add(gimFr.GenericArguments[0]);
                                        ins.OpCode = OpCodes.Call;
                                        ins.Operand = ginst;
                                        count++;
                                        continue;
                                    }
                                }
                            }
                            else if (mName == "StaticFieldRefAccess" && mr is GenericInstanceMethod gimSfr)
                            {
                                string helperName = null;
                                bool returnsByRef = mr.ReturnType is ByReferenceType;
                                if (gimSfr.GenericArguments.Count == 1)
                                {
                                    if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.FieldInfo")
                                        helperName = "StaticFieldRefAccessByField";
                                    else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                                        helperName = "StaticFieldRefAccessByName";
                                    else if (mr.Parameters.Count == 2 && mr.Parameters[0].ParameterType.FullName == "System.Type")
                                        helperName = "StaticFieldRefAccessByTypeAndName";
                                }
                                else if (gimSfr.GenericArguments.Count == 2)
                                {
                                    if (returnsByRef)
                                    {
                                        if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                                            helperName = "StaticFieldRefAccessValue";
                                        else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.FieldInfo")
                                            helperName = "StaticFieldRefAccessValueByField";
                                    }
                                    else
                                    {
                                        if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                                            helperName = "StaticFieldRefAccessTyped";
                                        else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.FieldInfo")
                                            helperName = "StaticFieldRefAccessTypedByField";
                                    }
                                }

                                var openHelper = helperName != null ? _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public) : null;
                                if (openHelper != null)
                                {
                                    var importedOpen = mod.ImportReference(openHelper);
                                    var ginst = new GenericInstanceMethod(importedOpen);
                                    foreach (var ga in gimSfr.GenericArguments)
                                        ginst.GenericArguments.Add(ga);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 5. BaseUnityPlugin.get_Info -> HarmonyBridge.GetPluginInfo & PluginInfo.get_Instance -> HarmonyBridge.GetPluginInfoInstance
                        if (declFull == "BepInEx.BaseUnityPlugin" && mName == "get_Info")
                        {
                            var m = _harmonyBridgeType.GetMethod("GetPluginInfo", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "BepInEx.PluginInfo" && mName == "get_Instance")
                        {
                            var m = _harmonyBridgeType.GetMethod("GetPluginInfoInstance", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.FormattableString" && mName == "Invariant" && mr.Parameters.Count == 1)
                        {
                            var m = _harmonyBridgeType.GetMethod("FormattableStringInvariant", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.Reflection.AssemblyMetadataAttribute" && (mName == "get_Key" || mName == "get_Value") && mr.Parameters.Count == 0)
                        {
                            string hName = mName == "get_Key" ? "GetAssemblyMetadataKey" : "GetAssemblyMetadataValue";
                            var m = _harmonyBridgeType.GetMethod(hName, BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "BepInEx.Paths" && mName == "get_GameRootPath" && mr.Parameters.Count == 0)
                        {
                            var m = ResolveRuntimeType("BepInEx.Paths")?.GetMethod("get_BepInExRootPath", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "BepInEx.Paths" && mName == "get_ManagedPath" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetManagedPathSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "BepInEx.Paths" && mName == "get_BepInExAssemblyDirectory" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetBepInExAssemblyDirectorySafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.Globalization.TextInfo" && mName == "ToTitleCase" && mr.Parameters.Count == 1 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("ToTitleCaseSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.Linq.Enumerable" && mr is GenericInstanceMethod gimEnum && gimEnum.GenericArguments.Count == 1 && _harmonyBridgeType != null)
                        {
                            if (mName == "ToHashSet" && (mr.Parameters.Count == 1 || mr.Parameters.Count == 2))
                            {
                                string hName = mr.Parameters.Count == 1 ? "EnumerableToHashSet" : "EnumerableToHashSetComparer";
                                var openM = _harmonyBridgeType.GetMethod(hName, BindingFlags.Static | BindingFlags.Public);
                                if (openM != null)
                                {
                                    var ginst = new GenericInstanceMethod(mod.ImportReference(openM));
                                    ginst.GenericArguments.Add(gimEnum.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "TakeLast" && mr.Parameters.Count == 2)
                            {
                                var openM = _harmonyBridgeType.GetMethod("EnumerableTakeLast", BindingFlags.Static | BindingFlags.Public);
                                if (openM != null)
                                {
                                    var ginst = new GenericInstanceMethod(mod.ImportReference(openM));
                                    ginst.GenericArguments.Add(gimEnum.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                        }
                        if (declFull == "System.Runtime.CompilerServices.RuntimeHelpers" && mName == "GetSubArray" &&
                            mr is GenericInstanceMethod gimSub && gimSub.GenericArguments.Count == 1 && mr.Parameters.Count == 2 && _harmonyBridgeType != null)
                        {
                            var openM = _harmonyBridgeType.GetMethod("GetSubArraySafe", BindingFlags.Static | BindingFlags.Public);
                            if (openM != null)
                            {
                                var ginst = new GenericInstanceMethod(mod.ImportReference(openM));
                                ginst.GenericArguments.Add(gimSub.GenericArguments[0]);
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = ginst;
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "System.IO.Path" && mName == "GetRelativePath" && mr.Parameters.Count == 2 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("PathGetRelativePath", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.Application" && mName == "get_dataPath" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetApplicationDataPathSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.Diagnostics.Process" && mName == "Start" && _harmonyBridgeType != null)
                        {
                            string pHelper = mr.Parameters.Count == 1
                                ? (mr.Parameters[0].ParameterType.FullName == "System.String" ? "ProcessStartSafe" : "ProcessStartInfoSafe")
                                : (mr.Parameters.Count == 2 ? "ProcessStartWithArgsSafe" : null);
                            if (pHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(pHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "System.IO.FileSystemWatcher" && mName == "set_EnableRaisingEvents" && mr.Parameters.Count == 1 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("SetFileSystemWatcherEnableRaisingEventsSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "HarmonyLib.HarmonyMethod" && ins.OpCode == OpCodes.Newobj && mName == ".ctor" && mr.Parameters.Count > 0)
                        {
                            if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.MethodInfo")
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateHarmonyMethod", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.Delegate")
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateHarmonyMethodFromDelegate", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mr.Parameters.Count == 2 && mr.Parameters[0].ParameterType.FullName == "System.Type" && mr.Parameters[1].ParameterType.FullName == "System.String")
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateHarmonyMethodFromType", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldnull));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mr.Parameters.Count == 3 && mr.Parameters[0].ParameterType.FullName == "System.Type")
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateHarmonyMethodFromType", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mr.Parameters.Count == 5 && mr.Parameters[0].ParameterType.FullName == "System.Reflection.MethodInfo")
                            {
                                var m = _harmonyBridgeType.GetMethod("CreateHarmonyMethodWithPrio", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (ins.OpCode == OpCodes.Newobj && mName == ".ctor" && mr.Parameters.Count == 0 &&
                            (declFull == "ToolItemSkill" || declFull == "ToolItemBasic" || declFull == "ToolCrest"))
                        {
                            var soType = ResolveRuntimeType("UnityEngine.ScriptableObject");
                            var createOpen = soType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                                .FirstOrDefault(m => m.Name == "CreateInstance" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                            if (createOpen != null)
                            {
                                var ginst = new GenericInstanceMethod(mod.ImportReference(createOpen));
                                ginst.GenericArguments.Add(mr.DeclaringType);
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = ginst;
                                count++;
                                continue;
                            }
                        }

                        // 5z. System.Type.GetField(string, BindingFlags) & FieldInfo.GetValue/SetValue -> HarmonyBridge best-effort shims for publicized IL2CPP fields (e.g. SendRandomEventV4.trackingInts)
                        if (declFull == "System.Type" && mName == "GetField" && mr.Parameters.Count == 2 &&
                            mr.Parameters[0].ParameterType.FullName == "System.String" &&
                            mr.Parameters[1].ParameterType.FullName == "System.Reflection.BindingFlags" &&
                            _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetFieldBestEffort", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.Reflection.FieldInfo" && _harmonyBridgeType != null)
                        {
                            if (mName == "GetValue" && mr.Parameters.Count == 1)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetFieldValueBestEffort", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "SetValue" && mr.Parameters.Count == 2 &&
                                     mr.Parameters[0].ParameterType.FullName == "System.Object" &&
                                     mr.Parameters[1].ParameterType.FullName == "System.Object")
                            {
                                var m = _harmonyBridgeType.GetMethod("SetFieldValueBestEffort", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }

                        // 6. Assembly.get_Location / GetManifestResourceStream / GetManifestResourceNames
                        if (declFull == "System.Reflection.Assembly")
                        {
                            if (mName == "get_Location")
                            {
                                var m = _harmonyBridgeType.GetMethod("GetAssemblyLocation", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetManifestResourceStream" && mr.Parameters.Count == 1)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetManifestResourceStream", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetManifestResourceStream" && mr.Parameters.Count == 2)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetManifestResourceStreamByType", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetManifestResourceNames" && mr.Parameters.Count == 0)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetManifestResourceNames", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "GetCallingAssembly" && mr.Parameters.Count == 0)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetCallingAssemblySafe", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "ToolItemManager" && mName == "get_IsChangingActiveState" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("ToolItemManager_GetIsChangingActiveState", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if ((declFull == "InControl.IKeyboardProvider" || declFull == "InControl.UnityKeyboardProvider") &&
                            mName == "GetKeyIsPressed" && mr.Parameters.Count == 1 && _harmonyBridgeType != null)
                        {
                            var m = _harmonyBridgeType.GetMethod("InControl_GetKeyIsPressed", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "InControl.InputManager" && mr.Parameters.Count == 0 && _harmonyBridgeType != null)
                        {
                            string imHelper = mName == "get_KeyboardProvider" ? "InControl_GetKeyboardProviderSafe"
                                            : mName == "get_MouseProvider" ? "InControl_GetMouseProviderSafe" : null;
                            if (imHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(imHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "InControl.IMouseProvider" && _harmonyBridgeType != null)
                        {
                            string mpHelper = mName == "GetButtonIsPressed" ? "InControl_MouseGetButtonIsPressed"
                                            : mName == "GetButtonWasPressed" ? "InControl_MouseGetButtonWasPressed"
                                            : mName == "GetDeltaScroll" ? "InControl_MouseGetDeltaScroll"
                                            : mName == "GetDeltaX" ? "InControl_MouseGetDeltaX"
                                            : mName == "GetDeltaY" ? "InControl_MouseGetDeltaY"
                                            : mName == "GetPosition" ? "InControl_MouseGetPosition" : null;
                            if (mpHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(mpHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "System.Math" && mName == "Clamp" && mr.Parameters.Count == 3 && _harmonyBridgeType != null)
                        {
                            string p0 = mr.Parameters[0].ParameterType.FullName;
                            string clampHelper = p0 == "System.Byte" ? "MathClampByte"
                                               : p0 == "System.Int32" ? "MathClampInt"
                                               : p0 == "System.Int64" ? "MathClampLong"
                                               : p0 == "System.Single" ? "MathClampFloat"
                                               : p0 == "System.Double" ? "MathClampDouble" : null;
                            if (clampHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(clampHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "UnityEngine.ColorUtility" && mr.Parameters.Count == 1)
                        {
                            string cuHelper = mName == "ToHtmlStringRGB" ? "ColorUtility_ToHtmlStringRGB"
                                            : (mName == "ToHtmlStringRGBA" ? "ColorUtility_ToHtmlStringRGBA" : null);
                            if (cuHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(cuHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }

                        // 6b. System.Reflection.CustomAttributeExtensions -> HarmonyBridge.GetCustomAttributeSafe / GetCustomAttributesSafe
                        if (declFull == "System.Reflection.CustomAttributeExtensions")
                        {
                            if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count == 1)
                            {
                                string helperName = mName == "GetCustomAttribute" ? "GetCustomAttributeSafe"
                                                  : (mName == "GetCustomAttributes" ? "GetCustomAttributesSafe" : null);
                                var openHelper = helperName != null ? _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public) : null;
                                if (openHelper != null)
                                {
                                    if (mr.Parameters.Count == 1)
                                    {
                                        il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4_1));
                                        i++;
                                    }
                                    var importedOpen = mod.ImportReference(openHelper);
                                    var ginst = new GenericInstanceMethod(importedOpen);
                                    ginst.GenericArguments.Add(gim.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else if ((mName == "GetCustomAttribute" || mName == "GetCustomAttributes" || mName == "IsDefined") && mr.Parameters.Count >= 2)
                            {
                                string helperName = mName == "GetCustomAttribute" ? "GetCustomAttributeByTypeSafe"
                                                  : (mName == "GetCustomAttributes" ? "GetCustomAttributesByTypeSafe" : "IsDefinedSafe");
                                var helper = _harmonyBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                if (helper != null)
                                {
                                    if (mr.Parameters.Count == 2)
                                    {
                                        il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4_1));
                                        i++;
                                    }
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(helper);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 7. UnityEngine.Input.GetKey / GetKeyDown / GetKeyUp / GetMouseButton* / mousePosition -> MobileVirtualInput
                        if (declFull == "UnityEngine.Input" && _mobileVirtualInputType != null)
                        {
                            if ((mName == "GetKey" || mName == "GetKeyDown" || mName == "GetKeyUp") && mr.Parameters.Count == 1)
                            {
                                string p0Type = mr.Parameters[0].ParameterType.FullName;
                                string helperName = p0Type == "System.String" ? (mName + "String") : mName;
                                var m = _mobileVirtualInputType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if ((mName == "GetMouseButton" || mName == "GetMouseButtonDown" || mName == "GetMouseButtonUp") && mr.Parameters.Count == 1)
                            {
                                var m = _mobileVirtualInputType.GetMethod(mName, BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "get_mousePosition" && mr.Parameters.Count == 0)
                            {
                                var m = _mobileVirtualInputType.GetMethod("get_mousePosition", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if ((mName == "get_anyKey" || mName == "get_anyKeyDown") && mr.Parameters.Count == 0)
                            {
                                var m = _mobileVirtualInputType.GetMethod(mName, BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 7b. UnityEngine.InputSystem (ButtonControl / KeyControl / InputAction) -> MobileVirtualInput
                        if (_mobileVirtualInputType != null && mr.HasThis && mr.Parameters.Count == 0)
                        {
                            if (declFull == "UnityEngine.InputSystem.Controls.ButtonControl" || declFull == "UnityEngine.InputSystem.Controls.KeyControl")
                            {
                                string helperName = mName == "get_isPressed" ? "ButtonControl_get_isPressed"
                                                  : mName == "get_wasPressedThisFrame" ? "ButtonControl_get_wasPressedThisFrame"
                                                  : mName == "get_wasReleasedThisFrame" ? "ButtonControl_get_wasReleasedThisFrame"
                                                  : null;
                                if (helperName != null)
                                {
                                    var m = _mobileVirtualInputType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                    if (m != null)
                                    {
                                        ins.OpCode = OpCodes.Call;
                                        ins.Operand = mod.ImportReference(m);
                                        count++;
                                        continue;
                                    }
                                }
                            }
                            else if (declFull == "UnityEngine.InputSystem.InputAction")
                            {
                                string helperName = mName == "IsPressed" ? "InputAction_IsPressed"
                                                  : (mName == "WasPressedThisFrame" || mName == "get_triggered") ? "InputAction_WasPressedThisFrame"
                                                  : mName == "WasReleasedThisFrame" ? "InputAction_WasReleasedThisFrame"
                                                  : null;
                                if (helperName != null)
                                {
                                    var m = _mobileVirtualInputType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                    if (m != null)
                                    {
                                        ins.OpCode = OpCodes.Call;
                                        ins.Operand = mod.ImportReference(m);
                                        count++;
                                        continue;
                                    }
                                }
                            }
                        }

                        // 7z. Bridge AssetHelper v1.2.x ManagedAsset<T>.Load() / get_IsLoaded() & pre-bind ManagedAssetBase<T>.Load()
                        if ((declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAssetBase`1", StringComparison.Ordinal) ||
                             declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAsset`1", StringComparison.Ordinal)) &&
                            mName == "Load" && mr.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            if (mr.ReturnType != null && mr.ReturnType.FullName == "System.Void")
                            {
                                var loadVoidMi = _assetHelperBridgeType.GetMethod("InvokeManagedAssetLoadVoid", BindingFlags.Static | BindingFlags.Public);
                                if (loadVoidMi != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(loadVoidMi);
                                    count++;
                                    continue;
                                }
                            }
                            else if (declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAsset`1", StringComparison.Ordinal) &&
                                     mr.DeclaringType is GenericInstanceType gitLoad &&
                                     gitLoad.GenericArguments.Count == 1)
                            {
                                var openLoadMi = _assetHelperBridgeType.GetMethod("InvokeManagedAssetLoad", BindingFlags.Static | BindingFlags.Public);
                                if (openLoadMi != null)
                                {
                                    var importedOpen = mod.ImportReference(openLoadMi);
                                    var ginst = new GenericInstanceMethod(importedOpen);
                                    ginst.GenericArguments.Add(gitLoad.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else
                            {
                                var preBindMi = _assetHelperBridgeType.GetMethod("EnsureManagedAssetBoundBeforeLoad", BindingFlags.Static | BindingFlags.Public);
                                if (preBindMi != null)
                                {
                                    il.InsertBefore(ins, il.Create(OpCodes.Dup));
                                    il.InsertBefore(ins, il.Create(OpCodes.Call, mod.ImportReference(preBindMi)));
                                    i += 2;
                                    count++;
                                }
                            }
                        }
                        if ((declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAssetBase`1", StringComparison.Ordinal) ||
                             declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAsset`1", StringComparison.Ordinal)) &&
                            mName == "get_IsLoaded" && mr.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            var isLoadedMi = _assetHelperBridgeType.GetMethod("GetManagedAssetIsLoaded", BindingFlags.Static | BindingFlags.Public);
                            if (isLoadedMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(isLoadedMi);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "Silksong.AssetHelper.ManagedAssets.ManagedAssetExtensions" &&
                            mName == "InstantiateAsset" && _assetHelperBridgeType != null)
                        {
                            var postInstMi = _assetHelperBridgeType.GetMethod("OnManagedAssetInstantiated", BindingFlags.Static | BindingFlags.Public);
                            if (postInstMi != null)
                            {
                                var dupIns = il.Create(OpCodes.Dup);
                                var callIns = il.Create(OpCodes.Call, mod.ImportReference(postInstMi));
                                il.InsertAfter(ins, dupIns);
                                il.InsertAfter(dupIns, callIns);
                                i += 2;
                                count++;
                            }
                        }
                        if (declFull == "UnityEngine.Object" && mName == "DestroyImmediate" && mr.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var safeDesImmMi = _assetHelperBridgeType.GetMethod("SafeDestroyImmediateUnityObject", BindingFlags.Static | BindingFlags.Public);
                            if (safeDesImmMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(safeDesImmMi);
                                count++;
                                continue;
                            }
                        }
                        if (declFull == "UnityEngine.Object" && mName == "Destroy" && mr.Parameters.Count == 1 && _assetHelperBridgeType != null &&
                            (modName.IndexOf("Bonemaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             modName.IndexOf("PaleAutomaton", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             modName.IndexOf("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             modName.IndexOf("Phantinel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             modName.IndexOf("Forsaken", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            var safeDesMi = _assetHelperBridgeType.GetMethod("SafeDeactivateAndDestroyLater", BindingFlags.Static | BindingFlags.Public);
                            if (safeDesMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(safeDesMi);
                                count++;
                                continue;
                            }
                        }

                        // 8. ManagedAssetBase<T>.get_Handle -> AssetHelperBridge.GetHandle<T>
                        if ((declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAssetBase`1", StringComparison.Ordinal) ||
                             declFull.StartsWith("Silksong.AssetHelper.ManagedAssets.ManagedAsset`1", StringComparison.Ordinal)) &&
                            mName == "get_Handle" &&
                            mr.DeclaringType is GenericInstanceType git &&
                            git.GenericArguments.Count == 1 &&
                            _assetHelperBridgeType != null)
                        {
                            var openGetHandle = _assetHelperBridgeType.GetMethod("GetHandle", BindingFlags.Static | BindingFlags.Public);
                            if (openGetHandle != null)
                            {
                                var importedOpen = mod.ImportReference(openGetHandle);
                                var ginst = new GenericInstanceMethod(importedOpen);
                                ginst.GenericArguments.Add(git.GenericArguments[0]);
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = ginst;
                                count++;
                                continue;
                            }
                        }

                        // 9. Rigidbody2D.get_linearDrag / set_linearDrag / linearDamping / angularDamping / linearVelocity & ColliderDistance2D.get_isOverlapped
                        if (declFull == "UnityEngine.Rigidbody2D")
                        {
                            string rbHelper = (mName == "get_linearDrag" || mName == "get_linearDamping") ? "GetLinearDrag"
                                            : (mName == "set_linearDrag" || mName == "set_linearDamping") ? "SetLinearDrag"
                                            : mName == "get_angularDamping" ? "GetAngularDamping"
                                            : mName == "set_angularDamping" ? "SetAngularDamping"
                                            : mName == "get_linearVelocity" ? "GetLinearVelocity"
                                            : mName == "set_linearVelocity" ? "SetLinearVelocity"
                                            : mName == "get_linearVelocityX" ? "GetLinearVelocityX"
                                            : mName == "set_linearVelocityX" ? "SetLinearVelocityX"
                                            : mName == "get_linearVelocityY" ? "GetLinearVelocityY"
                                            : mName == "set_linearVelocityY" ? "SetLinearVelocityY"
                                            : null;
                            if (rbHelper != null)
                            {
                                var m = _harmonyBridgeType.GetMethod(rbHelper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "UnityEngine.AudioSource" && mName == "set_volume" && mr.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var m = _assetHelperBridgeType.GetMethod("SetAudioSourceVolumeSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.AssetBundle" && mName == "UnloadAsync" && mr.Parameters.Count == 1)
                        {
                            var m = _harmonyBridgeType.GetMethod("AssetBundleUnloadAsync", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.AssetBundle" && mr.Parameters.Count == 1 &&
                            mr.Parameters[0].ParameterType.FullName == "System.String" && _assetHelperBridgeType != null)
                        {
                            if (mName == "LoadFromFile")
                            {
                                var m = _assetHelperBridgeType.GetMethod("LoadAssetBundleFromFileSafe", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                            else if (mName == "LoadFromFileAsync")
                            {
                                var m = _assetHelperBridgeType.GetMethod("LoadAssetBundleFromFileAsyncSafe", BindingFlags.Static | BindingFlags.Public);
                                if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                            }
                        }
                        if (declFull == "UnityEngine.UI.InputField" && (mName == "get_onSubmit" || mName == "set_onSubmit" || mName == "get_onEndEdit" || mName == "set_onEndEdit"))
                        {
                            bool isGetter = mName.StartsWith("get_", StringComparison.Ordinal);
                            var m = _harmonyBridgeType.GetMethod(isGetter ? "GetInputFieldOnSubmit" : "SetInputFieldOnSubmit", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "System.AppDomain" && (mName == "add_AssemblyLoad" || mName == "remove_AssemblyLoad" || mName == "add_ProcessExit" || mName == "remove_ProcessExit") && mr.Parameters.Count == 1)
                        {
                            var m = _harmonyBridgeType.GetMethod("AppDomain_NoOpEventHandler", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "BepInEx.Hook" && mName == "Patch" && mr.Parameters.Count == 4)
                        {
                            var m = _harmonyBridgeType.GetMethod("BepInExHookPatchSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.Canvas" && (mName == "get_vertexColorAlwaysGammaSpace" || mName == "set_vertexColorAlwaysGammaSpace"))
                        {
                            var m = _harmonyBridgeType.GetMethod(mName == "get_vertexColorAlwaysGammaSpace" ? "GetCanvasVertexColorAlwaysGammaSpace" : "SetCanvasVertexColorAlwaysGammaSpace", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.Collider2D" && (mName == "get_compositeOperation" || mName == "set_compositeOperation"))
                        {
                            var m = _harmonyBridgeType.GetMethod(mName == "get_compositeOperation" ? "GetCollider2DCompositeOperation" : "SetCollider2DCompositeOperation", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.Input" && mName == "get_mousePositionDelta" && mr.Parameters.Count == 0)
                        {
                            var m = _harmonyBridgeType.GetMethod("GetInputMousePositionDelta", BindingFlags.Static | BindingFlags.Public);
                            if (m != null) { ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(m); count++; continue; }
                        }
                        if (declFull == "UnityEngine.ColliderDistance2D" && mName == "get_isOverlapped")
                        {
                            var m = _harmonyBridgeType.GetMethod("GetColliderDistanceIsOverlapped", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Ldobj;
                                ins.Operand = mr.DeclaringType;
                                var callIns = il.Create(OpCodes.Call, mod.ImportReference(m));
                                il.InsertAfter(ins, callIns);
                                i++;
                                count++;
                                continue;
                            }
                        }

                        // 9b. PrepatcherPlugin.PlayerDataAccess & PlayerDataVariableEvents bridge
                        if (declFull == "PrepatcherPlugin.PlayerDataAccess")
                        {
                            if (mName.StartsWith("get_", StringComparison.Ordinal) && mr.Parameters.Count == 0)
                            {
                                string pdField = mName.Substring(4);
                                var openGet = _harmonyBridgeType.GetMethod("GetPlayerDataField", BindingFlags.Static | BindingFlags.Public);
                                if (openGet != null)
                                {
                                    var ginst = new GenericInstanceMethod(mod.ImportReference(openGet));
                                    ginst.GenericArguments.Add(mr.ReturnType);
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldstr, pdField));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName.StartsWith("set_", StringComparison.Ordinal) && mr.Parameters.Count == 1)
                            {
                                string pdField = mName.Substring(4);
                                var openSet = _harmonyBridgeType.GetMethod("SetPlayerDataField", BindingFlags.Static | BindingFlags.Public);
                                if (openSet != null)
                                {
                                    var ginst = new GenericInstanceMethod(mod.ImportReference(openSet));
                                    ginst.GenericArguments.Add(mr.Parameters[0].ParameterType);
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldstr, pdField));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                        }
                        if (declFull.StartsWith("PrepatcherPlugin.", StringComparison.Ordinal))
                        {
                            if (ins.OpCode == OpCodes.Newobj && mName == ".ctor" && mr.Parameters.Count == 2 &&
                                mr.DeclaringType != null && mr.DeclaringType.Name != "PlayerDataVariableHandler" &&
                                i > 0 && (instrs[i - 1].OpCode == OpCodes.Ldftn || instrs[i - 1].OpCode == OpCodes.Ldvirtftn) &&
                                instrs[i - 1].Operand is MethodReference handlerMr &&
                                handlerMr.Parameters.Count == 3)
                            {
                                var openFuncCtor = typeof(Func<,,,>).GetConstructors().FirstOrDefault();
                                if (openFuncCtor != null)
                                {
                                    var importedOpenCtor = mod.ImportReference(openFuncCtor);
                                    var funcGit = new GenericInstanceType(importedOpenCtor.DeclaringType);
                                    funcGit.GenericArguments.Add(handlerMr.Parameters[0].ParameterType);
                                    funcGit.GenericArguments.Add(handlerMr.Parameters[1].ParameterType);
                                    funcGit.GenericArguments.Add(handlerMr.Parameters[2].ParameterType);
                                    funcGit.GenericArguments.Add(handlerMr.ReturnType);
                                    var boundCtor = new MethodReference(".ctor", mod.TypeSystem.Void, funcGit) { HasThis = true };
                                    boundCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                                    boundCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.IntPtr));
                                    ins.Operand = boundCtor;
                                    count++;
                                    continue;
                                }
                            }
                            else if ((mName.StartsWith("add_On", StringComparison.Ordinal) || mName.StartsWith("remove_On", StringComparison.Ordinal)) &&
                                     mr.Parameters.Count == 1 && mr.ReturnType.FullName == "System.Void")
                            {
                                var regMethod = _harmonyBridgeType.GetMethod("RegisterPrepatcherEvent", BindingFlags.Static | BindingFlags.Public);
                                if (regMethod != null)
                                {
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldstr, $"{declFull}::{mName}"));
                                    i++;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(regMethod);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 9c. Silksong.FsmUtil.FsmUtil overloads with extra 'bool everyFrame'
                        // Strictly verify BOTH parameter count AND exact parameter types so 3-arg AddMethod(PlayMakerFSM, string, Action<FsmStateAction>)
                        // and 4-arg InsertMethod(PlayMakerFSM, string, Action<FsmStateAction>, int) are NEVER rewritten to mismatched arity helpers!
                        if (declFull == "Silksong.FsmUtil.FsmUtil")
                        {
                            string helper = null;
                            int pCount = mr.Parameters.Count;
                            if (mName == "InsertMethod" && pCount == 4)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string p1 = mr.Parameters[1].ParameterType.FullName;
                                string p2 = mr.Parameters[2].ParameterType.FullName;
                                string p3 = mr.Parameters[3].ParameterType.FullName;
                                if (p0 == "HutongGames.PlayMaker.FsmState" && p3 == "System.Boolean")
                                {
                                    if (p1 == "System.Int32" && p2 == "System.Action") helper = "FsmUtil_InsertMethod_State";
                                    else if (p1 == "System.Action" && p2 == "System.Int32") helper = "FsmUtil_InsertMethod_StateReversed";
                                }
                            }
                            else if (mName == "InsertMethod" && pCount == 5)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string p1 = mr.Parameters[1].ParameterType.FullName;
                                string p2 = mr.Parameters[2].ParameterType.FullName;
                                string p3 = mr.Parameters[3].ParameterType.FullName;
                                string p4 = mr.Parameters[4].ParameterType.FullName;
                                if (p0 == "PlayMakerFSM" && p1 == "System.String" && p2 == "System.Int32" && p3 == "System.Action" && p4 == "System.Boolean")
                                    helper = "FsmUtil_InsertMethod_Fsm";
                            }
                            else if (mName == "AddMethod" && pCount == 3)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string p1 = mr.Parameters[1].ParameterType.FullName;
                                string p2 = mr.Parameters[2].ParameterType.FullName;
                                if (p0 == "HutongGames.PlayMaker.FsmState" && p1 == "System.Action" && p2 == "System.Boolean")
                                    helper = "FsmUtil_AddMethod_State";
                            }
                            else if (mName == "AddMethod" && pCount == 4)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string p1 = mr.Parameters[1].ParameterType.FullName;
                                string p2 = mr.Parameters[2].ParameterType.FullName;
                                string p3 = mr.Parameters[3].ParameterType.FullName;
                                if (p0 == "PlayMakerFSM" && p1 == "System.String" && p2 == "System.Action" && p3 == "System.Boolean")
                                    helper = "FsmUtil_AddMethod_Fsm";
                            }
                            else if (mName == "InsertActionBefore" && pCount == 2)
                            {
                                string p0 = mr.Parameters[0].ParameterType.FullName;
                                string p1 = mr.Parameters[1].ParameterType.FullName;
                                if (p0 == "HutongGames.PlayMaker.FsmStateAction" && p1 == "HutongGames.PlayMaker.FsmStateAction")
                                    helper = "FsmUtil_InsertActionBefore";
                            }

                            if (helper != null)
                            {
                                var m = _adapterBuilderType?.Assembly.GetType("SilksongMobileAPI.PlayMakerHybridCLRBridge")?.GetMethod(helper, BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 9d. Weaver_Crest / PC Mod embedded WAV & AudioSource bridge
                        if (declFull.EndsWith(".Sounds", StringComparison.Ordinal) || declFull == "Weaver_Crest.Patches.Sounds")
                        {
                            if (mName == "LoadEmbeddedWav" && mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                            {
                                var m = _harmonyBridgeType.GetMethod("LoadEmbeddedWavByModName", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    var ldMod = il.Create(OpCodes.Ldstr, modName);
                                    var stTmp = new VariableDefinition(mod.TypeSystem.String);
                                    md.Body.Variables.Add(stTmp);
                                    md.Body.InitLocals = true;
                                    // Stack has [fileName]; store fileName, push modName, push fileName, call LoadEmbeddedWavByModName
                                    il.InsertBefore(ins, il.Create(OpCodes.Stloc, stTmp));
                                    il.InsertBefore(ins, ldMod);
                                    il.InsertBefore(ins, il.Create(OpCodes.Ldloc, stTmp));
                                    i += 3;
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                            else if (mName == "Play" && mr.Parameters.Count == 2 &&
                                     mr.Parameters[0].ParameterType.FullName == "UnityEngine.AudioClip" &&
                                     mr.Parameters[1].ParameterType.FullName == "System.Single")
                            {
                                var m = _harmonyBridgeType.GetMethod("PlayModAudioClip", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 9e. UnityWebRequestMultimedia.GetAudioClip -> HarmonyBridge.GetAudioClipSafe (fixes Windows 'file:///\\?\' paths)
                        if (declFull == "UnityEngine.Networking.UnityWebRequestMultimedia" && mName == "GetAudioClip" &&
                            mr.Parameters.Count == 2 && mr.Parameters[0].ParameterType.FullName == "System.String")
                        {
                            var m = _harmonyBridgeType.GetMethod("GetAudioClipSafe", BindingFlags.Static | BindingFlags.Public);
                            if (m != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(m);
                                count++;
                                continue;
                            }
                        }

                        // 9f. Resources.GetBuiltinResource -> HarmonyBridge.GetBuiltinResourceSafe (fixes LegacyRuntime.ttf / Arial.ttf fallback)
                        if (declFull == "UnityEngine.Resources" && mName == "GetBuiltinResource")
                        {
                            if (mr is GenericInstanceMethod gimRes && gimRes.GenericArguments.Count == 1 && mr.Parameters.Count == 1)
                            {
                                var openHelper = _harmonyBridgeType.GetMethod("GetBuiltinResourceGenericSafe", BindingFlags.Static | BindingFlags.Public);
                                if (openHelper != null)
                                {
                                    var importedOpen = mod.ImportReference(openHelper);
                                    var ginst = new GenericInstanceMethod(importedOpen);
                                    ginst.GenericArguments.Add(gimRes.GenericArguments[0]);
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = ginst;
                                    count++;
                                    continue;
                                }
                            }
                            else if (!mr.IsGenericInstance && mr.Parameters.Count == 2)
                            {
                                var m = _harmonyBridgeType.GetMethod("GetBuiltinResourceSafe", BindingFlags.Static | BindingFlags.Public);
                                if (m != null)
                                {
                                    ins.OpCode = OpCodes.Call;
                                    ins.Operand = mod.ImportReference(m);
                                    count++;
                                    continue;
                                }
                            }
                        }

                        // 9g. GradientColorKey constructor redirect (prevents MissingMethodException in IL2CPP/HybridCLR)
                        if (ins.OpCode == OpCodes.Newobj && declFull == "UnityEngine.GradientColorKey" && mName == ".ctor")
                        {
                            var shimType = _adapterBuilderType?.Assembly.GetType("SilksongMobileAPI.BepInExShims");
                            var shimMi = shimType?.GetMethod("CreateGradientColorKey", BindingFlags.Static | BindingFlags.Public);
                            if (shimMi != null)
                            {
                                ins.OpCode = OpCodes.Call;
                                ins.Operand = mod.ImportReference(shimMi);
                                count++;
                                continue;
                            }
                        }

                        // 10. Auto-stub stripped UnityEngine calls
                        if (declFull.StartsWith("UnityEngine.", StringComparison.Ordinal))
                        {
                            if (TryAutoStubStrippedUnityCall(modName, mod, md, il, ref i, ins, mr))
                            {
                                count++;
                                continue;
                            }
                        }
                    }

                    // 11. Weave managed postfix hook dispatch into Needleforge.Attacks.* 0-arg void methods (e.g., AttackBase.StartAttack)
                    if (td.FullName.StartsWith("Needleforge.Attacks.", StringComparison.Ordinal) &&
                        !md.IsConstructor && !md.IsAbstract && !md.HasGenericParameters && !td.HasGenericParameters &&
                        md.Parameters.Count == 0 && md.ReturnType.FullName == "System.Void" &&
                        !md.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        var dispatchManaged = _harmonyBridgeType.GetMethod("DispatchManagedZeroArgPostfix", BindingFlags.Static | BindingFlags.Public);
                        if (dispatchManaged != null)
                        {
                            var importedDispatch = mod.ImportReference(dispatchManaged);
                            string mKey = $"{td.FullName.Replace('/', '+')}::{md.Name}()";
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                ExpandShortBranchIfNeeded(instrs[k]);
                            }
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                var retIns = instrs[k];
                                if (retIns.OpCode != OpCodes.Ret) continue;
                                // Mutate retIns in place so all branches targeting retIns hit our woven postfix call first
                                retIns.OpCode = OpCodes.Ldstr;
                                retIns.Operand = mKey;
                                var ldInst = il.Create(md.IsStatic ? OpCodes.Ldnull : OpCodes.Ldarg_0);
                                var callDisp = il.Create(OpCodes.Call, importedDispatch);
                                var newRet = il.Create(OpCodes.Ret);
                                il.InsertAfter(retIns, ldInst);
                                il.InsertAfter(ldInst, callDisp);
                                il.InsertAfter(callDisp, newRet);
                                k += 3;
                                count++;
                            }
                        }
                    }

                    // 11b. Awakened Grand Mother Silk: neutralize DebugPatches.ForceInvincible & bridge AssetManager manual bundle methods
                    if (td.FullName == "Awakened_Grand_Mother_Silk.Source.Patches.DebugPatches" && md.Name == "ForceInvincible")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    else if (td.FullName == "Awakened_Grand_Mother_Silk.Source.AssetManager" && _assetHelperBridgeType != null)
                    {
                        var getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle", BindingFlags.Static | BindingFlags.Public);
                        if (md.Name == "ManuallyLoadBundles" && md.Parameters.Count == 0)
                        {
                            var safeMi = _assetHelperBridgeType.GetMethod("AwakenedGmsManuallyLoadBundlesSafe", BindingFlags.Static | BindingFlags.Public);
                            if (safeMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Call, mod.ImportReference(safeMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "Initialize" && md.Parameters.Count == 0 && getTypeFromHandle != null)
                        {
                            var safeInitMi = _assetHelperBridgeType.GetMethod("AwakenedGmsInitializeSafe", BindingFlags.Static | BindingFlags.Public);
                            if (safeInitMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldtoken, td);
                                il.Emit(OpCodes.Call, mod.ImportReference(getTypeFromHandle));
                                il.Emit(OpCodes.Call, mod.ImportReference(safeInitMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "UnloadManualBundles" && md.Parameters.Count == 0 && getTypeFromHandle != null)
                        {
                            var safeUnloadMi = _assetHelperBridgeType.GetMethod("AwakenedGmsUnloadManualBundlesSafe", BindingFlags.Static | BindingFlags.Public);
                            if (safeUnloadMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldtoken, td);
                                il.Emit(OpCodes.Call, mod.ImportReference(getTypeFromHandle));
                                il.Emit(OpCodes.Call, mod.ImportReference(safeUnloadMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                    }
                    // 11c. Auric Aegis (PrimedSentinel): safe First Idle reset guard, Stun Control disable, Phase 1 HP clamp, & coroutine SetState guard
                    else if (td.FullName == "Silksong.Mods.PrimedSentinel.PrimedSentinel" && _assetHelperBridgeType != null)
                    {
                        var getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle", BindingFlags.Static | BindingFlags.Public);
                        if (md.Name == "OnFsmEnabled" && md.Parameters.Count == 1 && getTypeFromHandle != null && instrs.Count > 0)
                        {
                            var preFsmMi = _assetHelperBridgeType.GetMethod("PrePrimedSentinelOnFsmEnabled", BindingFlags.Static | BindingFlags.Public);
                            if (preFsmMi != null)
                            {
                                var first = instrs[0];
                                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                                il.InsertBefore(first, il.Create(OpCodes.Ldtoken, td));
                                il.InsertBefore(first, il.Create(OpCodes.Call, mod.ImportReference(getTypeFromHandle)));
                                il.InsertBefore(first, il.Create(OpCodes.Call, mod.ImportReference(preFsmMi)));
                                count++;
                            }
                        }
                        else if (md.Name == "OnFSMStateEntered" && md.Parameters.Count == 1)
                        {
                            var safeDisMi = _assetHelperBridgeType.GetMethod("SafeDisableWatcherStunControlFsm", BindingFlags.Static | BindingFlags.Public);
                            if (safeDisMi != null)
                            {
                                var safeDisRef = mod.ImportReference(safeDisMi);
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if (instrs[k].OpCode == OpCodes.Call && instrs[k].Operand is MethodReference mr &&
                                        mr.Name == "Destroy" && mr.DeclaringType != null && mr.DeclaringType.FullName == "UnityEngine.Object" &&
                                        mr.Parameters.Count == 1)
                                    {
                                        instrs[k].Operand = safeDisRef;
                                        count++;
                                    }
                                }
                            }
                        }
                        else if (md.Name == "FixedUpdate" && md.Parameters.Count == 0 && getTypeFromHandle != null)
                        {
                            var postFixedMi = _assetHelperBridgeType.GetMethod("PostPrimedSentinelFixedUpdate", BindingFlags.Static | BindingFlags.Public);
                            if (postFixedMi != null)
                            {
                                var importedTypeFromHandle = mod.ImportReference(getTypeFromHandle);
                                var importedPostFixed = mod.ImportReference(postFixedMi);
                                for (int k = 0; k < instrs.Count; k++) ExpandShortBranchIfNeeded(instrs[k]);
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    var retIns = instrs[k];
                                    if (retIns.OpCode != OpCodes.Ret) continue;
                                    retIns.OpCode = OpCodes.Ldtoken;
                                    retIns.Operand = td;
                                    var callType = il.Create(OpCodes.Call, importedTypeFromHandle);
                                    var callPost = il.Create(OpCodes.Call, importedPostFixed);
                                    var newRet = il.Create(OpCodes.Ret);
                                    il.InsertAfter(retIns, callType);
                                    il.InsertAfter(callType, callPost);
                                    il.InsertAfter(callPost, newRet);
                                    k += 3;
                                    count++;
                                }
                            }
                        }
                        else if (md.Name == "deathReset" && md.Parameters.Count == 0 && getTypeFromHandle != null && instrs.Count > 0)
                        {
                            var preDeathMi = _assetHelperBridgeType.GetMethod("PrePrimedSentinelDeathReset", BindingFlags.Static | BindingFlags.Public);
                            if (preDeathMi != null)
                            {
                                var first = instrs[0];
                                il.InsertBefore(first, il.Create(OpCodes.Ldtoken, td));
                                il.InsertBefore(first, il.Create(OpCodes.Call, mod.ImportReference(getTypeFromHandle)));
                                il.InsertBefore(first, il.Create(OpCodes.Call, mod.ImportReference(preDeathMi)));
                                count++;
                            }
                        }
                    }
                    else if (td.DeclaringType != null && td.DeclaringType.FullName == "Silksong.Mods.PrimedSentinel.PrimedSentinel" &&
                             (td.Name.StartsWith("<ForceNextState>", StringComparison.Ordinal) || td.Name.StartsWith("<scheduleNextState>", StringComparison.Ordinal)) &&
                             md.Name == "MoveNext" && _assetHelperBridgeType != null)
                    {
                        var safeSetStateMi = _assetHelperBridgeType.GetMethod("PrimedSentinelSafeSetState", BindingFlags.Static | BindingFlags.Public);
                        if (safeSetStateMi != null)
                        {
                            var safeSetStateRef = mod.ImportReference(safeSetStateMi);
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is MethodReference mr &&
                                    mr.Name == "SetState" && mr.Parameters.Count == 1)
                                {
                                    instrs[k].OpCode = OpCodes.Call;
                                    instrs[k].Operand = safeSetStateRef;
                                    count++;
                                }
                            }
                        }
                    }
                    // 11d. TribeTowerPerformance.BloodHook: skip raw BepInEx.Hook reflection on ObjectPool.Spawn (SceneOptimizer already disables blood particles)
                    else if (td.FullName == "TribeTowerPerformance.BloodHook" && md.Name == "Install" && md.Parameters.Count == 0)
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    // 11e. HKS1in10000: replace System.Drawing.Bitmap per-pixel loop with fast ImageConversion.LoadImage loader & verified jumpscare.ogg path
                    else if (td.FullName == "HKS1in10000" && _assetHelperBridgeType != null)
                    {
                        if (md.Name == "LoadOverlayFrames" && md.Parameters.Count == 0)
                        {
                            var fastLoadMi = _assetHelperBridgeType.GetMethod("HKS1in10000LoadOverlayFramesFast", BindingFlags.Static | BindingFlags.Public);
                            if (fastLoadMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(fastLoadMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "LoadJumpscareSound" && md.Parameters.Count == 0)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                    }

                    // 12. Fix PharloomsGlory.Constants.get_BASE_PLUGIN_PATH (searches 1 level deep in Paths.PluginPath)
                    if (td.Name == "Constants" && md.Name == "get_BASE_PLUGIN_PATH" && md.Parameters.Count == 0 &&
                        md.ReturnType.FullName == "System.String")
                    {
                        var getModDir = _harmonyBridgeType.GetMethod("GetModBaseDirectory", BindingFlags.Static | BindingFlags.Public);
                        var getExecAsm = typeof(Assembly).GetMethod("GetExecutingAssembly", BindingFlags.Static | BindingFlags.Public);
                        if (getModDir != null && getExecAsm != null)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Call, mod.ImportReference(getExecAsm));
                            il.Emit(OpCodes.Call, mod.ImportReference(getModDir));
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                    }

                    // 13. Make AudioFadeOut / AudioSourceFadeOut completely null-safe (fixes ShamanDiveBind OnEnable/StartFadeout/Start/Update NREs)
                    if ((td.Name == "AudioFadeOut" || td.Name == "AudioSourceFadeOut") &&
                        (md.Name == "Start" || md.Name == "OnEnable" || md.Name == "StartFadeout") &&
                        md.Parameters.Count == 0)
                    {
                        var sourceField = td.Fields.FirstOrDefault(f => f.Name == "source" && f.FieldType.FullName == "UnityEngine.AudioSource");
                        var volumeField = td.Fields.FirstOrDefault(f => f.Name == "initialVolume");
                        var fadingField = td.Fields.FirstOrDefault(f => f.Name == "isFadingOut" || f.Name == "fadingOut");
                        var compType = ResolveRuntimeType("UnityEngine.Component");
                        var getCompOpen = compType?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                        var audioSourceType = ResolveRuntimeType("UnityEngine.AudioSource");
                        var getVolMethod = audioSourceType?.GetProperty("volume")?.GetGetMethod();
                        var setVolMethod = audioSourceType?.GetProperty("volume")?.GetSetMethod();

                        if (sourceField != null && volumeField != null && fadingField != null &&
                            getCompOpen != null && audioSourceType != null && getVolMethod != null && setVolMethod != null)
                        {
                            var importedGetCompOpen = mod.ImportReference(getCompOpen);
                            var getCompAudio = new GenericInstanceMethod(importedGetCompOpen);
                            getCompAudio.GenericArguments.Add(mod.ImportReference(audioSourceType));

                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();

                            var lblHasSource = il.Create(OpCodes.Nop);
                            var lblRet = il.Create(OpCodes.Ret);

                            // if (this.source == null) { this.source = base.GetComponent<AudioSource>(); if (this.source != null) this.initialVolume = this.source.volume; }
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, sourceField);
                            il.Emit(OpCodes.Brtrue_S, lblHasSource);

                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Call, getCompAudio);
                            il.Emit(OpCodes.Stfld, sourceField);

                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, sourceField);
                            il.Emit(OpCodes.Brfalse_S, lblRet);

                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, sourceField);
                            il.Emit(OpCodes.Callvirt, mod.ImportReference(getVolMethod));
                            il.Emit(OpCodes.Stfld, volumeField);

                            il.Append(lblHasSource);
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, sourceField);
                            il.Emit(OpCodes.Brfalse_S, lblRet);

                            if (md.Name == "Start")
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, sourceField);
                                il.Emit(OpCodes.Callvirt, mod.ImportReference(getVolMethod));
                                il.Emit(OpCodes.Stfld, volumeField);

                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Stfld, fadingField);
                            }
                            else if (md.Name == "OnEnable")
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, sourceField);
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, volumeField);
                                il.Emit(OpCodes.Callvirt, mod.ImportReference(setVolMethod));

                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Stfld, fadingField);
                            }
                            else if (md.Name == "StartFadeout")
                            {
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, sourceField);
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldfld, volumeField);
                                il.Emit(OpCodes.Callvirt, mod.ImportReference(setVolMethod));

                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldc_I4_1);
                                il.Emit(OpCodes.Stfld, fadingField);
                            }

                            il.Append(lblRet);
                            count++;
                        }
                    }

                    // 13b. Guard AudioFadeOut.Update against null this.source
                    if ((td.Name == "AudioFadeOut" || td.Name == "AudioSourceFadeOut") && md.Name == "Update" && md.Parameters.Count == 0 && instrs.Count > 0)
                    {
                        var sourceField = td.Fields.FirstOrDefault(f => f.Name == "source" && f.FieldType.FullName == "UnityEngine.AudioSource");
                        if (sourceField != null)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                                ExpandShortBranchIfNeeded(instrs[k]);
                            var first = instrs[0];
                            var retGuard = il.Create(OpCodes.Ret);
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                            il.InsertBefore(first, il.Create(OpCodes.Ldfld, sourceField));
                            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
                            il.InsertBefore(first, retGuard);
                            count++;
                        }
                    }

                    // 13c. Guard DiveEffect.EnableAnimationAndDamager & DisableDamager against null Dive (before HeroController.Start runs SetupDive)
                    if (td.Name == "DiveEffect" && (md.Name == "EnableAnimationAndDamager" || md.Name == "DisableDamager") && md.Parameters.Count == 1 && instrs.Count > 0)
                    {
                        var diveField = td.Fields.FirstOrDefault(f => f.Name == "Dive");
                        if (diveField != null)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                                ExpandShortBranchIfNeeded(instrs[k]);
                            var first = instrs[0];
                            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, diveField));
                            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
                            il.InsertBefore(first, il.Create(OpCodes.Ret));
                            count++;
                        }
                    }

                    // 14. Bonemaster / BossKit mobile optimization & lag elimination:
                    //     - Shelf: disable get_Active (prevent loading unrepacked PC bundles during save load)
                    //     - SharpDrawing: neutralize LateUpdate, Start, Prepare, Begin, Finish, FinishLater (stops synchronous GPU readbacks & CPU upscaling on bone spears/props)
                    //     - Repaint: neutralize LateUpdate, Start, Begin, Finish (stops 4K atlas ReadPixels every frame when Shade is Hero)
                    //     - Cylinder: neutralize OnAudioFilterRead, Awake, Update (stops real-time audio thread DSP math saturation)
                    //     - Silhouette: neutralize Of (stops 96x96 RenderTexture ReadPixels/GetPixels during BoneCounter.Build)
                    //     - Spray: neutralize BodyPoints (returns empty Vector3[], stops 128x128 ReadPixels during particle emission)
                    //     - Recolour: neutralize Baked (returns null, stops GpuCopy.ReadNow)
                    //     - Settings.Bind: set default Shade to "Stock" and SharpDrawings to false (0)
                    //     - Hitches: neutralize Update (stops 50ms Hitch timer)
                    if (modName.IndexOf("Bonemaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        modName.IndexOf("ThirdAcolyte", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        td.FullName.StartsWith("BossKit.", StringComparison.Ordinal) ||
                        td.FullName.StartsWith("Bonemaster.", StringComparison.Ordinal) ||
                        td.FullName.StartsWith("ThirdAcolyte.", StringComparison.Ordinal) ||
                        td.FullName.StartsWith("HkCore.", StringComparison.Ordinal) ||
                        td.FullName.StartsWith("HkBossLoader.", StringComparison.Ordinal))
                    {
                        if ((td.FullName.StartsWith("HkCore.", StringComparison.Ordinal) ||
                             td.FullName.StartsWith("HkBossLoader.", StringComparison.Ordinal)) &&
                            _assetHelperBridgeType != null)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Callvirt || instrs[k].OpCode == OpCodes.Call) &&
                                    instrs[k].Operand is MethodReference mr &&
                                    mr.DeclaringType is GenericInstanceType git &&
                                    (git.ElementType.FullName == "System.Collections.Generic.IReadOnlyDictionary`2" ||
                                     git.ElementType.FullName == "System.Collections.Generic.Dictionary`2") &&
                                    git.GenericArguments.Count == 2 &&
                                    git.GenericArguments[0].FullName == "System.String" &&
                                    git.GenericArguments[1] is GenericInstanceType valGit &&
                                    valGit.ElementType.FullName == "System.ValueTuple`2" &&
                                    valGit.GenericArguments.Count == 2)
                                {
                                    string vt0 = valGit.GenericArguments[0].FullName;
                                    string vt1 = valGit.GenericArguments[1].FullName;
                                    string helperName = null;
                                    if (vt0 == "System.String" && vt1 == "System.String")
                                    {
                                        if (mr.Name == "get_Item") helperName = "SafeDictGet_Str_VT2StrStr";
                                        else if (mr.Name == "get_Keys") helperName = "SafeDictGetKeys_Str_VT2StrStr";
                                    }
                                    else if (vt0 == "System.String[]" && vt1 == "System.String[]")
                                    {
                                        if (mr.Name == "TryGetValue") helperName = "SafeDictTryGet_Str_VT2StrArrStrArr";
                                    }
                                    else if (vt0 == "System.String" && vt1 == "System.Int32")
                                    {
                                        if (mr.Name == "get_Item") helperName = "SafeDictGet_Str_VT2StrInt";
                                        else if (mr.Name == "ContainsKey") helperName = "SafeDictContainsKey_Str_VT2StrInt";
                                    }
                                    if (helperName != null)
                                    {
                                        var hMi = _assetHelperBridgeType.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                                        if (hMi != null)
                                        {
                                            instrs[k] = il.Create(OpCodes.Call, mod.ImportReference(hMi));
                                            count++;
                                        }
                                    }
                                }
                            }
                        }
                        if (td.FullName == "BossKit.World.Shelf" && md.Name == "get_Active" && md.Parameters.Count == 0)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "ThirdAcolyte.World.Shelf" && md.Name == "Init" && md.Parameters.Count == 0 && md.ReturnType.FullName == "System.Void")
                        {
                            // Prevent ThirdAcolyte.World.Shelf from loading Hang_07 (4,113 handles) concurrently on Menu_Title while FastPreloaderUI loads Cradle_03 on Loading.Preload;
                            // AssetHelperBridge.CoEnsureThirdAcolyteShelfPreloaded() runs serially inside FastPreloaderUI instead.
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "BossKit.Fight.SharpDrawing")
                        {
                            if (md.Name == ".cctor" || md.Name == "LateUpdate" || md.Name == "Start" || md.Name == "Prepare" || md.Name == "Begin" || md.Name == "Finish")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                            else if (md.Name == "FinishLater" && _assetHelperBridgeType != null)
                            {
                                var emptyMi = _assetHelperBridgeType.GetMethod("EmptyCoroutine", BindingFlags.Static | BindingFlags.Public);
                                if (emptyMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Call, mod.ImportReference(emptyMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Core.Repaint")
                        {
                            if (md.Name == "LateUpdate" || md.Name == "Start" || md.Name == "Begin" || md.Name == "Finish")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "Bonemaster.World.Cylinder")
                        {
                            if (md.Name == "OnAudioFilterRead" || md.Name == "Awake" || md.Name == "Update")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "Bonemaster.Boss.Silhouette" && md.Name == "Of")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((td.FullName == "BossKit.Core.Spray" || td.FullName == "ThirdAcolyte.Core.Spray") && md.Name == "BodyPoints")
                        {
                            if (md.ReturnType is ArrayType at)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldc_I4_0);
                                il.Emit(OpCodes.Newarr, at.ElementType);
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "ThirdAcolyte.Boss.Moves.ChoirBurst/Choir" && md.Name == "Transient" && md.ReturnType.FullName == "System.Single")
                        {
                            // Neutralize AudioClip.GetData() on compressed Android clips (returns ChoirBurst's own 0.69f fallback cleanly with zero allocation/error).
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldc_R4, 0.69f);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((td.FullName == "BossKit.Core.Recolour" || td.FullName == "ThirdAcolyte.Core.Recolour") && (md.Name == ".cctor" || md.Name == "Baked" || md.Name == "LateUpdate"))
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            if (md.ReturnType.FullName != "System.Void")
                                il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "BossKit.Core.Shake" && _assetHelperBridgeType != null)
                        {
                            if (md.Name == ".cctor")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                            else if (md.Name == "Shortened" && md.Parameters.Count == 2)
                            {
                                var shortMi = _assetHelperBridgeType.GetMethod("BonemasterShakeShortened", BindingFlags.Static | BindingFlags.Public);
                                if (shortMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, mod.ImportReference(shortMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.LensCloud" && md.Name == "Throw" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                        {
                            var throwMi = _assetHelperBridgeType.GetMethod("BonemasterLensCloudThrow", BindingFlags.Static | BindingFlags.Public);
                            if (throwMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(throwMi));
                                il.Emit(OpCodes.Ldnull);
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.LensCloud" && md.Name == "ClearAll" && md.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            var clearMi = _assetHelperBridgeType.GetMethod("BonemasterLensCloudClearAll", BindingFlags.Static | BindingFlags.Public);
                            if (clearMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Call, mod.ImportReference(clearMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.LensCloud" && (md.Name == "LateUpdate" || md.Name == "Start"))
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            if (md.ReturnType.FullName != "System.Void")
                                il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "HkCore.PlayerDataOverlay" && md.Name == "RedirectHkFieldUsers")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        // HkCore.PlayerDataOverlay::InHkCode runs natively (33B IL2CPP-safe) — never force true for Silksong HUD/Hero in HK scenes.
                        else if (td.FullName == "HkCore.PlayerDataOverlay" && (md.Name == "EnsureSlot" || md.Name == "Set") && _assetHelperBridgeType != null)
                        {
                            var syncMi = _assetHelperBridgeType.GetMethod("HkCoreSyncOverlayToPlayerData", BindingFlags.Static | BindingFlags.Public);
                            if (syncMi != null)
                            {
                                var syncRef = mod.ImportReference(syncMi);
                                for (int k = instrs.Count - 1; k >= 0; k--)
                                {
                                    if (instrs[k].OpCode == OpCodes.Ret)
                                    {
                                        il.InsertBefore(instrs[k], il.Create(OpCodes.Call, syncRef));
                                        count++;
                                    }
                                }
                            }
                        }
                        else if (td.FullName == "HkCore.HkToolRefills" && md.Name == "Init" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var trInitMi = _assetHelperBridgeType.GetMethod("HkCoreToolRefillsInitBridge", BindingFlags.Static | BindingFlags.Public);
                            if (trInitMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(trInitMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "HkBossLoader.GodseekerMode" && md.Name == "Init" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var gsInitMi = _assetHelperBridgeType.GetMethod("HkBossLoaderGodseekerInitBridge", BindingFlags.Static | BindingFlags.Public);
                            if (gsInitMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(gsInitMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "HkCore.HkLanguage" && _assetHelperBridgeType != null)
                        {
                            var tcMi = _assetHelperBridgeType.GetMethod("TextInfoToTitleCaseSafe", BindingFlags.Static | BindingFlags.Public);
                            if (tcMi != null)
                            {
                                var tcRef = mod.ImportReference(tcMi);
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                        instrs[k].Operand is MethodReference mr &&
                                        mr.Name == "ToTitleCase" &&
                                        mr.DeclaringType != null &&
                                        mr.DeclaringType.FullName == "System.Globalization.TextInfo")
                                    {
                                        instrs[k] = il.Create(OpCodes.Call, tcRef);
                                        count++;
                                    }
                                }
                            }
                        }
                        else if (td.FullName == "HkCore.HkScenes/<LoadShaders>d__63" && md.Name == "MoveNext")
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is MethodReference mr &&
                                    mr.Name == "get_graphicsDeviceType")
                                {
                                    instrs[k] = il.Create(OpCodes.Ldc_I4, 21); // GraphicsDeviceType.Vulkan -> maps to "glvk"
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.Prop" && md.Name == "FitHurt" && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "BossKit.Fight.SpriteProp" && md.Name == "Outline" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                        {
                            var outlineMi = _assetHelperBridgeType.GetMethod("BonemasterSpritePropOutline", BindingFlags.Static | BindingFlags.Public);
                            if (outlineMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(outlineMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.SpriteProp" && md.Name == "Show" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var showMi = _assetHelperBridgeType.GetMethod("BonemasterSpritePropShow", BindingFlags.Static | BindingFlags.Public);
                            if (showMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(showMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.SpriteProp" && md.Name == ".ctor")
                        {
                            foreach (var fd in td.Fields)
                            {
                                if (fd.Name == "turns" || fd.Name == "turning")
                                    fd.FieldType = mod.TypeSystem.Object;
                            }
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Newobj &&
                                    k + 1 < instrs.Count &&
                                    instrs[k + 1].OpCode == OpCodes.Stfld &&
                                    instrs[k + 1].Operand is FieldReference fr &&
                                    fr.Name == "turns")
                                {
                                    instrs[k].OpCode = OpCodes.Ldnull;
                                    instrs[k].Operand = null;
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.SpriteProp" && md.Name == "Turn" && md.Parameters.Count == 3 && _assetHelperBridgeType != null)
                        {
                            var turnMi = _assetHelperBridgeType.GetMethod("BonemasterSpritePropTurn", BindingFlags.Static | BindingFlags.Public);
                            if (turnMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Ldarg_2);
                                il.Emit(OpCodes.Ldarg_3);
                                il.Emit(OpCodes.Call, mod.ImportReference(turnMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.SpriteProp" && md.Name == "Update" && md.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            var stepTurnMi = _assetHelperBridgeType.GetMethod("BonemasterSpritePropStepTurn", BindingFlags.Static | BindingFlags.Public);
                            if (stepTurnMi != null)
                            {
                                int startTurnIdx = -1;
                                int placeCallIdx = -1;
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if (startTurnIdx < 0 &&
                                        instrs[k].OpCode == OpCodes.Ldarg_0 &&
                                        k + 1 < instrs.Count &&
                                        (instrs[k + 1].OpCode == OpCodes.Ldflda || instrs[k + 1].OpCode == OpCodes.Ldfld) &&
                                        instrs[k + 1].Operand is FieldReference fr &&
                                        fr.Name == "turning")
                                    {
                                        startTurnIdx = k;
                                    }
                                    else if (startTurnIdx >= 0 &&
                                             instrs[k].OpCode == OpCodes.Ldarg_0 &&
                                             k + 1 < instrs.Count &&
                                             instrs[k + 1].OpCode == OpCodes.Call &&
                                             instrs[k + 1].Operand is MethodReference mr &&
                                             mr.Name == "Place")
                                    {
                                        placeCallIdx = k;
                                        break;
                                    }
                                }
                                if (startTurnIdx >= 0 && placeCallIdx > startTurnIdx + 1)
                                {
                                    // Keep instrs[startTurnIdx] as Ldarg_0 so all switch branch targets remain intact
                                    instrs[startTurnIdx + 1].OpCode = OpCodes.Call;
                                    instrs[startTurnIdx + 1].Operand = mod.ImportReference(stepTurnMi);
                                    for (int k = startTurnIdx + 2; k < placeCallIdx; k++)
                                    {
                                        instrs[k].OpCode = OpCodes.Nop;
                                        instrs[k].Operand = null;
                                    }
                                    foreach (var v in md.Body.Variables)
                                    {
                                        if (v.VariableType != null && v.VariableType.FullName.IndexOf("ValueTuple", StringComparison.Ordinal) >= 0)
                                            v.VariableType = mod.TypeSystem.Object;
                                    }
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "Bonemaster.Boss.ImpactFrame" || td.FullName.StartsWith("Bonemaster.Boss.ImpactFrame/", StringComparison.Ordinal))
                        {
                            foreach (var fd in td.Fields)
                            {
                                if (fd.FieldType != null && fd.FieldType.FullName.IndexOf("ValueTuple", StringComparison.Ordinal) >= 0)
                                    fd.FieldType = mod.TypeSystem.Object;
                            }
                            if (md.Name != ".ctor" && md.Name != ".cctor")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                if (md.ReturnType.FullName == "System.Boolean" || md.ReturnType.FullName == "System.Int32")
                                    il.Emit(OpCodes.Ldc_I4_0);
                                else if (md.ReturnType.FullName == "System.Single")
                                    il.Emit(OpCodes.Ldc_R4, 0f);
                                else if (md.ReturnType.FullName == "UnityEngine.Vector3")
                                {
                                    var vLocal = new VariableDefinition(md.ReturnType);
                                    md.Body.Variables.Add(vLocal);
                                    md.Body.InitLocals = true;
                                    il.Emit(OpCodes.Ldloc_0);
                                }
                                else if (md.ReturnType.FullName != "System.Void")
                                    il.Emit(OpCodes.Ldnull);
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "Bonemaster.World.Corpse" && md.Name == "Entry") ||
                                 (td.FullName.StartsWith("Bonemaster.World.Corpse/<Lay>d__", StringComparison.Ordinal) && md.Name == "MoveNext"))
                        {
                            for (int k = 0; k < instrs.Count - 1; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Ldstr &&
                                    instrs[k].Operand is string flagKey &&
                                    (flagKey == "defeatedAntQueen" || flagKey == "hasNeedolinMemoryPowerup") &&
                                    instrs[k + 1].OpCode == OpCodes.Callvirt &&
                                    instrs[k + 1].Operand is MethodReference gbm &&
                                    gbm.Name == "GetBool")
                                {
                                    instrs[k].OpCode = OpCodes.Pop;
                                    instrs[k].Operand = null;
                                    instrs[k + 1].OpCode = OpCodes.Ldc_I4_1;
                                    instrs[k + 1].Operand = null;
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "Bonemaster.Settings" && md.Name == "Bind")
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Ldstr && (string)instrs[k].Operand == "Hero")
                                {
                                    instrs[k].Operand = "Stock";
                                    count++;
                                }
                                else if (instrs[k].OpCode == OpCodes.Ldstr && (string)instrs[k].Operand == "SharpDrawings")
                                {
                                    for (int j = k; j < Math.Min(k + 6, instrs.Count); j++)
                                    {
                                        if (instrs[j].OpCode == OpCodes.Ldc_I4_1)
                                        {
                                            instrs[j].OpCode = OpCodes.Ldc_I4_0;
                                            count++;
                                            break;
                                        }
                                    }
                                }
                                else if (instrs[k].OpCode == OpCodes.Ldstr &&
                                         ((string)instrs[k].Operand == "AlwaysHis" || (string)instrs[k].Operand == "Abandoned"))
                                {
                                    for (int j = k; j < Math.Min(k + 6, instrs.Count); j++)
                                    {
                                        if (instrs[j].OpCode == OpCodes.Ldc_I4_0)
                                        {
                                            instrs[j].OpCode = OpCodes.Ldc_I4_1;
                                            count++;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.Title" && md.Name == "WantBig" && md.Parameters.Count == 0)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((td.FullName == "BossKit.Core.Clock/Hitches" || td.FullName == "ThirdAcolyte.Core.Clock/Hitches") && md.Name == "Update")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((td.FullName == "BossKit.World.Borrow" || td.FullName == "ThirdAcolyte.World.Borrow") && md.Name == "FindLoaded" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var findLoadedMi = _assetHelperBridgeType.GetMethod("BonemasterBorrowFindLoaded", BindingFlags.Static | BindingFlags.Public);
                            if (findLoadedMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(findLoadedMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.World.Borrow" || td.FullName == "ThirdAcolyte.World.Borrow") && md.Name == "Open" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var openFastMi = _assetHelperBridgeType.GetMethod("BonemasterBorrowOpenFast", BindingFlags.Static | BindingFlags.Public);
                            if (openFastMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(openFastMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.World.Borrow" || td.FullName == "ThirdAcolyte.World.Borrow") && md.Name == "IndexFiles" && md.Parameters.Count == 0)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((td.FullName == "BossKit.World.Borrow" || td.FullName == "ThirdAcolyte.World.Borrow") && (md.Name == "LoadAll" || md.Name == "ByAddress" || md.Name == "LoadAtTitle") && md.ReturnType.FullName == "System.Collections.IEnumerator" && _assetHelperBridgeType != null)
                        {
                            var emptyCoroMi = _assetHelperBridgeType.GetMethod("EmptyCoroutine", BindingFlags.Static | BindingFlags.Public);
                            if (emptyCoroMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Call, mod.ImportReference(emptyCoroMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.World.Words" && _assetHelperBridgeType != null)
                        {
                            if (md.Name == ".cctor")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                            else if (md.Name == "Set" && md.Parameters.Count == 3)
                            {
                                var setMi = _assetHelperBridgeType.GetMethod("BonemasterWordsSet", BindingFlags.Static | BindingFlags.Public);
                                if (setMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Ldarg_2);
                                    il.Emit(OpCodes.Call, mod.ImportReference(setMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "Get" && md.Parameters.Count == 3)
                            {
                                var getMi = _assetHelperBridgeType.GetMethod("BonemasterWordsGet", BindingFlags.Static | BindingFlags.Public);
                                if (getMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Ldarg_2);
                                    il.Emit(OpCodes.Call, mod.ImportReference(getMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "Has" && md.Parameters.Count == 3)
                            {
                                var hasMi = _assetHelperBridgeType.GetMethod("BonemasterWordsHas", BindingFlags.Static | BindingFlags.Public);
                                if (hasMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Ldarg_2);
                                    il.Emit(OpCodes.Call, mod.ImportReference(hasMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.Machines" && _assetHelperBridgeType != null)
                        {
                            if (md.Name == ".cctor")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                            else if (md.Name == "On" && md.Parameters.Count == 3)
                            {
                                var onMi = _assetHelperBridgeType.GetMethod("BonemasterMachinesOn", BindingFlags.Static | BindingFlags.Public);
                                if (onMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Ldarg_2);
                                    il.Emit(OpCodes.Call, mod.ImportReference(onMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "OnHit" && md.Parameters.Count == 2)
                            {
                                var onHitMi = _assetHelperBridgeType.GetMethod("BonemasterMachinesOnHit", BindingFlags.Static | BindingFlags.Public);
                                if (onHitMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, mod.ImportReference(onHitMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "OnFsmStart" && md.Parameters.Count == 1)
                            {
                                var onFsmMi = _assetHelperBridgeType.GetMethod("BonemasterMachinesOnFsmStart", BindingFlags.Static | BindingFlags.Public);
                                if (onFsmMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Call, mod.ImportReference(onFsmMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "OnHitPrefix" && md.Parameters.Count == 2)
                            {
                                var onHitPreMi = _assetHelperBridgeType.GetMethod("BonemasterMachinesOnHitPrefix", BindingFlags.Static | BindingFlags.Public);
                                if (onHitPreMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, mod.ImportReference(onHitPreMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                        }
                        else if (td.FullName == "BossKit.Fight.Title" && _assetHelperBridgeType != null)
                        {
                            if (md.Name == ".cctor")
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                            else if (md.Name == "Set" && md.Parameters.Count == 4)
                            {
                                var setMi = _assetHelperBridgeType.GetMethod("BonemasterTitleSet", BindingFlags.Static | BindingFlags.Public);
                                if (setMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Ldarg_2);
                                    il.Emit(OpCodes.Ldarg_3);
                                    il.Emit(OpCodes.Call, mod.ImportReference(setMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "Get" && md.Parameters.Count == 2)
                            {
                                var getMi = _assetHelperBridgeType.GetMethod("BonemasterTitleGet", BindingFlags.Static | BindingFlags.Public);
                                if (getMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, mod.ImportReference(getMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                            else if (md.Name == "ShowBig" && md.Parameters.Count == 1)
                            {
                                var showBigMi = _assetHelperBridgeType.GetMethod("BonemasterTitleShowBig", BindingFlags.Static | BindingFlags.Public);
                                if (showBigMi != null)
                                {
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Call, mod.ImportReference(showBigMi));
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                }
                            }
                        }
                        else if ((td.FullName == "BossKit.Core.Pictures" || td.FullName == "ThirdAcolyte.Core.Pictures") && md.Name == "Find" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                        {
                            var picFindMi = _assetHelperBridgeType.GetMethod("BonemasterPicturesFind", BindingFlags.Static | BindingFlags.Public);
                            if (picFindMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(picFindMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Core.DrawingCrop" && md.Name == ".cctor")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "BossKit.Core.DrawingCrop" && md.Name == "Bottom" && md.Parameters.Count == 3 && _assetHelperBridgeType != null)
                        {
                            var cropMi = _assetHelperBridgeType.GetMethod("BonemasterDrawingCropBottom", BindingFlags.Static | BindingFlags.Public);
                            if (cropMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Ldarg_2);
                                il.Emit(OpCodes.Call, mod.ImportReference(cropMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.Core.Pool" || td.FullName == "ThirdAcolyte.Core.Pool") && md.Name == "Find" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var poolFindMi = _assetHelperBridgeType.GetMethod("BonemasterPoolFind", BindingFlags.Static | BindingFlags.Public);
                            if (poolFindMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(poolFindMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.Core.Sound" || td.FullName == "ThirdAcolyte.Core.Sound") && md.Name == "InMemory" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var soundInMemMi = _assetHelperBridgeType.GetMethod("BonemasterSoundInMemory", BindingFlags.Static | BindingFlags.Public);
                            if (soundInMemMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(soundInMemMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.Core.Library" || td.FullName == "ThirdAcolyte.Core.Library") && md.Name == "Get" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var libGetMi = _assetHelperBridgeType.GetMethod("BonemasterLibraryGet", BindingFlags.Static | BindingFlags.Public);
                            if (libGetMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(libGetMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if ((td.FullName == "BossKit.Core.Spray" || td.FullName == "ThirdAcolyte.Core.Spray") && md.Name == "FindMaterial" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var findMatMi = _assetHelperBridgeType.GetMethod("BonemasterFindMaterial", BindingFlags.Static | BindingFlags.Public);
                            if (findMatMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(findMatMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Core.Cutout" && md.Name == "Make" && md.Parameters.Count == 5 && _assetHelperBridgeType != null)
                        {
                            var cutoutMakeMi = _assetHelperBridgeType.GetMethod("BonemasterCutoutMake", BindingFlags.Static | BindingFlags.Public);
                            if (cutoutMakeMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Ldarg_2);
                                il.Emit(OpCodes.Ldarg_3);
                                il.Emit(OpCodes.Ldarg_S, md.Parameters[4]);
                                il.Emit(OpCodes.Call, mod.ImportReference(cutoutMakeMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (td.FullName == "BossKit.Core.GpuCopy" && md.Name == ".cctor")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (td.FullName == "Bonemaster.World.HeartDeath" && md.Name == "Find" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var poolFindMi = _assetHelperBridgeType.GetMethod("BonemasterPoolFind", BindingFlags.Static | BindingFlags.Public);
                            if (poolFindMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(poolFindMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }

                        // Replace any remaining Resources.FindObjectsOfTypeAll<T>() calls inside BossKit/Bonemaster methods with pre-cached O(1) arrays
                        if (_assetHelperBridgeType != null && md.Body != null && md.Body.Instructions.Count > 0)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Call && instrs[k].Operand is GenericInstanceMethod gim &&
                                    gim.Name == "FindObjectsOfTypeAll" && gim.DeclaringType != null &&
                                    gim.DeclaringType.FullName == "UnityEngine.Resources" && gim.GenericArguments.Count == 1)
                                {
                                    string gaName = gim.GenericArguments[0].FullName;
                                    string bridgeHelperName = null;
                                    if (gaName == "UnityEngine.Material") bridgeHelperName = "BonemasterGetCachedMaterialsArray";
                                    else if (gaName == "EnemyDeathEffectsProfile") bridgeHelperName = "BonemasterGetCachedDeathProfilesArray";
                                    else if (gaName == "tk2dSpriteAnimation") bridgeHelperName = "BonemasterGetCachedLibrariesArray";
                                    else if (gaName == "UnityEngine.Sprite") bridgeHelperName = "BonemasterGetCachedSpritesArray";
                                    else if (gaName == "UnityEngine.AudioClip") bridgeHelperName = "BonemasterGetCachedAudioClipsArray";

                                    if (bridgeHelperName != null)
                                    {
                                        var helperMi = _assetHelperBridgeType.GetMethod(bridgeHelperName, BindingFlags.Static | BindingFlags.Public);
                                        if (helperMi != null)
                                        {
                                            instrs[k].Operand = mod.ImportReference(helperMi);
                                            count++;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 15. Fix WeaverCrest progression (prevent PurgeFromSave from unequipping custom skills every 1s),
                    // suppress tutorial popups, eliminate 25-step synchronous asset warmup lag spike when switching crests,
                    // and prevent custom Sprint FSM / HeavyFinisher crash on Grand Reed & multi-hit combos.
                    if (td.FullName == "WeaverCrestMod.WeaverProgression")
                    {
                        if (md.Name == "QueueLearnedPrompt" || md.Name == "TickLearnedPrompts")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((md.Name == "Earned" ||
                                  md.Name == "get_SilkWaltzEarned" ||
                                  md.Name == "get_PhantomDiveEarned" ||
                                  md.Name == "get_BlinkEarned" ||
                                  md.Name == "get_AirSpinEarned" ||
                                  md.Name == "get_BackstepEarned" ||
                                  md.Name == "get_ThreadNeedleFound") &&
                                 md.ReturnType.FullName == "System.Boolean")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (md.Name == "SyncTool" && instrs.Count > 0)
                        {
                            for (int k = 1; k < instrs.Count; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Callvirt &&
                                    instrs[k].Operand is MethodReference umr &&
                                    umr.Name == "Unlock" &&
                                    instrs[k - 1].OpCode == OpCodes.Ldc_I4_1)
                                {
                                    // Change PopupFlags.Default (1) -> PopupFlags.None (0)
                                    instrs[k - 1].OpCode = OpCodes.Ldc_I4_0;
                                    count++;
                                }
                            }
                        }
                    }
                    if (td.FullName == "WeaverCrestMod.WeaverKnowledgeRelic" &&
                        md.Name == "get_Learned" && md.Parameters.Count == 0 && md.ReturnType.FullName == "System.Boolean")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    if (td.FullName == "WeaverCrestMod.WeaverFeatures" &&
                        md.Name == "PurgeFromSave" && md.ReturnType.FullName == "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    if (td.FullName == "WeaverCrestMod.WeaverSprintStateRegistry" &&
                        md.Name == "Apply" && !md.ReturnType.IsValueType && md.ReturnType.FullName != "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    if (td.FullName == "WeaverCrestMod.WeaverCombat")
                    {
                        if ((md.Name == "TickAssetWarmup" ||
                             md.Name == "InstallAssetWarmupHooks" ||
                             md.Name == "ResolveChargedSlashVoice" ||
                             md.Name == "LaunchHeavyFinisher" ||
                             md.Name == "InterruptCrossSlashReadyFromDamage") &&
                            md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((md.Name == "get_HasPendingAssetWarmup" ||
                                  md.Name == "TryStartDashCollisionAttackFromRunInput" ||
                                  md.Name == "TryStartDashCollisionAttack") &&
                                 md.ReturnType.FullName == "System.Boolean")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (md.Name == "GetAllOutBundle" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                        {
                            var getBundleMi = _assetHelperBridgeType.GetMethod("WeaverGetAllOutBundle", BindingFlags.Static | BindingFlags.Public);
                            if (getBundleMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(getBundleMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "IsFixedEnemyName" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                        {
                            var fixedEnemyMi = _assetHelperBridgeType.GetMethod("WeaverIsFixedEnemyName", BindingFlags.Static | BindingFlags.Public);
                            if (fixedEnemyMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(fixedEnemyMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "TryLoadAllOutAsset" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                        {
                            var loadAssetMi = _assetHelperBridgeType.GetMethod("WeaverTryLoadAllOutAsset", BindingFlags.Static | BindingFlags.Public);
                            if (loadAssetMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(loadAssetMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "EnsureCrossSlashZoneAssets" && md.Parameters.Count == 0 && _assetHelperBridgeType != null)
                        {
                            var ensureClipsMi = _assetHelperBridgeType.GetMethod("EnsureWeaverCrossSlashClipsReady", BindingFlags.Static | BindingFlags.Public);
                            if (ensureClipsMi != null)
                            {
                                var importedEnsure = mod.ImportReference(ensureClipsMi);
                                for (int k = instrs.Count - 1; k >= 0; k--)
                                {
                                    if (instrs[k].OpCode == OpCodes.Ret)
                                    {
                                        il.InsertBefore(instrs[k], il.Create(OpCodes.Call, importedEnsure));
                                        count++;
                                    }
                                }
                            }
                        }
                        else if (md.Name == "ResolveCrossSlashCaptureCircle" && md.Parameters.Count == 2 && md.ReturnType.FullName == "System.Single")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldarg_2);
                            il.Emit(OpCodes.Ldarg_1);
                            il.Emit(OpCodes.Stobj, md.Parameters[0].ParameterType);
                            il.Emit(OpCodes.Ldc_R4, 5.8f);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (md.Name == "TryStartCrossSlashSkill" ||
                                 md.Name == "TryStartPhantomDiveSkill" ||
                                 md.Name == "TryStartBrutalSmashSkill" ||
                                 md.Name == "TryStartCocoonSkill" ||
                                 md.Name == "TryFireSilkBullet" ||
                                 md.Name == "TryFireSilkBulletVolley" ||
                                 md.Name == "PlayBrutalSmashReleaseAndFire")
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if (instrs[k].OpCode == OpCodes.Call &&
                                    instrs[k].Operand is MethodReference cmr &&
                                    cmr.Name == "IsWeaverEquipped" &&
                                    cmr.Parameters.Count == 0)
                                {
                                    instrs[k].OpCode = OpCodes.Ldc_I4_1;
                                    instrs[k].Operand = null;
                                    count++;
                                }
                            }
                        }
                    }

                    if (td.FullName == "WeaverCrestMod.WeaverSilkSkillCast" && md.Name == "TryReportSuccessfulEnemyHit")
                    {
                        for (int k = 0; k < instrs.Count; k++)
                        {
                            if (instrs[k].OpCode == OpCodes.Call &&
                                instrs[k].Operand is MethodReference cmr &&
                                cmr.Name == "IsWeaverEquipped" &&
                                cmr.Parameters.Count == 0)
                            {
                                instrs[k].OpCode = OpCodes.Ldc_I4_1;
                                instrs[k].Operand = null;
                                count++;
                            }
                        }
                    }

                    // 15b. Also allow WeaverCrest's Silk Skill coroutine state machines (<AimAndFireBrutalSmash>, <PlayBrutalSmashReleaseAndFire>,
                    // <FireBrutalSmashProjectile>, <ExplodeBrutalSmash>, <RunCocoon>, <PlayCrossSlashSkill>, <PlayPhantomDiveSkill>)
                    // to continue running when cast on non-Weaver crests, and make Cross Slash invulnerable during its windup!
                    if (td.DeclaringType != null && td.DeclaringType.Name == "WeaverCombat" && md.Name == "MoveNext" &&
                        (td.Name.StartsWith("<AimAndFireBrutalSmash>", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<PlayBrutalSmashReleaseAndFire>", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<FireBrutalSmashProjectile>", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<ExplodeBrutalSmash>", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<RunCocoon>", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<PlayCrossSlashSkill", StringComparison.Ordinal) ||
                         td.Name.StartsWith("<PlayPhantomDiveSkill", StringComparison.Ordinal)))
                    {
                        for (int k = 0; k < instrs.Count; k++)
                        {
                            if (instrs[k].OpCode == OpCodes.Call &&
                                instrs[k].Operand is MethodReference cmr &&
                                cmr.Name == "IsWeaverEquipped" &&
                                cmr.Parameters.Count == 0)
                            {
                                instrs[k].OpCode = OpCodes.Ldc_I4_1;
                                instrs[k].Operand = null;
                                count++;
                            }
                            else if (k > 0 &&
                                     instrs[k].OpCode == OpCodes.Stfld &&
                                     instrs[k].Operand is FieldReference cfr &&
                                     cfr.Name == "crossSlashInvulnerablePhase" &&
                                     instrs[k - 1].OpCode == OpCodes.Ldc_I4_0)
                            {
                                instrs[k - 1].OpCode = OpCodes.Ldc_I4_1;
                                count++;
                            }
                        }
                    }

                    // 15b-1b. HeroController::reaperModeEffect MissingFieldException fix: redirect ldfld/stfld to HarmonyBridge safe helpers
                    if (_harmonyBridgeType != null)
                    {
                        MethodInfo getReaperEffMi = null;
                        MethodInfo setReaperEffMi = null;
                        for (int k = 0; k < instrs.Count; k++)
                        {
                            if ((instrs[k].OpCode == OpCodes.Ldfld || instrs[k].OpCode == OpCodes.Stfld) &&
                                instrs[k].Operand is FieldReference rfr &&
                                rfr.Name == "reaperModeEffect" &&
                                rfr.DeclaringType != null &&
                                rfr.DeclaringType.Name == "HeroController")
                            {
                                if (instrs[k].OpCode == OpCodes.Ldfld)
                                {
                                    if (getReaperEffMi == null)
                                        getReaperEffMi = _harmonyBridgeType.GetMethod("GetHeroReaperModeEffect", BindingFlags.Static | BindingFlags.Public);
                                    if (getReaperEffMi != null)
                                    {
                                        instrs[k].OpCode = OpCodes.Call;
                                        instrs[k].Operand = mod.ImportReference(getReaperEffMi);
                                        count++;
                                    }
                                }
                                else
                                {
                                    if (setReaperEffMi == null)
                                        setReaperEffMi = _harmonyBridgeType.GetMethod("SetHeroReaperModeEffect", BindingFlags.Static | BindingFlags.Public);
                                    if (setReaperEffMi != null)
                                    {
                                        instrs[k].OpCode = OpCodes.Call;
                                        instrs[k].Operand = mod.ImportReference(setReaperEffMi);
                                        count++;
                                    }
                                }
                            }
                        }
                    }

                    // 15b-2. Bonemaster Android catalog bundle match: replace String.Equals(string, string) inside <Open>d__* with BonemasterBundleFileNameMatches
                    if ((modName.IndexOf("Bonemaster", StringComparison.OrdinalIgnoreCase) >= 0 || modName.IndexOf("ThirdAcolyte", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        td.Name.StartsWith("<Open>", StringComparison.Ordinal) && md.Name == "MoveNext" && _assetHelperBridgeType != null)
                    {
                        var bmMatchMi = _assetHelperBridgeType.GetMethod("BonemasterBundleFileNameMatches", BindingFlags.Static | BindingFlags.Public);
                        if (bmMatchMi != null)
                        {
                            var importedBmMatch = mod.ImportReference(bmMatchMi);
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is MethodReference smr &&
                                    smr.Name == "Equals" && smr.Parameters.Count == 1 &&
                                    smr.DeclaringType != null && smr.DeclaringType.FullName == "System.String")
                                {
                                    instrs[k].OpCode = OpCodes.Call;
                                    instrs[k].Operand = importedBmMatch;
                                    count++;
                                }
                            }
                        }
                    }

                    // 15c. BossRush: Fix Run History crash caused by ValueTuple in HistoryPage.Draw / HistoryPage.Build / StatsPages
                    if (modName.IndexOf("BossRush", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        td.FullName.StartsWith("BossRush.Menu.", StringComparison.Ordinal))
                    {
                        if (td.FullName == "BossRush.Menu.HistoryPage")
                        {
                            if (md.Name == "Draw" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                            {
                                var bridgeMi = _assetHelperBridgeType.GetMethod("BossRushHistoryDraw", BindingFlags.Static | BindingFlags.Public);
                                if (bridgeMi != null)
                                {
                                    var imported = mod.ImportReference(bridgeMi);
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, imported);
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                    LogInfo("[CecilEngine] Redirected BossRush.Menu.HistoryPage.Draw -> AssetHelperBridge.BossRushHistoryDraw");
                                }
                            }
                            else if (md.Name == "Build" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                            {
                                var bridgeMi = _assetHelperBridgeType.GetMethod("BossRushHistoryBuild", BindingFlags.Static | BindingFlags.Public);
                                if (bridgeMi != null)
                                {
                                    var imported = mod.ImportReference(bridgeMi);
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Call, imported);
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                    LogInfo("[CecilEngine] Redirected BossRush.Menu.HistoryPage.Build -> AssetHelperBridge.BossRushHistoryBuild");
                                }
                            }
                        }
                        else if (td.FullName == "BossRush.Menu.StatsPages")
                        {
                            if (md.Name == "BuildTotals" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                            {
                                var bridgeMi = _assetHelperBridgeType.GetMethod("BossRushStatsBuildTotals", BindingFlags.Static | BindingFlags.Public);
                                if (bridgeMi != null)
                                {
                                    var imported = mod.ImportReference(bridgeMi);
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, imported);
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                    LogInfo("[CecilEngine] Redirected BossRush.Menu.StatsPages.BuildTotals -> AssetHelperBridge.BossRushStatsBuildTotals");
                                }
                            }
                            else if (md.Name == "BuildBosses" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                            {
                                var bridgeMi = _assetHelperBridgeType.GetMethod("BossRushStatsBuildBosses", BindingFlags.Static | BindingFlags.Public);
                                if (bridgeMi != null)
                                {
                                    var imported = mod.ImportReference(bridgeMi);
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Call, imported);
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                    LogInfo("[CecilEngine] Redirected BossRush.Menu.StatsPages.BuildBosses -> AssetHelperBridge.BossRushStatsBuildBosses");
                                }
                            }
                            else if (md.Name == "DrawTable" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                            {
                                var bridgeMi = _assetHelperBridgeType.GetMethod("BossRushStatsDrawTable", BindingFlags.Static | BindingFlags.Public);
                                if (bridgeMi != null)
                                {
                                    var imported = mod.ImportReference(bridgeMi);
                                    md.Body.Instructions.Clear();
                                    md.Body.Variables.Clear();
                                    md.Body.ExceptionHandlers.Clear();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Ldarg_1);
                                    il.Emit(OpCodes.Call, imported);
                                    il.Emit(OpCodes.Ret);
                                    count++;
                                    LogInfo("[CecilEngine] Redirected BossRush.Menu.StatsPages.DrawTable -> AssetHelperBridge.BossRushStatsDrawTable");
                                }
                            }
                        }
                    }

                    // 15b. Fix Stylemeter HitContext.Read (skip HealthManager.IsBlockingByDirection which triggers nested hooks)
                    if (td.FullName == "Stylemeter.Game.HitContext" && md.Name == "Read" && md.Parameters.Count == 2)
                    {
                        for (int k = 0; k < instrs.Count; k++)
                        {
                            if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                instrs[k].Operand is MethodReference hmr &&
                                hmr.Name == "IsBlockingByDirection" && hmr.Parameters.Count == 3)
                            {
                                // Stack has: [hm, dir, attackType, specialType] -> pop 4, push 0 (false)
                                instrs[k].OpCode = OpCodes.Pop;
                                instrs[k].Operand = null;
                                var p1 = il.Create(OpCodes.Pop);
                                var p2 = il.Create(OpCodes.Pop);
                                var p3 = il.Create(OpCodes.Pop);
                                var ldc0 = il.Create(OpCodes.Ldc_I4_0);
                                il.InsertAfter(instrs[k], p1);
                                il.InsertAfter(p1, p2);
                                il.InsertAfter(p2, p3);
                                il.InsertAfter(p3, ldc0);
                                k += 4;
                                count++;
                            }
                        }
                    }

                    // 15c. Fix WeaverSacredHeartHoming crash when throwing Straight Pin / red tools:
                    //      - Strip Component.GetComponent<IProjectile>() / GetComponentInChildren<IProjectile>(bool) in Configure()
                    //      - Route ApplyVelocity(Vector2) to HarmonyBridge.WeaverSacredHeart_ApplyVelocitySafe
                    //      - Make WeaverSacredHeart.EnsureWhiteTrail a safe no-op on mobile GPUs
                    if (td.Name == "WeaverSacredHeart" && md.Name == "EnsureWhiteTrail" && md.Parameters.Count == 1 && md.ReturnType.FullName == "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    if (td.Name == "WeaverSacredHeartHoming")
                    {
                        if (md.Name == "Configure")
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is GenericInstanceMethod gimProj &&
                                    gimProj.GenericArguments.Count == 1 &&
                                    gimProj.GenericArguments[0].Name == "IProjectile")
                                {
                                    int pCount = gimProj.Parameters.Count;
                                    instrs[k].OpCode = OpCodes.Pop; // pop 'this' or bool
                                    instrs[k].Operand = null;
                                    var cur = instrs[k];
                                    for (int p = 0; p < pCount; p++)
                                    {
                                        var popExtra = il.Create(OpCodes.Pop);
                                        il.InsertAfter(cur, popExtra);
                                        cur = popExtra;
                                        k++;
                                    }
                                    var ldNull = il.Create(OpCodes.Ldnull);
                                    il.InsertAfter(cur, ldNull);
                                    k++;
                                    count++;
                                }
                            }
                        }
                        else if (md.Name == "ApplyVelocity" && md.Parameters.Count == 1 && md.ReturnType.FullName == "System.Void")
                        {
                            var safeApplyMi = _harmonyBridgeType.GetMethod("WeaverSacredHeart_ApplyVelocitySafe", BindingFlags.Static | BindingFlags.Public);
                            if (safeApplyMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Ldarg_1);
                                il.Emit(OpCodes.Call, mod.ImportReference(safeApplyMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                    }

                    // 15d. Fix crash when entering Save Slots 2, 3, or 4:
                    //      - In WeaverProgression.SyncTool, replace ToolItem.Unlock(Action, PopupFlags) with HarmonyBridge.WeaverProgression_SafeUnlockTool(ToolItem)
                    //      - In WeaverAbilityMarker.ShowLearnedPrompt, make it a safe log-only method without calling CollectableUIMsg.Spawn(UIMsgDisplay, ...)
                    if (td.Name == "WeaverProgression" && md.Name == "SyncTool")
                    {
                        var safeUnlockMi = _harmonyBridgeType.GetMethod("WeaverProgression_SafeUnlockTool", BindingFlags.Static | BindingFlags.Public);
                        if (safeUnlockMi != null)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is MethodReference umr &&
                                    umr.Name == "Unlock" && umr.Parameters.Count == 2)
                                {
                                    // Stack has [tool, null, 1] -> pop 1, pop null, call WeaverProgression_SafeUnlockTool(tool)
                                    var popFlags = il.Create(OpCodes.Pop);
                                    var popAction = il.Create(OpCodes.Pop);
                                    il.InsertBefore(instrs[k], popFlags);
                                    il.InsertBefore(instrs[k], popAction);
                                    k += 2;
                                    instrs[k].OpCode = OpCodes.Call;
                                    instrs[k].Operand = mod.ImportReference(safeUnlockMi);
                                    count++;
                                }
                            }
                        }
                    }
                    if (td.Name == "WeaverAbilityMarker" && md.Name == "ShowLearnedPrompt" && md.ReturnType.FullName == "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }

                    // 15e. Pharloom's Glory lighting: call AssetHelperBridge.ApplyPharloomsGloryLightingImmediately at the end of PatcherManager.SetColorValues
                    if (td.FullName == "PharloomsGlory.Managers.PatcherManager" && md.Name == "SetColorValues" && _assetHelperBridgeType != null)
                    {
                        var applyLightMi = _assetHelperBridgeType.GetMethod("ApplyPharloomsGloryLightingImmediately", BindingFlags.Static | BindingFlags.Public);
                        if (applyLightMi != null && instrs.Count > 0 && instrs[instrs.Count - 1].OpCode == OpCodes.Ret)
                        {
                            il.InsertBefore(instrs[instrs.Count - 1], il.Create(OpCodes.Call, mod.ImportReference(applyLightMi)));
                            count++;
                        }
                    }

                    // 16. Fix Stylemeter MeterHud boxes (□) on mobile GPUs by preserving original TMP_FontAsset atlas & shader material
                    // Note: WithBorder is an instance method on MeterHud (arg0 = this, arg1 = TMP_FontAsset).
                    if (td.Name == "MeterHud")
                    {
                        if (md.Name == "WithBorder" && md.Parameters.Count == 1 && !md.ReturnType.IsValueType)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(md.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if ((md.Name == "Underlay" || md.Name == "Outline") && md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (md.Name == "Describe" && md.Parameters.Count == 1 && md.ReturnType.FullName == "System.String")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldstr, "ok (prewarmed)");
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                    }

                    // 17. Pharloom's Glory: batch-extract 95 scene assets in 4 single-scene passes and immediately enable Red Coral Gorge visuals
                    // Leave PharloomsGlory.Util.GetSpriteName and GetAllGameObjects completely intact so all 31 VALID_TEXTURE_SHEETS match custom PNGs natively.
                    if (td.FullName == "PharloomsGlory.Modifiers.SceneModifier" && md.Name == "Modify" && md.Parameters.Count == 1 && _assetHelperBridgeType != null)
                    {
                        var beforeModMi = _assetHelperBridgeType.GetMethod("PharloomsGloryBeforeSceneModify", BindingFlags.Static | BindingFlags.Public);
                        if (beforeModMi != null && instrs.Count > 0)
                        {
                            var firstIns = instrs[0];
                            il.InsertBefore(firstIns, il.Create(OpCodes.Ldarg_1));
                            il.InsertBefore(firstIns, il.Create(OpCodes.Call, mod.ImportReference(beforeModMi)));
                            count++;
                        }
                        var afterModMi = _assetHelperBridgeType.GetMethod("PharloomsGloryAfterSceneModify", BindingFlags.Static | BindingFlags.Public);
                        if (afterModMi != null && instrs.Count > 0 && instrs[instrs.Count - 1].OpCode == OpCodes.Ret)
                        {
                            var retIns = instrs[instrs.Count - 1];
                            il.InsertBefore(retIns, il.Create(OpCodes.Ldarg_1));
                            il.InsertBefore(retIns, il.Create(OpCodes.Call, mod.ImportReference(afterModMi)));
                            count++;
                        }
                    }
                    if (td.FullName == "PharloomsGlory.Plugin" && md.Name == "OnSceneLoaded" && md.Parameters.Count == 2 && _assetHelperBridgeType != null)
                    {
                        var skipScMi = _assetHelperBridgeType.GetMethod("ShouldSkipPharloomsGlorySceneLoaded", BindingFlags.Static | BindingFlags.Public);
                        if (skipScMi != null && instrs.Count > 0)
                        {
                            var firstIns = instrs[0];
                            il.InsertBefore(firstIns, il.Create(OpCodes.Ldarg_1));
                            il.InsertBefore(firstIns, il.Create(OpCodes.Ldarg_2));
                            il.InsertBefore(firstIns, il.Create(OpCodes.Call, mod.ImportReference(skipScMi)));
                            il.InsertBefore(firstIns, il.Create(OpCodes.Brfalse, firstIns));
                            il.InsertBefore(firstIns, il.Create(OpCodes.Ret));
                            count++;
                        }
                    }
                    if (td.FullName == "PharloomsGlory.Managers.AssetManager" && _assetHelperBridgeType != null)
                    {
                        if (md.Name == "get_IsLoaded" && md.Parameters.Count == 0 && md.ReturnType.FullName == "System.Boolean")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                        else if (md.Name == "LoadAllAssets" && md.Parameters.Count == 0 && md.ReturnType.FullName == "System.Void")
                        {
                            var batchLoadMi = _assetHelperBridgeType.GetMethod("LoadPharloomsGloryAssets", BindingFlags.Static | BindingFlags.Public);
                            if (batchLoadMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Ldarg_0);
                                il.Emit(OpCodes.Call, mod.ImportReference(batchLoadMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                            }
                        }
                        else if (md.Name == "GetAsset" && md.Parameters.Count == 2 && md.ReturnType.FullName == "System.Boolean")
                        {
                            var filterMi = _assetHelperBridgeType.GetMethod("FilterLoadedManagedAsset", BindingFlags.Static | BindingFlags.Public);
                            if (filterMi != null && instrs.Count >= 2 && instrs[instrs.Count - 1].OpCode == OpCodes.Ret)
                            {
                                var retIns = instrs[instrs.Count - 1];
                                il.InsertBefore(retIns, il.Create(OpCodes.Ldarg_2));
                                il.InsertBefore(retIns, il.Create(OpCodes.Ldind_Ref));
                                il.InsertBefore(retIns, il.Create(OpCodes.Call, mod.ImportReference(filterMi)));
                                count++;
                            }
                        }
                    }
                    // 21. AeternalEverwatcher: prevent SIGSEGV in Unity 6000 LateUpdateManager::IntegrateLists (m_AddLists)
                    //     caused by synchronous Object.Destroy(fsm) inside SceneManager.sceneLoaded("Coral_39"),
                    //     and guard Helpers.ModifyTerrain / InitSandEffects against missing scene objects.
                    if (modName.IndexOf("AeternalEverwatcher", StringComparison.OrdinalIgnoreCase) >= 0 && _assetHelperBridgeType != null)
                    {
                        if (md.Name == "<Awake>b__23_0" && md.HasBody)
                        {
                            var safeDisMi = _assetHelperBridgeType.GetMethod("SafeDisableWatcherStunControlFsm", BindingFlags.Static | BindingFlags.Public);
                            if (safeDisMi != null)
                            {
                                var safeDisRef = mod.ImportReference(safeDisMi);
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if (instrs[k].OpCode == OpCodes.Call && instrs[k].Operand is MethodReference mr &&
                                        mr.Name == "Destroy" && mr.DeclaringType != null && mr.DeclaringType.FullName == "UnityEngine.Object" &&
                                        mr.Parameters.Count == 1)
                                    {
                                        instrs[k].Operand = safeDisRef;
                                        count++;
                                    }
                                }
                            }
                        }
                        if (td.FullName == "AeternalEverwatcher.Helpers" && md.Name == "InitSandEffects" && md.HasBody)
                        {
                            var safeDesMi = _assetHelperBridgeType.GetMethod("SafeDeactivateAndDestroyLater", BindingFlags.Static | BindingFlags.Public);
                            if (safeDesMi != null)
                            {
                                var safeDesRef = mod.ImportReference(safeDesMi);
                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if (instrs[k].OpCode == OpCodes.Call && instrs[k].Operand is MethodReference mr &&
                                        mr.Name == "Destroy" && mr.DeclaringType != null && mr.DeclaringType.FullName == "UnityEngine.Object" &&
                                        mr.Parameters.Count == 1)
                                    {
                                        instrs[k].Operand = safeDesRef;
                                        count++;
                                    }
                                }
                            }
                        }
                        if (td.FullName == "AeternalEverwatcher.Helpers" && md.Name == "ModifyTerrain" && md.Parameters.Count == 0 && md.HasBody)
                        {
                            var safeModMi = _assetHelperBridgeType.GetMethod("SafeAeternalEverwatcherModifyTerrain", BindingFlags.Static | BindingFlags.Public);
                            if (safeModMi != null)
                            {
                                md.Body.Instructions.Clear();
                                md.Body.Variables.Clear();
                                md.Body.ExceptionHandlers.Clear();
                                il.Emit(OpCodes.Call, mod.ImportReference(safeModMi));
                                il.Emit(OpCodes.Ret);
                                count++;
                                continue;
                            }
                        }
                        if (td.FullName == "AeternalEverwatcher.PatchesLikeFromEldenRing" && md.Name == "HealthManager_Hit" && md.HasBody)
                        {
                            for (int k = 0; k < instrs.Count; k++)
                            {
                                if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                    instrs[k].Operand is MethodReference rmr &&
                                    rmr.Name == "get_root" && rmr.DeclaringType != null &&
                                    rmr.DeclaringType.FullName == "UnityEngine.Transform")
                                {
                                    instrs[k].OpCode = OpCodes.Nop;
                                    instrs[k].Operand = null;
                                    count++;
                                }
                            }
                        }
                    }

                    if (td.FullName == "PharloomsGlory.Components.FishParticleManager" && md.Name == "Update" && md.Parameters.Count == 0)
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }

                    // 18. TripleProjectiles: prevent ObjectPool corruption and crash when hitting enemies with Silk Waltz or in Red Coral Gorge
                    if (td.Name == "TripleProjectilesPlugin" && md.Name == "FilterObject" && md.Parameters.Count == 2)
                    {
                        var safeFilterMi = _harmonyBridgeType.GetMethod("TripleProjectiles_FilterObject", BindingFlags.Static | BindingFlags.Public);
                        if (safeFilterMi != null)
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldarg_1);
                            il.Emit(OpCodes.Call, mod.ImportReference(safeFilterMi));
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                    }
                    if (td.Name == "ProjectileCloneMarker" && md.Name == "OnDisable" && md.Parameters.Count == 0)
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }

                    // 19. Stylemeter: neutralize synchronous disk session logging during combat
                    if (modName.IndexOf("Stylemeter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !md.IsConstructor && md.ReturnType.FullName == "System.Void" &&
                        instrs.Any(insOp => insOp.Operand is MethodReference fmr &&
                                            (fmr.DeclaringType?.FullName == "System.IO.File" ||
                                             fmr.DeclaringType?.FullName == "System.IO.StreamWriter")))
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }

                    // 19a. Rewrite PC Addressables platform folder string "StandaloneWindows64" -> "Android" (fixes Final Judge Lava Rock bundle lookup & RevoltAndDevotion)
                    for (int k = 0; k < instrs.Count; k++)
                    {
                        if (instrs[k].OpCode == OpCodes.Ldstr && instrs[k].Operand is string strLit && strLit == "StandaloneWindows64")
                        {
                            instrs[k].Operand = "Android";
                            count++;
                        }
                        else if ((instrs[k].OpCode == OpCodes.Call || instrs[k].OpCode == OpCodes.Callvirt) &&
                                 instrs[k].Operand is MethodReference pmr)
                        {
                            string pDecl = pmr.DeclaringType?.FullName ?? "";
                            if (pDecl == "CameraLockArea" && pmr.Name == "get_IgnoreInSuperjump" && pmr.Parameters.Count == 0)
                            {
                                instrs[k].OpCode = OpCodes.Pop;
                                instrs[k].Operand = null;
                                md.Body.GetILProcessor().InsertAfter(instrs[k], Instruction.Create(OpCodes.Ldc_I4_0));
                                k++;
                                count++;
                                continue;
                            }
                            if (pDecl == "HarmonyLib.AccessTools" && (pmr.Name == "Method" || pmr.Name == "DeclaredMethod") && !pmr.HasThis)
                            {
                                if (pmr.Parameters.Count == 4 && pmr.Parameters[0].ParameterType.FullName == "System.Type")
                                {
                                    var bridgeType = new TypeReference("SilksongMobileAPI", "HarmonyBridge", mod, mobileApiRef);
                                    var bm = new MethodReference(pmr.Name == "DeclaredMethod" ? "AccessToolsDeclaredMethod" : "AccessToolsMethod", pmr.ReturnType, bridgeType) { HasThis = false };
                                    foreach (var p in pmr.Parameters) bm.Parameters.Add(new ParameterDefinition(p.ParameterType));
                                    instrs[k].OpCode = OpCodes.Call;
                                    instrs[k].Operand = mod.ImportReference(bm);
                                    count++;
                                    continue;
                                }
                                else if (pmr.Parameters.Count == 3 && pmr.Parameters[0].ParameterType.FullName == "System.String")
                                {
                                    var bridgeType = new TypeReference("SilksongMobileAPI", "HarmonyBridge", mod, mobileApiRef);
                                    var bm = new MethodReference("AccessToolsMethodByString", pmr.ReturnType, bridgeType) { HasThis = false };
                                    foreach (var p in pmr.Parameters) bm.Parameters.Add(new ParameterDefinition(p.ParameterType));
                                    instrs[k].OpCode = OpCodes.Call;
                                    instrs[k].Operand = mod.ImportReference(bm);
                                    count++;
                                    continue;
                                }
                            }
                            if (pDecl == "WidowSceneLoader" && pmr.Name == "LoadWidowIntoScene" && pmr.Parameters.Count == 2)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "AssetHelperBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("RevoltLoadWidowIntoScene", pmr.ReturnType, bridgeType)
                                {
                                    HasThis = false
                                };
                                foreach (var p in pmr.Parameters)
                                    bridgeMethod.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "WidowSceneLoader" && pmr.Name == "LoadAsset" && pmr.Parameters.Count == 1)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "AssetHelperBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("RevoltLoadAsset", pmr.ReturnType, bridgeType)
                                {
                                    HasThis = false
                                };
                                foreach (var p in pmr.Parameters)
                                    bridgeMethod.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "HutongGames.PlayMaker.Fsm" && pmr.Name == "GetState" && pmr.Parameters.Count == 1 && pmr.HasThis)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("GetFsmStateSafe", pmr.ReturnType, bridgeType)
                                {
                                    HasThis = false
                                };
                                bridgeMethod.Parameters.Add(new ParameterDefinition("fsm", Mono.Cecil.ParameterAttributes.None, pmr.DeclaringType));
                                bridgeMethod.Parameters.Add(new ParameterDefinition("stateName", Mono.Cecil.ParameterAttributes.None, pmr.Parameters[0].ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "HutongGames.PlayMaker.Fsm" && pmr.Name == "set_States" && pmr.Parameters.Count == 1 && pmr.HasThis)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("SetFsmStatesSafe", mod.TypeSystem.Void, bridgeType)
                                {
                                    HasThis = false
                                };
                                bridgeMethod.Parameters.Add(new ParameterDefinition("fsm", Mono.Cecil.ParameterAttributes.None, pmr.DeclaringType));
                                bridgeMethod.Parameters.Add(new ParameterDefinition("states", Mono.Cecil.ParameterAttributes.None, pmr.Parameters[0].ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "HutongGames.PlayMaker.FsmState" && pmr.Name == "get_Actions" && pmr.Parameters.Count == 0 && pmr.HasThis)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("GetStateActionsSafe", pmr.ReturnType, bridgeType)
                                {
                                    HasThis = false
                                };
                                bridgeMethod.Parameters.Add(new ParameterDefinition("state", Mono.Cecil.ParameterAttributes.None, pmr.DeclaringType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "HutongGames.PlayMaker.FsmState" && pmr.Name == "set_Actions" && pmr.Parameters.Count == 1 && pmr.HasThis)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("SetStateActionsSafe", mod.TypeSystem.Void, bridgeType)
                                {
                                    HasThis = false
                                };
                                bridgeMethod.Parameters.Add(new ParameterDefinition("state", Mono.Cecil.ParameterAttributes.None, pmr.DeclaringType));
                                bridgeMethod.Parameters.Add(new ParameterDefinition("actions", Mono.Cecil.ParameterAttributes.None, pmr.Parameters[0].ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                            else if (pDecl == "HutongGames.PlayMaker.FsmState" && pmr.Name == "CopyActionData" && pmr.Parameters.Count == 1 && pmr.HasThis)
                            {
                                var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                                var bridgeMethod = new MethodReference("CopyActionDataSafe", mod.TypeSystem.Void, bridgeType)
                                {
                                    HasThis = false
                                };
                                bridgeMethod.Parameters.Add(new ParameterDefinition("targetState", Mono.Cecil.ParameterAttributes.None, pmr.DeclaringType));
                                bridgeMethod.Parameters.Add(new ParameterDefinition("sourceState", Mono.Cecil.ParameterAttributes.None, pmr.Parameters[0].ParameterType));
                                instrs[k].OpCode = OpCodes.Call;
                                instrs[k].Operand = mod.ImportReference(bridgeMethod);
                                count++;
                            }
                        }
                        else if (instrs[k].OpCode == OpCodes.Newobj && instrs[k].Operand is MethodReference ctorMr &&
                                 ctorMr.DeclaringType?.FullName == "HutongGames.PlayMaker.FsmState" &&
                                 ctorMr.Parameters.Count == 1 &&
                                 ctorMr.Parameters[0].ParameterType?.FullName == "HutongGames.PlayMaker.FsmState")
                        {
                            var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                            var bridgeMethod = new MethodReference("CloneFsmStateSafe", ctorMr.DeclaringType, bridgeType)
                            {
                                HasThis = false
                            };
                            bridgeMethod.Parameters.Add(new ParameterDefinition("sourceState", Mono.Cecil.ParameterAttributes.None, ctorMr.Parameters[0].ParameterType));
                            instrs[k].OpCode = OpCodes.Call;
                            instrs[k].Operand = mod.ImportReference(bridgeMethod);
                            count++;
                        }
                        else if (instrs[k].OpCode == OpCodes.Ldfld && instrs[k].Operand is FieldReference ldfPfr &&
                                 ldfPfr.DeclaringType?.FullName == "HutongGames.PlayMaker.FsmState" && ldfPfr.Name == "actions")
                        {
                            var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                            var bridgeMethod = new MethodReference("GetStateActionsSafe", ldfPfr.FieldType, bridgeType)
                            {
                                HasThis = false
                            };
                            bridgeMethod.Parameters.Add(new ParameterDefinition("state", Mono.Cecil.ParameterAttributes.None, ldfPfr.DeclaringType));
                            instrs[k].OpCode = OpCodes.Call;
                            instrs[k].Operand = mod.ImportReference(bridgeMethod);
                            count++;
                        }
                        else if (instrs[k].OpCode == OpCodes.Stfld && instrs[k].Operand is FieldReference pfr &&
                                 pfr.DeclaringType?.FullName == "HutongGames.PlayMaker.FsmState" && pfr.Name == "actions")
                        {
                            var bridgeType = new TypeReference("SilksongMobileAPI", "PlayMakerHybridCLRBridge", mod, mobileApiRef);
                            var bridgeMethod = new MethodReference("SetStateActionsSafe", mod.TypeSystem.Void, bridgeType)
                            {
                                HasThis = false
                            };
                            bridgeMethod.Parameters.Add(new ParameterDefinition("state", Mono.Cecil.ParameterAttributes.None, pfr.DeclaringType));
                            bridgeMethod.Parameters.Add(new ParameterDefinition("actions", Mono.Cecil.ParameterAttributes.None, pfr.FieldType));
                            instrs[k].OpCode = OpCodes.Call;
                            instrs[k].Operand = mod.ImportReference(bridgeMethod);
                            count++;
                        }

                    // 19c. Direct compatibility for PC Silksong.I18N.dll and PC AssetHelper.dll
                    if (td.FullName == "Silksong.I18N.LanguagePatches" &&
                        (md.Name == "LoadAllModSheets" || md.Name == "LoadModSheet") &&
                        md.ReturnType.FullName == "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        if (_harmonyBridgeType != null)
                        {
                            var syncMi = _harmonyBridgeType.GetMethod("SyncModLocalizationIntoGame", BindingFlags.Static | BindingFlags.Public);
                            if (syncMi != null)
                                il.Emit(OpCodes.Call, mod.ImportReference(syncMi));
                        }
                        il.Emit(OpCodes.Ret);
                        count++;
                    }
                    if ((td.FullName == "Silksong.AssetHelper.AssetsToolsPatch" && md.Name == "Init") ||
                        (td.FullName == "Silksong.AssetHelper.Core.BundleMetadata" && md.Name == "Setup") ||
                        (td.FullName == "Silksong.AssetHelper.Plugin.StartupOverrideManager" && md.Name == "Hook") ||
                        (td.FullName == "Silksong.AssetHelper.AssetHelperPlugin" && md.Name == "InitLibLogging"))
                    {
                        if (md.ReturnType.FullName == "System.Void")
                        {
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();
                            md.Body.ExceptionHandlers.Clear();
                            il.Emit(OpCodes.Ret);
                            count++;
                        }
                    }

                    }

                    // 19b. Stitchwork UI scaling: lower GUIHelper.get_Scale minimum clamp from 0.75f to 0.35f so lowering in-game resolution on mobile keeps Stitchwork UI at exact 1080p proportions
                    if (td.FullName == "Stitchwork.GUI.GUIHelper" && md.Name == "get_Scale")
                    {
                        for (int k = 0; k < instrs.Count; k++)
                        {
                            if (instrs[k].OpCode == OpCodes.Ldc_R4 && instrs[k].Operand is float fVal && Math.Abs(fVal - 0.75f) < 0.001f)
                            {
                                instrs[k].Operand = 0.35f;
                                count++;
                            }
                        }
                    }
                    if (td.FullName == "Stitchwork.GUI.ScreenSizeWatch" && md.Name == "Tick" && md.ReturnType.FullName == "System.Void")
                    {
                        md.Body.Instructions.Clear();
                        md.Body.Variables.Clear();
                        md.Body.ExceptionHandlers.Clear();
                        il.Emit(OpCodes.Ret);
                        count++;
                    }

                    // 20. Resolution-independent scaling for all PC Mod OnGUI() methods (fixes oversized/tiny UI across resolutions)
                    if (md.Name == "OnGUI" && !md.IsStatic && !md.IsAbstract && md.Parameters.Count == 0 &&
                        md.ReturnType.FullName == "System.Void" && instrs.Count > 0 && _harmonyBridgeType != null)
                    {
                        var beginGuiMi = _harmonyBridgeType.GetMethod("BeginPcModOnGuiScale", BindingFlags.Static | BindingFlags.Public);
                        var endGuiMi = _harmonyBridgeType.GetMethod("EndPcModOnGuiScale", BindingFlags.Static | BindingFlags.Public);
                        if (beginGuiMi != null && endGuiMi != null)
                        {
                            var importedBegin = mod.ImportReference(beginGuiMi);
                            var importedEnd = mod.ImportReference(endGuiMi);
                            for (int k = 0; k < instrs.Count; k++)
                                ExpandShortBranchIfNeeded(instrs[k]);
                            il.InsertBefore(instrs[0], il.Create(OpCodes.Call, importedBegin));
                            for (int k = 1; k < instrs.Count; k++)
                            {
                                var rIns = instrs[k];
                                if (rIns.OpCode != OpCodes.Ret) continue;
                                // Mutate rIns in place so branches to ret hit EndPcModOnGuiScale() first
                                rIns.OpCode = OpCodes.Call;
                                rIns.Operand = importedEnd;
                                var newRet = il.Create(OpCodes.Ret);
                                il.InsertAfter(rIns, newRet);
                                k++;
                            }
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        private static void ExpandShortBranchIfNeeded(Instruction ins)
        {
            if (ins == null) return;
            if (ins.OpCode == OpCodes.Br_S) ins.OpCode = OpCodes.Br;
            else if (ins.OpCode == OpCodes.Brfalse_S) ins.OpCode = OpCodes.Brfalse;
            else if (ins.OpCode == OpCodes.Brtrue_S) ins.OpCode = OpCodes.Brtrue;
            else if (ins.OpCode == OpCodes.Leave_S) ins.OpCode = OpCodes.Leave;
            else if (ins.OpCode == OpCodes.Beq_S) ins.OpCode = OpCodes.Beq;
            else if (ins.OpCode == OpCodes.Bne_Un_S) ins.OpCode = OpCodes.Bne_Un;
            else if (ins.OpCode == OpCodes.Bge_S) ins.OpCode = OpCodes.Bge;
            else if (ins.OpCode == OpCodes.Bge_Un_S) ins.OpCode = OpCodes.Bge_Un;
            else if (ins.OpCode == OpCodes.Bgt_S) ins.OpCode = OpCodes.Bgt;
            else if (ins.OpCode == OpCodes.Bgt_Un_S) ins.OpCode = OpCodes.Bgt_Un;
            else if (ins.OpCode == OpCodes.Ble_S) ins.OpCode = OpCodes.Ble;
            else if (ins.OpCode == OpCodes.Ble_Un_S) ins.OpCode = OpCodes.Ble_Un;
            else if (ins.OpCode == OpCodes.Blt_S) ins.OpCode = OpCodes.Blt;
            else if (ins.OpCode == OpCodes.Blt_Un_S) ins.OpCode = OpCodes.Blt_Un;
        }


        private static Type ResolveRuntimeType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            try
            {
                var corlibType = typeof(object).Assembly.GetType(typeName, false);
                if (corlibType != null) return corlibType;
            }
            catch { }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null) continue;
                try
                {
                    string aName = asm.GetName().Name ?? "";
                    if (aName == "netstandard" || aName.StartsWith("SilksongMobileAPI_AutoShims_", StringComparison.Ordinal))
                        continue;
                    var t = asm.GetType(typeName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void FlattenMissingNestedTypeReference(TypeReference tref)
        {
            if (tref == null || tref.DeclaringType == null) return;

            // Represent a missing nested type as a top-level shim type under a dotted namespace.
            // This avoids emitting a parent TypeDef without the nested TypeDef required by the
            // original TypeRef resolution scope (e.g. MonoDetour.Cil.ILManipulationInfo/Manipulator).
            var parentNames = new Stack<string>();
            var root = tref;
            while (root.DeclaringType != null)
            {
                parentNames.Push(root.DeclaringType.Name ?? "");
                root = root.DeclaringType;
            }

            string flattenedNamespace = root.Namespace ?? "";
            foreach (var parentName in parentNames)
            {
                if (string.IsNullOrEmpty(parentName)) continue;
                flattenedNamespace = string.IsNullOrEmpty(flattenedNamespace)
                    ? parentName
                    : flattenedNamespace + "." + parentName;
            }

            tref.DeclaringType = null;
            tref.Namespace = flattenedNamespace;
        }

        private static int VerifyAndAutoShimTypeReferences(string modName, ModuleDefinition mod, AssemblyNameReference mobileApiRef, out byte[] shimBytes)
        {
            shimBytes = null;
            EnsureBoundMainApi();
            var bepInExRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "BepInEx");
            var missingRootTypes = new Dictionary<string, TypeReference>(StringComparer.Ordinal);

            // Touch all fields, properties, methods, and local variables so Deferred reader populates all TypeReferences
            foreach (var td in GetAllTypesRecursive(mod.Types))
            {
                foreach (var fd in td.Fields) { var _ = fd.FieldType; }
                foreach (var pd in td.Properties) { var _ = pd.PropertyType; }
                foreach (var md in td.Methods)
                {
                    var _ = md.ReturnType;
                    foreach (var p in md.Parameters) { var __ = p.ParameterType; }
                    if (md.HasBody)
                    {
                        foreach (var v in md.Body.Variables) { var ___ = v.VariableType; }
                    }
                }
            }

            foreach (var tref in mod.GetTypeReferences().ToArray())
            {
                if (tref == null || tref is TypeSpecification) continue;
                var rootTref = tref;
                while (rootTref.DeclaringType != null)
                    rootTref = rootTref.DeclaringType;

                string scopeName = rootTref.Scope?.Name ?? "";
                if (rootTref.Namespace == "System" && rootTref.Name.StartsWith("ValueTuple", StringComparison.Ordinal))
                {
                    // On Android IL2CPP, UnityLinker strips System.ValueTuple`5..`8 from mscorlib.dll (only ValueTuple and ValueTuple`1..`4 exist in mscorlib).
                    // Only redirect to mscorlib if the exact ValueTuple arity exists in typeof(object).Assembly; otherwise fall through to synthesize it in AutoShims!
                    if (typeof(object).Assembly.GetType(rootTref.FullName.Replace('/', '+'), false) != null)
                    {
                        var mscorlibRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
                        if (mscorlibRef == null)
                        {
                            mscorlibRef = new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0))
                            {
                                PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }
                            };
                            mod.AssemblyReferences.Add(mscorlibRef);
                        }
                        else if (mscorlibRef.PublicKeyToken == null || mscorlibRef.PublicKeyToken.Length == 0)
                        {
                            mscorlibRef.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                        }
                        rootTref.Scope = mscorlibRef;
                        continue;
                    }
                }
                else if (scopeName == "mscorlib")
                {
                    if (typeof(object).Assembly.GetType(tref.FullName.Replace('/', '+'), false) != null)
                        continue;
                }

                string rtFullName = tref.FullName.Replace('/', '+');
                if (_mainApiAssembly != null)
                {
                    try
                    {
                        if (_mainApiAssembly.GetType(rtFullName, false) != null)
                        {
                            rootTref.Scope = mobileApiRef;
                            continue;
                        }
                    }
                    catch { }
                }

                var foundType = ResolveRuntimeType(rtFullName);
                if (foundType != null)
                {
                    string foundAsmName = foundType.Assembly.GetName().Name ?? "";
                    if (foundAsmName == "IRIS" || foundAsmName == "SilksongMobileAPI")
                    {
                        rootTref.Scope = mobileApiRef;
                    }
                    else if (foundAsmName == "BepInEx" && bepInExRef != null)
                    {
                        rootTref.Scope = bepInExRef;
                    }
                    else if (!string.IsNullOrEmpty(foundAsmName) &&
                             !string.Equals(foundAsmName, scopeName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (scopeName == "netstandard" && (foundAsmName == "mscorlib" || foundAsmName.StartsWith("System", StringComparison.Ordinal)))
                        {
                            // Keep scope as netstandard if netstandard on runtime forwards it, unless it is ValueTask/AsyncValueTaskMethodBuilder which are only in mscorlib on Android
                            if (rootTref.Name.IndexOf("ValueTask", StringComparison.Ordinal) < 0)
                                continue;
                        }
                        var targetAsmRef = mod.AssemblyReferences.FirstOrDefault(r => string.Equals(r.Name, foundAsmName, StringComparison.OrdinalIgnoreCase));
                        if (targetAsmRef == null)
                        {
                            var ver = foundType.Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
                            targetAsmRef = new AssemblyNameReference(foundAsmName, ver)
                            {
                                PublicKeyToken = foundType.Assembly.GetName().GetPublicKeyToken() ?? Array.Empty<byte>()
                            };
                            mod.AssemblyReferences.Add(targetAsmRef);
                        }
                        else if (foundAsmName == "mscorlib" && (targetAsmRef.PublicKeyToken == null || targetAsmRef.PublicKeyToken.Length == 0))
                        {
                            targetAsmRef.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                        }
                        rootTref.Scope = targetAsmRef;
                        LogInfo($"[PCMod:{modName}] Redirected TypeRef '{rtFullName}' scope '{scopeName}' -> '{foundAsmName}'");
                    }
                    continue;
                }

                string rootFullName = rootTref.FullName;
                if (tref.DeclaringType != null)
                {
                    string rootRtName = rootFullName.Replace('/', '+');
                    bool rootExists = ResolveRuntimeType(rootRtName) != null ||
                                      (_mainApiAssembly != null && _mainApiAssembly.GetType(rootRtName, false) != null);

                    // If the outer type is absent, synthesize it too; if it exists but the nested
                    // type does not, synthesize only the missing nested type.
                    if (!rootExists && !missingRootTypes.ContainsKey(rootFullName))
                        missingRootTypes[rootFullName] = rootTref;

                    FlattenMissingNestedTypeReference(tref);
                    string flattenedFullName = tref.FullName;
                    if (!missingRootTypes.ContainsKey(flattenedFullName))
                        missingRootTypes[flattenedFullName] = tref;
                    continue;
                }
                if (!missingRootTypes.ContainsKey(rootFullName))
                    missingRootTypes[rootFullName] = rootTref;
            }

            if (missingRootTypes.Count == 0) return 0;

            string shimAsmName = $"SilksongMobileAPI_AutoShims_{modName}";
            var shimNameDef = new AssemblyNameDefinition(shimAsmName, new Version(1, 0, 0, 0));
            var shimAsmDef = AssemblyDefinition.CreateAssembly(shimNameDef, shimAsmName + ".dll", ModuleKind.Dll);
            var shimMod = shimAsmDef.MainModule;

            var allModTypes = GetAllTypesRecursive(mod.Types).ToArray();
            var allMemberRefs = mod.GetMemberReferences().ToArray();

            foreach (var kv in missingRootTypes)
            {
                var rootTref = kv.Value;
                bool isInterface = allModTypes.Any(td => td.HasInterfaces && td.Interfaces.Any(i => i.InterfaceType?.FullName == rootTref.FullName));
                bool isValType = !isInterface && rootTref.IsValueType;

                Mono.Cecil.TypeAttributes attrs;
                TypeReference baseType;
                if (isInterface)
                {
                    attrs = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface | Mono.Cecil.TypeAttributes.Abstract;
                    baseType = null;
                }
                else if (isValType)
                {
                    attrs = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.SequentialLayout | Mono.Cecil.TypeAttributes.AnsiClass | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit;
                    baseType = new TypeReference("System", "ValueType", shimMod, shimMod.TypeSystem.CoreLibrary);
                }
                else
                {
                    attrs = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.BeforeFieldInit;
                    baseType = shimMod.TypeSystem.Object;
                }

                if (rootTref.Namespace == "System" && rootTref.Name.StartsWith("ValueTuple`", StringComparison.Ordinal))
                {
                    isValType = true;
                    attrs = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.SequentialLayout | Mono.Cecil.TypeAttributes.AnsiClass | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit;
                    baseType = new TypeReference("System", "ValueType", shimMod, shimMod.TypeSystem.CoreLibrary);
                }

                var shimTd = new TypeDefinition(rootTref.Namespace ?? "", rootTref.Name, attrs, baseType);
                int backtickIdx = rootTref.Name.IndexOf('`');
                if (backtickIdx > 0 && int.TryParse(rootTref.Name.Substring(backtickIdx + 1), out int genCount) && genCount > 0)
                {
                    for (int g = 0; g < genCount; g++)
                        shimTd.GenericParameters.Add(new GenericParameter($"T{g}", shimTd));
                }

                // Add any referenced fields or methods on this missing type
                var addedMethods = new HashSet<string>(StringComparer.Ordinal);
                if (!isInterface && !isValType) addedMethods.Add(".ctor::0");

                if (rootTref.Namespace == "System" && rootTref.Name.StartsWith("ValueTuple`", StringComparison.Ordinal) && shimTd.GenericParameters.Count > 0)
                {
                    var vtCtor = new MethodDefinition(
                        ".ctor",
                        Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                        shimMod.TypeSystem.Void);
                    var vtIl = vtCtor.Body.GetILProcessor();

                    for (int gi = 0; gi < shimTd.GenericParameters.Count; gi++)
                    {
                        var gp = shimTd.GenericParameters[gi];
                        string fldName = (gi == 7) ? "Rest" : $"Item{gi + 1}";
                        string prmName = (gi == 7) ? "rest" : $"item{gi + 1}";
                        shimTd.Fields.Add(new FieldDefinition(fldName, Mono.Cecil.FieldAttributes.Public, gp));
                        vtCtor.Parameters.Add(new ParameterDefinition(prmName, Mono.Cecil.ParameterAttributes.None, gp));
                    }

                    var selfGenInst = new GenericInstanceType(shimTd);
                    for (int gi = 0; gi < shimTd.GenericParameters.Count; gi++)
                        selfGenInst.GenericArguments.Add(shimTd.GenericParameters[gi]);

                    for (int gi = 0; gi < shimTd.GenericParameters.Count; gi++)
                    {
                        vtIl.Emit(OpCodes.Ldarg_0);
                        vtIl.Emit(OpCodes.Ldarg, vtCtor.Parameters[gi]);
                        string fldName = (gi == 7) ? "Rest" : $"Item{gi + 1}";
                        var fldRef = new FieldReference(fldName, shimTd.Fields[gi].FieldType, selfGenInst);
                        vtIl.Emit(OpCodes.Stfld, fldRef);
                    }
                    vtIl.Emit(OpCodes.Ret);
                    shimTd.Methods.Add(vtCtor);
                    addedMethods.Add($".ctor::{shimTd.GenericParameters.Count}");
                    shimMod.Types.Add(shimTd);
                    LogInfo($"[PCMod:{modName}] Auto-synthesized missing type '{rootTref.FullName}' in '{shimAsmName}'.");
                    continue;
                }
                else if (!isInterface && !isValType)
                {
                    var defaultCtor = new MethodDefinition(
                        ".ctor",
                        Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                        shimMod.TypeSystem.Void);
                    var cil = defaultCtor.Body.GetILProcessor();
                    cil.Emit(OpCodes.Ldarg_0);
                    var objCtor = new MethodReference(".ctor", shimMod.TypeSystem.Void, shimMod.TypeSystem.Object) { HasThis = true };
                    cil.Emit(OpCodes.Call, objCtor);
                    cil.Emit(OpCodes.Ret);
                    shimTd.Methods.Add(defaultCtor);
                }

                foreach (var mref in allMemberRefs)
                {
                    var decl = mref.DeclaringType is GenericInstanceType git ? git.ElementType : mref.DeclaringType;
                    if (decl == null || decl.FullName != rootTref.FullName) continue;

                    if (mref is FieldReference fref && !isInterface)
                    {
                        if (shimTd.Fields.All(f => f.Name != fref.Name))
                        {
                            shimTd.Fields.Add(new FieldDefinition(fref.Name, Mono.Cecil.FieldAttributes.Public, shimMod.TypeSystem.Object));
                        }
                    }
                    else if (mref is MethodReference mr)
                    {
                        for (int p = 0; p < mr.Parameters.Count; p++)
                        {
                            mr.Parameters[p].ParameterType = mod.TypeSystem.Object;
                        }
                        if (mr.Name != ".ctor" &&
                            mr.ReturnType.FullName != "System.Void" &&
                            mr.ReturnType.FullName != "System.Boolean" &&
                            mr.ReturnType.FullName != "System.Int32" &&
                            mr.ReturnType.FullName != "System.Single" &&
                            mr.ReturnType.FullName != "System.String")
                        {
                            mr.ReturnType = mod.TypeSystem.Object;
                        }
                        string sigKey = $"{mr.Name}::{mr.Parameters.Count}";
                        if (!addedMethods.Add(sigKey)) continue;

                        if (mr.Name == ".ctor" && !isInterface)
                        {
                            var ctor = new MethodDefinition(
                                ".ctor",
                                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                                shimMod.TypeSystem.Void);
                            for (int p = 0; p < mr.Parameters.Count; p++)
                                ctor.Parameters.Add(new ParameterDefinition($"p{p}", Mono.Cecil.ParameterAttributes.None, shimMod.TypeSystem.Object));
                            var cil = ctor.Body.GetILProcessor();
                            if (!isValType)
                            {
                                cil.Emit(OpCodes.Ldarg_0);
                                var objCtor = new MethodReference(".ctor", shimMod.TypeSystem.Void, shimMod.TypeSystem.Object) { HasThis = true };
                                cil.Emit(OpCodes.Call, objCtor);
                            }
                            cil.Emit(OpCodes.Ret);
                            shimTd.Methods.Add(ctor);
                        }
                        else if (mr.Name != ".ctor")
                        {
                            var mAttrs = Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig;
                            if (!mr.HasThis)
                                mAttrs |= Mono.Cecil.MethodAttributes.Static;
                            else if (isInterface)
                                mAttrs |= Mono.Cecil.MethodAttributes.Abstract | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.NewSlot;
                            else if (!isValType)
                                mAttrs |= Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.NewSlot;

                            TypeReference retType = mr.ReturnType.FullName == "System.Void" ? shimMod.TypeSystem.Void
                                                  : mr.ReturnType.FullName == "System.Boolean" ? shimMod.TypeSystem.Boolean
                                                  : mr.ReturnType.FullName == "System.Int32" ? shimMod.TypeSystem.Int32
                                                  : mr.ReturnType.FullName == "System.Single" ? shimMod.TypeSystem.Single
                                                  : mr.ReturnType.FullName == "System.String" ? shimMod.TypeSystem.String
                                                  : shimMod.TypeSystem.Object;

                            var mDef = new MethodDefinition(mr.Name, mAttrs, retType);
                            for (int p = 0; p < mr.Parameters.Count; p++)
                                mDef.Parameters.Add(new ParameterDefinition($"p{p}", Mono.Cecil.ParameterAttributes.None, shimMod.TypeSystem.Object));

                            if (!isInterface)
                            {
                                var cil = mDef.Body.GetILProcessor();
                                if (retType.FullName == "System.Void") cil.Emit(OpCodes.Ret);
                                else if (retType.FullName == "System.Boolean" || retType.FullName == "System.Int32") { cil.Emit(OpCodes.Ldc_I4_0); cil.Emit(OpCodes.Ret); }
                                else if (retType.FullName == "System.Single") { cil.Emit(OpCodes.Ldc_R4, 0f); cil.Emit(OpCodes.Ret); }
                                else if (retType.FullName == "System.String") { cil.Emit(OpCodes.Ldstr, ""); cil.Emit(OpCodes.Ret); }
                                else { cil.Emit(OpCodes.Ldnull); cil.Emit(OpCodes.Ret); }
                            }
                            shimTd.Methods.Add(mDef);
                        }
                    }
                }

                shimMod.Types.Add(shimTd);
                LogInfo($"[PCMod:{modName}] Auto-synthesized missing type '{rootTref.FullName}' in '{shimAsmName}'.");
            }

            NormalizeDynamicModuleCorlibRef(shimMod);
            using (var msShim = new MemoryStream())
            {
                shimAsmDef.Write(msShim);
                shimBytes = msShim.ToArray();
                Assembly.Load(shimBytes);
            }

            var shimAsmRef = new AssemblyNameReference(shimAsmName, new Version(1, 0, 0, 0));
            mod.AssemblyReferences.Add(shimAsmRef);
            foreach (var kv in missingRootTypes)
            {
                kv.Value.Scope = shimAsmRef;
            }
            foreach (var tref in mod.GetTypeReferences())
            {
                if (tref == null || tref is TypeSpecification) continue;
                var r = tref;
                while (r.DeclaringType != null) r = r.DeclaringType;
                if (missingRootTypes.ContainsKey(r.FullName))
                {
                    r.Scope = shimAsmRef;
                }
            }

            return missingRootTypes.Count;
        }

        private static bool TryAutoStubStrippedUnityCall(
            string modName,
            ModuleDefinition mod,
            MethodDefinition callerMd,
            ILProcessor il,
            ref int instrIdx,
            Instruction ins,
            MethodReference mr)
        {
            if (mr.Name == ".ctor" && mr.DeclaringType != null &&
                (mr.DeclaringType.FullName == "UnityEngine.GradientAlphaKey" || mr.DeclaringType.FullName == "UnityEngine.GradientColorKey"))
            {
                bool isAlpha = mr.DeclaringType.FullName == "UnityEngine.GradientAlphaKey";
                if (ins.OpCode == OpCodes.Newobj)
                {
                    string helperName = isAlpha ? "CreateGradientAlphaKey" : "CreateGradientColorKey";
                    var helperMi = _assetHelperBridgeType?.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                    if (helperMi != null)
                    {
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = mod.ImportReference(helperMi);
                        LogInfo($"[PCMod:{modName}] Redirected newobj constructor to AssetHelperBridge.{helperName}");
                        return true;
                    }
                }
                else if (ins.OpCode == OpCodes.Call)
                {
                    string helperName = isAlpha ? "InitGradientAlphaKey" : "InitGradientColorKey";
                    var helperMi = _assetHelperBridgeType?.GetMethod(helperName, BindingFlags.Static | BindingFlags.Public);
                    if (helperMi != null)
                    {
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = mod.ImportReference(helperMi);
                        LogInfo($"[PCMod:{modName}] Redirected call constructor to AssetHelperBridge.{helperName}");
                        return true;
                    }
                }
            }

            string mName = mr.Name;
            if (string.IsNullOrEmpty(mName) ||
                mName == ".ctor" ||
                mName == ".cctor" ||
                ins.OpCode == OpCodes.Newobj ||
                mr is GenericInstanceMethod ||
                mr.HasGenericParameters)
            {
                return false;
            }

            string declFull = (mr.DeclaringType?.FullName ?? "").Replace('/', '+');
            var runtimeType = ResolveRuntimeType(declFull);
            if (runtimeType == null) return false;

            bool existsOnMobile = runtimeType.GetMethods(AllFlags | BindingFlags.FlattenHierarchy).Any(m => m.Name == mName);
            if (existsOnMobile) return false;

            // Restore stripped ParticleSystem.EmitParams & ParticleSystem.Particle property getters/setters via direct native IL field access
            // (struct fields are preserved by IL2CPP for Emit_Injected/GetParticles/SetParticles even when managed property accessors are stripped).
            if ((declFull == "UnityEngine.ParticleSystem+EmitParams" || declFull == "UnityEngine.ParticleSystem+Particle") && mr.HasThis && !callerMd.DeclaringType.HasGenericParameters)
            {
                bool isEmitParams = declFull == "UnityEngine.ParticleSystem+EmitParams";
                var particleType = isEmitParams ? ResolveRuntimeType("UnityEngine.ParticleSystem+Particle") : runtimeType;
                var mParticleFi = isEmitParams ? runtimeType.GetField("m_Particle", AllFlags) : null;
                if (particleType != null && (!isEmitParams || mParticleFi != null))
                {
                    string pFieldName = null;
                    string flagFieldName = null;
                    bool isGetter = mName.StartsWith("get_", StringComparison.Ordinal);
                    bool isSetter = mName.StartsWith("set_", StringComparison.Ordinal);
                    bool isStartSize = false;
                    switch (mName)
                    {
                        case "get_position":
                        case "set_position":
                            pFieldName = "m_Position"; flagFieldName = "m_PositionSet"; break;
                        case "get_velocity":
                        case "set_velocity":
                            pFieldName = "m_Velocity"; flagFieldName = "m_VelocitySet"; break;
                        case "get_startLifetime":
                        case "set_startLifetime":
                            pFieldName = "m_StartLifetime"; flagFieldName = "m_StartLifetimeSet"; break;
                        case "get_remainingLifetime":
                        case "set_remainingLifetime":
                            if (!isEmitParams) { pFieldName = "m_Lifetime"; }
                            break;
                        case "get_startColor":
                        case "set_startColor":
                            pFieldName = "m_StartColor"; flagFieldName = "m_StartColorSet"; break;
                        case "get_startSize":
                        case "set_startSize":
                            pFieldName = "m_StartSize"; flagFieldName = "m_StartSizeSet"; isStartSize = true; break;
                    }

                    bool sigOk = pFieldName != null && (isGetter || isSetter)
                        && ((isGetter && mr.Parameters.Count == 0 && mr.ReturnType.FullName != "System.Void")
                         || (isSetter && mr.Parameters.Count == 1 && mr.ReturnType.FullName == "System.Void"));

                    if (sigOk)
                    {
                        var pFieldFi = particleType.GetField(pFieldName, AllFlags);
                        var flagFieldFi = (isEmitParams && isSetter && flagFieldName != null) ? runtimeType.GetField(flagFieldName, AllFlags) : null;
                        var vec3Type = isStartSize ? ResolveRuntimeType("UnityEngine.Vector3") : null;
                        var vec3XFi = (isStartSize && isGetter && vec3Type != null) ? vec3Type.GetField("x", AllFlags) : null;
                        var vec3Ctor = (isStartSize && isSetter && vec3Type != null) ? vec3Type.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) }) : null;

                        string valTypeFull = isGetter ? mr.ReturnType.FullName : mr.Parameters[0].ParameterType.FullName;
                        bool typeMatches = pFieldFi != null && (isStartSize
                            ? (pFieldFi.FieldType.FullName == "UnityEngine.Vector3" && valTypeFull == "System.Single")
                            : (pFieldFi.FieldType.FullName == valTypeFull.Replace('/', '+')));

                        bool canEmit = typeMatches
                            && (!isEmitParams || !isSetter || (flagFieldFi != null && flagFieldFi.FieldType == typeof(bool)))
                            && (!isStartSize || (isGetter ? vec3XFi != null : vec3Ctor != null));

                        if (canEmit)
                        {
                            string bridgeName = $"__MobileFieldBridge_{mr.DeclaringType.Name}_{mName}";
                            var bridge = callerMd.DeclaringType.Methods.FirstOrDefault(m => m.Name == bridgeName);
                            if (bridge == null)
                            {
                                bridge = new MethodDefinition(
                                    bridgeName,
                                    Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                                    mr.ReturnType);
                                var thisParamType = new ByReferenceType(mr.DeclaringType);
                                bridge.Parameters.Add(new ParameterDefinition("__this", Mono.Cecil.ParameterAttributes.None, thisParamType));
                                foreach (var p in mr.Parameters)
                                    bridge.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

                                var bil = bridge.Body.GetILProcessor();
                                bil.Emit(OpCodes.Ldarg_0);
                                if (isEmitParams)
                                    bil.Emit(OpCodes.Ldflda, mod.ImportReference(mParticleFi));

                                if (isGetter)
                                {
                                    if (isStartSize)
                                    {
                                        bil.Emit(OpCodes.Ldflda, mod.ImportReference(pFieldFi));
                                        bil.Emit(OpCodes.Ldfld, mod.ImportReference(vec3XFi));
                                    }
                                    else
                                    {
                                        bil.Emit(OpCodes.Ldfld, mod.ImportReference(pFieldFi));
                                    }
                                    bil.Emit(OpCodes.Ret);
                                }
                                else
                                {
                                    if (isStartSize)
                                    {
                                        bil.Emit(OpCodes.Ldarg_1);
                                        bil.Emit(OpCodes.Ldarg_1);
                                        bil.Emit(OpCodes.Ldarg_1);
                                        bil.Emit(OpCodes.Newobj, mod.ImportReference(vec3Ctor));
                                    }
                                    else
                                    {
                                        bil.Emit(OpCodes.Ldarg_1);
                                    }
                                    bil.Emit(OpCodes.Stfld, mod.ImportReference(pFieldFi));
                                    if (isEmitParams && flagFieldFi != null)
                                    {
                                        bil.Emit(OpCodes.Ldarg_0);
                                        bil.Emit(OpCodes.Ldc_I4_1);
                                        bil.Emit(OpCodes.Stfld, mod.ImportReference(flagFieldFi));
                                    }
                                    bil.Emit(OpCodes.Ret);
                                }

                                callerMd.DeclaringType.Methods.Add(bridge);
                            }

                            ins.OpCode = OpCodes.Call;
                            ins.Operand = bridge;
                            LogInfo($"[PCMod:{modName}] Bridged stripped ParticleSystem struct accessor via native fields: {mr.DeclaringType.FullName}.{mName}");
                            return true;
                        }
                    }
                }
            }

            string retFull = mr.ReturnType?.FullName ?? "System.Void";
            if (retFull != "System.Void" &&
                retFull != "System.Single" &&
                retFull != "System.Int32" &&
                retFull != "System.Boolean" &&
                retFull != "System.String" &&
                !retFull.StartsWith("UnityEngine.", StringComparison.Ordinal))
            {
                return false;
            }

            var stub = new MethodDefinition(
                $"__MobileStub_{mr.DeclaringType.Name}_{mName}_{instrIdx}",
                Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                mr.ReturnType);

            if (mr.HasThis)
            {
                var thisParamType = mr.DeclaringType.IsValueType ? (TypeReference)new ByReferenceType(mr.DeclaringType) : mr.DeclaringType;
                stub.Parameters.Add(new ParameterDefinition("__this", Mono.Cecil.ParameterAttributes.None, thisParamType));
            }
            foreach (var p in mr.Parameters)
            {
                stub.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            }

            var sil = stub.Body.GetILProcessor();
            if (retFull == "System.Void")
            {
                sil.Emit(OpCodes.Ret);
            }
            else if (retFull == "System.Single")
            {
                sil.Emit(OpCodes.Ldc_R4, 0f);
                sil.Emit(OpCodes.Ret);
            }
            else if (retFull == "System.Int32" || retFull == "System.Boolean")
            {
                sil.Emit(OpCodes.Ldc_I4_0);
                sil.Emit(OpCodes.Ret);
            }
            else if (retFull == "System.String")
            {
                sil.Emit(OpCodes.Ldstr, "");
                sil.Emit(OpCodes.Ret);
            }
            else if (mr.ReturnType.IsValueType || (ResolveRuntimeType(retFull.Replace('/', '+'))?.IsValueType ?? false))
            {
                stub.Body.InitLocals = true;
                var retVar = new VariableDefinition(mr.ReturnType);
                stub.Body.Variables.Add(retVar);
                sil.Emit(OpCodes.Ldloca, retVar);
                sil.Emit(OpCodes.Initobj, mr.ReturnType);
                sil.Emit(OpCodes.Ldloc, retVar);
                sil.Emit(OpCodes.Ret);
            }
            else
            {
                sil.Emit(OpCodes.Ldnull);
                sil.Emit(OpCodes.Ret);
            }

            callerMd.DeclaringType.Methods.Add(stub);
            ins.OpCode = OpCodes.Call;
            ins.Operand = stub;
            LogInfo($"[PCMod:{modName}] Auto-stubbed stripped UnityEngine call: {mr.DeclaringType.FullName}.{mName}");
            return true;
        }

        // ======================================================================
        // Dynamic CIL FieldRef<T, F> Getter Emitter (ldarg.0; ldflda <field>; ret)
        // ======================================================================
        public static MethodInfo EmitFieldRefGetterMethod(Type instType, Type fieldType, FieldInfo field)
        {
            if (instType == null || fieldType == null || field == null) return null;
            int id = _nextFieldRefId++;
            string asmNameStr = $"SilksongMobileAPI_FieldRef_{id}";
            var asmName = new AssemblyNameDefinition(asmNameStr, new Version(1, 0, 0, 0));
            var asmDef = AssemblyDefinition.CreateAssembly(asmName, asmNameStr, ModuleKind.Dll);
            var mod = asmDef.MainModule;

            var holderType = new TypeDefinition(
                "SilksongMobileAPI.FieldRefs",
                $"FieldRefHolder_{id}",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                mod.TypeSystem.Object);
            mod.Types.Add(holderType);

            var importedInst = mod.ImportReference(instType);
            var importedFieldType = mod.ImportReference(fieldType);
            var importedField = mod.ImportReference(field);

            var mDef = new MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                new ByReferenceType(importedFieldType));
            mDef.Parameters.Add(new ParameterDefinition("instance", Mono.Cecil.ParameterAttributes.None, importedInst));

            var il = mDef.Body.GetILProcessor();
            if (field.IsStatic)
            {
                il.Emit(OpCodes.Ldsflda, importedField);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, importedField);
            }
            il.Emit(OpCodes.Ret);

            holderType.Methods.Add(mDef);
            NormalizeDynamicModuleCorlibRef(mod);

            using (var ms = new MemoryStream())
            {
                asmDef.Write(ms);
                var loadedAsm = Assembly.Load(ms.ToArray());
                var loadedType = loadedAsm.GetType($"SilksongMobileAPI.FieldRefs.FieldRefHolder_{id}", false);
                return loadedType?.GetMethod("Invoke", BindingFlags.Static | BindingFlags.Public);
            }
        }

        public static MethodInfo EmitStaticFieldRefGetterMethod(Type fieldType, FieldInfo field)
        {
            if (fieldType == null || field == null || !field.IsStatic) return null;
            int id = _nextFieldRefId++;
            string asmNameStr = $"SilksongMobileAPI_StaticFieldRef_{id}";
            var asmName = new AssemblyNameDefinition(asmNameStr, new Version(1, 0, 0, 0));
            var asmDef = AssemblyDefinition.CreateAssembly(asmName, asmNameStr, ModuleKind.Dll);
            var mod = asmDef.MainModule;

            var holderType = new TypeDefinition(
                "SilksongMobileAPI.FieldRefs",
                $"StaticFieldRefHolder_{id}",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                mod.TypeSystem.Object);
            mod.Types.Add(holderType);

            var importedFieldType = mod.ImportReference(fieldType);
            var importedField = mod.ImportReference(field);

            var mDef = new MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                new ByReferenceType(importedFieldType));

            var il = mDef.Body.GetILProcessor();
            il.Emit(OpCodes.Ldsflda, importedField);
            il.Emit(OpCodes.Ret);

            holderType.Methods.Add(mDef);
            NormalizeDynamicModuleCorlibRef(mod);

            using (var ms = new MemoryStream())
            {
                asmDef.Write(ms);
                var loadedAsm = Assembly.Load(ms.ToArray());
                var loadedType = loadedAsm.GetType($"SilksongMobileAPI.FieldRefs.StaticFieldRefHolder_{id}", false);
                return loadedType?.GetMethod("Invoke", BindingFlags.Static | BindingFlags.Public);
            }
        }

        private static void NormalizeDynamicModuleCorlibRef(ModuleDefinition mod)
        {
            foreach (var aref in mod.AssemblyReferences)
            {
                if (aref.Name == "mscorlib")
                {
                    aref.Version = new Version(4, 0, 0, 0);
                    aref.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                }
            }
        }

        // ======================================================================
        // Dynamic Hook Adapter Pair Emitter (Matches Mobile BepInEx.dll 1:1)
        // ======================================================================
        public static bool EmitDynamicHookPair(int chainId, MethodBase targetMethod, out MethodInfo prefixHook, out MethodInfo postfixHook)
        {
            prefixHook = null;
            postfixHook = null;
            EnsureBoundMainApi();
            if (targetMethod == null || _adapterBuilderType == null) return false;

            var dispatchPrefix = _adapterBuilderType.GetMethod("DispatchPrefix", BindingFlags.Static | BindingFlags.Public);
            var dispatchPostfix = _adapterBuilderType.GetMethod("DispatchPostfix", BindingFlags.Static | BindingFlags.Public);
            if (dispatchPrefix == null || dispatchPostfix == null) return false;

            string asmNameStr = $"SilksongMobileAPI_DynHooks_{chainId}";
            var asmName = new AssemblyNameDefinition(asmNameStr, new Version(1, 0, 0, 0));
            var asmDef = AssemblyDefinition.CreateAssembly(asmName, asmNameStr, ModuleKind.Dll);
            var mod = asmDef.MainModule;

            var holderType = new TypeDefinition(
                "SilksongMobileAPI.DynHooks",
                $"HookHolder_{chainId}",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                mod.TypeSystem.Object);
            mod.Types.Add(holderType);

            var preDef = BuildSingleHookMethod(mod, chainId, targetMethod, isPostfix: false, mod.ImportReference(dispatchPrefix));
            var postDef = BuildSingleHookMethod(mod, chainId, targetMethod, isPostfix: true, mod.ImportReference(dispatchPostfix));

            holderType.Methods.Add(preDef);
            holderType.Methods.Add(postDef);
            NormalizeDynamicModuleCorlibRef(mod);

            using (var ms = new MemoryStream())
            {
                asmDef.Write(ms);
                var loadedAsm = Assembly.Load(ms.ToArray());
                var loadedType = loadedAsm.GetType($"SilksongMobileAPI.DynHooks.HookHolder_{chainId}", false);
                if (loadedType != null)
                {
                    prefixHook = loadedType.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public);
                    postfixHook = loadedType.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
                    return prefixHook != null && postfixHook != null;
                }
            }
            return false;
        }

        private static MethodDefinition BuildSingleHookMethod(
            ModuleDefinition mod,
            int chainId,
            MethodBase targetMethod,
            bool isPostfix,
            MethodReference dispatchMethodRef)
        {
            Type targetRetType = (targetMethod is MethodInfo mi) ? mi.ReturnType : typeof(void);
            bool hasResult = isPostfix && targetRetType != null && targetRetType != typeof(void);

            var retType = (!isPostfix)
                ? mod.TypeSystem.Boolean
                : (hasResult ? mod.ImportReference(targetRetType) : mod.TypeSystem.Void);

            var hookDef = new MethodDefinition(
                isPostfix ? "Postfix" : "Prefix",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                retType);

            bool hasInstance = !targetMethod.IsStatic && targetMethod.DeclaringType != null;

            int instanceParamIdx = -1;
            if (hasInstance)
            {
                var instType = targetMethod.DeclaringType;
                var importedInst = mod.ImportReference(instType);
                if (instType.IsValueType) importedInst = new ByReferenceType(importedInst);
                hookDef.Parameters.Add(new ParameterDefinition("__instance", Mono.Cecil.ParameterAttributes.None, importedInst));
                instanceParamIdx = 0;
            }

            var targetParams = targetMethod.GetParameters();
            int firstTargetParamIdx = hookDef.Parameters.Count;
            foreach (var tp in targetParams)
            {
                var importedPt = mod.ImportReference(tp.ParameterType);
                hookDef.Parameters.Add(new ParameterDefinition(tp.Name ?? $"arg{tp.Position}", Mono.Cecil.ParameterAttributes.None, importedPt));
            }

            int resultParamIdx = -1;
            TypeReference cleanResultTypeRef = null;
            if (hasResult)
            {
                cleanResultTypeRef = mod.ImportReference(targetRetType);
                // CRITICAL: Mobile BepInEx HookMethod requires by-value T result as the LAST parameter on Postfix
                hookDef.Parameters.Add(new ParameterDefinition("result", Mono.Cecil.ParameterAttributes.None, cleanResultTypeRef));
                resultParamIdx = hookDef.Parameters.Count - 1;
            }

            hookDef.Body.InitLocals = true;
            var argsVar = new VariableDefinition(new ArrayType(mod.TypeSystem.Object));
            var resBoxVar = new VariableDefinition(mod.TypeSystem.Object);
            hookDef.Body.Variables.Add(argsVar);
            hookDef.Body.Variables.Add(resBoxVar);

            VariableDefinition runOrigVar = null;
            if (!isPostfix)
            {
                runOrigVar = new VariableDefinition(mod.TypeSystem.Boolean);
                hookDef.Body.Variables.Add(runOrigVar);
            }

            var il = hookDef.Body.GetILProcessor();

            // Fast-path guard: skip object[] allocation and parameter boxing when no Prefix/Postfix is active on this chain!
            var shouldRunMi = _adapterBuilderType?.GetMethod(isPostfix ? "ShouldRunPostfix" : "ShouldRunPrefix", BindingFlags.Static | BindingFlags.Public);
            if (shouldRunMi != null)
            {
                var proceedLbl = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldc_I4, chainId);
                il.Emit(OpCodes.Call, mod.ImportReference(shouldRunMi));
                il.Emit(OpCodes.Brtrue_S, proceedLbl);
                if (!isPostfix)
                {
                    il.Emit(OpCodes.Ldc_I4_1);
                }
                else if (hasResult)
                {
                    il.Emit(OpCodes.Ldarg, hookDef.Parameters[resultParamIdx]);
                }
                il.Emit(OpCodes.Ret);
                il.Append(proceedLbl);
            }

            if (targetParams.Length == 0)
            {
                var emptyOpen = typeof(Array).GetMethod("Empty", BindingFlags.Static | BindingFlags.Public);
                if (emptyOpen != null)
                {
                    var gEmpty = new GenericInstanceMethod(mod.ImportReference(emptyOpen));
                    gEmpty.GenericArguments.Add(mod.TypeSystem.Object);
                    il.Emit(OpCodes.Call, gEmpty);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Newarr, mod.TypeSystem.Object);
                }
                il.Emit(OpCodes.Stloc, argsVar);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, targetParams.Length);
                il.Emit(OpCodes.Newarr, mod.TypeSystem.Object);
                il.Emit(OpCodes.Stloc, argsVar);
            }

            for (int i = 0; i < targetParams.Length; i++)
            {
                var tp = targetParams[i];
                var pType = tp.ParameterType;
                bool isByRef = pType.IsByRef;
                var elemType = isByRef ? pType.GetElementType() : pType;
                var importedElem = mod.ImportReference(elemType);
                int pIdx = firstTargetParamIdx + i;

                il.Emit(OpCodes.Ldloc, argsVar);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, hookDef.Parameters[pIdx]);
                if (isByRef)
                {
                    EmitLdindOrLdobj(il, elemType, importedElem);
                }
                if (elemType.IsValueType)
                {
                    il.Emit(OpCodes.Box, importedElem);
                }
                il.Emit(OpCodes.Stelem_Ref);
            }

            if (hasResult)
            {
                il.Emit(OpCodes.Ldarg, hookDef.Parameters[resultParamIdx]);
                if (targetRetType.IsValueType)
                    il.Emit(OpCodes.Box, cleanResultTypeRef);
                il.Emit(OpCodes.Stloc, resBoxVar);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stloc, resBoxVar);
            }

            il.Emit(OpCodes.Ldc_I4, chainId);
            if (hasInstance)
            {
                var instType = targetMethod.DeclaringType;
                var importedInstClean = mod.ImportReference(instType);
                il.Emit(OpCodes.Ldarg, hookDef.Parameters[instanceParamIdx]);
                if (instType.IsValueType)
                {
                    EmitLdindOrLdobj(il, instType, importedInstClean);
                    il.Emit(OpCodes.Box, importedInstClean);
                }
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
            il.Emit(OpCodes.Ldloc, argsVar);
            il.Emit(OpCodes.Ldloca, resBoxVar);
            il.Emit(OpCodes.Call, dispatchMethodRef);

            if (!isPostfix)
            {
                il.Emit(OpCodes.Stloc, runOrigVar);
            }

            for (int i = 0; i < targetParams.Length; i++)
            {
                var tp = targetParams[i];
                if (!tp.ParameterType.IsByRef) continue;
                var elemType = tp.ParameterType.GetElementType();
                var importedElem = mod.ImportReference(elemType);
                int pIdx = firstTargetParamIdx + i;

                il.Emit(OpCodes.Ldarg, hookDef.Parameters[pIdx]);
                il.Emit(OpCodes.Ldloc, argsVar);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                if (elemType.IsValueType)
                    il.Emit(OpCodes.Unbox_Any, importedElem);
                else
                    il.Emit(OpCodes.Castclass, importedElem);
                EmitStindOrStobj(il, elemType, importedElem);
            }

            if (hasResult)
            {
                var nullableUnder = Nullable.GetUnderlyingType(targetRetType);
                if (nullableUnder != null)
                {
                    var underRef = mod.ImportReference(nullableUnder);
                    // Build Nullable<T>::.ctor(!0) directly in Cecil without calling ImportReference(ConstructorInfo),
                    // because ImportReference on a constructed generic MethodBase invokes Module.ResolveMethodToken which throws in IL2CPP!
                    var openNullType = cleanResultTypeRef.GetElementType();
                    TypeReference gp0 = (openNullType != null && openNullType.GenericParameters.Count > 0)
                        ? (TypeReference)openNullType.GenericParameters[0]
                        : new GenericParameter(openNullType ?? cleanResultTypeRef);
                    var nullableCtor = new MethodReference(".ctor", mod.TypeSystem.Void, cleanResultTypeRef) { HasThis = true };
                    nullableCtor.Parameters.Add(new ParameterDefinition(gp0));
                    var fallbackLabel = il.Create(OpCodes.Nop);
                    var endLabel = il.Create(OpCodes.Nop);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Isinst, underRef);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Unbox_Any, underRef);
                    il.Emit(OpCodes.Newobj, nullableCtor);
                    il.Emit(OpCodes.Br_S, endLabel);
                    il.Append(fallbackLabel);
                    il.Emit(OpCodes.Ldarg, hookDef.Parameters[resultParamIdx]);
                    il.Append(endLabel);
                }
                else if (targetRetType.IsValueType)
                {
                    var fallbackLabel = il.Create(OpCodes.Nop);
                    var endLabel = il.Create(OpCodes.Nop);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Isinst, cleanResultTypeRef);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Unbox_Any, cleanResultTypeRef);
                    il.Emit(OpCodes.Br_S, endLabel);
                    il.Append(fallbackLabel);
                    il.Emit(OpCodes.Ldarg, hookDef.Parameters[resultParamIdx]);
                    il.Append(endLabel);
                }
                else
                {
                    var fallbackLabel = il.Create(OpCodes.Nop);
                    var endLabel = il.Create(OpCodes.Nop);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Isinst, cleanResultTypeRef);
                    il.Emit(OpCodes.Brfalse_S, fallbackLabel);
                    il.Emit(OpCodes.Ldloc, resBoxVar);
                    il.Emit(OpCodes.Castclass, cleanResultTypeRef);
                    il.Emit(OpCodes.Br_S, endLabel);
                    il.Append(fallbackLabel);
                    il.Emit(OpCodes.Ldarg, hookDef.Parameters[resultParamIdx]);
                    il.Append(endLabel);
                }
            }
            else if (!isPostfix)
            {
                il.Emit(OpCodes.Ldloc, runOrigVar);
            }
            il.Emit(OpCodes.Ret);

            return hookDef;
        }

        private static void EmitLdindOrLdobj(ILProcessor il, Type t, TypeReference importedType)
        {
            if (!t.IsValueType) { il.Emit(OpCodes.Ldind_Ref); return; }
            if (t == typeof(bool) || t == typeof(sbyte) || t == typeof(byte)) { il.Emit(OpCodes.Ldind_I1); return; }
            if (t == typeof(short) || t == typeof(ushort)) { il.Emit(OpCodes.Ldind_I2); return; }
            if (t == typeof(int) || t == typeof(uint)) { il.Emit(OpCodes.Ldind_I4); return; }
            if (t == typeof(long) || t == typeof(ulong)) { il.Emit(OpCodes.Ldind_I8); return; }
            if (t == typeof(float)) { il.Emit(OpCodes.Ldind_R4); return; }
            if (t == typeof(double)) { il.Emit(OpCodes.Ldind_R8); return; }
            il.Emit(OpCodes.Ldobj, importedType);
        }

        private static void EmitStindOrStobj(ILProcessor il, Type t, TypeReference importedType)
        {
            if (!t.IsValueType) { il.Emit(OpCodes.Stind_Ref); return; }
            if (t == typeof(bool) || t == typeof(sbyte) || t == typeof(byte)) { il.Emit(OpCodes.Stind_I1); return; }
            if (t == typeof(short) || t == typeof(ushort)) { il.Emit(OpCodes.Stind_I2); return; }
            if (t == typeof(int) || t == typeof(uint)) { il.Emit(OpCodes.Stind_I4); return; }
            if (t == typeof(long) || t == typeof(ulong)) { il.Emit(OpCodes.Stind_I8); return; }
            if (t == typeof(float)) { il.Emit(OpCodes.Stind_R4); return; }
            if (t == typeof(double)) { il.Emit(OpCodes.Stind_R8); return; }
            il.Emit(OpCodes.Stobj, importedType);
        }

        public static byte[] SynthesizeUnifiedFsmUtilAssembly(string mainApiDllPath)
        {
            EnsureBoundMainApi();
            if (string.IsNullOrEmpty(mainApiDllPath) || !File.Exists(mainApiDllPath)) return null;
            try
            {
                byte[] apiBytes = File.ReadAllBytes(mainApiDllPath);
                using (var msIn = new MemoryStream(apiBytes, writable: false))
                {
                    var asmDef = AssemblyDefinition.ReadAssembly(msIn, new ReaderParameters { ReadWrite = false, InMemory = true, ReadingMode = ReadingMode.Immediate });
                    var mod = asmDef.MainModule;
                    asmDef.Name.Name = "Silksong.FsmUtil";
                    asmDef.Name.Version = new Version(0, 3, 17, 0);
                    mod.Name = "Silksong.FsmUtil.dll";

                    var apiRef = new AssemblyNameReference("IRIS", new Version(1, 0, 0, 0));
                    mod.AssemblyReferences.Add(apiRef);

                    var keepTypes = new HashSet<TypeDefinition>();
                    foreach (var td in mod.Types.ToArray())
                    {
                        if (td.Name == "<Module>" || (td.Namespace != null && td.Namespace.StartsWith("Silksong.FsmUtil", StringComparison.Ordinal)))
                        {
                            keepTypes.Add(td);
                        }
                    }

                    foreach (var td in GetAllTypesRecursive(keepTypes))
                    {
                        foreach (var md in td.Methods)
                        {
                            if (!md.HasBody) continue;
                            var instrs = md.Body.Instructions;
                            for (int i = 0; i < instrs.Count; i++)
                            {
                                if (instrs[i].Operand is MethodReference mr && mr.DeclaringType != null &&
                                    mr.DeclaringType.Namespace == "SilksongMobileAPI")
                                {
                                    var extType = new TypeReference("SilksongMobileAPI", mr.DeclaringType.Name, mod, apiRef, false);
                                    var extMethod = new MethodReference(mr.Name, mr.ReturnType, extType)
                                    {
                                        HasThis = mr.HasThis,
                                        ExplicitThis = mr.ExplicitThis,
                                        CallingConvention = mr.CallingConvention
                                    };
                                    foreach (var p in mr.Parameters)
                                        extMethod.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                    foreach (var gp in mr.GenericParameters)
                                        extMethod.GenericParameters.Add(new GenericParameter(gp.Name, extMethod));
                                    instrs[i].Operand = extMethod;
                                }
                            }
                        }
                    }

                    foreach (var td in mod.Types.ToArray())
                    {
                        if (!keepTypes.Contains(td))
                            mod.Types.Remove(td);
                    }

                    using (var msOut = new MemoryStream())
                    {
                        asmDef.Write(msOut);
                        return msOut.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarn($"[UnifiedFsmUtil] Failed to synthesize standalone Silksong.FsmUtil assembly: {ex.Message}");
                return null;
            }
        }
    }
}
