﻿#if NET6_0_OR_GREATER

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader.NativeUtils;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace MelonLoader.Fixes
{
    internal static class InjectedInternalCalls
    {
        private const string _unityInjectedSuffix = "_Injected";
        private const string _customICallSuffix = "_INative";

        private static Dictionary<string, (DynamicMethodDefinition, MethodInfo, IntPtr)> _lookup = new();

        private delegate IntPtr dil2cpp_resolve_icall(IntPtr signature);
        private static NativeHook<dil2cpp_resolve_icall> il2cpp_resolve_icall_hook;

        private static Type _stringType;
        private static Type _intPtrType;
        private static Type _exceptionType;
        private static Type _il2CppObjectBaseType;

        private static MethodInfo _stringConcat;
        private static MethodInfo _objectToString;
        private static MethodInfo _melonLoggerError;
        private static MethodInfo _stringToIl2CppPtr;
        private static MethodInfo _il2CppPtrToString;
        private static MethodInfo _il2CppObjectBaseGetPointer;

        internal static unsafe void Install()
        {
            try
            {
                Type objectType = typeof(object);
                Type il2cppType = typeof(IL2CPP);
                Type melonLoggerType = typeof(MelonLogger);

                _il2CppObjectBaseType = typeof(Il2CppObjectBase);
                _exceptionType = typeof(Exception);
                _intPtrType = typeof(IntPtr);
                _stringType = typeof(string);

                _stringConcat = _stringType.GetMethod(nameof(string.Concat), [_stringType, _stringType]);
                if (_stringConcat == null)
                    throw new Exception("Failed to get string.Concat");

                _objectToString = objectType.GetMethod(nameof(ToString));
                if (_objectToString == null)
                    throw new Exception("Failed to get object.ToString");

                _stringToIl2CppPtr = il2cppType.GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp));
                if (_stringToIl2CppPtr == null)
                    throw new Exception("Failed to get IL2CPP.ManagedStringToIl2Cpp");

                _melonLoggerError = melonLoggerType.GetMethod(nameof(MelonLogger.Error),
                    BindingFlags.Static | BindingFlags.Public,
                    [_stringType]);
                if (_melonLoggerError == null)
                    throw new Exception("Failed to get MelonLogger.Error");

                _il2CppPtrToString = il2cppType.GetMethod(nameof(IL2CPP.Il2CppStringToManaged));
                if (_il2CppPtrToString == null)
                    throw new Exception("Failed to get IL2CPP.Il2CppStringToManaged");

                PropertyInfo pointerProp = _il2CppObjectBaseType.GetProperty(nameof(Il2CppObjectBase.Pointer));
                if (_il2CppPtrToString == null)
                    throw new Exception("Failed to get Il2CppObjectBase.Pointer");
                _il2CppObjectBaseGetPointer = pointerProp.GetMethod;

                string gameAssemblyName = "GameAssembly";
                NativeLibrary gameAssemblyLib = NativeLibrary.Load(gameAssemblyName);
                if (gameAssemblyLib == null)
                    throw new Exception($"Failed to load {gameAssemblyName} Native Library");

                IntPtr il2cpp_resolve_icall = gameAssemblyLib.GetExport(nameof(il2cpp_resolve_icall));
                if (il2cpp_resolve_icall == IntPtr.Zero)
                    throw new Exception($"Failed to get {nameof(il2cpp_resolve_icall)} Native Export");

                MelonDebug.Msg("Patching il2cpp_resolve_icall...");
                IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate((dil2cpp_resolve_icall)il2cpp_resolve_icall_Detour);
                il2cpp_resolve_icall_hook = new NativeHook<dil2cpp_resolve_icall>(il2cpp_resolve_icall, detourPtr);
                il2cpp_resolve_icall_hook.Attach();
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
            }
        }

        internal static void Shutdown()
        {
            if (il2cpp_resolve_icall_hook != null)
            {
                if (il2cpp_resolve_icall_hook.IsHooked)
                    il2cpp_resolve_icall_hook.Detach();
                il2cpp_resolve_icall_hook = null;
            }

            if (_lookup != null)
            {
                if (_lookup.Count > 0)
                    _lookup.Clear();
                _lookup = null;
            }
        }

        private static IntPtr il2cpp_resolve_icall_Detour(IntPtr signature)
        {
            // Convert Pointer to String
            string signatureStr = Marshal.PtrToStringAnsi(signature);
            if (string.IsNullOrEmpty(signatureStr))
                return IntPtr.Zero;

            // Check Cache
            if (_lookup.TryGetValue(signatureStr, out var result))
                return result.Item3;

            // Run Original
            IntPtr originalResult = il2cpp_resolve_icall_hook.Trampoline(signature);
            if (originalResult != IntPtr.Zero)
            {
                // Cache Original Result
                _lookup[signatureStr] = (null, null, originalResult);
                return originalResult;
            }

            // Check if Injection is Needed
            if (!ShouldInject(signatureStr, out MethodInfo unityShimMethod))
                return IntPtr.Zero;

            // Create Injected Function
            var pair = _lookup[signatureStr] = GenerateTrampoline(unityShimMethod);
            return pair.Item3;
        }

        private static bool ShouldInject(string signature, out MethodInfo unityShimMethod)
        {
            unityShimMethod = null;

            // Check if the Unity Injected Shim ICall Exists
            IntPtr unityInjectedShimResult = il2cpp_resolve_icall_hook.Trampoline(
                Marshal.StringToHGlobalAnsi($"{signature}{_unityInjectedSuffix}"));
            if (unityInjectedShimResult == IntPtr.Zero)
                return false;

            // Split the Signature
            string[] split = signature.Split("::");
            string typeName = split[0];

            // Find Managed Type
            Type newType = Il2CppInteropFixes.FixedFindType(typeName);
            if (newType == null)
                return false;

            // Find Managed Method
            string methodName = split[1];
            MethodInfo method = newType.FindMethod(methodName);
            if (method == null)
                return false;

            // Inject ICall
            unityShimMethod = method;
            return true;
        }

        private static (DynamicMethodDefinition, MethodInfo, IntPtr) GenerateTrampoline(MethodInfo unityShimMethod)
        {
            // Convert Method Parameters to Native Parameters
            var methodParams = unityShimMethod.GetParameters();
            int offset = unityShimMethod.IsStatic ? 0 : 1;
            Type[] paramTypes = new Type[methodParams.Length + offset];
            if (!unityShimMethod.IsStatic)
                paramTypes[0] = _intPtrType;
            for (int i = 0; i < methodParams.Length; i++)
            {
                if ((methodParams[i].ParameterType != _stringType)
                    && methodParams[i].ParameterType.IsValueType)
                    paramTypes[i + offset] = methodParams[i].ParameterType;
                else
                    paramTypes[i + offset] = _intPtrType;
            }

            // Convert Return Type
            Type returnType = unityShimMethod.ReturnType;
            if ((returnType == _stringType)
                || !returnType.IsValueType)
                returnType = _intPtrType;

            // Create New Injected ICall Method
            string newMethodName = $"{unityShimMethod.Name}{_customICallSuffix}";
            var trampoline = new DynamicMethodDefinition(
                newMethodName,
                returnType,
                paramTypes);
            var ilGenerator = trampoline.GetILGenerator();

            // Begin Try-Catch
            ilGenerator.BeginExceptionBlock();

            // Emit This Object
            if (!unityShimMethod.IsStatic)
                ilGenerator.EmitPtrArgToManagedObject(0, unityShimMethod.DeclaringType);

            // Convert Method Parameters to Managed Objects
            for (var i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                var paramType = param.ParameterType;
                if (paramType == _stringType)
                    ilGenerator.EmitPtrArgToString(i + offset);
                else if (paramType.IsValueType)
                    ilGenerator.EmitArg(i + offset);
                else
                    ilGenerator.EmitPtrArgToManagedObject(i + offset, paramType);
            }

            // Call Existing Method
            ilGenerator.Emit(OpCodes.Call, unityShimMethod);

            // Convert Managed Return
            var oldreturnType = unityShimMethod.ReturnType;
            if (oldreturnType == _stringType)
                ilGenerator.EmitStringToPtr();
            else if ((oldreturnType == _il2CppObjectBaseType)
                || oldreturnType.IsSubclassOf(_il2CppObjectBaseType))
                ilGenerator.EmitIl2CppObjectBaseToPtr();

            // Cache Return Value in Lcal
            LocalBuilder returnLocal = null;
            if (returnType != typeof(void))
            {
                returnLocal = ilGenerator.DeclareLocal(returnType);
                ilGenerator.Emit(OpCodes.Stloc, returnLocal);
            }

            // End Try-Catch
            ilGenerator.EmitExceptionCatch();

            // Restore Return Value from Local
            if (returnLocal != null)
                ilGenerator.Emit(OpCodes.Ldloc, returnLocal);

            // Return even if there is no Return Value
            ilGenerator.Emit(OpCodes.Ret);

            // Return the New Method
            MethodInfo newMethod = trampoline.Generate().Pin();
            return (trampoline, newMethod, newMethod.GetNativeStart());
        }

        private static void EmitArg(this ILGenerator ilGenerator, 
            int index)
            => ilGenerator.Emit(OpCodes.Ldarg, index);

        private static void EmitPtrArgToString(this ILGenerator ilGenerator,
            int argIndex)
        {
            ilGenerator.EmitArg(argIndex);
            ilGenerator.Emit(OpCodes.Call, _il2CppPtrToString);
        }

        private static void EmitStringToPtr(this ILGenerator ilGenerator)
            => ilGenerator.Emit(OpCodes.Call, _stringToIl2CppPtr);

        private static void EmitPtrArgToManagedObject(this ILGenerator ilGenerator,
            int argIndex,
            Type managedType)
        {
            ilGenerator.EmitArg(argIndex);

            var labelNull = ilGenerator.DefineLabel();
            var labelDone = ilGenerator.DefineLabel();
            ilGenerator.Emit(OpCodes.Brfalse, labelNull);
            ilGenerator.EmitArg(argIndex);

            ilGenerator.Emit(OpCodes.Newobj,
                managedType.GetConstructor([ _intPtrType ]));

            ilGenerator.Emit(OpCodes.Br, labelDone);
            ilGenerator.MarkLabel(labelNull);
            ilGenerator.Emit(OpCodes.Ldnull);
            ilGenerator.MarkLabel(labelDone);
        }

        private static void EmitIl2CppObjectBaseToPtr(this ILGenerator ilGenerator)
        {
            var labelNull = ilGenerator.DefineLabel();
            var labelDone = ilGenerator.DefineLabel();
            ilGenerator.Emit(OpCodes.Dup);
            ilGenerator.Emit(OpCodes.Brfalse, labelNull);

            ilGenerator.Emit(OpCodes.Call, _il2CppObjectBaseGetPointer);

            ilGenerator.Emit(OpCodes.Br, labelDone);
            ilGenerator.MarkLabel(labelNull);
            ilGenerator.Emit(OpCodes.Pop);
            ilGenerator.Emit(OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Conv_I);
            ilGenerator.MarkLabel(labelDone);
        }

        private static void EmitExceptionCatch(this ILGenerator ilGenerator)
        {
            var exceptionLocal = ilGenerator.DeclareLocal(_exceptionType);
            ilGenerator.BeginCatchBlock(_exceptionType);

            ilGenerator.Emit(OpCodes.Stloc, exceptionLocal);
            ilGenerator.Emit(OpCodes.Ldstr, "Exception in IL2CPP Injected ICall: ");
            ilGenerator.Emit(OpCodes.Ldloc, exceptionLocal);

            ilGenerator.Emit(OpCodes.Callvirt, _objectToString);
            ilGenerator.Emit(OpCodes.Call, _stringConcat);
            ilGenerator.Emit(OpCodes.Call, _melonLoggerError);

            ilGenerator.EndExceptionBlock();
        }
    }
}

#endif