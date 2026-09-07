using System;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.UIX;

namespace DesktopBuddy;

public class DesktopSession
{
    public DesktopStreamer Streamer;
    public DesktopTextureProvider Texture;
    public RawImage TextureImage;
    public Canvas Canvas;
    public Slot Root;
    public bool UpdateInProgress;
    public bool UseTextureBridge;
    public bool BridgeRegistrationPending;
    public int SharedTextureSlot = -1;
    public int PendingBridgeDisplayIndex = -1;
    public bool BridgeDisplayIndexApplied;
    public int LastKnownW, LastKnownH;

    public Component LastActiveSource;
    public HashSet<uint> ActiveTouchIds = new();

    /// <summary>
    /// Panel is pinned: its Grabbable is disabled so it cannot be moved, which frees the grab
    /// action to act as right-click instead.
    /// </summary>
    public bool PanelLocked;
    public double LastGrabTick;

    /// <summary>
    /// Fractional wheel notches banked from thumbstick deflection. Scrolling is accumulated
    /// over time rather than emitted per frame, so the speed does not scale with framerate.
    /// </summary>
    public float StickScrollAccum;

    public int LastScrollSign;
    public double LastScrollTick;

    public int StreamId;
    public IntPtr Hwnd;

    public uint ProcessId;
    public double TimeSinceChildCheck;
    public double TimeSinceValidCheck;
    public bool LastValidState = true;
    public TextRenderer TitleText;
    public string LastTitle;
    public HashSet<IntPtr> SeenRelatedHwnds = new();
    public bool Cleaned;

    public double ResizeDebounceUntil;
    public int PendingResizeW, PendingResizeH;
    public int PendingVisualResizeW, PendingVisualResizeH;
    public BoxCollider Collider;
    public Slot TopBarRenderHost;
    public SettingsPanelState SettingsPanel;
    public Slot CullingPreviewSlot;
    public CurvedPlaneMesh PanelMesh;
    public float PanelCanvasScale;
    public string OwnerUserId;
    public ValueUserOverride<bool> ViewerStreamAllowed;
    public ValueUserOverride<bool> PreviewStreamAllowed;
    public ValueUserOverride<bool> StreamVisualAllowed;
    public ValueUserOverride<bool> FinalStreamAllowedOverride;
    public ValueField<bool> ViewerAllowedField;
    public ValueField<bool> PreviewAllowedField;
    public ValueField<bool> FinalStreamAllowedField;
    public BooleanValueDriver<Uri> StreamUrlDriver;
    public Uri StreamUrl;
    public readonly Dictionary<string, bool> ViewerStreamEnabledByUserId = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> CullingAppliedStreamAllowedByUserId = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, double> CullingOutOfRangeSinceByUserId = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> CullingPresentUserIds = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> CullingStaleUserIds = new();
    public bool LocalPreviewingRemoteStream;
    public int CullingGateGeneration;
    public double CullingOutOfRangeSince = -1;

    public DesktopKeyboardSource KeyboardSource;

    public ulong LinuxInputSessionId;

    /// <summary>ScreenCast session owning this panel's capture stream; closed on cleanup.</summary>
    public ulong LinuxCaptureSessionId;

    /// <summary>
    /// Captured region in compositor coordinates plus the workspace size, used to translate
    /// panel-local input into the workspace space the input session addresses.
    /// </summary>
    public int LinuxPositionX, LinuxPositionY;
    public int LinuxWorkspaceWidth, LinuxWorkspaceHeight;

    public FfmpegEncoder Encoder;
    internal ILiveStreamSource StreamSource;
    public int EncoderInitialStreamId;
    public int EncoderInitialSourceW, EncoderInitialSourceH;
    public bool StreamUsesMediaMtx;
    public AudioCapture StreamAudioCapture;
    public Action StartStreamAudioCapture;
    public VideoTextureProvider VideoTexture;
    public AudioOutput StreamAudioOutput;
    public Slider<float> StreamVolumeSlider;
    public bool FeedsVirtualCamera;
    public Slot VCamSlot;
    public Camera VCamCamera;
    public RenderTextureProvider VCamPreviewTexture;
    public bool VCamRenderPending;
    public long VCamLastSubmitTicks;
    public UI_UnlitMaterial VCamIndicator;
    public bool VCamLastLitState;
    public Slot VMicSlot;
    public AudioListener VMicListener;
    public UI_UnlitMaterial VMicIndicator;
    public bool VMicMuted;
    public Light AdaptiveLight;
    public D3D11AverageColorSampler AdaptiveLightSampler;
    public long AdaptiveLightLastSampleTicks;
    public DesktopAudioSource SpatialAudioSource;
    public AudioOutput SpatialAudioOutput;
    public bool OwnsAudioRedirect;

    public Action<int, int> OnResize;
}

public sealed class SettingsPanelState
{
    public Slot RenderHost;
    public Slot RenderRoot;
    public Slot SurfaceSlot;
    public Canvas Canvas;
    public RectTransform ModalRect;
    public RenderTextureProvider RenderTexture;
    public Camera Camera;
    public CurvedPlaneMesh Mesh;
    public BlurMaterial BackgroundBlur;
    public StaticTexture2D BackgroundBlurMask;
    public int BackgroundBlurMaskWidth;
    public int BackgroundBlurMaskHeight;
    public Slot ContentRoot;
    public Slot TabRoot;
    public ScrollRect ContentScroll;
    public ScrollRect DebugLogScroll;
    public Text DebugLogText;
    public List<UiScrollbarState> Scrollbars = new();
    public Slot OwnerRoot;
    public DesktopSession Session;
    public int RenderWidth;
    public int RenderHeight;
    public int ModalWidth;
    public int ModalHeight;
    public float CanvasScale;
    public SettingsPanelTab ActiveTab = SettingsPanelTab.Viewers;
    public bool ViewerCullingPreviewEnabled;
    public string ViewerCullingMode = "frustum";
    public float ViewerFrustumAngle = 120f;
    public float ViewerFrustumDepth = 3f;
    public float ViewerDistance = 3f;
    public int DebugLogRefreshGeneration;
    public string DebugLogContent;
    public int ViewerListRefreshGeneration;
    public string ViewerListSignature;
    public int StickScrollGeneration;
}

public enum SettingsPanelTab
{
    Viewers,
    General,
    Stream,
    Audio,
    Network,
    Devices,
    Debug,
    UpdateInfo
}
