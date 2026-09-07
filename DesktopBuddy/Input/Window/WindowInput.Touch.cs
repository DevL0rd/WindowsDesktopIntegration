using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBuddy;

public static partial class WindowInput
{

    public static void SendHover(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxMove(u, v); return; }
        var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
        SetCursorPos(pt.X, pt.Y);
    }

    public static void SendTouchDown(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxTouchDown(touchId, u, v); return; }
        lock (_sendLock)
        {
            if (!EnsureTouchInit()) return;
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            if (touchId < MAX_TOUCH_CONTACTS)
            {
                _lastPosition[touchId] = pt;
                _moveFailLogged[touchId] = false;
            }

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            contact.touchFlags = TOUCH_FLAG_NONE;
            contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
            contact.orientation = 90;
            contact.pressure = 32000;
            contact.rcContact.Top = pt.Y - 2;
            contact.rcContact.Bottom = pt.Y + 2;
            contact.rcContact.Left = pt.X - 2;
            contact.rcContact.Right = pt.X + 2;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[Touch] Down FAILED id={touchId} screen=({pt.X},{pt.Y}) err={err}");
            }
            else
            {
                Log.Msg($"[Touch] Down OK id={touchId} screen=({pt.X},{pt.Y})");
            }
        }
    }

    public static void SendTouchMove(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxTouchMove(touchId, u, v); return; }
        lock (_sendLock)
        {
            if (!_touchInitialized) return;
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            if (touchId < MAX_TOUCH_CONTACTS) _lastPosition[touchId] = pt;

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            contact.touchFlags = TOUCH_FLAG_NONE;
            contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
            contact.orientation = 90;
            contact.pressure = 32000;
            contact.rcContact.Top = pt.Y - 2;
            contact.rcContact.Bottom = pt.Y + 2;
            contact.rcContact.Left = pt.X - 2;
            contact.rcContact.Right = pt.X + 2;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                if (touchId < MAX_TOUCH_CONTACTS && !_moveFailLogged[touchId])
                {
                    _moveFailLogged[touchId] = true;
                    int err = Marshal.GetLastWin32Error();
                    Log.Msg($"[Touch] Move FAILED id={touchId} err={err} (further move errors suppressed)");
                }
            }
        }
    }

    public static void SendTouchUp(IntPtr hWnd, float u, float v, int clientW, int clientH, uint touchId = 0, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxTouchUp(touchId); return; }
        lock (_sendLock)
        {
            if (!_touchInitialized) return;
            var pt = (touchId < MAX_TOUCH_CONTACTS) ? _lastPosition[touchId] : UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);

            var contact = new POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;
            contact.pointerInfo.ptPixelLocation = pt;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_UP;

            _touchArr[0] = contact;
            if (!InjectTouchInput(1, _touchArr))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Msg($"[Touch] Up FAILED id={touchId} err={err}");
            }
            else
            {
                Log.Msg($"[Touch] Up OK id={touchId}");
            }
        }
    }

    /// <summary>
    /// Issues a right-click at the given panel point. Linux only for now: the Windows path is
    /// entirely touch-based and has no secondary-button equivalent.
    /// </summary>
    public static void SendRightClick(IntPtr hWnd, float u, float v, int clientW, int clientH, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxRightClick(u, v); return; }
        Log.Msg("[Input] Right-click is not implemented on this platform");
    }

    public static void SendScroll(IntPtr hWnd, float u, float v, int clientW, int clientH, int wheelDelta, IntPtr monitorHandle = default)
    {
        if (DesktopBuddyPlatform.IsLinux) { LinuxMove(u, v); LinuxScroll(wheelDelta); return; }
        lock (_sendLock)
        {
            var pt = UvToScreen(hWnd, u, v, clientW, clientH, monitorHandle);
            SetCursorPos(pt.X, pt.Y);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, IntPtr.Zero);
        }
    }

}
