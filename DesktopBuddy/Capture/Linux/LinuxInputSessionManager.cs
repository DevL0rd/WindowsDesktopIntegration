using System;

namespace DesktopBuddy;

/// <summary>
/// Owns the single RemoteDesktop session used for input injection across every panel.
///
/// Capture and input have to come from separate portal sessions: binding ScreenCast to a
/// RemoteDesktop session makes xdg-desktop-portal-kde collapse source selection into one
/// whole-workspace toggle, which is why individual monitors never appeared in the picker.
///
/// The input session still carries a workspace stream, because absolute pointer injection is
/// addressed against a stream node. One session serves all panels, so this costs a single
/// portal grant no matter how many screens are shared — fewer than the old design, which
/// created one per panel.
/// </summary>
internal static class LinuxInputSessionManager
{
    private static readonly object Lock = new();
    private static ulong _sessionId;
    private static int _workspaceWidth;
    private static int _workspaceHeight;

    internal static ulong SessionId { get { lock (Lock) return _sessionId; } }
    internal static int WorkspaceWidth { get { lock (Lock) return _workspaceWidth; } }
    internal static int WorkspaceHeight { get { lock (Lock) return _workspaceHeight; } }

    /// <summary>
    /// Returns the shared input session, creating it on first use. The saved restore token
    /// keeps this silent after the first approval.
    /// </summary>
    internal static ulong Ensure(out int workspaceWidth, out int workspaceHeight)
    {
        lock (Lock)
        {
            if (_sessionId != 0)
            {
                workspaceWidth = _workspaceWidth;
                workspaceHeight = _workspaceHeight;
                return _sessionId;
            }
        }

        ulong id = 0;
        int w = 0, h = 0;
        try
        {
            string savedToken = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.LinuxInputRestoreToken);
            using var bridge = new LinuxNativeBridge();
            id = bridge.SessionStart(string.IsNullOrEmpty(savedToken) ? null : savedToken,
                out var selection, out string newToken, out _);
            if (id != 0)
            {
                w = selection.Width > 0 ? (int)selection.Width : 0;
                h = selection.Height > 0 ? (int)selection.Height : 0;

                // The portal mints a fresh grant per start and never invalidates the old one,
                // so the token we are replacing has to be revoked or it accumulates.
                if (!string.IsNullOrEmpty(newToken) && newToken != savedToken)
                {
                    DesktopBuddyMod.SaveConfigValue(DesktopBuddyMod.LinuxInputRestoreToken, newToken);
                    if (!string.IsNullOrEmpty(savedToken))
                        bridge.PortalRevokeToken("remote-desktop", savedToken);
                }
                DesktopBuddyMod.Msg($"[LinuxInput] Shared input session {id} ready, workspace {w}x{h}");
            }
            else
            {
                DesktopBuddyMod.Msg("[LinuxInput] Shared input session could not be created; panels will capture without input");
            }
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[LinuxInput] Input session error: {ex.Message}"); }

        lock (Lock)
        {
            if (_sessionId == 0 && id != 0)
            {
                _sessionId = id;
                _workspaceWidth = w;
                _workspaceHeight = h;
            }
            workspaceWidth = _workspaceWidth;
            workspaceHeight = _workspaceHeight;
            return _sessionId;
        }
    }

    /// <summary>Closes the shared session once no panels remain.</summary>
    internal static void Release()
    {
        ulong id;
        lock (Lock)
        {
            if (_sessionId == 0) return;
            id = _sessionId;
            _sessionId = 0;
            _workspaceWidth = 0;
            _workspaceHeight = 0;
        }

        try
        {
            using var bridge = new LinuxNativeBridge();
            int status = bridge.InputStop(id);
            DesktopBuddyMod.Msg(status == 0
                ? $"[LinuxInput] Released shared input session {id}"
                : $"[LinuxInput] Shared input session {id} stop returned {status}: {bridge.GetInputLastError() ?? "(no error)"}");
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[LinuxInput] Input session release error: {ex.Message}"); }

        if (WindowInput.LinuxInputSession == id)
            WindowInput.LinuxInputSession = 0;
    }
}
