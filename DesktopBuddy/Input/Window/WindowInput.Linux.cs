using System.Collections.Generic;

namespace DesktopBuddy;

public static partial class WindowInput
{

    internal static ulong LinuxInputSession;

    /// <summary>
    /// Captured region of the focused panel, in compositor coordinates, plus the workspace
    /// size. Input is injected against the workspace stream of the shared RemoteDesktop
    /// session, but panel coordinates are relative to the captured source, so they have to be
    /// offset into workspace space. Zero workspace values mean "capture is the whole
    /// workspace", where the mapping is the identity.
    /// </summary>
    internal static int LinuxCaptureX, LinuxCaptureY;
    internal static int LinuxCaptureW, LinuxCaptureH;
    internal static int LinuxWorkspaceW, LinuxWorkspaceH;

    internal static void SetLinuxInputTarget(ulong sessionId, int captureX, int captureY,
        int captureW, int captureH, int workspaceW, int workspaceH)
    {
        LinuxInputSession = sessionId;
        LinuxCaptureX = captureX;
        LinuxCaptureY = captureY;
        LinuxCaptureW = captureW;
        LinuxCaptureH = captureH;
        LinuxWorkspaceW = workspaceW;
        LinuxWorkspaceH = workspaceH;
    }

    /// <summary>Maps panel-local normalized coordinates onto workspace-normalized ones.</summary>
    private static void MapToWorkspace(float u, float v, out double outU, out double outV)
    {
        int ws = LinuxWorkspaceW, hs = LinuxWorkspaceH;
        int cw = LinuxCaptureW, ch = LinuxCaptureH;

        if (ws <= 0 || hs <= 0 || cw <= 0 || ch <= 0)
        {
            outU = u;
            outV = v;
            return;
        }

        double globalX = LinuxCaptureX + (double)u * cw;
        double globalY = LinuxCaptureY + (double)v * ch;
        outU = globalX / ws;
        outV = globalY / hs;
    }

    private static LinuxNativeBridge _linuxInput;
    private static readonly object _linuxInputLock = new();

    private static LinuxNativeBridge LinuxInput()
    {
        if (_linuxInput != null) return _linuxInput;
        lock (_linuxInputLock)
        {
            if (_linuxInput == null)
            {
                var bridge = new LinuxNativeBridge();
                bridge.TryLoad();
                _linuxInput = bridge;
            }
        }
        return _linuxInput;
    }

    internal static void LinuxMove(float u, float v)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        MapToWorkspace(u, v, out double mu, out double mv);
        LinuxInput().InputMotion(s, mu, mv);
    }

    internal static void LinuxTouchDown(uint slot, float u, float v)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        var b = LinuxInput();
        MapToWorkspace(u, v, out double mu, out double mv);

        if (PointerMode)
        {
            b.InputMotion(s, mu, mv);
            b.InputButton(s, BtnLeft, true);
            return;
        }

        int rc = b.TouchDown(s, slot, mu, mv);
        if (rc != 0)
            Log.Msg($"[LinuxInput] touch down rc={rc} session={s} slot={slot} err={b.GetInputLastError() ?? "(none)"}");
    }

    internal static void LinuxTouchMove(uint slot, float u, float v)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        MapToWorkspace(u, v, out double mu, out double mv);
        var b = LinuxInput();

        // In pointer mode a drag is just motion with the button already held, which is what
        // makes click-and-drag select text the way a real mouse does.
        if (PointerMode) { b.InputMotion(s, mu, mv); return; }

        b.TouchMotion(s, slot, mu, mv);
    }

    internal static void LinuxTouchUp(uint slot)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        var b = LinuxInput();
        if (PointerMode) { b.InputButton(s, BtnLeft, false); return; }
        b.TouchUp(s, slot);
    }

    private const int BtnLeft = 0x110;
    private const int BtnRight = 0x111;

    /// <summary>
    /// True when interaction should be sent as mouse buttons rather than touch contacts.
    ///
    /// The portal offers both, and they are not interchangeable from the application's point
    /// of view: a touch contact makes apps engage their touchscreen behaviour (drag pans
    /// instead of selecting, press-and-hold opens a context menu), and some shell surfaces
    /// such as the tray and task manager do not accept touch at all.
    /// </summary>
    private static bool PointerMode =>
        DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.LinuxPointerInput) ?? true;

    /// <summary>Moves the pointer to the panel point and issues a full right-click there.</summary>
    internal static void LinuxRightClick(float u, float v)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        var b = LinuxInput();
        MapToWorkspace(u, v, out double mu, out double mv);
        b.InputMotion(s, mu, mv);
        b.InputButton(s, BtnRight, true);
        b.InputButton(s, BtnRight, false);
    }

    internal static void LinuxScroll(int wheelDelta)
    {
        ulong s = LinuxInputSession;
        if (s == 0 || wheelDelta == 0) return;

        LinuxInput().InputScroll(s, wheelDelta > 0 ? 1 : -1);
    }

    internal static void LinuxTypeString(string text)
    {
        ulong s = LinuxInputSession;
        if (s == 0 || string.IsNullOrEmpty(text)) return;
        var b = LinuxInput();
        foreach (char c in text)
        {
            int keysym = CharToKeysym(c);
            b.InputKey(s, keysym, true);
            b.InputKey(s, keysym, false);
        }
    }

    internal static void LinuxKey(ushort vk, bool pressed)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        int keysym = VkToKeysym(vk);
        if (keysym != 0) LinuxInput().InputKey(s, keysym, pressed);
    }

    internal static void LinuxTapKey(ushort vk)
    {
        ulong s = LinuxInputSession;
        if (s == 0) return;
        int keysym = VkToKeysym(vk);
        if (keysym == 0) return;
        var b = LinuxInput();
        b.InputKey(s, keysym, true);
        b.InputKey(s, keysym, false);
    }

    private static int CharToKeysym(char c) => c <= 0xFF ? c : (0x01000000 + c);

    private static readonly Dictionary<ushort, int> _vkKeysyms = new()
    {
        [0x08] = 0xFF08,
        [0x09] = 0xFF09,
        [0x0D] = 0xFF0D,
        [0x1B] = 0xFF1B,
        [0x20] = 0x0020,
        [0x21] = 0xFF55,
        [0x22] = 0xFF56,
        [0x23] = 0xFF57,
        [0x24] = 0xFF50,
        [0x25] = 0xFF51,
        [0x26] = 0xFF52,
        [0x27] = 0xFF53,
        [0x28] = 0xFF54,
        [0x2E] = 0xFFFF,
        [0x10] = 0xFFE1,
        [0xA0] = 0xFFE1,
        [0xA1] = 0xFFE2,
        [0x11] = 0xFFE3,
        [0xA2] = 0xFFE3,
        [0xA3] = 0xFFE4,
        [0x12] = 0xFFE9,
        [0xA4] = 0xFFE9,
        [0xA5] = 0xFFEA,
    };

    private static int VkToKeysym(ushort vk)
    {
        if (_vkKeysyms.TryGetValue(vk, out int ks))
            return ks;

        if (vk >= 0x41 && vk <= 0x5A)
            return 0x61 + (vk - 0x41);

        if (vk >= 0x30 && vk <= 0x39)
            return vk;
        return 0;
    }
}
