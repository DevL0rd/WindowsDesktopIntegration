using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopBuddy.Shared;
using Renderite.Shared;
using FrooxEngine;
using SkyFrost.Base;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    internal static void SpawnStreaming(World world, IntPtr hwnd, string title, IntPtr monitorHandle = default, int monitorIndex = -1, bool startPrivate = false)
    {
        try
        {
            Msg($"[SpawnStreaming] Starting for '{title}' hwnd={hwnd} monitorIndex={monitorIndex} startPrivate={startPrivate}");
            if (hwnd != IntPtr.Zero)
            {
                WindowEnumerator.GetWindowThreadProcessId(hwnd, out uint processId);
                if (!WindowEnumerator.TryValidateStandaloneProcessWindow(hwnd, processId, out string currentTitle, out string validationReason))
                {
                    Msg($"[SpawnStreaming] Ignored hwnd={hwnd}: {validationReason}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(currentTitle))
                    title = currentTitle;
            }

            var localUser = world.LocalUser;
            if (localUser == null) { Msg("[SpawnStreaming] LocalUser is null, aborting"); return; }
            var userRoot = localUser.Root;
            if (userRoot == null) { Msg("[SpawnStreaming] UserRoot is null, aborting"); return; }

            var root = (localUser.Root.Slot.Parent ?? world.RootSlot).AddSlot("Desktop Buddy");

            var headPos = userRoot.HeadPosition;
            var headRot = userRoot.HeadRotation;
            var forward = headRot * float3.Forward;
            float userScale = GetUserSpawnScale(userRoot);
            root.LocalScale = float3.One * userScale;
            root.GlobalPosition = headPos + forward * (0.8f * userScale);
            // Face the user, then pitch about the panel's own right axis so the tilt is
            // applied in panel space and stays correct whichever way the user is turned.
            float spawnTilt = Config?.GetValue(SpawnTilt) ?? 0f;
            spawnTilt = MathX.Clamp(spawnTilt, -85f, 85f);
            root.GlobalRotation = floatQ.LookRotation(forward, float3.Up) * floatQ.Euler(spawnTilt, 0f, 0f);
            var destroyer = root.AttachComponent<DestroyOnUserLeave>();

            destroyer.TargetUser.Target = localUser;

            Msg($"[SpawnStreaming] Slot created at pos={root.GlobalPosition} userScale={userScale}");

            StartStreaming(root, hwnd, title, monitorHandle: monitorHandle, monitorIndex: monitorIndex, startPrivate: startPrivate);
        }
        catch (Exception ex)
        {
            Msg($"ERROR in SpawnStreaming: {ex}");
        }
    }

    private static float GetUserSpawnScale(UserRoot userRoot)
    {
        try
        {
            var scale = userRoot?.Slot?.GlobalScale ?? float3.One;
            float userScale = MathF.Max(scale.x, MathF.Max(scale.y, scale.z));
            return float.IsFinite(userScale) && userScale > 0.001f ? userScale : 1f;
        }
        catch (Exception ex)
        {
            Msg($"[SpawnStreaming] Failed to read user scale, using 1: {ex.Message}");
            return 1f;
        }
    }

    private static void StartStreaming(Slot root, IntPtr hwnd, string title, IntPtr monitorHandle = default, int monitorIndex = -1, bool startPrivate = false)
    {
        Msg($"[StartStreaming] Window: {title} (hwnd={hwnd} monitorIndex={monitorIndex})");

        if (!DesktopBuddyPlatform.IsLinux)
            WindowInput.RestoreIfMinimized(hwnd);

        var streamer = new DesktopStreamer(hwnd, monitorHandle);
        var world = root.World;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!streamer.TryInitialCapture())
                {
                    Msg($"[StartStreaming] Failed initial capture for: {title}");
                    streamer.Dispose();
                    world.RunInUpdates(0, () =>
                    {
                        try
                        {
                            if (root != null && !root.IsDestroyed)
                                root.Destroy();
                        }
                        catch (Exception ex) { Msg($"[StartStreaming] Failed-capture destroy error: {ex}"); }
                    });
                    return;
                }
                world.RunInUpdates(0, () =>
                {
                    try { FinishStartStreaming(root, hwnd, title, streamer, monitorIndex, startPrivate); }
                    catch (Exception ex)
                    {
                        Msg($"[StartStreaming] FinishStartStreaming callback error: {ex}");
                        try { streamer.Dispose(); } catch (Exception disposeEx) { Msg($"[StartStreaming] Streamer dispose after finish error: {disposeEx}"); }
                        try { if (root != null && !root.IsDestroyed) root.Destroy(); } catch (Exception destroyEx) { Msg($"[StartStreaming] Root destroy after finish error: {destroyEx}"); }
                    }
                });
            }
            catch (Exception ex)
            {
                Msg($"[StartStreaming] Background capture task error: {ex}");
                try { streamer.Dispose(); } catch (Exception disposeEx) { Msg($"[StartStreaming] Streamer dispose after task error: {disposeEx}"); }
                try
                {
                    world.RunInUpdates(0, () =>
                    {
                        try { if (root != null && !root.IsDestroyed) root.Destroy(); }
                        catch (Exception destroyEx) { Msg($"[StartStreaming] Root destroy after task error: {destroyEx}"); }
                    });
                }
                catch (Exception scheduleEx) { Msg($"[StartStreaming] Failed to schedule cleanup after task error: {scheduleEx}"); }
            }
        });
    }

    private static void FinishStartStreaming(Slot root, IntPtr hwnd, string title, DesktopStreamer streamer, int monitorIndex = -1, bool startPrivate = false)
    {
        if (root == null || root.IsDestroyed)
        {
            Msg($"[StartStreaming] Root slot destroyed before capture completed: {title}");
            streamer.Dispose();
            return;
        }

        int w = streamer.Width;
        int h = streamer.Height;
        Grabbable grabbable = null;

        Msg($"[StartStreaming] Capture size: {w}x{h}");

        float canvasScale = 0.0005f;
        float worldHalfH = h / 2f * canvasScale;
        float worldHalfW = w / 2f * canvasScale;
        BoxCollider collider = null;
        Msg("[StartStreaming] Panel grab/click colliders are curved mesh colliders");
        CurvedPlaneMesh frontPlaneRef = null;
        CurvedPlaneMesh backPlaneRef = null;
        CurvedPlaneMesh streamPlaneRef = null;
        CurvedPlaneMesh topBarStripRef = null;
        CurvedPlaneMesh topBarBackStripRef = null;
        DesktopUVRayExit displayRayExitRef = null;
        TextRenderer titleTextRef = null;
        Slot deviceIndicatorsSlot = null;
        string panelCurvePreferenceKey = GetPanelCurvePreferenceKey(hwnd);
        float currentPanelCurvature = GetPanelCurvePreference(panelCurvePreferenceKey, DesktopPanelCurvature);
        DesktopSession session = null;

        void ApplyPanelCurvature(float curvature)
        {
            currentPanelCurvature = MathX.Clamp(curvature, 0f, 1f);

            if (frontPlaneRef != null && !frontPlaneRef.IsDestroyed)
                frontPlaneRef.Curvature.Value = currentPanelCurvature;

            if (backPlaneRef != null && !backPlaneRef.IsDestroyed)
                backPlaneRef.Curvature.Value = currentPanelCurvature;

            if (streamPlaneRef != null && !streamPlaneRef.IsDestroyed)
                streamPlaneRef.Curvature.Value = currentPanelCurvature;

            if (topBarStripRef != null && !topBarStripRef.IsDestroyed)
                topBarStripRef.Curvature.Value = currentPanelCurvature;

            if (topBarBackStripRef != null && !topBarBackStripRef.IsDestroyed)
                topBarBackStripRef.Curvature.Value = currentPanelCurvature;

            if (session != null)
            {
                UpdateAdaptiveScreenLightPosition(session);
                ResizeSettingsPanel(session, session.LastKnownW > 0 ? session.LastKnownW : w, session.LastKnownH > 0 ? session.LastKnownH : h, canvasScale, currentPanelCurvature);
            }
        }

        var displaySlot = root.AddLocalSlot("Display", false);
        displaySlot.LocalScale = float3.One * canvasScale;
        Msg("[StartStreaming] Display slot (local) created");

        var texSlot = displaySlot.AddSlot("Texture");
        var procTex = TextureProviderSettings.ClampWrap(texSlot.AttachComponent<DesktopTextureProvider>());
        procTex.DisplayIndex.Value = int.MinValue;
        OurProviders.Add(procTex);
        int sharedTextureSlot = -1;
        int pendingBridgeDisplayIndex = -1;
        bool useTextureBridge = TextureBridgeChannel != null && TextureBridgeChannel.IsOpen &&
            (DesktopBuddyPlatform.IsLinux || hwnd != IntPtr.Zero || streamer.MonitorHandle != IntPtr.Zero || monitorIndex >= 0);
        if (useTextureBridge)
        {
            Msg("[StartStreaming] Shared texture bridge registration deferred until first current-size frame");
        }
        else if (hwnd == IntPtr.Zero && monitorIndex >= 0)
        {
            procTex.DisplayIndex.Value = monitorIndex;
            Msg($"[StartStreaming] WARNING: Shared texture bridge unavailable; falling back to built-in monitor DisplayIndex={monitorIndex}");
        }
        else
        {
            Msg($"[StartStreaming] WARNING: Cannot set up texture (hwnd={hwnd}, monitorIndex={monitorIndex}, bridge={(TextureBridgeChannel?.IsOpen ?? false)})");
        }
        Msg("[StartStreaming] Texture component created");

        var interactionSlot = displaySlot.AddSlot("InteractionCanvas");
        interactionSlot.LocalScale = float3.One;
        var interactionCanvas = interactionSlot.AttachComponent<Canvas>();
        interactionCanvas.Size.Value = new float2(w, h);
        var ui = new UIBuilder(interactionCanvas);
        ui.Canvas.Collider.RawTarget.Enabled = false;

        var displayBg = ui.Image(new colorX(0f, 0f, 0f, 1f));
        displayBg.Tint.Value = colorX.Clear;
        displayBg.InteractionTarget.Value = false;
        ui.NestInto(displayBg.RectTransform);
        Msg("[StartStreaming] Interaction canvas created");

        var displayCameraSlot = interactionSlot.AddSlot("InteractionCamera");
        displayCameraSlot.LocalPosition = float3.Zero;
        displayCameraSlot.LocalRotation = floatQ.Identity;
        displayRayExitRef = displayCameraSlot.AttachComponent<DesktopUVRayExit>();
        displayRayExitRef.Size = new float2(w, h);

        frontPlaneRef = AddCurvedTexturePlane(displaySlot, "FrontCurvedPlane", w, h, 1f, procTex, 0f, flipY: true, offsetUnits: 100f);
        ApplyPanelCurvature(currentPanelCurvature);

        uint processId = 0;
        if (!DesktopBuddyPlatform.IsLinux)
            WindowEnumerator.GetWindowThreadProcessId(hwnd, out processId);
        Msg($"[StartStreaming] Process ID: {processId}");

        var seenRelatedHwnds = new HashSet<IntPtr>();

        session = new DesktopSession
        {
            Streamer = streamer,
            LinuxInputSessionId = streamer.LinuxInputSessionId,
            LinuxCaptureSessionId = streamer.LinuxCaptureSessionId,
            LinuxPositionX = streamer.LinuxPositionX,
            LinuxPositionY = streamer.LinuxPositionY,
            LinuxWorkspaceWidth = streamer.LinuxWorkspaceWidth,
            LinuxWorkspaceHeight = streamer.LinuxWorkspaceHeight,
            Texture = procTex,
            Canvas = ui.Canvas,
            Root = root,
            Hwnd = hwnd,
            ProcessId = processId,
            Collider = collider,
            UseTextureBridge = useTextureBridge,
            BridgeRegistrationPending = useTextureBridge,
            SharedTextureSlot = sharedTextureSlot,
            PendingBridgeDisplayIndex = pendingBridgeDisplayIndex,
            BridgeDisplayIndexApplied = pendingBridgeDisplayIndex < 0,
            LastKnownW = w,
            LastKnownH = h,
            PanelMesh = frontPlaneRef,
            PanelCanvasScale = canvasScale,
            OwnerUserId = root.World.LocalUser?.UserID,
            SeenRelatedHwnds = seenRelatedHwnds,
        };
        ActiveSessions.Add(session);
        LinuxCursorEffectSuspender.Sync(ActiveSessions.Count);
        LinuxSessionLifetime.Sync(ActiveSessions.Count);
        root.Destroyed += _ => CleanupAndRemoveSession(session, "root destroyed");
        if (Config?.GetValue(DynamicLightsEnabled) ?? false)
            CreateAdaptiveScreenLight(root, session, hwnd, streamer.MonitorHandle);
        DesktopCanvasIds.Add(ui.Canvas.ReferenceID);
        Msg($"[StartStreaming] Registered canvas {ui.Canvas.ReferenceID} for locomotion suppression");

        DesktopInputWiring.Wire(root, hwnd, streamer, session, frontPlaneRef, () => grabbable);

        float barH = 60f;
        float barMarginBottom = 12f * canvasScale;
        float barPad = 7f;
        float barGap = 8f;
        float avatarW = 46f;
        int initialBarRenderW = Math.Max(1, w);
        const float deviceIndicatorTopOffset = 0.02f;
        float DeviceIndicatorY() => worldHalfH + deviceIndicatorTopOffset;
        float DeviceIndicatorZ() => 0.001f + GetCurvedPanelDepth(frontPlaneRef, canvasScale);
        void UpdateDeviceIndicators()
        {
            if (deviceIndicatorsSlot != null && !deviceIndicatorsSlot.IsDestroyed)
                deviceIndicatorsSlot.LocalPosition = new float3(0f, DeviceIndicatorY(), DeviceIndicatorZ());
        }

        int barRenderHostId = Interlocked.Increment(ref _nextTopBarRenderHostId);
        var barRenderHost = root.AddSlot($"DesktopBuddyTopBarRenderHost {barRenderHostId}", false);
        session.TopBarRenderHost = barRenderHost;
        barRenderHost.PersistentSelf = false;
        barRenderHost.AttachComponent<HiddenLayer>();
        Msg($"[TopBar] Render host created under buddy root id={barRenderHostId}");
        root.Destroyed += _ =>
        {
            if (barRenderHost != null && !barRenderHost.IsDestroyed)
                barRenderHost.Destroy();
        };
        var barRenderRoot = barRenderHost.AddSlot("TopBarRender");
        barRenderRoot.AttachComponent<HiddenLayer>();
        var barBackRenderRoot = barRenderHost.AddSlot("TopBarBackRender");
        barBackRenderRoot.AttachComponent<HiddenLayer>();

        var barCameraSlot = barRenderHost.AddSlot("TopBarCamera");
        barCameraSlot.LocalPosition = new float3(0f, 0f, -1f);
        var barRenderTex = barCameraSlot.AttachComponent<RenderTextureProvider>();
        barRenderTex.Size.Value = new int2(initialBarRenderW, (int)barH);
        barRenderTex.WrapModeU.Value = TextureWrapMode.Clamp;
        barRenderTex.WrapModeV.Value = TextureWrapMode.Clamp;

        var barCamera = barCameraSlot.AttachComponent<Camera>();
        barCamera.Projection.Value = CameraProjection.Orthographic;
        barCamera.OrthographicSize.Value = barH * 0.5f;
        barCamera.UseTransformScale.Value = true;
        barCamera.Clear.Value = CameraClearMode.Color;
        barCamera.ClearColor.Value = colorX.Clear;
        barCamera.NearClipping.Value = 0.01f;
        barCamera.FarClipping.Value = 4f;
        barCamera.Postprocessing.Value = false;
        barCamera.RenderShadows.Value = false;
        barCamera.ForwardOnly.Value = true;
        barCamera.RenderTexture.Target = barRenderTex;
        barCamera.SelectiveRender.Add(barRenderRoot);

        var barBackCameraSlot = barRenderHost.AddSlot("TopBarBackCamera");
        barBackCameraSlot.LocalPosition = new float3(0f, 0f, -1f);
        var barBackRenderTex = barBackCameraSlot.AttachComponent<RenderTextureProvider>();
        barBackRenderTex.Size.Value = new int2(initialBarRenderW, (int)barH);
        barBackRenderTex.WrapModeU.Value = TextureWrapMode.Clamp;
        barBackRenderTex.WrapModeV.Value = TextureWrapMode.Clamp;

        var barBackCamera = barBackCameraSlot.AttachComponent<Camera>();
        barBackCamera.Projection.Value = CameraProjection.Orthographic;
        barBackCamera.OrthographicSize.Value = barH * 0.5f;
        barBackCamera.UseTransformScale.Value = true;
        barBackCamera.Clear.Value = CameraClearMode.Color;
        barBackCamera.ClearColor.Value = colorX.Clear;
        barBackCamera.NearClipping.Value = 0.01f;
        barBackCamera.FarClipping.Value = 4f;
        barBackCamera.Postprocessing.Value = false;
        barBackCamera.RenderShadows.Value = false;
        barBackCamera.ForwardOnly.Value = true;
        barBackCamera.RenderTexture.Target = barBackRenderTex;
        barBackCamera.SelectiveRender.Add(barBackRenderRoot);

        var barSlot = barRenderRoot.AddSlot("TopBar");
        barSlot.LocalScale = float3.One;

        var barCanvas = barSlot.AttachComponent<Canvas>();
        barCanvas.Size.Value = new float2(initialBarRenderW, barH);
        barCanvas.Collider.Target.SetTrigger();

        const float topBarBackgroundOffset = 500f;
        const float topBarForegroundOffset = -500f;
        const float topBarFillOffset = -1000f;
        const float topBarTopOffset = -1500f;
        const float topBarTextOffset = -2000f;

        var barMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barMat.BlendMode.Value = BlendMode.Alpha;
        barMat.ZWrite.Value = ZWrite.Off;
        barMat.OffsetUnits.Value = topBarBackgroundOffset;

        var barElementMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barElementMat.BlendMode.Value = BlendMode.Alpha;
        barElementMat.Sidedness.Value = Sidedness.Front;
        barElementMat.ZWrite.Value = ZWrite.Off;
        barElementMat.OffsetFactor.Value = -1f;
        barElementMat.OffsetUnits.Value = topBarForegroundOffset;

        var barFillMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barFillMat.BlendMode.Value = BlendMode.Alpha;
        barFillMat.Sidedness.Value = Sidedness.Front;
        barFillMat.ZWrite.Value = ZWrite.Off;
        barFillMat.OffsetFactor.Value = -1f;
        barFillMat.OffsetUnits.Value = topBarFillOffset;

        var barTopMat = barSlot.AttachComponent<UI_UnlitMaterial>();
        barTopMat.BlendMode.Value = BlendMode.Alpha;
        barTopMat.Sidedness.Value = Sidedness.Front;
        barTopMat.ZWrite.Value = ZWrite.Off;
        barTopMat.OffsetFactor.Value = -1f;
        barTopMat.OffsetUnits.Value = topBarTopOffset;

        var barTextMat = barSlot.AttachComponent<UI_TextUnlitMaterial>();
        barTextMat.BlendMode.Value = BlendMode.Alpha;
        barTextMat.Sidedness.Value = Sidedness.Front;
        barTextMat.ZWrite.Value = ZWrite.Off;
        barTextMat.OffsetFactor.Value = -1f;
        barTextMat.OffsetUnits.Value = topBarTextOffset;

        var barBackSlot = barBackRenderRoot.AddSlot("TopBarBackPanel");
        barBackSlot.LocalScale = float3.One;

        var barBackCanvas = barBackSlot.AttachComponent<Canvas>();
        barBackCanvas.Size.Value = new float2(initialBarRenderW, barH);
        barBackCanvas.Collider.RawTarget.Enabled = false;

        var barBackMat = barBackSlot.AttachComponent<UI_UnlitMaterial>();
        barBackMat.BlendMode.Value = BlendMode.Alpha;
        barBackMat.Sidedness.Value = Sidedness.Double;
        barBackMat.ZWrite.Value = ZWrite.Off;
        barBackMat.OffsetUnits.Value = topBarBackgroundOffset;

        var barBackTextMat = barBackSlot.AttachComponent<UI_TextUnlitMaterial>();
        barBackTextMat.BlendMode.Value = BlendMode.Alpha;
        barBackTextMat.Sidedness.Value = Sidedness.Double;
        barBackTextMat.ZWrite.Value = ZWrite.Off;
        barBackTextMat.OffsetFactor.Value = -1f;
        barBackTextMat.OffsetUnits.Value = topBarTextOffset;

        var barUi = new UIBuilder(barCanvas);
        var barBgColor = new colorX(0.055f, 0.06f, 0.08f, 0.8f);
        var barBg = barUi.Image(barBgColor);
        barBg.Material.Target = barMat;
        var roundedSprite = TextureProviderSettings.ClampWrap(barSlot.AttachComponent<SpriteProvider>());
        roundedSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        roundedSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        roundedSprite.FixedSize.Value = barH * 0.5f;
        barBg.Sprite.Target = roundedSprite;
        barBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBg.Tint.Value = barBgColor;

        var barBackUi = new UIBuilder(barBackCanvas);
        var barBackBg = barBackUi.Image(new colorX(0.08f, 0.08f, 0.1f, 1f));
        barBackBg.Material.Target = barBackMat;
        barBackBg.Sprite.Target = roundedSprite;
        barBackBg.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        barBackBg.Tint.Value = new colorX(0.055f, 0.06f, 0.08f, 0.8f);

        var barMask = barBg.Slot.AttachComponent<Mask>();
        barMask.ShowMaskGraphic.Value = true;
        barUi.NestInto(barBg.RectTransform);
        var barLayout = barUi.HorizontalLayout(8f, padding: 8f, childAlignment: Alignment.MiddleLeft);
        barLayout.ForceExpandWidth.Value = false;

        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = 1f;

        var localUser = root.World.LocalUser;

        barUi.Style.MinWidth = 48f;
        barUi.Style.PreferredWidth = 48f;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var imageSpaceSlot = barUi.Empty("Image Space");
        var avatarMask = imageSpaceSlot.AttachComponent<Mask>();
        avatarMask.ShowMaskGraphic.Value = false;
        var imgMaskImage = imageSpaceSlot.GetComponent<Image>();
        var avatarMaskSprite = TextureProviderSettings.ClampWrap(imageSpaceSlot.AttachComponent<SpriteProvider>());
        avatarMaskSprite.Texture.Target = UIBuilder.GetCircleTexture(root.World);
        avatarMaskSprite.Borders.Value = new float4(0.49f, 0.49f, 0.49f, 0.49f);
        avatarMaskSprite.FixedSize.Value = avatarW * 0.5f;
        imgMaskImage.Sprite.Target = avatarMaskSprite;
        imgMaskImage.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        imgMaskImage.Material.Target = barElementMat;

        barUi.NestInto(imageSpaceSlot);
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var cloudUserInfo = barSlot.AttachComponent<CloudUserInfo>();
        var defaultImg = new Uri("resdb:///bb7d7f1414e0c0a44b4684ecd2a5dc2086c18b3f70c9ed53d467fe96af94e9a9.png");
        var avatarTex = TextureProviderSettings.ClampWrap(barSlot.AttachComponent<StaticTexture2D>());
        cloudUserInfo.UserId.ForceSet(localUser.UserID);
        avatarTex.URL.Value = defaultImg;
        int avatarRefreshAttempts = 0;
        void RefreshAvatarIcon()
        {
            if (root == null || root.IsDestroyed ||
                cloudUserInfo == null || cloudUserInfo.IsDestroyed ||
                avatarTex == null || avatarTex.IsDestroyed)
                return;

            Uri iconUri = cloudUserInfo.IconURL.Value;
            if (iconUri != null)
            {
                avatarTex.URL.Value = iconUri;
                return;
            }

            if (++avatarRefreshAttempts < 120)
                root.World.RunInUpdates(10, RefreshAvatarIcon);
        }
        root.World.RunInUpdates(1, RefreshAvatarIcon);

        var avatarImage = barUi.Image(avatarTex);
        avatarImage.Material.Target = barTopMat;
        avatarImage.InteractionTarget.Value = false;
        barUi.NestOut();

        const float expandGap = 6f;
        const float expandPadding = 6f;
        const float expandSeparatorW = 1f;
        const float expandButtonW = 36f;
        const float curveLabelW = 38f;
        const float curveSliderW = 80f;
        const float volumeIconW = 24f;
        const float volumeSliderW = 100f;
        const float expandContentMaxW =
            expandPadding * 2f +
            expandGap * 12f +
            expandSeparatorW * 3f +
            expandButtonW * 6f +
            curveLabelW +
            curveSliderW +
            volumeIconW +
            volumeSliderW;

        float barCollapsedW = barPad * 2f + avatarW;
        float expandContentW = expandContentMaxW;
        float barExpandedW = barCollapsedW + barGap + expandContentW;
        int barRenderW = Math.Max(1, w);

        void StyleButton(Button btn) => TopBarControlStyles.StyleButton(btn, barTextMat, roundedSprite, barElementMat);

        barUi.Style.MinWidth = 0f;
        barUi.Style.PreferredWidth = 0f;
        barUi.Style.MinHeight = 0f;
        barUi.Style.PreferredHeight = 0f;
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = -1f;

        var toggleBtn = barUi.Button("≡");
        barUi.Style.FlexibleWidth = -1f;
        barUi.Style.FlexibleHeight = 1f;
        toggleBtn.Slot.ActiveSelf = false;
        barUi.Style.MinWidth = expandContentW;
        barUi.Style.PreferredWidth = expandContentW;
        barUi.Style.MinHeight = 48f;
        barUi.Style.PreferredHeight = 48f;
        var expandPanel = barUi.Empty("ExpandPanel");
        expandPanel.ActiveSelf = false;
        var ep = new UIBuilder(expandPanel);
        var epLayout = ep.HorizontalLayout(expandGap, padding: expandPadding, childAlignment: Alignment.MiddleLeft);
        epLayout.ForceExpandWidth.Value = false;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = 1f;

        ep.Style.MinWidth = expandButtonW;
        ep.Style.PreferredWidth = expandButtonW;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var settingsBtn = ep.Button("\u2699");
        StyleButton(settingsBtn);
        settingsBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            int panelW = session.LastKnownW > 0 ? session.LastKnownW : w;
            int panelH = session.LastKnownH > 0 ? session.LastKnownH : h;
            ToggleSettingsPanel(root, session, panelW, panelH, canvasScale, currentPanelCurvature);
        };

        ep.Style.MinWidth = expandButtonW;
        ep.Style.PreferredWidth = expandButtonW;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var previewBtn = ep.Button("\U0001F441"); StyleButton(previewBtn);

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var separatorA = ep.Image(new colorX(0.42f, 0.44f, 0.48f, 0.48f));
        separatorA.Material.Target = barElementMat;

        ep.Style.MinWidth = expandButtonW;
        ep.Style.PreferredWidth = expandButtonW;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;

        var kbBtn = ep.Button("⌨️"); StyleButton(kbBtn);
        var anchorBtn = ep.Button("⚓");   StyleButton(anchorBtn);
        var lockBtn = ep.Button("📌"); StyleButton(lockBtn);
        var privateBtn = ep.Button("🔒"); StyleButton(privateBtn);

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        var separatorB = ep.Image(new colorX(0.42f, 0.44f, 0.48f, 0.48f));
        separatorB.Material.Target = barElementMat;

        ep.Style.MinWidth = expandButtonW;
        ep.Style.PreferredWidth = expandButtonW;
        ep.Style.MinHeight = 40f;
        ep.Style.PreferredHeight = 40f;
        ep.Style.FlexibleWidth = -1f;
        ep.Style.FlexibleHeight = -1f;
        var resyncBtn = ep.Button("🔄");  StyleButton(resyncBtn);

        ep.Style.MinWidth = 1f;
        ep.Style.PreferredWidth = 1f;
        ep.Style.MinHeight = 32f;
        ep.Style.PreferredHeight = 32f;
        var separatorC = ep.Image(new colorX(0.42f, 0.44f, 0.48f, 0.48f));
        separatorC.Material.Target = barElementMat;

        ep.Style.MinWidth = curveLabelW;
        ep.Style.PreferredWidth = curveLabelW;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;
        ep.Style.FlexibleWidth = -1f;
        var curveText = ep.Text("Curve", bestFit: true, alignment: Alignment.MiddleCenter);
        curveText.Size.Value = 14f;
        curveText.Color.Value = new colorX(0.6f, 0.6f, 0.6f, 1f);
        curveText.Material.Target = barTextMat;

        ep.Style.FlexibleWidth = -1f;
        ep.Style.MinWidth = curveSliderW;
        ep.Style.PreferredWidth = curveSliderW;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;

        var curveRow = ep.Empty("Curve");
        var curveUi = new UIBuilder(curveRow);
        curveUi.Style.FlexibleWidth = 1f;
        curveUi.Style.FlexibleHeight = 1f;
        var curveSlider = curveUi.Slider<float>(20f, currentPanelCurvature, 0f, 1f, false,
            out var curveLine, out var curveFillLine, out var curveHandle);
        curveLine.Tint.Value = SettingsPanelSoft;
        curveFillLine.Tint.Value = SettingsAccent;
        curveHandle.Tint.Value = SettingsText;
        curveLine.Material.Target = barElementMat;
        curveFillLine.Material.Target = barFillMat;
        curveHandle.Material.Target = barTopMat;
        ApplyPurpleBlueGradient(curveFillLine, 10f, 0.98f, interactionTarget: false);
        var curveHandleGradient = ApplyPurpleBlueGradient(curveHandle, 18f, 0.98f, interactionTarget: false);
        if (curveHandleGradient != null && curveSlider.ColorDrivers.Count > 0)
            curveSlider.ColorDrivers[0].ColorDrive.Target = curveHandleGradient.TintBottomRight;
        curveRow.GetComponentInChildren<Image>(image => image.Slot.Name == "Background").Material.Target = barElementMat;
        curveRow.ForeachComponentInChildren<FrooxEngine.UIX.Text>(text => text.Material.Target = barTextMat);
        float pendingPanelCurvature = currentPanelCurvature;
        curveSlider.Value.OnValueChange += (SyncField<float> field) =>
        {
            pendingPanelCurvature = MathX.Clamp(field.Value, 0f, 1f);
            SetPanelCurvePreference(panelCurvePreferenceKey, pendingPanelCurvature);
        };
        curveSlider.IsPressed.OnValueChange += (SyncField<bool> field) =>
        {
            if (!field.Value)
            {
                ApplyPanelCurvature(pendingPanelCurvature);
                UpdateDeviceIndicators();
            }
        };

        ep.Style.MinWidth = volumeIconW;
        ep.Style.PreferredWidth = volumeIconW;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;
        ep.Style.FlexibleWidth = -1f;
        var volIcon = ep.Text("🔊", bestFit: false, alignment: Alignment.MiddleCenter);
        volIcon.Size.Value = 16f;
        volIcon.Color.Value = new colorX(0.6f, 0.6f, 0.6f, 1f);
        volIcon.Material.Target = barTextMat;

        ep.Style.FlexibleWidth = -1f;
        ep.Style.MinWidth = 80f;
        ep.Style.PreferredWidth = volumeSliderW;
        ep.Style.MinHeight = 48f;
        ep.Style.PreferredHeight = 48f;

        var streamVolRow = ep.Empty("StreamVol");
        var streamVolUi = new UIBuilder(streamVolRow);
        streamVolUi.Style.FlexibleWidth = 1f;
        streamVolUi.Style.FlexibleHeight = 1f;
        float streamOutputVolume = NormalizeStreamAudioOutputVolume(Config?.GetValue(StreamAudioOutputVolume) ?? 1f);
        var volSlider = streamVolUi.Slider<float>(20f, streamOutputVolume, 0f, 1f, false,
            out var volLine, out var volFillLine, out var volHandle);
        session.StreamVolumeSlider = volSlider;
        var volSliderOverride = streamVolRow.AttachComponent<ValueUserOverride<float>>();
        volSliderOverride.Target.Target = volSlider.Value;
        volSliderOverride.Default.Value = streamOutputVolume;
        volSliderOverride.CreateOverrideOnWrite.Value = true;
        volSliderOverride.PersistentOverrides.Value = false;
        volSliderOverride.ClearOnUserLeave.Value = true;
        volLine.Tint.Value = SettingsPanelSoft;
        volFillLine.Tint.Value = SettingsAccent;
        volHandle.Tint.Value = SettingsText;
        volLine.Material.Target = barElementMat;
        volFillLine.Material.Target = barFillMat;
        volHandle.Material.Target = barTopMat;
        ApplyPurpleBlueGradient(volFillLine, 10f, 0.98f, interactionTarget: false);
        var volHandleGradient = ApplyPurpleBlueGradient(volHandle, 18f, 0.98f, interactionTarget: false);
        if (volHandleGradient != null && volSlider.ColorDrivers.Count > 0)
            volSlider.ColorDrivers[0].ColorDrive.Target = volHandleGradient.TintBottomRight;
        streamVolRow.GetComponentInChildren<Image>(image => image.Slot.Name == "Background").Material.Target = barElementMat;
        streamVolRow.ForeachComponentInChildren<FrooxEngine.UIX.Text>(text => text.Material.Target = barTextMat);

        var widthField = barSlot.AttachComponent<ValueField<float>>();
        widthField.Value.Value = barCollapsedW;
        var widthSmooth = barSlot.AttachComponent<SmoothValue<float>>();
        widthSmooth.Speed.Value = 10f;
        widthSmooth.TargetValue.Value = barCollapsedW;
        widthSmooth.Value.Target = widthField.Value;
        widthSmooth.WriteBack.Value = false;

        BlurMaterial topBarBlur = null;
        StaticTexture2D topBarBlurMask = null;
        int topBarBlurMaskCanvasWidth = 0;
        int topBarBlurMaskCanvasHeight = 0;
        int topBarBlurMaskPillWidth = 0;
        int topBarBlurMaskPillHeight = 0;

        float barYPos = -worldHalfH - barH / 2f * canvasScale - barMarginBottom;
        widthField.Value.Value = barCollapsedW;
        widthSmooth.TargetValue.Value = barCollapsedW;
        float currentBarWidth = barCollapsedW;
        bool barExpanded = false;

        void UpdateTopBarBlurMask(int canvasWidthPx, int canvasHeightPx, int pillWidthPx, int pillHeightPx)
        {
            if (topBarBlur == null || topBarBlur.IsDestroyed ||
                topBarBlurMask == null || topBarBlurMask.IsDestroyed ||
                root == null || root.IsDestroyed)
                return;

            canvasWidthPx = Math.Max(1, canvasWidthPx);
            canvasHeightPx = Math.Max(1, canvasHeightPx);
            pillWidthPx = Math.Max(1, Math.Min(canvasWidthPx, pillWidthPx));
            pillHeightPx = Math.Max(1, Math.Min(canvasHeightPx, pillHeightPx));

            if (topBarBlurMaskCanvasWidth == canvasWidthPx &&
                topBarBlurMaskCanvasHeight == canvasHeightPx &&
                topBarBlurMaskPillWidth == pillWidthPx &&
                topBarBlurMaskPillHeight == pillHeightPx)
                return;

            topBarBlurMaskCanvasWidth = canvasWidthPx;
            topBarBlurMaskCanvasHeight = canvasHeightPx;
            topBarBlurMaskPillWidth = pillWidthPx;
            topBarBlurMaskPillHeight = pillHeightPx;

            var tex = topBarBlurMask;
            var blur = topBarBlur;
            var engine = root.Engine;
            if (tex == null || blur == null || engine?.LocalDB == null)
                return;

            byte[] data = CreateCenteredRoundedMaskPixels(
                canvasWidthPx,
                canvasHeightPx,
                pillWidthPx,
                pillHeightPx,
                barH * 0.5f,
                out int texW,
                out int texH);

            Task.Run(async () =>
            {
                try
                {
                    var bitmap = new Bitmap2D(data, texW, texH, Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.Linear, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri == null)
                        return;

                    var texWorld = tex.World;
                    if (texWorld == null || tex.IsDestroyed || blur.IsDestroyed)
                        return;

                    texWorld.RunInUpdates(0, () =>
                    {
                        if (tex.IsDestroyed || blur.IsDestroyed)
                            return;

                        tex.URL.Value = uri;
                        blur.SpreadMagnitudeTexture.Target = tex;
                        blur.SpreadTextureScale.Value = float2.One;
                        blur.SpreadTextureOffset.Value = float2.Zero;
                    });
                }
                catch (Exception ex)
                {
                    Msg($"[TopBar] Blur mask generation failed: {ex.Message}");
                }
            });
        }

        void ApplyBarLayout(float width)
        {
            if (barCanvas != null && !barCanvas.IsDestroyed)
                barCanvas.Size.Value = new float2(barRenderW, barH);

            if (barRenderTex != null && !barRenderTex.IsDestroyed)
                barRenderTex.Size.Value = new int2(barRenderW, (int)barH);

            if (barBackRenderTex != null && !barBackRenderTex.IsDestroyed)
                barBackRenderTex.Size.Value = new int2(barRenderW, (int)barH);

            if (barBg != null && !barBg.IsDestroyed)
                barBg.RectTransform.SetFixedRect(new Rect(width * -0.5f, -barH * 0.5f, width, barH), new float2(0.5f, 0.5f));

            if (barSlot != null && !barSlot.IsDestroyed)
                barSlot.LocalPosition = float3.Zero;

            if (barBackCanvas != null && !barBackCanvas.IsDestroyed)
                barBackCanvas.Size.Value = new float2(barRenderW, barH);

            if (barBackBg != null && !barBackBg.IsDestroyed)
                barBackBg.RectTransform.SetFixedRect(new Rect(width * -0.5f, -barH * 0.5f, width, barH), new float2(0.5f, 0.5f));

            if (topBarStripRef != null && !topBarStripRef.IsDestroyed)
            {
                topBarStripRef.Size.Value = new float2(barRenderW, barH);
                topBarStripRef.Slot.LocalPosition = new float3(0f, barYPos, TopBarSurfaceZOffset);
            }

            if (topBarBackStripRef != null && !topBarBackStripRef.IsDestroyed)
            {
                topBarBackStripRef.Size.Value = new float2(barRenderW, barH);
                topBarBackStripRef.Slot.LocalPosition = new float3(0f, barYPos, TopBarBackZOffset);
            }

            UpdateTopBarBlurMask(
                Math.Max(1, barRenderW),
                Math.Max(1, (int)MathF.Ceiling(barH)),
                Math.Max(1, (int)MathF.Ceiling(width)),
                Math.Max(1, (int)MathF.Ceiling(barH)));
        }

        void BarUpdateLoop()
        {
            if (root == null || root.IsDestroyed ||
                barSlot == null || barSlot.IsDestroyed ||
                barCanvas == null || barCanvas.IsDestroyed ||
                widthField == null || widthField.IsDestroyed ||
                widthSmooth == null || widthSmooth.IsDestroyed)
                return;

            float width = widthField.Value.Value;
            if (width != currentBarWidth)
            {
                currentBarWidth = width;
                ApplyBarLayout(width);
            }

            float target = widthSmooth.TargetValue.Value;
            if (Math.Abs(width - target) > 0.5f)
                root.World.RunInUpdates(1, BarUpdateLoop);
        }

        Button barHoverButton = barBg.Slot.AttachComponent<Button>();
        barHoverButton.RequireInitialPress.Value = false;
        if (barHoverButton.ColorDrivers.Count > 0)
        {
            var cd = barHoverButton.ColorDrivers[0];
            cd.NormalColor.Value = barBgColor;
            cd.HighlightColor.Value = barBgColor;
            cd.PressColor.Value = barBgColor;
            cd.DisabledColor.Value = barBgColor;
        }

        int hoverCollapseGeneration = 0;
        bool hoverCollapseScheduled = false;

        void SetBarExpanded(bool expanded)
        {
            if (root == null || root.IsDestroyed || widthSmooth == null || widthSmooth.IsDestroyed)
                return;

            if (barExpanded == expanded)
                return;

            barExpanded = expanded;
            if (expandPanel != null && !expandPanel.IsDestroyed)
                expandPanel.ActiveSelf = barExpanded;
            widthSmooth.TargetValue.Value = barExpanded ? barExpandedW : barCollapsedW;
            root.World.RunInUpdates(1, BarUpdateLoop);
        }

        bool AnyBarControlHovered()
        {
            return (barHoverButton != null && !barHoverButton.IsDestroyed && barHoverButton.IsHovering.Value) ||
                   (settingsBtn != null && !settingsBtn.IsDestroyed && settingsBtn.IsHovering.Value) ||
                   (previewBtn != null && !previewBtn.IsDestroyed && previewBtn.IsHovering.Value) ||
                   (kbBtn != null && !kbBtn.IsDestroyed && kbBtn.IsHovering.Value) ||
                   (anchorBtn != null && !anchorBtn.IsDestroyed && anchorBtn.IsHovering.Value) ||
                   (lockBtn != null && !lockBtn.IsDestroyed && lockBtn.IsHovering.Value) ||
                   (privateBtn != null && !privateBtn.IsDestroyed && privateBtn.IsHovering.Value) ||
                   (resyncBtn != null && !resyncBtn.IsDestroyed && resyncBtn.IsHovering.Value) ||
                   (curveSlider != null && !curveSlider.IsDestroyed && curveSlider.IsHovering.Value) ||
                   (volSlider != null && !volSlider.IsDestroyed && volSlider.IsHovering.Value);
        }

        void ScheduleCollapseWhenHoverLeaves()
        {
            if (root == null || root.IsDestroyed) return;
            if (hoverCollapseScheduled)
                return;

            hoverCollapseScheduled = true;
            int generation = ++hoverCollapseGeneration;
            root.World.RunInSeconds(TopBarHoverCollapseDelaySeconds, () =>
            {
                if (root == null || root.IsDestroyed)
                    return;

                if (generation != hoverCollapseGeneration)
                    return;

                if (AnyBarControlHovered())
                {
                    hoverCollapseScheduled = false;
                    return;
                }

                hoverCollapseScheduled = false;
                SetBarExpanded(false);
            });
        }

        void TrackHover(Button button)
        {
            if (button == null)
                return;
            button.LocalHoverEnter += (_, _) =>
            {
                hoverCollapseGeneration++;
                hoverCollapseScheduled = false;
                SetBarExpanded(true);
            };
            button.LocalHoverStay += (_, _) => SetBarExpanded(true);
            button.LocalHoverLeave += (_, _) => ScheduleCollapseWhenHoverLeaves();
        }

        void PollSharedBarHover()
        {
            if (root == null || root.IsDestroyed)
                return;

            if (AnyBarControlHovered())
            {
                if (hoverCollapseScheduled)
                {
                    hoverCollapseGeneration++;
                    hoverCollapseScheduled = false;
                }
                SetBarExpanded(true);
            }
            else if (barExpanded)
            {
                ScheduleCollapseWhenHoverLeaves();
            }

            root.World.RunInUpdates(4, PollSharedBarHover);
        }

        TrackHover(barHoverButton);
        TrackHover(settingsBtn);
        TrackHover(previewBtn);
        TrackHover(kbBtn);
        TrackHover(anchorBtn);
        TrackHover(lockBtn);
        TrackHover(privateBtn);
        TrackHover(resyncBtn);
        root.World.RunInUpdates(4, PollSharedBarHover);
        topBarStripRef = AddCurvedRenderPlane(
            root,
            "TopBarCurvedMesh",
            barRenderW,
            barH,
            canvasScale,
            barYPos,
            TopBarSurfaceZOffset,
            barRenderTex,
            barCamera,
            addCollider: true,
            sidedness: Sidedness.Front,
            zWrite: ZWrite.Off,
            offsetUnits: 120f,
            blendMode: BlendMode.Alpha,
            renderQueue: SettingsUiRenderQueue,
            alphaCutoff: 0.01f);
        RegisterTopBarRaycastPortal(topBarStripRef?.Slot, barRenderRoot);
        topBarBackStripRef = AddCurvedRenderPlane(
            root,
            "TopBarBackCurvedMesh",
            barRenderW,
            barH,
            canvasScale,
            barYPos,
            TopBarBackZOffset,
            barBackRenderTex,
            null,
            addCollider: false,
            sidedness: Sidedness.Back,
            zWrite: ZWrite.Off,
            offsetUnits: 121f,
            blendMode: BlendMode.Alpha,
            renderQueue: SettingsUiRenderQueue,
            alphaCutoff: 0.01f,
            textureScale: new float2(-1f, 1f),
            textureOffset: new float2(1f, 0f));
        ApplyPanelCurvature(currentPanelCurvature);
        ApplyBarLayout(barCollapsedW);
        SetBarExpanded(true);
        ScheduleCollapseWhenHoverLeaves();
        root.World.RunInUpdates(1, BarUpdateLoop);

        Msg("[TopBar] Created bottom hover menu");

        Slot keyboardSlot = null;
        DesktopKeyboardSource keyboardSource = null;
        kbBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Keyboard] Button pressed!");
            if (keyboardSlot == null || keyboardSlot.IsDestroyed ||
                keyboardSource == null || keyboardSource.IsDestroyed)
            {
                keyboardSlot = root.AddLocalSlot("Desktop Keyboard Focus", false);
                keyboardSource = keyboardSlot.AttachComponent<DesktopKeyboardSource>();
                session.KeyboardSource = keyboardSource;
            }

            keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);
            keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
            keyboardSlot.LocalScale = float3.One;

            Msg("[Keyboard] Opening userspace virtual keyboard for DesktopBuddy");
            keyboardSource.OpenKeyboard();
        };

        VideoTextureProvider videoTexRef = null;
        previewBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            if (displaySlot == null || displaySlot.IsDestroyed ||
                videoTexRef == null || videoTexRef.IsDestroyed)
            {
                Msg("[Preview] No remote stream visual available");
                return;
            }

            session.LocalPreviewingRemoteStream = !session.LocalPreviewingRemoteStream;
            if (session.PreviewStreamAllowed != null && !session.PreviewStreamAllowed.IsDestroyed)
                session.PreviewStreamAllowed.SetOverride(root.World.LocalUser, session.LocalPreviewingRemoteStream);
            if (session.StreamVisualAllowed != null && !session.StreamVisualAllowed.IsDestroyed)
                session.StreamVisualAllowed.SetOverride(root.World.LocalUser, session.LocalPreviewingRemoteStream);
            displaySlot.ActiveSelf = !session.LocalPreviewingRemoteStream;

            var img = previewBtn.Slot.GetComponent<Image>();
            if (img != null)
                img.Tint.Value = session.LocalPreviewingRemoteStream
                    ? new colorX(0.24f, 0.18f, 0.42f, 0.95f)
                    : new colorX(0.12f, 0.13f, 0.17f, 0.88f);

            Msg($"[Preview] Local stream preview={(session.LocalPreviewingRemoteStream ? "remote" : "direct")}");
        };

        resyncBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Resync] Button pressed");
            if (videoTexRef == null || videoTexRef.IsDestroyed)
            {
                Msg("[Resync] No stream available");
                return;
            }

            var savedUrl = session.StreamUrl ?? videoTexRef.URL.Value;
            if (savedUrl == null)
            {
                Msg("[Resync] No URL is currently bound");
                return;
            }

            Msg($"[Resync] Forcing full reload: {savedUrl}");
            ClearRemoteStreamUrl(session, "manual resync");
            root.World.RunInUpdates(10, () =>
            {
                if (videoTexRef != null && !videoTexRef.IsDestroyed)
                {
                    SetRemoteStreamUrl(session, savedUrl, "manual resync restore");
                    Msg($"[Resync] URL restored: {savedUrl}");
                }
            });
        };

        bool isAnchored = false;
        var anchorActiveColor = new colorX(0.2f, 0.45f, 0.25f, 1f);
        anchorBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Anchor] Button pressed");
            var localUser = root.World.LocalUser;
            if (localUser?.Root == null) return;
            if (!isAnchored)
            {
                root.SetParent(localUser.Root.Slot, keepGlobalTransform: true);
                Msg($"[Anchor] Anchored to user");
                isAnchored = true;
            }
            else
            {
                root.SetParent(root.World.RootSlot, keepGlobalTransform: true);
                Msg($"[Anchor] Unanchored to world");
                isAnchored = false;
            }
            var img = anchorBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isAnchored ? anchorActiveColor : colorX.Clear;
        };

        lockBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            if (session == null) return;
            session.PanelLocked = !session.PanelLocked;
            if (grabbable != null && !grabbable.IsDestroyed)
                grabbable.Enabled = !session.PanelLocked;
            session.LastGrabTick = 0;
            Msg($"[Lock] Panel {(session.PanelLocked ? "pinned (grab acts as right-click)" : "unpinned")}");
            var lockImg = lockBtn.Slot.GetComponent<Image>();
            if (lockImg != null) lockImg.Tint.Value = session.PanelLocked ? anchorActiveColor : colorX.Clear;
        };

        deviceIndicatorsSlot = CreateVirtualDeviceControls(
            root,
            session,
            DeviceIndicatorY(),
            DeviceIndicatorZ(),
            Config?.GetValue(SpatialAudioEnabled) ?? false);

        bool isPrivate = startPrivate;
        string savedStreamUrl = null;

        var rootVis = root.AttachComponent<ValueUserOverride<bool>>();
        rootVis.Target.Target = root.ActiveSelf_Field;
        rootVis.Default.Value = !isPrivate;
        rootVis.CreateOverrideOnWrite.Value = false;
        if (isPrivate)
            rootVis.SetOverride(root.World.LocalUser, true);

        void ApplyPrivateButtonTint()
        {
            var img = privateBtn.Slot.GetComponent<Image>();
            if (img != null) img.Tint.Value = isPrivate ? new colorX(0.5f, 0.2f, 0.2f, 1f) : colorX.Clear;
        }

        privateBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            isPrivate = !isPrivate;
            Msg($"[Private] Mode: {isPrivate}");

            rootVis.Default.Value = !isPrivate;
            rootVis.SetOverride(root.World.LocalUser, true);

            if (videoTexRef != null && !videoTexRef.IsDestroyed)
            {
                if (isPrivate)
                {
                    savedStreamUrl = (session.StreamUrl ?? videoTexRef.URL.Value)?.ToString();
                    ClearRemoteStreamUrl(session, "private mode");
                    Msg("[Private] Stream disconnected");
                }
                else
                {

                    Uri currentUrl = null;
                    if (savedStreamUrl != null)
                        currentUrl = new Uri(savedStreamUrl);
                    else if (session.StreamId > 0)
                        currentUrl = GetSharedStreamUrl(session.Hwnd, session.StreamId)
                                     ?? GetBuiltInStreamUrl(session.StreamId);

                    if (currentUrl != null)
                    {
                        SetRemoteStreamUrl(session, currentUrl, "private mode restore");
                        Msg($"[Private] Stream restored: {currentUrl}");
                    }
                    else
                    {
                        Msg($"[Private] Going public but no stream URL available yet (streamId={session.StreamId})");
                    }
                }
            }

            ApplyPrivateButtonTint();
        };
        ApplyPrivateButtonTint();
        if (isPrivate)
            Msg("[Private] Initial mode: true");

        bool isDesktopCapture = hwnd == IntPtr.Zero;
        uint capturedPid = processId;

        Canvas streamCanvasRef = null;

        {
            backPlaneRef = AddCurvedBackPlane(root, w, h, canvasScale);
            ApplyPanelCurvature(currentPanelCurvature);
            Msg("[BackPanel] Created curved backing");
        }

        if (!_updateShown && Config!.GetValue(CheckForUpdates))
        {
            _updateShown = true;
            var capturedRoot = root;
            var capturedWorld = root.World;
            var capturedSession = session;
            int capturedW = w;
            int capturedH = h;
            float capturedCanvasScale = canvasScale;
            float capturedCurvature = currentPanelCurvature;
            System.Threading.Tasks.Task.Run(() =>
            {
                CheckForUpdate();
                if (_latestVersion == null) return;
                capturedWorld.RunInUpdates(0, () =>
                {
                    if (capturedRoot.IsDestroyed || capturedSession == null) return;
                    OpenSettingsPanel(
                        capturedRoot,
                        capturedSession,
                        capturedSession.LastKnownW > 0 ? capturedSession.LastKnownW : capturedW,
                        capturedSession.LastKnownH > 0 ? capturedSession.LastKnownH : capturedH,
                        capturedCanvasScale,
                        capturedCurvature,
                        SettingsPanelTab.UpdateInfo);
                });
            });
        }

        bool useMediaMtx = IsMediaMtxEnabled;
        root.World.RunInUpdates(1, () =>
        {
            if (root == null || root.IsDestroyed || session == null || session.Cleaned)
                return;

            var remoteStream = BuildRemoteStream(new RemoteStreamBuildContext
            {
                Root = root,
                Hwnd = hwnd,
                Width = w,
                Height = h,
                CanvasScale = canvasScale,
                Session = session,
                UseMediaMtx = useMediaMtx,
                VolumeSlider = volSlider,
                StreamOutputVolume = streamOutputVolume,
                IsPrivate = () => isPrivate,
                CurrentPanelCurvature = () => currentPanelCurvature,
                LinuxPipeWireNodeId = streamer.LinuxPipeWireNodeId
            });
            if (remoteStream != null)
            {
                videoTexRef = remoteStream.VideoTexture;
                streamPlaneRef = remoteStream.StreamPlane;
                streamCanvasRef = remoteStream.StreamCanvas;
                ApplyPanelCurvature(currentPanelCurvature);
            }
        });

        grabbable = root.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
        Msg("[StartStreaming] Grabbable attached");

        StartThrowDismissalTracker(root, grabbable, localUser);

        void UpdateLayout(int newW, int newH)
        {
            w = newW;
            h = newH;
            worldHalfW = newW / 2f * canvasScale;
            worldHalfH = newH / 2f * canvasScale;
            barRenderW = Math.Max(1, newW);
            barYPos = -worldHalfH - barH / 2f * canvasScale - barMarginBottom;
            ApplyBarLayout(currentBarWidth);

            if (session.Collider != null && !session.Collider.IsDestroyed)
                session.Collider.Size.Value = new float3(newW * canvasScale, newH * canvasScale, 0.001f);

            if (ui.Canvas != null && !ui.Canvas.IsDestroyed)
                ui.Canvas.Size.Value = new float2(newW, newH);

            if (frontPlaneRef != null && !frontPlaneRef.IsDestroyed)
                frontPlaneRef.Size.Value = new float2(newW, newH);

            if (displayRayExitRef != null && !displayRayExitRef.IsDestroyed)
                displayRayExitRef.Size = new float2(newW, newH);

            if (backPlaneRef != null && !backPlaneRef.IsDestroyed)
                backPlaneRef.Size.Value = new float2(newW, newH);

            if (streamPlaneRef != null && !streamPlaneRef.IsDestroyed)
                streamPlaneRef.Size.Value = new float2(newW, newH);

            if (streamCanvasRef != null && !streamCanvasRef.IsDestroyed)
                streamCanvasRef.Size.Value = new float2(newW, newH);

            UpdateAdaptiveScreenLightPosition(session);
            ResizeSettingsPanel(session, newW, newH, canvasScale, currentPanelCurvature);

            if (keyboardSlot != null && keyboardSlot.ActiveSelf && !keyboardSlot.IsDestroyed)
                keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.15f, -0.08f);

            UpdateDeviceIndicators();

            Msg($"[Resize] UI updated to {newW}x{newH}");
        }
        session.OnResize = UpdateLayout;

        root.PersistentSelf = false;
        root.Name = $"Desktop: {title}";
        session.TitleText = titleTextRef;
        session.LastTitle = title;

        ScheduleUpdate(root.World);

        root.Tag = "Desktop Buddy";
        if (!DesktopBuddyPlatform.IsLinux)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                Msg($"[StartStreaming] Focus request START hwnd={hwnd} title={title}");
                bool focused = WindowInput.FocusWindow(hwnd);
                Msg(focused
                    ? $"[StartStreaming] Window focused, streaming started for: {title}"
                    : $"[StartStreaming] Streaming started, but Windows did not foreground the window yet: {title}");
            });
        }

        bool useSpatialAudio = Config?.GetValue(SpatialAudioEnabled) ?? false;
        if (!DesktopBuddyPlatform.IsLinux && useSpatialAudio && !isDesktopCapture && processId != 0)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Msg($"[AudioRouter] Background route START pid={processId}");
                    if (!VBCable.HasCableInputDevice()) return;
                    string cableId = VBCable.FindCableInputDeviceId();
                    if (cableId == null) return;
                    AudioRouter.SetProcessOutputDevice(processId, cableId);
                    session.OwnsAudioRedirect = true;
                    Msg($"[AudioRouter] Background route DONE pid={processId}");
                }
                catch (Exception ex)
                {
                    Msg($"[AudioRouter] Background route failed for PID {processId}: {ex.Message}");
                }
            });
        }
    }
}
