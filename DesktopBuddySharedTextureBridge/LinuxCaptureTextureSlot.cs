using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class LinuxCaptureTextureSlot : IBridgeTextureSlot
    {
        /// <summary>
        /// Frames a texture retired on teardown is kept alive before it is destroyed. Matches the
        /// deferral the shared-texture path uses for its native handles.
        /// </summary>
        private const int DeferredDestroyFrames = 3;

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

            RetireTexture();
            _bridge.Dispose();
            _requests.Clear();
        }

        /// <summary>
        /// Hands the texture to the deferred queue rather than destroying it here.
        /// </summary>
        /// <remarks>
        /// Teardown is the one case where no replacement texture is published: the slot is being
        /// unregistered, so a consumer that still holds this source for the rest of the frame has
        /// nothing valid to re-read. Renderite's own <c>DuplicableDisplay.UnregisterRequest</c>
        /// hit this and responded by never destroying the texture at all, commenting the call out
        /// with "destroying and recreating it causes issues". It can afford that because it keeps
        /// one texture per monitor; slots here come and go with every share, so holding them
        /// forever would leak a full-resolution texture per share. Deferring instead keeps the
        /// texture alive past any in-flight reference without growing without bound.
        /// </remarks>
        private void RetireTexture()
        {
            if (_texture == null)
                return;

            DeferredDestroys.Enqueue(new DeferredTextureDestroy
            {
                Texture = _texture,
                FramesRemaining = DeferredDestroyFrames
            });
            _texture = null;
        }

        /// <summary>
        /// Destroys the texture immediately. Only safe where a valid replacement is published in
        /// the same call, which is what the resize path does — and what Renderite's own display
        /// driver does for the same reason.
        /// </summary>
        private void DestroyTexture()
        {
            if (_texture == null)
                return;

            try { UnityEngine.Object.Destroy(_texture); }
            catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Texture destroy failed: {ex.Message}"); }
            _texture = null;
        }

        private struct DeferredTextureDestroy
        {
            public Texture2D Texture;
            public int FramesRemaining;
        }

        private static readonly ConcurrentQueue<DeferredTextureDestroy> DeferredDestroys =
            new ConcurrentQueue<DeferredTextureDestroy>();

        /// <summary>Destroys retired textures whose deferral has elapsed. Main thread only.</summary>
        internal static void ProcessDeferredTextureDestroys()
        {
            int count = DeferredDestroys.Count;
            for (int i = 0; i < count; i++)
            {
                if (!DeferredDestroys.TryDequeue(out var pending))
                    return;

                pending.FramesRemaining--;
                if (pending.FramesRemaining > 0)
                {
                    DeferredDestroys.Enqueue(pending);
                    continue;
                }

                try { UnityEngine.Object.Destroy(pending.Texture); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogWarning($"[LinuxCapture] Deferred texture destroy failed: {ex.Message}"); }
            }
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
