using System;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    internal static new DesktopBuddyConfig Config;
    private static int _runtimeBitrateMbps = 10;
    private static int _runtimeStreamFps = 60;
    private static int _runtimeMaxStreamResolution = 2560;
    private static string _runtimeEncoderPreference = "auto";
    internal static readonly DesktopBuddyConfigKey<bool> SpatialAudioEnabled =
        new("spatialAudio", "Enable spatial in-game audio (redirects window audio to VB-Cable). When off, use Windows volume slider instead.", () => false);
    internal static readonly DesktopBuddyConfigKey<bool> CheckForUpdates =
        new("checkForUpdates", "Check for updates and show a notification when a new version is available on startup.", () => true);
    internal static readonly DesktopBuddyConfigKey<bool> ShowContextMenuItem =
        new("showContextMenuItem", "Show DesktopBuddy in the Resonite context menu.", () => true);
    internal static readonly DesktopBuddyConfigKey<bool> ThrowToDestroy =
        new("throwToDestroy", "Destroy DesktopBuddy panels when thrown quickly.", () => true);
    internal static readonly DesktopBuddyConfigKey<bool> SpawnNewWindowsInGame =
        new("spawnNewWindowsInGame", "Automatically spawn DesktopBuddy panels for new standalone windows from the same process.", () => true);
    internal static readonly DesktopBuddyConfigKey<bool> SpawnNewWindowsPrivate =
        new("spawnNewWindowsPrivate", "Automatically spawned new-window panels start private.", () => true);
    internal static readonly DesktopBuddyConfigKey<bool> NewWindowsStartPrivate =
        new("newWindowsStartPrivate", "New DesktopBuddy panels you spawn (monitors, windows, desktop) start in Private mode.", () => false);
    internal static readonly DesktopBuddyConfigKey<bool> DynamicLightsEnabled =
        new("dynamicLights", "Cast a dynamic in-game light that matches the captured screen's average color.", () => false);
    internal static readonly DesktopBuddyConfigKey<int> Bitrate =
        new("bitrate", "Video encoding bitrate in Mbps.", () => 10);
    internal static readonly DesktopBuddyConfigKey<int> StreamFps =
        new("streamFps", "Nominal stream FPS for encoder timing. Capture remains event-driven and is not frame-capped.", () => 60);
    internal static readonly DesktopBuddyConfigKey<int> MaxStreamResolution =
        new("maxStreamResolution", "Maximum encoded stream long-edge resolution. 2560 is 2K/QHD.", () => 2560);
    internal static readonly DesktopBuddyConfigKey<bool> UseMediaMtx =
        new("useMediaMtx", "Use an external MediaMTX server for streaming instead of the built-in Cloudflare HTTP stream.", () => false);
    internal static readonly DesktopBuddyConfigKey<string> MediaMtxHost =
        new("mediaMtxHost", "MediaMTX server address (IP or hostname).", () => "");
    internal static readonly DesktopBuddyConfigKey<int> MediaMtxPort =
        new("mediaMtxPort", "MediaMTX RTSP port.", () => 8554);
    internal static readonly DesktopBuddyConfigKey<string> MediaMtxStreamName =
        new("mediaMtxStreamName", "MediaMTX stream name (path component of the RTSP URL). Leave blank to auto-generate a random name per session.", () => "");
    internal static readonly DesktopBuddyConfigKey<string> StreamNetworkMode =
        new("streamNetworkMode", "Built-in stream access mode: cloudflare or port_forward.", () => "cloudflare");
    internal static readonly DesktopBuddyConfigKey<string> PortForwardHostMode =
        new("portForwardHostMode", "Port-forward host mode: auto or manual.", () => "auto");
    internal static readonly DesktopBuddyConfigKey<string> PortForwardAutoIpMode =
        new("portForwardAutoIpMode", "Auto port-forward host IP source. External public IPv4 is always used.", () => "external");
    internal static readonly DesktopBuddyConfigKey<string> PortForwardHost =
        new("portForwardHost", "Manual public hostname or IP for port-forwarded built-in streams.", () => "");
    internal static readonly DesktopBuddyConfigKey<bool> PortForwardUseNat =
        new("portForwardUseNat", "Automatically create a UPnP/NAT TCP port mapping for the built-in stream port.", () => false);
    internal static readonly DesktopBuddyConfigKey<string> PanelCurvePreferences =
        new("panelCurvePreferences", "Saved DesktopBuddy panel curve values, keyed by application executable path or shared desktop capture.", () => "");
    internal static readonly DesktopBuddyConfigKey<string> LinuxSharedSources =
        new("linuxSharedSources", "Saved Linux desktop/window sources for instant re-share, with their restore tokens and icons.", () => "");
    internal static readonly DesktopBuddyConfigKey<float> SpawnTilt =
        new("spawnTilt", "Tilt of newly spawned panels, in degrees. Positive leans the top away from you like a monitor on a stand; negative tips it towards you, which reads better on a panel spawned above eye level. 0 is upright.", () => 0.0f);
    internal static readonly DesktopBuddyConfigKey<float> StickScrollSpeed =
        new("stickScrollSpeed", "Controller thumbstick scroll speed, in wheel notches per second at full deflection.", () => 8.0f);
    internal static readonly DesktopBuddyConfigKey<bool> LinuxPointerInput =
        new("linuxPointerInput", "Send laser interaction as real mouse button events instead of touchscreen events. Touch mode makes apps treat the panel as a touchscreen: drag pans instead of selecting text, press-and-hold opens context menus, and some shell surfaces ignore it entirely.", () => true);
    internal static readonly DesktopBuddyConfigKey<string> LinuxInputRestoreToken =
        new("linuxInputRestoreToken", "Portal restore token for the shared Linux input session, so input permission is only asked for once.", () => "");
    internal static readonly DesktopBuddyConfigKey<bool> LinuxSuspendShakeCursor =
        new("linuxSuspendShakeCursor", "Suspend KWin's shake-cursor effect while sharing, so laser and mouse input do not magnify the cursor (KDE only).", () => true);
    internal static readonly DesktopBuddyConfigKey<string> ViewerCullingMode =
        new("viewerCullingMode", "Viewer culling mode for remote streams: frustum or distance.", () => "frustum");
    internal static readonly DesktopBuddyConfigKey<bool> ViewerCullingPreview =
        new("viewerCullingPreview", "Show the viewer culling preview guide on DesktopBuddy panels.", () => false);
    internal static readonly DesktopBuddyConfigKey<float> ViewerFrustumWidth =
        new("viewerFrustumWidth", "Viewer frustum culling preview angle in degrees.", () => 120.0f);
    internal static readonly DesktopBuddyConfigKey<float> ViewerFrustumDepth =
        new("viewerFrustumDepth", "Viewer frustum culling depth in meters.", () => 3.0f);
    internal static readonly DesktopBuddyConfigKey<float> ViewerDistance =
        new("viewerDistance", "Viewer distance culling radius in meters.", () => 3.0f);
    internal static readonly DesktopBuddyConfigKey<string> EncoderPreference =
        new("encoderPreference", "Explicit stream encoder preference, or auto.", () => "auto");
    internal static readonly DesktopBuddyConfigKey<string> PreferredGpuLuid =
        new("preferredGpuLuid", "Preferred DXGI adapter LUID for DesktopBuddy capture/encoding, or blank for auto.", () => "");
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioOutputVolume =
        new("streamAudioOutputVolume", "Default local stream AudioOutput volume. 0 means viewers start muted and opt in.", () => 0.0f);
    internal static readonly DesktopBuddyConfigKey<string> StreamAudioGlobalMode =
        new("streamAudioGlobalMode", "Stream AudioOutput global mode: auto, global, or positional.", () => "positional");
    internal static readonly DesktopBuddyConfigKey<bool> StreamAudioSpatialize =
        new("streamAudioSpatialize", "Enable stream AudioOutput spatialization.", () => true);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioSpatialBlend =
        new("streamAudioSpatialBlend", "Stream AudioOutput spatial blend.", () => 1.0f);
    internal static readonly DesktopBuddyConfigKey<string> StreamAudioDistanceSpace =
        new("streamAudioDistanceSpace", "Stream AudioOutput distance space: local or global.", () => "global");
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioDopplerLevel =
        new("streamAudioDopplerLevel", "Stream AudioOutput doppler level.", () => 0.0f);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioPitch =
        new("streamAudioPitch", "Stream AudioOutput pitch.", () => 1.0f);
    internal static readonly DesktopBuddyConfigKey<bool> StreamAudioIgnoreAudioEffects =
        new("streamAudioIgnoreAudioEffects", "Bypass Resonite audio effects for stream playback.", () => true);
    internal static readonly DesktopBuddyConfigKey<string> StreamAudioTypeGroup =
        new("streamAudioTypeGroup", "Stream AudioOutput type group: multimedia, sound_effect, voice, or ui.", () => "multimedia");
    internal static readonly DesktopBuddyConfigKey<string> StreamAudioRolloffMode =
        new("streamAudioRolloffMode", "Stream AudioOutput rolloff mode: logarithmic_fade_off or linear.", () => "linear");
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioMinDistance =
        new("streamAudioMinDistance", "Stream AudioOutput minimum distance.", () => 1.0f);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioMaxDistance =
        new("streamAudioMaxDistance", "Stream AudioOutput maximum distance.", () => 30.0f);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioSpatializationStartDistance =
        new("streamAudioSpatializationStartDistance", "Stream AudioOutput spatialization start distance.", () => 0.01f);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioSpatializationTransitionRange =
        new("streamAudioSpatializationTransitionRange", "Stream AudioOutput spatialization transition range.", () => 0.01f);
    internal static readonly DesktopBuddyConfigKey<int> StreamAudioPriority =
        new("streamAudioPriority", "Stream AudioOutput priority.", () => 128);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioMinScale =
        new("streamAudioMinScale", "Stream AudioOutput local-distance minimum scale clamp.", () => 0.0f);
    internal static readonly DesktopBuddyConfigKey<float> StreamAudioMaxScale =
        new("streamAudioMaxScale", "Stream AudioOutput local-distance maximum scale clamp.", () => 1000.0f);

    private static void BindConfigKeys()
    {
        Config.Bind(SpatialAudioEnabled);
        Config.Bind(CheckForUpdates);
        Config.Bind(ShowContextMenuItem);
        Config.Bind(ThrowToDestroy);
        Config.Bind(SpawnNewWindowsInGame);
        Config.Bind(SpawnNewWindowsPrivate);
        Config.Bind(NewWindowsStartPrivate);
        Config.Bind(DynamicLightsEnabled);
        Config.Bind(Bitrate);
        Config.Bind(StreamFps);
        Config.Bind(MaxStreamResolution);
        Config.Bind(UseMediaMtx);
        Config.Bind(MediaMtxHost);
        Config.Bind(MediaMtxPort);
        Config.Bind(MediaMtxStreamName);
        Config.Bind(StreamNetworkMode);
        Config.Bind(PortForwardHostMode);
        Config.Bind(PortForwardAutoIpMode);
        Config.Bind(PortForwardHost);
        Config.Bind(PortForwardUseNat);
        Config.Bind(PanelCurvePreferences);
        Config.Bind(LinuxSharedSources);
        Config.Bind(ViewerCullingMode);
        Config.Bind(ViewerCullingPreview);
        Config.Bind(ViewerFrustumWidth);
        Config.Bind(ViewerFrustumDepth);
        Config.Bind(ViewerDistance);
        Config.Bind(EncoderPreference);
        Config.Bind(PreferredGpuLuid);
        Config.Bind(StreamAudioOutputVolume);
        Config.Bind(StreamAudioGlobalMode);
        Config.Bind(StreamAudioSpatialize);
        Config.Bind(StreamAudioSpatialBlend);
        Config.Bind(StreamAudioDistanceSpace);
        Config.Bind(StreamAudioDopplerLevel);
        Config.Bind(StreamAudioPitch);
        Config.Bind(StreamAudioIgnoreAudioEffects);
        Config.Bind(StreamAudioTypeGroup);
        Config.Bind(StreamAudioRolloffMode);
        Config.Bind(StreamAudioMinDistance);
        Config.Bind(StreamAudioMaxDistance);
        Config.Bind(StreamAudioSpatializationStartDistance);
        Config.Bind(StreamAudioSpatializationTransitionRange);
        Config.Bind(StreamAudioPriority);
        Config.Bind(StreamAudioMinScale);
        Config.Bind(StreamAudioMaxScale);
    }

    internal static bool IsMediaMtxEnabled =>
        Config?.GetValue(UseMediaMtx) == true && !string.IsNullOrWhiteSpace(Config?.GetValue(MediaMtxHost));
}
