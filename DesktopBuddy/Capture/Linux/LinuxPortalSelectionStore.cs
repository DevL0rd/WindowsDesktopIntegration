namespace DesktopBuddy;

internal readonly struct LinuxPortalSelection
{
    public readonly uint NodeId;
    public readonly int Width;
    public readonly int Height;

    /// <summary>Shared RemoteDesktop session used for input; not owned by this capture.</summary>
    public readonly ulong InputSessionId;

    /// <summary>ScreenCast session owning this capture's stream; stopped when the panel closes.</summary>
    public readonly ulong CaptureSessionId;

    /// <summary>
    /// Where the captured source sits in compositor coordinates, and how big the whole
    /// workspace is. Input is injected against the workspace stream, so panel-local
    /// coordinates have to be offset into that space before they are sent.
    /// </summary>
    public readonly int PositionX;
    public readonly int PositionY;
    public readonly int WorkspaceWidth;
    public readonly int WorkspaceHeight;

    public LinuxPortalSelection(uint nodeId, int width, int height, ulong inputSessionId = 0,
        ulong captureSessionId = 0, int positionX = 0, int positionY = 0,
        int workspaceWidth = 0, int workspaceHeight = 0)
    {
        NodeId = nodeId;
        Width = width;
        Height = height;
        InputSessionId = inputSessionId;
        CaptureSessionId = captureSessionId;
        PositionX = positionX;
        PositionY = positionY;
        WorkspaceWidth = workspaceWidth;
        WorkspaceHeight = workspaceHeight;
    }
}

internal static class LinuxPortalSelectionStore
{
    private static readonly object Lock = new();
    private static LinuxPortalSelection? _pending;

    internal static void Set(LinuxPortalSelection selection)
    {
        lock (Lock)
            _pending = selection;
    }

    internal static bool TryConsume(out LinuxPortalSelection selection)
    {
        lock (Lock)
        {
            if (_pending.HasValue)
            {
                selection = _pending.Value;
                _pending = null;
                return true;
            }
        }

        selection = default;
        return false;
    }
}
