using System;
using System.Threading.Tasks;

namespace DesktopBuddy;

/// <summary>
/// Suspends KWin's "shake cursor to find it" effect while any desktop is shared.
///
/// While a share is active there are two things driving one cursor: DesktopBuddy injecting
/// absolute pointer motion at the laser's hit point, and the user's real mouse. The pointer
/// snaps between the two positions many times a second, KWin reads that as shaking, and
/// magnifies the cursor further the longer it continues.
///
/// The effect is unloaded at runtime rather than disabled in kwinrc, so a crash cannot leave
/// the user's configuration modified — a KWin reconfigure or restart brings it back.
/// Non-KDE desktops have no such effect and every call here is a harmless no-op.
/// </summary>
internal static class LinuxCursorEffectSuspender
{
    private const string EffectName = "shakecursor";

    private static readonly object Lock = new();
    private static bool _suspended;
    private static bool _busy;

    /// <summary>
    /// Drives the effect from the live session count. Sessions are removed from
    /// <c>ActiveSessions</c> in several places, so this is derived from the actual count on
    /// every update rather than counted through hooks that a removal path could bypass.
    /// Cheap and idempotent when nothing needs to change.
    /// </summary>
    internal static void Sync(int activeShareCount)
    {
        if (!DesktopBuddyPlatform.IsLinux) return;

        bool wanted = activeShareCount > 0
                      && (DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.LinuxSuspendShakeCursor) ?? true);

        lock (Lock)
        {
            if (_busy || wanted == _suspended) return;
            _busy = true;
        }

        // The D-Bus round trip is short but this runs from the update loop, so keep it off it.
        Task.Run(() =>
        {
            try
            {
                if (wanted) Suspend();
                else Restore();
            }
            finally { lock (Lock) { _busy = false; } }
        });
    }

    /// <summary>Restores the effect unconditionally, for shutdown paths.</summary>
    internal static void RestoreNow()
    {
        if (!DesktopBuddyPlatform.IsLinux) return;
        lock (Lock) { if (!_suspended) return; }
        Restore();
    }

    private static void Suspend()
    {
        try
        {
            using var bridge = new LinuxNativeBridge();
            // Only touch the effect if it was actually on, so we never switch on something
            // the user had deliberately turned off.
            if (bridge.KWinEffectLoaded(EffectName) != 1) return;
            if (!bridge.KWinEffectSet(EffectName, load: false)) return;

            lock (Lock) _suspended = true;
            DesktopBuddyMod.Msg("[CursorEffect] Suspended KWin shakecursor while sharing");
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[CursorEffect] Suspend failed: {ex.Message}"); }
    }

    private static void Restore()
    {
        try
        {
            using var bridge = new LinuxNativeBridge();
            if (bridge.KWinEffectSet(EffectName, load: true))
                DesktopBuddyMod.Msg("[CursorEffect] Restored KWin shakecursor");
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[CursorEffect] Restore failed: {ex.Message}"); }
        finally { lock (Lock) _suspended = false; }
    }
}
