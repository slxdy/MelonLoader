﻿using System;
using System.Diagnostics;
using MelonLoader.InternalUtils;
using MelonLoader.MonoInternals;
#pragma warning disable IDE0051 // Prevent the IDE from complaining about private unreferenced methods

namespace MelonLoader
{
	internal static class Core
    {
        internal static HarmonyLib.Harmony HarmonyInstance = null;

        private static int Initialize()
        {
            AppDomain curDomain = AppDomain.CurrentDomain;
            Fixes.UnhandledException.Install(curDomain);
            Fixes.ServerCertificateValidation.Install();

            MelonUtils.Setup(curDomain);
            Assertions.LemonAssertMapping.Setup();
            MelonHandler.Setup();

            if (!MonoLibrary.Setup()
                || !MonoResolveManager.Setup())
                return 1;

            HarmonyInstance = new HarmonyLib.Harmony(BuildInfo.Name);

            Fixes.ForcedCultureInfo.Install();
            Fixes.InstancePatchFix.Install();
            Fixes.ProcessFix.Install();
            PatchShield.Install();

            MelonPreferences.Load();
            MelonLaunchOptions.Load();
            bHaptics.Load();

            MelonCompatibilityLayer.Setup();
            MelonCompatibilityLayer.SetupModules(MelonCompatibilityLayer.SetupType.OnPreInitialization);

            MelonHandler.LoadMelonsFromDirectory<MelonPlugin>(MelonHandler.PluginsDirectory);
            MelonEvents.OnPreInitialization.Invoke();

            return 0;
        }

        private static int PreStart()
        {
            MelonEvents.OnApplicationEarlyStart.Invoke();
            return MelonStartScreen.LoadAndRun(Il2CppGameSetup);
        }

        private static int Il2CppGameSetup()
            => (MelonUtils.IsGameIl2Cpp()
                && !Il2CppAssemblyGenerator.Run())
                ? 1 : 0;

        private static int Start()
        {
            bHaptics.Start();

            MelonEvents.OnPreSupportModule.Invoke();

            if (!SupportModule.Setup())
                return 1;

            MelonCompatibilityLayer.SetupModules(MelonCompatibilityLayer.SetupType.OnApplicationStart);
            AddUnityDebugLog();

            RegisterTypeInIl2Cpp.SetReady();

            MelonHandler.LoadMelonsFromDirectory<MelonMod>(MelonHandler.ModsDirectory);

            MelonEvents.OnApplicationStart.Invoke();
            //MelonStartScreen.DisplayModLoadIssuesIfNeeded();

            return 0;
        }

        internal static void OnApplicationLateStart()
        {
            MelonEvents.OnApplicationLateStart.Invoke();
        }

        internal static void Quit()
        {
            MelonEvents.OnApplicationQuit.Invoke();
            MelonPreferences.Save();

            HarmonyInstance.UnpatchSelf();
            bHaptics.Quit();

            MelonLogger.Flush();

            if (MelonLaunchOptions.Core.QuitFix)
                Process.GetCurrentProcess().Kill();
        }

        private static void AddUnityDebugLog()
        {
            SupportModule.Interface.UnityDebugLog("--------------------------------------------------------------------------------------------------");
            SupportModule.Interface.UnityDebugLog("~   This Game has been MODIFIED using MelonLoader. DO NOT report any issues to the Developers!   ~");
            SupportModule.Interface.UnityDebugLog("--------------------------------------------------------------------------------------------------");
        }
    }
}