using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class LinuxCaptureTextureSlot : IBridgeTextureSlot
    {
        private const uint DrmFormatArgb8888 = 0x34325241;
        private const uint DrmFormatXrgb8888 = 0x34325258;
        private const uint DrmFormatAbgr8888 = 0x34324241;
        private const uint DrmFormatXbgr8888 = 0x34324258;

        private readonly ManualLogSource _log;
        private readonly HashSet<Action> _requests = new HashSet<Action>();
        private readonly LinuxNativeBridgeRenderer _bridge = new LinuxNativeBridgeRenderer();
        private readonly uint _pipeWireNodeId;

        private Texture2D _texture;
        private byte[] _pixels;
        private bool _disposed;
        private bool _captureStarted;
        private bool _started;
        private int _width;
        private int _height;
        private int _pollsWithoutFrame;
        private int _copyFailures;
        private int _unsupportedFrames;
        private int _uploadedFrames;
        private bool _loggedFirstFrame;

        public Texture UnityTexture => _texture;
        public int Width => Math.Max(1, _width);
        public int Height => Math.Max(1, _height);
        public int RequestCount => _requests.Count;
        public bool IsValid => !_disposed && _started && _texture != null;
        public string SourceName => "LinuxShmCapture";

        internal LinuxCaptureTextureSlot(uint pipeWireNodeId, int widthHint, int heightHint, ManualLogSource log)
        {
            _pipeWireNodeId = pipeWireNodeId;
            _width = Math.Max(1, widthHint);
            _height = Math.Max(1, heightHint);
            _log = log;
        }

        public bool TryBind()
        {
            if (_started) return true;
            if (_disposed) return false;

            EnsureCaptureStarted();
            PollAndUploadFrame();
            return _started;
        }

        public void Tick()
        {
            if (!_captureStarted || _disposed)
                return;

            PollAndUploadFrame();
        }

        private void EnsureCaptureStarted()
        {
            if (_captureStarted || _disposed)
                return;

            if (!LinuxCaptureGuard.BeginNativeStart(_pipeWireNodeId))
            {
                _captureStarted = false;
                return;
            }

            // Both stages run unmanaged code that can take the renderer down, so both stay inside
            // the guard. They are issued separately only so the marker can name which one lost.
            int status;
            try
            {
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Loading native capture library node={_pipeWireNodeId}");
                status = _bridge.LoadNative();
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Native capture library load returned {status}");

                if (status == 0)
                {
                    LinuxCaptureGuard.MarkStage(LinuxCaptureGuard.StageConnect);
                    SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] SHM capture start calling native bridge node={_pipeWireNodeId}");
                    status = _bridge.StartCapture(_pipeWireNodeId);
                    SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] SHM capture start returned {status} node={_pipeWireNodeId}");
                }
            }
            finally
            {
                LinuxCaptureGuard.EndNativeStart();
            }

            if (status != 0)
                SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] SHM capture did not start cleanly: {status}");

            _captureStarted = status == 0;
        }

        private bool PollAndUploadFrame()
        {
            int status = _bridge.PollFrame(out var frame);
            if (status == 1)
            {
                _pollsWithoutFrame++;
                if (_pollsWithoutFrame == 120 || _pollsWithoutFrame % 600 == 0)
                    SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Waiting for SHM frame ({_pollsWithoutFrame} polls)");
                return false;
            }

            if (status != 0 || frame.Status != 0)
            {
                SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] PollFrame status={status} frameStatus={frame.Status} fd={frame.Fd}");
                return false;
            }
            _pollsWithoutFrame = 0;

            if (!IsSupportedFourcc(frame.Fourcc) || frame.Width == 0 || frame.Height == 0)
            {
                _unsupportedFrames++;
                if (_unsupportedFrames <= 8 || _unsupportedFrames % 120 == 0)
                    SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Unsupported SHM frame count={_unsupportedFrames} fd={frame.Fd} {frame.Width}x{frame.Height} fourcc=0x{frame.Fourcc:X8} stride={frame.Stride}");
                _bridge.DiscardFrame(frame);
                return false;
            }

            int width = checked((int)frame.Width);
            int height = checked((int)frame.Height);
            int rowBytes = checked(width * 4);
            int byteCount = checked(rowBytes * height);
            if (_pixels == null || _pixels.Length != byteCount)
                _pixels = new byte[byteCount];

            int copyStatus = _bridge.CopyFrameBytes(frame, _pixels);
            if (copyStatus != 0)
            {
                _copyFailures++;
                if (_copyFailures <= 8 || _copyFailures % 120 == 0)
                    SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] SHM copy failed count={_copyFailures} status={copyStatus} fd={frame.Fd} {frame.Width}x{frame.Height} fourcc=0x{frame.Fourcc:X8} stride={frame.Stride}");
                return false;
            }

            if (frame.Fourcc == DrmFormatAbgr8888 || frame.Fourcc == DrmFormatXbgr8888)
                SwapRedBlue(_pixels);

            if (frame.Fourcc == DrmFormatXrgb8888 || frame.Fourcc == DrmFormatXbgr8888)
                ForceOpaqueAlpha(_pixels);

            bool resized = _texture == null || _texture.width != width || _texture.height != height;
            if (resized)
            {
                DestroyTexture();
                _texture = new Texture2D(width, height, TextureFormat.BGRA32, false, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                _width = width;
                _height = height;
            }

            _texture.LoadRawTextureData(_pixels);
            _texture.Apply(false, false);

            _uploadedFrames++;
            if (!_started)
                _started = true;

            if (!_loggedFirstFrame || resized)
                NotifyCallbacks();

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] First SHM frame uploaded {_width}x{_height} fourcc=0x{frame.Fourcc:X8} stride={frame.Stride}");
            }
            else if (_uploadedFrames % 120 == 0)
            {
                SharedTextureBridgePlugin.LogInfo($"[LinuxCapture] Uploaded SHM frames={_uploadedFrames} {_width}x{_height}");
            }

            return true;
        }

        private static bool IsSupportedFourcc(uint fourcc)
        {
            return fourcc == DrmFormatArgb8888 || fourcc == DrmFormatXrgb8888 ||
                fourcc == DrmFormatAbgr8888 || fourcc == DrmFormatXbgr8888;
        }

        private static void SwapRedBlue(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte red = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = red;
            }
        }

        private static void ForceOpaqueAlpha(byte[] pixels)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        public void RegisterRequest(Action onTextureChanged)
        {
            try
            {
                if (onTextureChanged != null) _requests.Add(onTextureChanged);
                if (_texture != null)
                    onTextureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LinuxCapture] RegisterRequest failed", ex);
            }
        }

        public void UnregisterRequest(Action onTextureChanged)
        {
            try
            {
                if (onTextureChanged != null) _requests.Remove(onTextureChanged);
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[LinuxCapture] UnregisterRequest failed", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DestroyTexture();
            _bridge.Dispose();
            _requests.Clear();
        }

        private void DestroyTexture()
        {
            if (_texture == null)
                return;

            try { UnityEngine.Object.Destroy(_texture); }
            catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Texture destroy failed: {ex.Message}"); }
            _texture = null;
        }

        private void NotifyCallbacks()
        {
            foreach (var cb in _requests)
            {
                try { cb?.Invoke(); }
                catch (Exception ex) { _log?.LogWarning($"[LinuxCapture] Callback error: {ex.Message}"); }
            }
        }
    }
}
