using System;
using System.Collections.Generic;
using System.Threading;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void UpdateLoop(World world)
    {
        _updateCount++;
        double dt = world.Time.Delta;
        bool hasSessionsInWorld = false;

        if (world.IsDestroyed)
        {
            Msg("[UpdateLoop] World destroyed, cleaning up sessions for this world");
            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];
                if (session.Root == null || session.Root.IsDestroyed || session.Root.World == world)
                {
                    Msg($"[UpdateLoop] Cleaning up session {i} (world destroyed)");
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                }
            }
            LinuxCursorEffectSuspender.Sync(ActiveSessions.Count);
        LinuxSessionLifetime.Sync(ActiveSessions.Count);
            _scheduledWorlds.Remove(world);
            return;
        }

        try
        {
            ProcessWindowEvents(world);
            int lastVCamIdx = -1;
            int lastVMicIdx = -1;

            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];

                if (session.Cleaned)
                {
                    ActiveSessions.RemoveAt(i);
                    LinuxCursorEffectSuspender.Sync(ActiveSessions.Count);
        LinuxSessionLifetime.Sync(ActiveSessions.Count);
                    continue;
                }

                if (session.Root == null || session.Root.IsDestroyed ||
                    session.Texture == null || session.Texture.IsDestroyed)
                {
                    Msg($"[UpdateLoop] Session {i} root/texture destroyed, cleaning up (root={session.Root != null} rootDestroyed={session.Root?.IsDestroyed} tex={session.Texture != null} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId})");
                    CleanupTrace($"UpdateLoop destroyed branch sessionIndex={i} root={(session.Root != null)} rootDestroyed={session.Root?.IsDestroyed} tex={(session.Texture != null)} texDestroyed={session.Texture?.IsDestroyed} hwnd={session.Hwnd} streamId={session.StreamId}");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        var stopTicks = TraceStart($"VideoTexture stop stream={session.StreamId}");
                        vtp.URL.Value = null;
                        vtp.Stop();
                        TraceDone($"VideoTexture stop stream={session.StreamId}", stopTicks);
                    }
                    session.VideoTexture = null;
                    var cleanupTicks = TraceStart($"CleanupSession call stream={session.StreamId}");
                    CleanupSession(session);
                    TraceDone($"CleanupSession call stream={session.StreamId}", cleanupTicks);
                    ActiveSessions.RemoveAt(i);
                    CleanupTrace($"ActiveSessions removed destroyed sessionIndex={i}");
                    continue;
                }

                if (session.Root.World != world) continue;
                hasSessionsInWorld = true;
                if (session.UpdateInProgress) continue;

                if (lastVCamIdx < 0 && session.VCamCamera != null && !session.VCamCamera.IsDestroyed)
                    lastVCamIdx = i;
                if (lastVMicIdx < 0 && session.VMicListener != null && !session.VMicListener.IsDestroyed)
                    lastVMicIdx = i;

                session.TimeSinceValidCheck += dt;
                if (session.TimeSinceValidCheck >= 0.5)
                {
                    session.TimeSinceValidCheck = 0;
                    bool streamerValid = session.Streamer == null || session.Streamer.IsValid;

                    bool sourceAlive = session.StreamSource == null || session.StreamSource.IsSourceAlive;
                    session.LastValidState = streamerValid && sourceAlive;
                }
                if (session.Streamer != null && !session.LastValidState)
                {
                    Msg($"[UpdateLoop] Window closed (IsValid=false), destroying viewer");
                    var vtp = session.VideoTexture;
                    if (vtp != null && !vtp.IsDestroyed)
                    {
                        Msg("[UpdateLoop] Disconnecting VideoTextureProvider before cleanup");
                        var stopTicks = TraceStart($"VideoTexture stop invalid hwnd={session.Hwnd} stream={session.StreamId}");
                        vtp.URL.Value = null;
                        vtp.Stop();
                        TraceDone($"VideoTexture stop invalid hwnd={session.Hwnd} stream={session.StreamId}", stopTicks);
                    }
                    var cleanupTicks = TraceStart($"CleanupSession invalid hwnd={session.Hwnd} stream={session.StreamId}");
                    CleanupSession(session);
                    TraceDone($"CleanupSession invalid hwnd={session.Hwnd} stream={session.StreamId}", cleanupTicks);
                    ActiveSessions.RemoveAt(i);
                    var rootToDestroy = session.Root;
                    world.RunInUpdates(10, () =>
                    {
                        Msg("[UpdateLoop] Deferred destroy executing");
                        if (rootToDestroy != null && !rootToDestroy.IsDestroyed)
                        {
                            rootToDestroy.DestroyChildren();
                            rootToDestroy.Destroy();
                        }
                        Msg("[UpdateLoop] Deferred destroy complete");
                    });
                    continue;
                }

                if (ProcessSessionResizeAndEncoding(world, session))
                    continue;

                if (session.PendingBridgeDisplayIndex >= 0 && !session.BridgeDisplayIndexApplied)
                {
                    if (TextureBridgeChannel != null &&
                        TextureBridgeChannel.IsTextureRunning(session.SharedTextureSlot))
                    {
                        session.Texture.DisplayIndex.Value = session.PendingBridgeDisplayIndex;
                        session.BridgeDisplayIndexApplied = true;
                        Msg($"[UpdateLoop] Shared texture bridge ready; applying DisplayIndex={session.PendingBridgeDisplayIndex} slot={session.SharedTextureSlot}");
                        RetriggerDesktopTexture(session.Texture);
                    }
                    else
                    {
                        if (_updateCount % 30 == 0)
                            Msg($"[UpdateLoop] Waiting for shared texture bind before applying DisplayIndex={session.PendingBridgeDisplayIndex} slot={session.SharedTextureSlot}");
                        continue;
                    }
                }

                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");
                    if (session.SharedTextureSlot >= 0 && session.BridgeDisplayIndexApplied && _updateCount % 5 == 0)
                    {
                        RetriggerDesktopTexture(session.Texture);
                    }
                    continue;
                }

                TickVirtualCamera(session, i, lastVCamIdx);
                TickVirtualMic(session, i, lastVMicIdx);

                Perf.IncrementFrames();
            }
        }
        catch (Exception ex)
        {
            Msg($"ERROR in UpdateLoop: {ex}");
            hasSessionsInWorld = HasSessionsInWorld(world);
        }

        if (hasSessionsInWorld)
        {
            world.RunInUpdates(1, () => UpdateLoop(world));
        }
        else
        {
            Msg("[UpdateLoop] No sessions left for this world, stopping loop");
            _scheduledWorlds.Remove(world);
        }
    }

    private static bool HasSessionsInWorld(World world)
    {
        for (int i = 0; i < ActiveSessions.Count; i++)
        {
            if (ActiveSessions[i].Root?.World == world)
                return true;
        }
        return false;
    }

}
