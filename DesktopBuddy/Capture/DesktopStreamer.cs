using System;

namespace DesktopBuddy;

public sealed class DesktopStreamer : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _monitorHandle;
    private IDesktopCaptureBackend _backend;
    private int _disposed;

    public IntPtr MonitorHandle => _monitorHandle;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsValid => _backend?.IsValid ?? false;
    public object D3dContextLock => _backend?.D3dContextLock;
    public IntPtr D3dDevice => _backend?.D3dDevice ?? IntPtr.Zero;
    public IntPtr D3dContext => _backend?.D3dContext ?? IntPtr.Zero;
    public IntPtr SharedTexture => _backend?.SharedTexture ?? IntPtr.Zero;
    public IntPtr SharedTextureHandle => _backend?.SharedTextureHandle ?? IntPtr.Zero;
    public int SharedTextureWidth => _backend?.SharedTextureWidth ?? 0;
    public int SharedTextureHeight => _backend?.SharedTextureHeight ?? 0;
    public bool HasCurrentSharedFrame => _backend?.HasCurrentSharedFrame ?? false;
    public bool IsResizeRecreatePending => _backend?.IsResizeRecreatePending ?? false;
    public uint LinuxPipeWireNodeId => _backend is ILinuxPipeWireSelection linux ? linux.PipeWireNodeId : 0;
    public ulong LinuxInputSessionId => _backend is ILinuxPipeWireSelection linux ? linux.InputSessionId : 0;
    public ulong LinuxCaptureSessionId => _backend is ILinuxPipeWireSelection linux ? linux.CaptureSessionId : 0;
    public int LinuxPositionX => _backend is ILinuxPipeWireSelection linux ? linux.PositionX : 0;
    public int LinuxPositionY => _backend is ILinuxPipeWireSelection linux ? linux.PositionY : 0;
    public int LinuxWorkspaceWidth => _backend is ILinuxPipeWireSelection linux ? linux.WorkspaceWidth : 0;
    public int LinuxWorkspaceHeight => _backend is ILinuxPipeWireSelection linux ? linux.WorkspaceHeight : 0;

    public Action<IntPtr, IntPtr, int, int> OnGpuFrame
    {
        get => _backend?.OnGpuFrame;
        set { if (_backend != null) _backend.OnGpuFrame = value; }
    }

    public DesktopStreamer(IntPtr hwnd, IntPtr monitorHandle = default)
    {
        _hwnd = hwnd;
        _monitorHandle = monitorHandle;
    }

    public bool TryInitialCapture()
    {
        _backend = CreateBackend();
        Log.Msg($"[DesktopStreamer] Initial capture starting backend={_backend.GetType().Name} hwnd={_hwnd} monitor=0x{_monitorHandle:X}");

        bool success = _backend.TryInitialCapture();
        if (!success)
        {
            Log.Msg($"[DesktopStreamer] Initial capture failed hwnd={_hwnd} monitor=0x{_monitorHandle:X}");
            _backend.Dispose();
            _backend = null;
            return false;
        }

        Width = _backend.Width;
        Height = _backend.Height;
        Log.Msg($"[DesktopStreamer] Capture initialized ({Width}x{Height})");
        return true;
    }

    private IDesktopCaptureBackend CreateBackend()
    {
        return DesktopBuddyPlatform.IsLinux
            ? new LinuxPortalCaptureBackend()
            : new WgcCaptureBackend(_hwnd, _monitorHandle);
    }

    public void RecreatePoolIfNeeded()
    {
        if (_backend == null) return;
        _backend.RecreatePoolIfNeeded();
        Width = _backend.Width;
        Height = _backend.Height;
    }

    public void FlushD3dContext() => _backend?.FlushD3dContext();

    public void StopCapture()
    {
        _backend?.StopCapture();
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _backend?.Dispose();
        _backend = null;
    }
}
