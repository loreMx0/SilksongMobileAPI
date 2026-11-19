using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;

namespace SilksongMobileAPI
{
    public static class CecilRuntimeRewriter
    {
        private static bool _initialized;
        private static MethodInfo _rewriteAssemblyBytesMethod;
        private static MethodInfo _emitDynamicHookPairMethod;
        private static MethodInfo _emitFieldRefMethod;
        private static MethodInfo _emitStaticFieldRefMethod;
        private static MethodInfo _synthesizeUnifiedFsmUtilMethod;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                byte[] cecilBytes = DecompressGZip(EmbeddedPayloads.MonoCecilGZip);
                var cecilAsm = Assembly.Load(cecilBytes);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"Loaded sanitized Mono.Cecil ({cecilBytes.Length} bytes) from in-memory GZip payload.");

                byte[] coreBytes = DecompressGZip(EmbeddedPayloads.CecilCoreGZip);
                var coreAsm = Assembly.Load(coreBytes);
                SilksongMobileAPIPlugin.LogDiagnosticInfo($"Loaded SilksongMobileAPI.CecilCore ({coreBytes.Length} bytes) from in-memory GZip payload.");

                var engineType = coreAsm.GetType("SilksongMobileAPI.CecilCore.CecilEngine", false);
                if (engineType != null)
                {
                    _rewriteAssemblyBytesMethod = engineType.GetMethod("RewritePCModAssembly", BindingFlags.Static | BindingFlags.Public);
                    _emitDynamicHookPairMethod = engineType.GetMethod("EmitDynamicHookPair", BindingFlags.Static | BindingFlags.Public);
                    _emitFieldRefMethod = engineType.GetMethod("EmitFieldRefGetterMethod", BindingFlags.Static | BindingFlags.Public);
                    _emitStaticFieldRefMethod = engineType.GetMethod("EmitStaticFieldRefGetterMethod", BindingFlags.Static | BindingFlags.Public);
                    _synthesizeUnifiedFsmUtilMethod = engineType.GetMethod("SynthesizeUnifiedFsmUtilAssembly", BindingFlags.Static | BindingFlags.Public);
                    SilksongMobileAPIPlugin.LogDiagnosticInfo("Bound SilksongMobileAPI.CecilCore engine.");
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[CecilRuntimeRewriter] Initialization failed: {ex}");
            }
        }

        public static byte[] RewritePCModDll(byte[] rawDllBytes, string dllPath)
        {
            EnsureInitialized();
            if (_rewriteAssemblyBytesMethod == null || rawDllBytes == null) return rawDllBytes;

            try
            {
                var res = _rewriteAssemblyBytesMethod.Invoke(null, new object[] { rawDllBytes, dllPath }) as byte[];
                return res ?? rawDllBytes;
            }
            catch (TargetInvocationException tie)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[CecilRuntimeRewriter] RewritePCModDll error for {Path.GetFileName(dllPath)}: {tie.InnerException ?? tie}");
                return rawDllBytes;
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticError($"[CecilRuntimeRewriter] RewritePCModDll error for {Path.GetFileName(dllPath)}: {ex}");
                return rawDllBytes;
            }
        }

        public static bool TryBuildDynamicHookPair(int chainId, MethodBase targetMethod, out MethodInfo prefixHook, out MethodInfo postfixHook)
        {
            prefixHook = null;
            postfixHook = null;
            EnsureInitialized();
            if (_emitDynamicHookPairMethod == null || targetMethod == null) return false;

            try
            {
                var args = new object[] { chainId, targetMethod, null, null };
                bool ok = (bool)_emitDynamicHookPairMethod.Invoke(null, args);
                if (ok)
                {
                    prefixHook = args[2] as MethodInfo;
                    postfixHook = args[3] as MethodInfo;
                    return prefixHook != null && postfixHook != null;
                }
            }
            catch (Exception ex)
            {
                var inner = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[CecilRuntimeRewriter] TryBuildDynamicHookPair({targetMethod.DeclaringType?.Name}.{targetMethod.Name}) failed: {inner.Message}");
            }
            return false;
        }

        public static bool TryBuildFieldRefDelegate<T, F>(FieldInfo field, out FieldRef<T, F> del)
        {
            del = null;
            EnsureInitialized();
            if (_emitFieldRefMethod == null || field == null) return false;

            try
            {
                var getterMi = _emitFieldRefMethod.Invoke(null, new object[] { typeof(T), typeof(F), field }) as MethodInfo;
                if (getterMi != null)
                {
                    del = (FieldRef<T, F>)Delegate.CreateDelegate(typeof(FieldRef<T, F>), getterMi);
                    return del != null;
                }
            }
            catch (System.Exception __apiEx_1) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("CecilRuntimeRewriter", "L114", __apiEx_1); }
            return false;
        }

        public static bool TryBuildStaticFieldRefDelegate<F>(FieldInfo field, out FieldRef<F> del)
        {
            del = null;
            EnsureInitialized();
            if (_emitStaticFieldRefMethod == null || field == null) return false;

            try
            {
                var getterMi = _emitStaticFieldRefMethod.Invoke(null, new object[] { typeof(F), field }) as MethodInfo;
                if (getterMi != null)
                {
                    del = (FieldRef<F>)Delegate.CreateDelegate(typeof(FieldRef<F>), getterMi);
                    return del != null;
                }
            }
            catch (System.Exception __apiEx_2) { SilksongMobileAPI.SilksongMobileAPIPlugin.LogApiCatch("CecilRuntimeRewriter", "L133", __apiEx_2); }
            return false;
        }

        private static byte[] DecompressGZip(byte[] gzBytes)
        {
            using (var msIn = new MemoryStream(gzBytes, writable: false))
            using (var gz = new GZipStream(msIn, CompressionMode.Decompress))
            using (var msOut = new MemoryStream())
            {
                gz.CopyTo(msOut);
                return msOut.ToArray();
            }
        }

        public static void EnsureUnifiedFsmUtilLoaded(string mainApiDllPath)
        {
            EnsureInitialized();
            if (_synthesizeUnifiedFsmUtilMethod == null) return;
            try
            {
                var synthMi = _synthesizeUnifiedFsmUtilMethod;
                if (synthMi != null)
                {
                    byte[] fsmBytes = synthMi.Invoke(null, new object[] { mainApiDllPath }) as byte[];
                    if (fsmBytes != null && fsmBytes.Length > 64)
                    {
                        Assembly.Load(fsmBytes);
                        SilksongMobileAPIPlugin.LogDiagnosticInfo($"[UnifiedFsmUtil] Loaded unified Silksong.FsmUtil v0.3.17 + Mobile Fork assembly into AppDomain ({fsmBytes.Length} bytes).");
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongMobileAPIPlugin.LogDiagnosticWarn($"[UnifiedFsmUtil] Load notice: {ex.Message}");
            }
        }
    }
}
