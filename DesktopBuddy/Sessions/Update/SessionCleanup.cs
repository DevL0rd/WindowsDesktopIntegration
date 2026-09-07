using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void CleanupSession(DesktopSession session)
    {
        if (session.Cleaned) { Msg($"[Cleanup] Already cleaned hwnd={session.Hwnd} streamId={session.StreamId}, skipping"); return; }
        session.Cleaned = true;
        Msg($"[Cleanup] === START === hwnd={session.Hwnd} streamId={session.StreamId}");
        CleanupTrace($"CleanupSession ENTER hwnd={session.Hwnd} streamId={session.StreamId} rootDestroyed={session.Root?.IsDestroyed} texDestroyed={session.Texture?.IsDestroyed}");

        session.ActiveTouchIds.Clear();
        CleanupTrace($"ActiveTouchIds cleared hwnd={session.Hwnd} streamId={session.StreamId}");

        if (session.LinuxInputSessionId != 0)
        {
            // The input session is shared by every panel, so it must NOT be stopped here.
            // Closing one panel would otherwise kill input for all the others. Releasing it
            // is LinuxSessionLifetime's job, once no panels remain.
            if (WindowInput.LinuxInputSession == session.LinuxInputSessionId)
                WindowInput.LinuxInputSession = 0;
            session.LinuxInputSessionId = 0;
        }

        if (session.LinuxCaptureSessionId != 0)
        {
            try
            {
                using var captureBridge = new LinuxNativeBridge();
                int status = captureBridge.ScreencastStop(session.LinuxCaptureSessionId);
                Msg(status == 0
                    ? $"[Cleanup] Stopped Linux capture session {session.LinuxCaptureSessionId}"
                    : $"[Cleanup] Linux capture session {session.LinuxCaptureSessionId} stop returned {status}: {captureBridge.GetInputLastError() ?? "(no error)"}");
            }
            catch (Exception ex) { Msg($"[Cleanup] Linux capture stop error: {ex.Message}"); }
            session.LinuxCaptureSessionId = 0;
        }

        if (VMic != null && session.VMicListener != null)
        {
            Msg("[Cleanup] Disposing VMic (listener destroyed)");
            var ticks = TraceStart("VMic.Dispose");
            VMic.Dispose();
            VMic = null;
            TraceDone("VMic.Dispose", ticks);
        }

        if (session.OwnsAudioRedirect && session.ProcessId != 0)
        {
            CleanupTrace($"Audio redirect reset check START pid={session.ProcessId}");
            bool otherSessionUsesSamePid = false;
            foreach (var s in ActiveSessions)
            {
                if (s != session && !s.Cleaned && s.ProcessId == session.ProcessId)
                {
                    otherSessionUsesSamePid = true;
                    break;
                }
            }
            if (!otherSessionUsesSamePid)
            {
                var ticks = TraceStart($"AudioRouter.ResetProcessToDefault pid={session.ProcessId}");
                AudioRouter.ResetProcessToDefault(session.ProcessId);
                TraceDone($"AudioRouter.ResetProcessToDefault pid={session.ProcessId}", ticks);
                Msg($"[Cleanup] Reset audio routing for PID {session.ProcessId}");
            }
            else
            {
                Msg($"[Cleanup] Keeping audio routing for PID {session.ProcessId} (other sessions still active)");
            }
        }

        session.SeenRelatedHwnds.Clear();
        session.AdaptiveLightSampler?.Dispose();
        session.AdaptiveLightSampler = null;

        if (session.TopBarRenderHost != null && !session.TopBarRenderHost.IsDestroyed)
        {
            Msg($"[Cleanup] Destroying top bar render host: {session.TopBarRenderHost.Name}");
            CleanupTrace($"TopBarRenderHost.Destroy START stream={session.StreamId}");
            try
            {
                session.TopBarRenderHost.Destroy();
            }
            catch (Exception ex)
            {
                Msg($"[Cleanup] Top bar render host destroy error: {ex.Message}");
                CleanupTrace($"TopBarRenderHost.Destroy ERROR stream={session.StreamId}: {ex}");
            }
            CleanupTrace($"TopBarRenderHost.Destroy DONE stream={session.StreamId}");
        }
        session.TopBarRenderHost = null;

        Msg($"[Cleanup] Removing canvas ID");
        CleanupTrace($"Removing canvas/provider ids hwnd={session.Hwnd} streamId={session.StreamId}");
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        if (session.SharedTextureSlot >= 0 && TextureBridgeChannel != null)
        {
            var ticks = TraceStart($"TextureBridge.StopTexture slot={session.SharedTextureSlot}");
            TextureBridgeChannel.StopTexture(session.SharedTextureSlot);
            TraceDone($"TextureBridge.StopTexture slot={session.SharedTextureSlot}", ticks);
            Msg($"[Cleanup] Stopped shared texture slot {session.SharedTextureSlot}");
        }

        if (session.Texture != null)
        {
            OurProviders.Remove(session.Texture);
            CleanupTrace("DesktopTextureProvider removed from provider set");
        }

        Msg($"[Cleanup] Disconnecting encoder");
        CleanupTrace($"Disconnecting encoder START hwnd={session.Hwnd} streamId={session.StreamId}");
        var streamer = session.Streamer;
        if (streamer != null)
        {
            Msg("[Cleanup] Detaching capture frame callback");
            CleanupTrace("Detach OnGpuFrame START");
            streamer.OnGpuFrame = null;
            CleanupTrace("Detach OnGpuFrame DONE");
        }
        CleanupTrace($"Shared stream driver-transfer START stream={session.StreamId}");
        TryTransferSharedStreamDriver(session, out DesktopSession replacementDriver, out FfmpegEncoder replacementEncoder);
        if (replacementDriver != null)
            CleanupTrace($"Replacement driver selected hwnd={replacementDriver.Hwnd} stream={session.StreamId}");
        CleanupTrace($"Shared stream driver-transfer DONE stream={session.StreamId}");
        if (replacementDriver != null && replacementEncoder != null)
        {
            var ticks = TraceStart($"ConnectEncoder replacement stream={session.StreamId}");
            ConnectEncoder(replacementDriver, replacementEncoder);
            TraceDone($"ConnectEncoder replacement stream={session.StreamId}", ticks);
            Msg($"[Cleanup] Transferred stream {session.StreamId} encoder driver to hwnd={replacementDriver.Hwnd}");
        }
        int streamId = session.StreamId;
        bool streamUsesMediaMtx = session.StreamUsesMediaMtx;
        IntPtr hwnd = session.Hwnd;
        session.Streamer = null;
        CleanupTrace($"Session streamer nulled hwnd={hwnd} stream={streamId}");

        Msg($"[Cleanup] Queuing background dispose for stream {streamId}");
        CleanupTrace($"QueueUserWorkItem cleanup START stream={streamId}");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Msg($"[Cleanup:BG] === START === stream {streamId}");
                CleanupTrace($"BG cleanup ENTER stream={streamId} hwnd={hwnd}");

                AudioCapture audioToDispose = null;
                FfmpegEncoder encoderToDispose = null;
                ILiveStreamSource sourceToDispose = null;
                bool shouldStopEncoder = false;

                if (streamer != null)
                {
                    Msg($"[Cleanup:BG] Stopping capture...");
                    var stopCaptureTicks = TraceStart($"DesktopStreamer.StopCapture stream={streamId}");
                    streamer.StopCapture();
                    TraceDone($"DesktopStreamer.StopCapture stream={streamId}", stopCaptureTicks);
                    Msg($"[Cleanup:BG] Capture stopped");

                    try
                    {
                        Msg($"[Cleanup:BG] Flushing D3D context...");
                        var flushTicks = TraceStart($"DesktopStreamer.FlushD3dContext stream={streamId}");
                        streamer.FlushD3dContext();
                        TraceDone($"DesktopStreamer.FlushD3dContext stream={streamId}", flushTicks);
                        Msg($"[Cleanup:BG] D3D context flushed");
                    }
                    catch (Exception ex)
                    {
                        Msg($"[Cleanup:BG] D3D flush error: {ex.Message}");
                    }
                }

                if (streamId > 0)
                {
                    CleanupTrace($"BG shared stream release START stream={streamId}");
                    shouldStopEncoder = ReleaseSharedStreamReference(hwnd, streamId, session, out audioToDispose, out encoderToDispose, out sourceToDispose);
                    CleanupTrace($"BG shared stream release DONE stream={streamId} shouldStopEncoder={shouldStopEncoder}");

                    if (shouldStopEncoder)
                    {
                        Msg($"[Cleanup:BG] Stopping encoder {streamId}...");
                        if (encoderToDispose != null)
                        {
                            var stopTicks = TraceStart($"FfmpegEncoder.Stop stream={streamId}");
                            encoderToDispose.Stop();
                            TraceDone($"FfmpegEncoder.Stop stream={streamId}", stopTicks);
                        }

                        if (streamUsesMediaMtx)
                        {
                            if (encoderToDispose != null)
                            {
                                var disposeTicks = TraceStart($"FfmpegEncoder.Dispose stream={streamId}");
                                encoderToDispose.Dispose();
                                TraceDone($"FfmpegEncoder.Dispose stream={streamId}", disposeTicks);
                            }
                            else if (sourceToDispose != null)
                            {
                                var disposeTicks = TraceStart($"StreamSource.Dispose stream={streamId}");
                                sourceToDispose.Dispose();
                                TraceDone($"StreamSource.Dispose stream={streamId}", disposeTicks);
                            }
                        }
                        else if (StreamServer != null)
                        {
                            var serverTicks = TraceStart($"BuiltInStreamServer.StopEncoder stream={streamId}");
                            StreamServer.StopEncoder(streamId);
                            TraceDone($"BuiltInStreamServer.StopEncoder stream={streamId}", serverTicks);
                        }
                        else if (encoderToDispose != null)
                        {
                            var disposeTicks = TraceStart($"FfmpegEncoder.Dispose stream={streamId}");
                            encoderToDispose.Dispose();
                            TraceDone($"FfmpegEncoder.Dispose stream={streamId}", disposeTicks);
                        }

                        Msg($"[Cleanup:BG] Encoder {streamId} stopped");
                    }

                }

                Msg($"[Cleanup:BG] Disposing streamer...");
                var streamerDisposeTicks = TraceStart($"DesktopStreamer.Dispose stream={streamId}");
                streamer?.Dispose();
                TraceDone($"DesktopStreamer.Dispose stream={streamId}", streamerDisposeTicks);
                Msg($"[Cleanup:BG] Streamer disposed");

                if (audioToDispose != null)
                {
                    Msg($"[Cleanup:BG] Disposing audio...");
                    var audioTicks = TraceStart($"AudioCapture.Dispose stream={streamId}");
                    audioToDispose.Dispose();
                    TraceDone($"AudioCapture.Dispose stream={streamId}", audioTicks);
                    Msg($"[Cleanup:BG] Audio disposed");
                }

                Msg($"[Cleanup:BG] === DONE === stream {streamId}");
                CleanupTrace($"BG cleanup DONE stream={streamId}");
            }
            catch (Exception ex)
            {
                Msg($"[Cleanup:BG] ERROR: {ex}");
                CleanupTrace($"BG cleanup ERROR stream={streamId}: {ex}");
            }
        });
        CleanupTrace($"QueueUserWorkItem cleanup DONE stream={streamId}");
        Msg($"[Cleanup] === END (bg queued) === stream {streamId}");
        CleanupTrace($"CleanupSession EXIT stream={streamId}");
    }

    private static void CleanupAndRemoveSession(DesktopSession session, string reason)
    {
        if (session == null)
            return;

        Msg($"[Cleanup] Session cleanup requested: {reason} hwnd={session.Hwnd} streamId={session.StreamId}");
        if (!session.Cleaned)
            CleanupSession(session);

        ActiveSessions.Remove(session);
        LinuxCursorEffectSuspender.Sync(ActiveSessions.Count);
        LinuxSessionLifetime.Sync(ActiveSessions.Count);
    }

}
