using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace DesktopBuddySharedTextureBridge
{
    [BepInPlugin("net.desktopbuddy.sharedtexturebridge", "DesktopBuddySharedTextureBridge", BridgeVersionInfo.Version)]
    public class SharedTextureBridgePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private SharedTextureBridge _bridge;

        private void Awake()
        {
            Log = Logger;
            LogInfo("DesktopBuddySharedTextureBridge starting...");

            LinuxCaptureGuard.Initialize(Log);

            try
            {
                new Harmony("net.desktopbuddy.sharedtexturebridge").PatchAll();
                LogInfo("Harmony patches applied");
            }
            catch (Exception ex)
            {
                LogError("Harmony PatchAll failed", ex);
                throw;
            }

            try
            {
                _bridge = new SharedTextureBridge(Log);
                LogInfo("SharedTextureBridge created");
            }
            catch (Exception ex)
            {
                LogError("SharedTextureBridge creation failed", ex);
                throw;
            }

            LogInfo("DesktopBuddySharedTextureBridge ready");
        }

        private void Update()
        {
            try
            {
                _bridge?.Update();
            }
            catch (Exception ex)
            {
                LogError("Update failed", ex);
            }
        }

        private void OnDestroy()
        {
            LogInfo("DesktopBuddySharedTextureBridge OnDestroy START");
            try
            {
                _bridge?.Dispose();
                LogInfo("SharedTextureBridge disposed");
            }
            catch (Exception ex)
            {
                LogError("SharedTextureBridge dispose failed", ex);
            }

            try
            {
                UnityD3D11Device.Dispose();
                LogInfo("UnityD3D11Device disposed");
            }
            catch (Exception ex)
            {
                LogError("UnityD3D11Device dispose failed", ex);
            }
            LogInfo("DesktopBuddySharedTextureBridge OnDestroy DONE");
        }

        internal static void LogInfo(string message)
        {
            Log?.LogInfo(message);
        }

        internal static void LogWarning(string message)
        {
            Log?.LogWarning(message);
        }

        internal static void LogError(string message, Exception ex)
        {
            Log?.LogError($"{message}: {ex}");
        }
    }
}
