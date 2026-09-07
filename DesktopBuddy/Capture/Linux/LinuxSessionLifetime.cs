using System;
using System.Threading.Tasks;

namespace DesktopBuddy;

/// <summary>
/// Releases the shared RemoteDesktop input session once no panels remain.
///
/// Like the cursor-effect suspender this is driven from the live session count rather than
/// from open/close hooks, because sessions are removed from <c>ActiveSessions</c> in several
/// places and not all of them run cleanup.
/// </summary>
internal static class LinuxSessionLifetime
{
    private static readonly object Lock = new();
    private static bool _busy;

    internal static void Sync(int activeShareCount)
    {
        if (!DesktopBuddyPlatform.IsLinux) return;
        if (activeShareCount > 0) return;
        if (LinuxInputSessionManager.SessionId == 0) return;

        lock (Lock)
        {
            if (_busy) return;
            _busy = true;
        }

        // Closing the portal session is a D-Bus round trip; keep it off the update loop.
        Task.Run(() =>
        {
            try { LinuxInputSessionManager.Release(); }
            catch (Exception ex) { DesktopBuddyMod.Msg($"[LinuxInput] Release failed: {ex.Message}"); }
            finally { lock (Lock) { _busy = false; } }
        });
    }
}
