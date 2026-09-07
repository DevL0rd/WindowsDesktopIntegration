using System;
using System.Reflection;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

internal static class DesktopInputWiring
{
    internal static DesktopCurvedScreenInput Wire(
        Slot root,
        IntPtr hwnd,
        DesktopStreamer streamer,
        DesktopSession session,
        CurvedPlaneMesh screenMesh,
        Func<Grabbable> getGrabbable)
    {
        var handlerField = typeof(InteractionLaser)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);

        bool IsActiveSource(Component source)
        {
            if (session.LastActiveSource == null || session.LastActiveSource.IsDestroyed)
                return true;
            return source == session.LastActiveSource;
        }

        void ClaimSource(Component source)
        {
            if (source != session.LastActiveSource)
                session.LastActiveSource = source;
        }

        InteractionHandler FindHandler(Component source)
        {
            if (source == null) return null;
            var laser = source.Slot?.GetComponent<InteractionLaser>();
            if (laser != null && handlerField != null)
            {
                var handlerRef = handlerField.GetValue(laser) as SyncRef<InteractionHandler>;
                return handlerRef?.Target;
            }
            return source.Slot?.GetComponentInParents<InteractionHandler>();
        }

        uint GetTouchId(Component source)
        {
            var handler = FindHandler(source);
            if (handler != null && handler.Side.Value == Chirality.Right)
                return 1;
            return 0;
        }

        bool ShouldIgnoreDesktopInput()
        {
            var grabbable = getGrabbable?.Invoke();
            return (grabbable != null && grabbable.IsGrabbed) || DesktopBuddyMod.IsDesktopMode(root.World);
        }

        void TargetLinuxInput()
        {
            if (!DesktopBuddyPlatform.IsLinux) return;
            // Input goes to the shared workspace session, so the focused panel's captured
            // region has to travel with it or clicks land on the wrong screen.
            WindowInput.SetLinuxInputTarget(
                session.LinuxInputSessionId,
                session.LinuxPositionX, session.LinuxPositionY,
                session.Streamer?.Width ?? 0, session.Streamer?.Height ?? 0,
                session.LinuxWorkspaceWidth, session.LinuxWorkspaceHeight);
        }

        void SendDesktopPressed(Component source, float2 point)
        {
            TargetLinuxInput();
            if (ShouldIgnoreDesktopInput()) return;
            ClaimSource(source);
            float u = point.x;
            float v = point.y;
            uint touchId = GetTouchId(source);

            session.ActiveTouchIds.Add(touchId);
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch down {touchId}");
        }

        void SendDesktopPressing(Component source, float2 point)
        {
            TargetLinuxInput();
            if (ShouldIgnoreDesktopInput()) return;
            uint touchId = GetTouchId(source);

            if (!session.ActiveTouchIds.Contains(touchId)) return;
            float u = point.x;
            float v = point.y;
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch move {touchId}");
        }

        void SendDesktopReleased(Component source, float2 point)
        {
            TargetLinuxInput();
            if (ShouldIgnoreDesktopInput()) return;
            uint touchId = GetTouchId(source);
            float u = point.x;
            float v = point.y;

            if (!session.ActiveTouchIds.Remove(touchId)) return;
            WindowInput.SendAtPointWhenTargetAcceptable(
                hwnd,
                u,
                v,
                streamer.Width,
                streamer.Height,
                streamer.MonitorHandle,
                () => WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, touchId, streamer.MonitorHandle),
                $"touch up {touchId}");
        }

        void SendDesktopHovering(Component source, float2 point)
        {
            TargetLinuxInput();
            if (ShouldIgnoreDesktopInput()) return;
            float hu = point.x;
            float hv = point.y;

            if (IsActiveSource(source))
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height, streamer.MonitorHandle);

            var mouse = root.World.InputInterface.Mouse;
            if (mouse != null)
            {
                float scrollY = mouse.ScrollWheelDelta.Value.y;
                if (scrollY != 0)
                {
                    ClaimSource(source);
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendAtPointWhenTargetAcceptable(
                        hwnd,
                        hu,
                        hv,
                        streamer.Width,
                        streamer.Height,
                        streamer.MonitorHandle,
                        () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                        "mouse wheel");
                }
            }

            try
            {
                var handler = FindHandler(source);
                var controller = handler != null
                    ? root.World.InputInterface.GetControllerNode(handler.Side.Value)
                    : null;
                if (controller != null)
                {
                    // While the panel is pinned the grab action cannot move it, so it is free
                    // to mean right-click instead. Digital.Pressed is already edge-triggered;
                    // the tick guard only stops a second source firing within the same frame.
                    if (session.PanelLocked && controller.ActionGrab.Pressed)
                    {
                        double grabTick = root.World.Time.WorldTime;
                        if (grabTick != session.LastGrabTick)
                        {
                            session.LastGrabTick = grabTick;
                            float ru = hu, rv = hv;
                            ClaimSource(source);
                            WindowInput.SendAtPointWhenTargetAcceptable(
                                hwnd, ru, rv, streamer.Width, streamer.Height, streamer.MonitorHandle,
                                () => WindowInput.SendRightClick(hwnd, ru, rv, streamer.Width, streamer.Height, streamer.MonitorHandle),
                                "right click");
                        }
                    }

                    float axisY = controller.Axis.Value.y;
                    if (Math.Abs(axisY) > 0.15f)
                    {
                        double tick = root.World.Time.WorldTime;
                        if (tick != session.LastScrollTick)
                        {
                            session.LastScrollTick = tick;

                            int sign = Math.Sign(axisY);
                            if (sign != session.LastScrollSign)
                            {
                                // Reversing should respond at once rather than working off
                                // whatever was banked in the old direction.
                                session.LastScrollSign = sign;
                                session.StickScrollAccum = 0f;
                            }

                            float speed = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.StickScrollSpeed) ?? 8f;
                            if (speed <= 0f) speed = 8f;

                            // Bank notches against elapsed time, so the rate is in notches per
                            // second rather than per frame. The old per-frame emit meant scroll
                            // speed tracked framerate: ~90 notches a second at 90 FPS.
                            session.StickScrollAccum += axisY * speed * (float)root.World.Time.Delta;

                            while (Math.Abs(session.StickScrollAccum) >= 1f)
                            {
                                int step = Math.Sign(session.StickScrollAccum);
                                session.StickScrollAccum -= step;
                                int wheelDelta = step * 120;
                                ClaimSource(source);
                                WindowInput.SendAtPointWhenTargetAcceptable(
                                    hwnd,
                                    hu,
                                    hv,
                                    streamer.Width,
                                    streamer.Height,
                                    streamer.MonitorHandle,
                                    () => WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta, streamer.MonitorHandle),
                                    "controller wheel");
                            }
                        }
                    }
                    else
                    {
                        session.LastScrollSign = 0;
                        session.StickScrollAccum = 0f;
                    }
                }
            }
            catch { }
        }

        var directInput = screenMesh.Slot.AttachComponent<DesktopCurvedScreenInput>();
        directInput.ScreenMesh = screenMesh;
        directInput.Pressed = SendDesktopPressed;
        directInput.Pressing = SendDesktopPressing;
        directInput.Released = SendDesktopReleased;
        directInput.Hovering = SendDesktopHovering;
        return directInput;
    }
}
