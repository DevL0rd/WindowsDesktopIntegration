using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBuddy;

internal sealed unsafe class LinuxNativeBridge : IDisposable
{
    private delegate* unmanaged[Cdecl]<uint, ulong*, int> _startNode;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int> _pollFrame;
    private delegate* unmanaged[Cdecl]<ulong, void> _stopCapture;
    private delegate* unmanaged[Cdecl]<ulong, int> _captureAlive;
    private delegate* unmanaged[Cdecl]<uint, int> _nodeExists;
    private delegate* unmanaged[Cdecl]<DbLinuxFrame*, byte*, UIntPtr, int> _copyAndCloseFrame;
    private delegate* unmanaged[Cdecl]<DbLinuxFrame*, void> _closeFrame;
    private delegate* unmanaged[Cdecl]<ulong*, int> _audioStart;
    private delegate* unmanaged[Cdecl]<ulong, float*, int, int> _audioPoll;
    private delegate* unmanaged[Cdecl]<ulong, void> _audioStop;
    private delegate* unmanaged[Cdecl]<uint, int, int, int, uint, byte*, UIntPtr, byte*, UIntPtr, int, int, int, ulong*, int> _streamStart;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int> _streamPushFrame;
    private delegate* unmanaged[Cdecl]<ulong, float*, int, int> _streamPushAudio;
    private delegate* unmanaged[Cdecl]<ulong, byte*, int, long*, int*, long, int*, int*, int> _streamRead;
    private delegate* unmanaged[Cdecl]<ulong, DbLinuxStreamInfo*, int> _streamInfo;
    private delegate* unmanaged[Cdecl]<ulong, void> _streamStop;
    private delegate* unmanaged[Cdecl]<IntPtr> _streamLastError;
    private delegate* unmanaged[Cdecl]<int, int, int> _vcamOpen;
    private delegate* unmanaged[Cdecl]<int, byte*, int, int> _vcamWrite;
    private delegate* unmanaged[Cdecl]<int, void> _vcamClose;
    private delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, DbLinuxSelection*, int> _inputStart;
    private delegate* unmanaged[Cdecl]<ulong, double, double, void> _inputMotion;
    private delegate* unmanaged[Cdecl]<ulong, uint, double, double, int> _inputTouchDown;
    private delegate* unmanaged[Cdecl]<ulong, uint, double, double, void> _inputTouchMotion;
    private delegate* unmanaged[Cdecl]<ulong, uint, void> _inputTouchUp;
    private delegate* unmanaged[Cdecl]<ulong, int, void> _inputScroll;
    private delegate* unmanaged[Cdecl]<ulong, int, int, void> _inputKey;
    private delegate* unmanaged[Cdecl]<ulong, int, int, void> _inputButton;
    private delegate* unmanaged[Cdecl]<ulong, int> _inputStop;
    private delegate* unmanaged[Cdecl]<byte*, nuint, int> _inputRevokeToken;
    private delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, DbLinuxSelection*, int> _screencastStart;
    private delegate* unmanaged[Cdecl]<ulong, int> _screencastStop;
    private delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, int> _portalRevokeToken;
    private delegate* unmanaged[Cdecl]<int, int, int, int, byte*, nuint, int> _kwinOutputName;
    private delegate* unmanaged[Cdecl]<byte*, nuint, int> _kwinEffectLoaded;
    private delegate* unmanaged[Cdecl]<byte*, nuint, int, int> _kwinEffectSet;
    private delegate* unmanaged[Cdecl]<IntPtr> _inputLastError;
    private IntPtr _module;
    private IntPtr _streamModule;
    private bool _disposed;

    private bool IsLoaded => _module != IntPtr.Zero;

    internal bool TryLoad()
    {
        if (IsLoaded) return true;

        string nativePath = ResolveNativePath();
        if (string.IsNullOrWhiteSpace(nativePath) || !File.Exists(nativePath))
        {
            Log.Msg($"[LinuxNativeBridge] Native library not found: {nativePath ?? "(null)"}");
            return false;
        }

        try
        {
            _module = NativeLibrary.Load(nativePath);
            _startNode = (delegate* unmanaged[Cdecl]<uint, ulong*, int>)NativeLibrary.GetExport(_module, "db_linux_capture_start_node");
            _pollFrame = (delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int>)NativeLibrary.GetExport(_module, "db_linux_capture_poll");
            _stopCapture = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_module, "db_linux_capture_stop");
            _captureAlive = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_module, "db_linux_capture_alive");
            _nodeExists = (delegate* unmanaged[Cdecl]<uint, int>)NativeLibrary.GetExport(_module, "db_linux_node_exists");
            _copyAndCloseFrame = (delegate* unmanaged[Cdecl]<DbLinuxFrame*, byte*, UIntPtr, int>)NativeLibrary.GetExport(_module, "db_linux_frame_copy_and_close");
            _closeFrame = (delegate* unmanaged[Cdecl]<DbLinuxFrame*, void>)NativeLibrary.GetExport(_module, "db_linux_frame_close");
            _audioStart = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(_module, "db_linux_audio_start");
            _audioPoll = (delegate* unmanaged[Cdecl]<ulong, float*, int, int>)NativeLibrary.GetExport(_module, "db_linux_audio_poll");
            _audioStop = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_module, "db_linux_audio_stop");
            _inputStart = (delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, DbLinuxSelection*, int>)NativeLibrary.GetExport(_module, "db_linux_input_start");
            _inputMotion = (delegate* unmanaged[Cdecl]<ulong, double, double, void>)NativeLibrary.GetExport(_module, "db_linux_input_motion");
            _inputTouchDown = (delegate* unmanaged[Cdecl]<ulong, uint, double, double, int>)NativeLibrary.GetExport(_module, "db_linux_input_touch_down");
            _inputTouchMotion = (delegate* unmanaged[Cdecl]<ulong, uint, double, double, void>)NativeLibrary.GetExport(_module, "db_linux_input_touch_motion");
            _inputTouchUp = (delegate* unmanaged[Cdecl]<ulong, uint, void>)NativeLibrary.GetExport(_module, "db_linux_input_touch_up");
            _inputScroll = (delegate* unmanaged[Cdecl]<ulong, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_scroll");
            _inputKey = (delegate* unmanaged[Cdecl]<ulong, int, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_key");
            _inputButton = (delegate* unmanaged[Cdecl]<ulong, int, int, void>)NativeLibrary.GetExport(_module, "db_linux_input_button");
            _inputStop = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_module, "db_linux_input_stop");
            _inputRevokeToken = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)NativeLibrary.GetExport(_module, "db_linux_input_revoke_token");
            _screencastStart = (delegate* unmanaged[Cdecl]<byte*, nuint, ulong*, DbLinuxSelection*, int>)NativeLibrary.GetExport(_module, "db_linux_screencast_start");
            _screencastStop = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(_module, "db_linux_screencast_stop");
            _portalRevokeToken = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, int>)NativeLibrary.GetExport(_module, "db_linux_portal_revoke_token");
            _kwinOutputName = (delegate* unmanaged[Cdecl]<int, int, int, int, byte*, nuint, int>)NativeLibrary.GetExport(_module, "db_linux_kwin_output_name");
            _kwinEffectLoaded = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)NativeLibrary.GetExport(_module, "db_linux_kwin_effect_loaded");
            _kwinEffectSet = (delegate* unmanaged[Cdecl]<byte*, nuint, int, int>)NativeLibrary.GetExport(_module, "db_linux_kwin_effect_set");
            _inputLastError = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(_module, "db_linux_input_last_error");
            Log.Msg($"[LinuxNativeBridge] Loaded {nativePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxNativeBridge] Load failed path={nativePath}: {ex.Message}");
            if (_module != IntPtr.Zero)
            {
                NativeLibrary.Free(_module);
                _module = IntPtr.Zero;
            }
            _startNode = null;
            _pollFrame = null;
            _stopCapture = null;
            _captureAlive = null;
            _nodeExists = null;
            _copyAndCloseFrame = null;
            _closeFrame = null;
            _audioStart = null;
            _audioPoll = null;
            _audioStop = null;
            _inputStart = null;
            _inputMotion = null;
            _inputTouchDown = null;
            _inputTouchMotion = null;
            _inputTouchUp = null;
            _inputScroll = null;
            _inputKey = null;
            _inputButton = null;
            _inputStop = null;
            _inputRevokeToken = null;
            _screencastStart = null;
            _screencastStop = null;
            _portalRevokeToken = null;
            _kwinOutputName = null;
            _kwinEffectLoaded = null;
            _kwinEffectSet = null;
            _inputLastError = null;
            return false;
        }
    }

    /// <summary>
    /// Opens the ScreenCast picker (or restores silently with a token) and keeps the session
    /// alive until <see cref="ScreencastStop"/>. Unlike the combined RemoteDesktop session,
    /// this offers each monitor separately. Returns the session id, or 0 on failure.
    /// </summary>
    internal ulong ScreencastStart(string restoreToken, out DbLinuxSelection selection,
        out string newRestoreToken, out bool isMonitor)
    {
        selection = default;
        newRestoreToken = null;
        isMonitor = false;
        if (!TryLoad() || _screencastStart == null) return 0;

        byte[] tokenBytes = string.IsNullOrEmpty(restoreToken)
            ? null
            : Encoding.UTF8.GetBytes(restoreToken);

        ulong id = 0;
        int status;
        fixed (DbLinuxSelection* selPtr = &selection)
        fixed (byte* tok = tokenBytes)
            status = _screencastStart(tok, (nuint)(tokenBytes?.Length ?? 0), &id, selPtr);

        if (status != 0 || id == 0)
        {
            Log.Msg($"[LinuxNativeBridge] Screencast start failed status={status}: {GetInputLastError() ?? "(none)"}");
            return 0;
        }

        if (selection.RestoreTokenLen > 0)
        {
            int len = (int)Math.Min(selection.RestoreTokenLen, 256u);
            fixed (byte* p = selection.RestoreToken)
                newRestoreToken = Encoding.UTF8.GetString(p, len);
        }
        isMonitor = selection.IsMonitor != 0;
        return id;
    }

    /// <summary>Stops a capture session and closes its portal session.</summary>
    internal int ScreencastStop(ulong sessionId)
    {
        if (sessionId == 0) return -10;
        if (!TryLoad() || _screencastStop == null) return -10;
        return _screencastStop(sessionId);
    }

    /// <summary>
    /// Revokes a persisted grant from a specific permission-store table. RemoteDesktop grants
    /// live in "remote-desktop" and ScreenCast grants in "screencast".
    /// </summary>
    internal bool PortalRevokeToken(string table, string restoreToken)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(restoreToken)) return false;
        if (!TryLoad() || _portalRevokeToken == null) return false;

        byte[] tableBytes = Encoding.UTF8.GetBytes(table);
        byte[] tokenBytes = Encoding.UTF8.GetBytes(restoreToken);
        int status;
        fixed (byte* t = tableBytes)
        fixed (byte* k = tokenBytes)
            status = _portalRevokeToken(t, (nuint)tableBytes.Length, k, (nuint)tokenBytes.Length);

        if (status != 0)
            Log.Msg($"[LinuxNativeBridge] Revoke {table} token failed status={status}: {GetInputLastError() ?? "(none)"}");
        return status == 0;
    }

    /// <summary>
    /// Returns the compositor's connector name for the output at the given geometry
    /// ("DP-5", "HDMI-A-1", ...), or null when nothing matches or KWin is unavailable.
    /// The ScreenCast portal never reports an output name, so it is recovered by matching
    /// the captured geometry against what the compositor reports.
    /// </summary>
    internal string KWinOutputName(int x, int y, int width, int height)
    {
        if (!TryLoad() || _kwinOutputName == null) return null;

        byte[] buffer = new byte[64];
        int len;
        fixed (byte* p = buffer)
            len = _kwinOutputName(x, y, width, height, p, (nuint)buffer.Length);

        return len > 0 ? Encoding.UTF8.GetString(buffer, 0, len) : null;
    }

    /// <summary>
    /// Returns 1 if the named KWin effect is loaded, 0 if not, negative if KWin is
    /// unavailable (any non-KDE desktop, where this is simply not applicable).
    /// </summary>
    internal int KWinEffectLoaded(string effect)
    {
        if (string.IsNullOrEmpty(effect)) return -1;
        if (!TryLoad() || _kwinEffectLoaded == null) return -1;

        byte[] bytes = Encoding.UTF8.GetBytes(effect);
        fixed (byte* p = bytes)
            return _kwinEffectLoaded(p, (nuint)bytes.Length);
    }

    /// <summary>Loads or unloads a KWin effect at runtime. Returns true on success.</summary>
    internal bool KWinEffectSet(string effect, bool load)
    {
        if (string.IsNullOrEmpty(effect)) return false;
        if (!TryLoad() || _kwinEffectSet == null) return false;

        byte[] bytes = Encoding.UTF8.GetBytes(effect);
        int status;
        fixed (byte* p = bytes)
            status = _kwinEffectSet(p, (nuint)bytes.Length, load ? 1 : 0);

        if (status != 0)
            Log.Msg($"[LinuxNativeBridge] KWin effect {(load ? "load" : "unload")} '{effect}' failed status={status}: {GetInputLastError() ?? "(none)"}");
        return status == 0;
    }

    /// <summary>
    /// Revokes a persisted portal grant so it stops appearing in the desktop's remembered
    /// screen-sharing permissions. Call whenever a restore token is superseded or discarded.
    /// </summary>
    internal bool InputRevokeToken(string restoreToken)
    {
        if (string.IsNullOrEmpty(restoreToken)) return false;
        if (!TryLoad() || _inputRevokeToken == null) return false;

        byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(restoreToken);
        int status;
        fixed (byte* tok = tokenBytes)
            status = _inputRevokeToken(tok, (nuint)tokenBytes.Length);

        if (status != 0)
            Log.Msg($"[LinuxNativeBridge] Revoke token failed status={status}: {GetInputLastError() ?? "(none)"}");
        return status == 0;
    }

    internal ulong SessionStart(string restoreToken, out DbLinuxSelection selection, out string newRestoreToken,
        out bool isMonitor)
    {
        selection = default;
        newRestoreToken = null;
        isMonitor = false;
        if (!TryLoad() || _inputStart == null) return 0;

        byte[] tokenBytes = string.IsNullOrEmpty(restoreToken)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(restoreToken);

        ulong id = 0;
        fixed (DbLinuxSelection* selPtr = &selection)
        fixed (byte* tok = tokenBytes)
            _inputStart(tok, (nuint)(tokenBytes?.Length ?? 0), &id, selPtr);

        if (id != 0)
        {
            if (selection.RestoreTokenLen > 0)
            {
                int len = (int)Math.Min(selection.RestoreTokenLen, 256u);
                fixed (byte* p = selection.RestoreToken)
                    newRestoreToken = System.Text.Encoding.UTF8.GetString(p, len);
            }
            isMonitor = selection.IsMonitor != 0;
        }
        return id;
    }

    internal string GetInputLastError()
    {
        if (_inputLastError == null)
            return null;
        try
        {
            IntPtr ptr = _inputLastError();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch { return null; }
    }

    internal void InputMotion(ulong sessionId, double u, double v)
    {
        if (_inputMotion == null || sessionId == 0) return;
        _inputMotion(sessionId, u, v);
    }

    internal int TouchDown(ulong sessionId, uint slot, double u, double v)
    {
        if (_inputTouchDown == null || sessionId == 0) return -2;
        return _inputTouchDown(sessionId, slot, u, v);
    }

    internal void TouchMotion(ulong sessionId, uint slot, double u, double v)
    {
        if (_inputTouchMotion == null || sessionId == 0) return;
        _inputTouchMotion(sessionId, slot, u, v);
    }

    internal void TouchUp(ulong sessionId, uint slot)
    {
        if (_inputTouchUp == null || sessionId == 0) return;
        _inputTouchUp(sessionId, slot);
    }

    internal void InputScroll(ulong sessionId, int steps)
    {
        if (_inputScroll == null || sessionId == 0) return;
        _inputScroll(sessionId, steps);
    }

    internal void InputKey(ulong sessionId, int keysym, bool pressed)
    {
        if (_inputKey == null || sessionId == 0) return;
        _inputKey(sessionId, keysym, pressed ? 1 : 0);
    }

    /// <summary>
    /// Stops a portal session. Returns the native status: 0 when the portal confirmed the
    /// close, 1 if the worker ended without reaching it, -1 if the session was not
    /// registered, -2 if the close failed, -3 on lock poisoning. Returns -10 when the native
    /// library is unavailable, which is distinct from anything the native side reports.
    /// </summary>
    /// <summary>Presses or releases a pointer button (evdev code; BTN_RIGHT is 0x111).</summary>
    internal void InputButton(ulong sessionId, int button, bool pressed)
    {
        if (_inputButton == null || sessionId == 0) return;
        _inputButton(sessionId, button, pressed ? 1 : 0);
    }

    internal int InputStop(ulong sessionId)
    {
        if (sessionId == 0) return -10;
        // Callers routinely build a bridge purely to stop a session (cleanup does), so the
        // library has to be loaded here. Without this the delegate is still null and the
        // session is silently never closed, leaking the portal grant.
        if (!TryLoad() || _inputStop == null) return -10;
        return _inputStop(sessionId);
    }

    internal int StartCapture(uint nodeId, out ulong captureId)
    {
        captureId = 0;
        if (!TryLoad() || _startNode == null) return -1;
        ulong id = 0;
        int status = _startNode(nodeId, &id);
        captureId = id;
        return status;
    }

    internal int PollFrame(ulong captureId, out DbLinuxFrame frame)
    {
        frame = default;
        if (!TryLoad() || _pollFrame == null || captureId == 0) return -1;
        DbLinuxFrame local = default;
        int status = _pollFrame(captureId, &local);
        frame = local;
        return status;
    }

    internal void StopCapture(ulong captureId)
    {
        if (_stopCapture == null || captureId == 0) return;
        _stopCapture(captureId);
    }

    internal bool IsCaptureAlive(ulong captureId)
    {
        if (!TryLoad() || _captureAlive == null || captureId == 0) return true;
        return _captureAlive(captureId) != 0;
    }

    /// <summary>
    /// Asks PipeWire whether <paramref name="nodeId"/> is a live node right now. Returns false
    /// only for a definite "no"; if the check itself cannot run or the server does not answer,
    /// this returns true so callers behave as they did before rather than discarding a source
    /// on the strength of a failed lookup.
    /// </summary>
    internal bool NodeExists(uint nodeId)
    {
        if (!TryLoad() || _nodeExists == null) return true;
        return _nodeExists(nodeId) != 0;
    }

    internal int CopyAndCloseFrame(DbLinuxFrame frame, byte[] destination)
    {
        if (!TryLoad() || _copyAndCloseFrame == null || destination == null || destination.Length == 0)
            return -1;

        fixed (byte* dst = destination)
            return _copyAndCloseFrame(&frame, dst, (UIntPtr)(ulong)destination.LongLength);
    }

    internal void CloseFrame(DbLinuxFrame frame)
    {
        if (_closeFrame == null || frame.Fd < 0) return;
        _closeFrame(&frame);
    }

    internal int StartStream(uint nodeId, int fps, int bitrateMbps, int maxResolution, uint adapterVendorId, string encoderPreference, string rtspUrl, bool audioEnabled, int audioSampleRate, int audioChannels, out ulong streamId)
    {
        streamId = 0;
        if (!TryLoadStream() || _streamStart == null) return -1;
        byte[] encoderBytes = string.IsNullOrEmpty(encoderPreference) ? Encoding.UTF8.GetBytes("auto") : Encoding.UTF8.GetBytes(encoderPreference);
        byte[] rtspBytes = string.IsNullOrEmpty(rtspUrl) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(rtspUrl);
        fixed (byte* encoderPtr = encoderBytes)
        fixed (byte* rtspPtr = rtspBytes)
        {
            ulong id = 0;
            int status = _streamStart(
                nodeId,
                fps,
                bitrateMbps,
                maxResolution,
                adapterVendorId,
                encoderPtr,
                (UIntPtr)(ulong)encoderBytes.LongLength,
                rtspPtr,
                (UIntPtr)(ulong)rtspBytes.LongLength,
                audioEnabled ? 1 : 0,
                audioSampleRate,
                audioChannels,
                &id);
            streamId = id;
            return status;
        }
    }

    internal int StartAudioCapture(out ulong captureId)
    {
        captureId = 0;
        if (!TryLoad() || _audioStart == null) return -1;
        ulong id = 0;
        int status = _audioStart(&id);
        captureId = id;
        return status;
    }

    internal int PollAudio(ulong captureId, float[] buffer)
    {
        if (!TryLoad() || _audioPoll == null || captureId == 0 || buffer == null || buffer.Length == 0)
            return 0;
        fixed (float* ptr = buffer)
            return _audioPoll(captureId, ptr, buffer.Length);
    }

    internal void StopAudioCapture(ulong captureId)
    {
        if (_audioStop == null || captureId == 0) return;
        _audioStop(captureId);
    }

    internal int PushStreamAudio(ulong streamId, float[] buffer, int frameCount)
    {
        if (!TryLoadStream() || _streamPushAudio == null || streamId == 0 || buffer == null || frameCount <= 0)
            return -1;
        fixed (float* ptr = buffer)
            return _streamPushAudio(streamId, ptr, frameCount);
    }

    internal int ReadStream(ulong streamId, byte[] destination, ref long readPos, ref bool aligned, long minimumKeyframePos, out bool keyframeAligned)
    {
        keyframeAligned = aligned;
        if (!TryLoadStream() || _streamRead == null || streamId == 0 || destination == null || destination.Length == 0)
            return 0;

        int alignedValue = aligned ? 1 : 0;
        int keyAlignedValue = keyframeAligned ? 1 : 0;
        int bytesRead = 0;
        long localReadPos = readPos;
        fixed (byte* dst = destination)
        {
            int status = _streamRead(streamId, dst, destination.Length, &localReadPos, &alignedValue, minimumKeyframePos, &keyAlignedValue, &bytesRead);
            readPos = localReadPos;
            aligned = alignedValue != 0;
            keyframeAligned = keyAlignedValue != 0;
            return status == 0 ? bytesRead : 0;
        }
    }

    internal int PushStreamFrame(ulong streamId, DbLinuxFrame frame)
    {
        if (!TryLoadStream() || _streamPushFrame == null || streamId == 0 || frame.Fd < 0)
            return -1;
        return _streamPushFrame(streamId, &frame);
    }

    internal DbLinuxStreamInfo GetStreamInfo(ulong streamId)
    {
        var info = default(DbLinuxStreamInfo);
        if (!TryLoadStream() || _streamInfo == null || streamId == 0)
            return info;
        _streamInfo(streamId, &info);
        return info;
    }

    internal void StopStream(ulong streamId)
    {
        if (_streamStop == null || streamId == 0) return;
        _streamStop(streamId);
    }

    private bool TryLoadStream()
    {
        if (_streamModule != IntPtr.Zero) return true;

        string streamPath = ResolveSiblingNativePath("libdesktopbuddy_linux_stream.so");
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath))
        {
            Log.Msg($"[LinuxNativeBridge] Stream library not found: {streamPath ?? "(null)"}");
            return false;
        }

        try
        {
            _streamModule = NativeLibrary.Load(streamPath);
            _streamStart = (delegate* unmanaged[Cdecl]<uint, int, int, int, uint, byte*, UIntPtr, byte*, UIntPtr, int, int, int, ulong*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_start");
            _streamPushFrame = (delegate* unmanaged[Cdecl]<ulong, DbLinuxFrame*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_push_frame");
            _streamPushAudio = (delegate* unmanaged[Cdecl]<ulong, float*, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_push_audio");
            _streamRead = (delegate* unmanaged[Cdecl]<ulong, byte*, int, long*, int*, long, int*, int*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_read");
            _streamInfo = (delegate* unmanaged[Cdecl]<ulong, DbLinuxStreamInfo*, int>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_info");
            _streamStop = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_stop");
            _streamLastError = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(_streamModule, "db_linux_stream_last_error");
            _vcamOpen = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_open");
            _vcamWrite = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_write");
            _vcamClose = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(_streamModule, "db_linux_vcam_close");
            Log.Msg($"[LinuxNativeBridge] Loaded stream library {streamPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Msg($"[LinuxNativeBridge] Stream library load failed path={streamPath}: {ex.Message}");
            if (_streamModule != IntPtr.Zero)
            {
                NativeLibrary.Free(_streamModule);
                _streamModule = IntPtr.Zero;
            }
            _streamStart = null;
            _streamPushFrame = null;
            _streamPushAudio = null;
            _streamRead = null;
            _streamInfo = null;
            _streamStop = null;
            _streamLastError = null;
            _vcamOpen = null;
            _vcamWrite = null;
            _vcamClose = null;
            return false;
        }
    }

    internal int VcamOpen(int width, int height)
    {
        if (!TryLoadStream() || _vcamOpen == null) return -1;
        return _vcamOpen(width, height);
    }

    internal int VcamWrite(int fd, byte[] data, int length)
    {
        if (_vcamWrite == null || fd < 0 || data == null || length <= 0) return -1;
        fixed (byte* ptr = data)
            return _vcamWrite(fd, ptr, length);
    }

    internal void VcamClose(int fd)
    {
        if (_vcamClose == null || fd < 0) return;
        _vcamClose(fd);
    }

    internal string GetStreamLastError()
    {
        if (_streamLastError == null)
            return null;
        try
        {
            IntPtr ptr = _streamLastError();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch { return null; }
    }

    private static string ResolveNativePath()
    {
        string overridePath = Environment.GetEnvironmentVariable("DESKTOPBUDDY_LINUX_NATIVE");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty;
        string alongside = Path.Combine(assemblyDir, "libdesktopbuddy_linux_native.so");
        if (File.Exists(alongside))
            return alongside;

        try { return DesktopBuddyRuntimePaths.FindFile("libdesktopbuddy_linux_native.so"); }
        catch { return Path.Combine(assemblyDir, "DesktopBuddyRuntime", "libdesktopbuddy_linux_native.so"); }
    }

    private static string ResolveSiblingNativePath(string fileName)
    {
        string overridePath = Environment.GetEnvironmentVariable("DESKTOPBUDDY_LINUX_STREAM");
        if (fileName == "libdesktopbuddy_linux_stream.so" && !string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string nativePath = ResolveNativePath();
        string nativeDir = Path.GetDirectoryName(nativePath);
        if (!string.IsNullOrWhiteSpace(nativeDir))
        {
            string sibling = Path.Combine(nativeDir, fileName);
            if (File.Exists(sibling))
                return sibling;
        }

        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty;
        string alongside = Path.Combine(assemblyDir, fileName);
        if (File.Exists(alongside))
            return alongside;

        try { return DesktopBuddyRuntimePaths.FindFile(fileName); }
        catch { return Path.Combine(assemblyDir, "DesktopBuddyRuntime", fileName); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _startNode = null;
        _pollFrame = null;
        _stopCapture = null;
        _captureAlive = null;
        _nodeExists = null;
        _copyAndCloseFrame = null;
        _closeFrame = null;
        _audioStart = null;
        _audioPoll = null;
        _audioStop = null;
        _inputStart = null;
        _inputMotion = null;
        _inputTouchDown = null;
        _inputTouchMotion = null;
        _inputTouchUp = null;
        _inputScroll = null;
        _inputKey = null;
        _inputStop = null;
        _inputLastError = null;
        _streamStart = null;
        _streamPushFrame = null;
        _streamPushAudio = null;
        _streamRead = null;
        _streamInfo = null;
        _streamStop = null;
        _streamLastError = null;
        _vcamOpen = null;
        _vcamWrite = null;
        _vcamClose = null;
        if (_streamModule != IntPtr.Zero)
        {
            NativeLibrary.Free(_streamModule);
            _streamModule = IntPtr.Zero;
        }
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
            _module = IntPtr.Zero;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DbLinuxSelection
{
    public uint NodeId;
    public uint Width;
    public uint Height;
    public uint IsMonitor;
    public uint RestoreTokenLen;
    public fixed byte RestoreToken[256];
    /// <summary>
    /// Offset of the captured source in compositor coordinates. Non-zero for a monitor that
    /// is not the leftmost one, and needed to map panel input onto the workspace when capture
    /// and input come from separate portal sessions.
    /// </summary>
    public int PositionX;
    public int PositionY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DbLinuxFrame
{
    public int Status;
    public int Fd;
    public uint Width;
    public uint Height;
    public uint Fourcc;
    public uint Offset;
    public int Stride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DbLinuxStreamInfo
{
    public int Running;
    public int Broken;
    public int Width;
    public int Height;
    public long WritePos;
    public long KeyframePos;
    public long Frames;
}
