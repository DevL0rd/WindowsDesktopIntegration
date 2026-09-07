using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using DesktopBuddy.Shared;
using InterprocessLib;
using Renderite.Unity;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class SharedTextureBridge : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private Messenger _messenger;
        private float _connectRetryTimer;
        private float _connectLogTimer;
        private const float ConnectRetryInterval = 1f;
        private const float ConnectLogInterval = 5f;

        private readonly Dictionary<int, IBridgeTextureSlot> _activeSlots = new Dictionary<int, IBridgeTextureSlot>();
        private readonly Dictionary<int, int> _activeGenerations = new Dictionary<int, int>();
        private static readonly ConcurrentDictionary<int, IDisplayTextureSource> _bridgeIndexToSlot = new ConcurrentDictionary<int, IDisplayTextureSource>();
        private readonly List<(int slot, int generation, IBridgeTextureSlot textureSlot)> _pendingBinds = new List<(int, int, IBridgeTextureSlot)>();
        private bool _rendererDevicePublished;

        internal SharedTextureBridge(ManualLogSource log)
        {
            _log = log;
            SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Constructed");
        }

        internal static IDisplayTextureSource GetSlotForBridgeIndex(int bridgeIndex)
        {
            _bridgeIndexToSlot.TryGetValue(bridgeIndex, out var textureSlot);
            return textureSlot;
        }

        internal int ActiveSlotCount => _activeSlots.Count;
        internal int PendingBindCount => _pendingBinds.Count;
        internal int TotalTextureRequestCount
        {
            get
            {
                int total = 0;
                foreach (var slot in _activeSlots.Values)
                    total += slot.RequestCount;
                return total;
            }
        }

        internal void Update()
        {
            SharedTextureSlot.ProcessDeferredNativeReleases();
            LinuxCaptureTextureSlot.ProcessDeferredTextureDestroys();
            TryEnsureMessenger();
            TryPublishRendererDevice();

            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogError("IPC action failed", ex); }
            }

            foreach (var slot in _activeSlots.Values)
            {
                try { slot.Tick(); }
                catch (Exception ex) { SharedTextureBridgePlugin.LogError("Texture slot tick failed", ex); }
            }

            for (int i = _pendingBinds.Count - 1; i >= 0; i--)
            {
                var (slot, generation, textureSlot) = _pendingBinds[i];
                if (!_activeGenerations.TryGetValue(slot, out int activeGeneration) || activeGeneration != generation)
                {
                    _pendingBinds.RemoveAt(i);
                    continue;
                }
                bool bound;
                try
                {
                    bound = textureSlot.TryBind();
                }
                catch (Exception ex)
                {
                    SharedTextureBridgePlugin.LogError($"Pending TryBind threw slot={slot}", ex);
                    continue;
                }
                if (!bound) continue;
                _pendingBinds.RemoveAt(i);
                WriteRunning(slot, generation, textureSlot);
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Slot {slot} gen={generation} bound: {textureSlot.Width}x{textureSlot.Height}");
            }
        }

        private void TryPublishRendererDevice()
        {
            if (_rendererDevicePublished || _messenger == null)
                return;

            try
            {
                if (!UnityD3D11Device.Initialize(_log) || !UnityD3D11Device.HasAdapterInfo)
                    return;

                _messenger.SendObject(SharedTextureBridgeProtocol.RendererDeviceMessageId, new SharedTextureRendererDeviceMessage
                {
                    AdapterLuid = UnityD3D11Device.AdapterLuid,
                    VendorId = UnityD3D11Device.AdapterVendorId,
                    Description = UnityD3D11Device.AdapterDescription
                });
                _rendererDevicePublished = true;
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Published renderer adapter '{UnityD3D11Device.AdapterDescription}' VendorId=0x{UnityD3D11Device.AdapterVendorId:X4} LUID=0x{UnityD3D11Device.AdapterLuid:X16}");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogWarning($"[SharedTextureBridge] Failed to publish renderer adapter: {ex.Message}");
            }
        }

        private void TryEnsureMessenger()
        {
            if (_messenger != null) return;

            _connectRetryTimer += Time.unscaledDeltaTime;
            _connectLogTimer += Time.unscaledDeltaTime;
            if (_connectLogTimer >= ConnectLogInterval)
            {
                _connectLogTimer = 0f;
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Waiting for InterprocessLib queue {SharedTextureBridgeProtocol.QueueName}");
            }
            if (_connectRetryTimer < ConnectRetryInterval) return;
            _connectRetryTimer = 0f;

            try
            {
                SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Creating Messenger");
                Messenger.OnWarning += OnWarning;
                Messenger.OnFailure += OnFailure;

                _messenger = new Messenger(
                    SharedTextureBridgeProtocol.OwnerId,
                    false,
                    SharedTextureBridgeProtocol.QueueName,
                    SimpleMemoryPackerPool.Instance);

                RegisterMessages();

                SharedTextureBridgePlugin.LogInfo($"Opened InterprocessLib queue: {SharedTextureBridgeProtocol.QueueName}");
            }
            catch (Exception ex)
            {
                Messenger.OnWarning -= OnWarning;
                Messenger.OnFailure -= OnFailure;
                _messenger?.Dispose();
                _messenger = null;
                SharedTextureBridgePlugin.LogWarning($"InterprocessLib queue not ready: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void RegisterMessages()
        {

            _messenger.ReceiveObject<SharedTextureStartMessage>(
                SharedTextureBridgeProtocol.StartMessageId,
                msg =>
                {
                    try
                    {
                        if (msg == null) return;
                        SharedTextureBridgePlugin.LogInfo(
                            $"[SharedTextureBridge] Received Start slot={msg.SlotId} gen={msg.Generation} name={msg.SharedTextureName} shared=0x{msg.SharedTextureHandle:X} {msg.SharedTextureWidth}x{msg.SharedTextureHeight}");
                        _mainThreadActions.Enqueue(() => StartSharedTexture(
                            msg.SlotId,
                            msg.Generation,
                            msg.SharedTextureHandle,
                            msg.SharedTextureName,
                            msg.SharedTextureWidth,
                            msg.SharedTextureHeight));
                    }
                    catch (Exception ex)
                    {
                        SharedTextureBridgePlugin.LogError("[SharedTextureBridge] Start callback failed", ex);
                    }
                });

            _messenger.ReceiveObject<SharedTextureStopMessage>(
                SharedTextureBridgeProtocol.StopMessageId,
                msg =>
                {
                    try
                    {
                        if (msg == null) return;
                        SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Received Stop slot={msg.SlotId} gen={msg.Generation}");
                        _mainThreadActions.Enqueue(() => StopSharedTexture(msg.SlotId, msg.Generation, true));
                    }
                    catch (Exception ex)
                    {
                        SharedTextureBridgePlugin.LogError("[SharedTextureBridge] Stop callback failed", ex);
                    }
                });

            _messenger.ReceiveObject<SharedTextureRunningMessage>(
                SharedTextureBridgeProtocol.RunningMessageId,
                _ => { });
            _messenger.ReceiveObject<SharedTextureStoppedMessage>(
                SharedTextureBridgeProtocol.StoppedMessageId,
                _ => { });
            _messenger.ReceiveObject<SharedTextureRendererDeviceMessage>(
                SharedTextureBridgeProtocol.RendererDeviceMessageId,
                _ => { });
        }

        private void StartSharedTexture(int slot, int generation, long sharedTextureHandleRaw, string sharedTextureName, int sharedTextureWidth, int sharedTextureHeight)
        {
            if (sharedTextureHandleRaw == -1 && TryParseLinuxCaptureName(sharedTextureName, out uint pipeWireNodeId))
            {
                StartLinuxCapture(slot, generation, pipeWireNodeId, sharedTextureWidth, sharedTextureHeight);
                return;
            }

            if (_activeSlots.ContainsKey(slot))
                StopSharedTexture(slot, _activeGenerations.TryGetValue(slot, out int oldGeneration) ? oldGeneration : 0, false);

            var sharedTextureHandle = new IntPtr(sharedTextureHandleRaw);

            SharedTextureBridgePlugin.LogInfo($"Starting shared texture slot={slot} gen={generation} name={sharedTextureName} shared=0x{sharedTextureHandleRaw:X} {sharedTextureWidth}x{sharedTextureHeight}");
            if (sharedTextureHandle == IntPtr.Zero || sharedTextureWidth <= 0 || sharedTextureHeight <= 0)
            {
                SharedTextureBridgePlugin.LogWarning($"Shared texture start ignored slot={slot} gen={generation}: missing handle or size");
                return;
            }

            SharedTextureSlot textureSlot;
            try
            {
                textureSlot = new SharedTextureSlot(sharedTextureHandle, sharedTextureWidth, sharedTextureHeight, _log);
                _activeSlots[slot] = textureSlot;
                _activeGenerations[slot] = generation;
                _bridgeIndexToSlot[SharedTextureBridgeProtocol.MagicIndexBase + slot] = textureSlot;
                SharedTextureBridgePlugin.LogInfo($"Registered bridge index={SharedTextureBridgeProtocol.MagicIndexBase + slot} slot={slot} gen={generation}");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"Shared texture slot construction failed slot={slot} gen={generation}", ex);
                return;
            }

            bool bound;
            try
            {
                bound = textureSlot.TryBind();
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"Initial TryBind threw slot={slot} gen={generation}", ex);
                return;
            }

            if (bound)
            {
                WriteRunning(slot, generation, textureSlot);
            }
            else
            {
                SharedTextureBridgePlugin.LogWarning($"Initial TryBind failed slot={slot} gen={generation}; adding pending bind");
                _pendingBinds.Add((slot, generation, textureSlot));
            }
        }

        private void StopSharedTexture(int slot, int generation, bool sendStopped)
        {
            if (!_activeSlots.ContainsKey(slot))
            {
                if (sendStopped)
                    WriteStopped(slot, generation);
                return;
            }
            if (_activeGenerations.TryGetValue(slot, out int activeGeneration) && activeGeneration != generation)
            {
                SharedTextureBridgePlugin.LogWarning($"Ignoring stale shared texture stop slot={slot} gen={generation} active={activeGeneration}");
                return;
            }

            SharedTextureBridgePlugin.LogInfo($"Stopping shared texture slot={slot} gen={generation}");
            _bridgeIndexToSlot.TryRemove(SharedTextureBridgeProtocol.MagicIndexBase + slot, out _);
            SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Removed bridge index for slot={slot}");
            try
            {
                _activeSlots[slot].Dispose();
                SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Slot dispose completed slot={slot}");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"[SharedTextureBridge] Slot dispose failed slot={slot}", ex);
            }
            _activeSlots.Remove(slot);
            _activeGenerations.Remove(slot);
            _pendingBinds.RemoveAll(p => p.slot == slot);
            if (sendStopped)
                WriteStopped(slot, generation);
            SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Stop complete slot={slot}");
        }

        private static bool TryParseLinuxCaptureName(string name, out uint pipeWireNodeId)
        {
            pipeWireNodeId = 0;
            const string prefix = "DesktopBuddyLinuxCapture:";
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            return uint.TryParse(name.Substring(prefix.Length), out pipeWireNodeId) && pipeWireNodeId != 0;
        }

        private void StartLinuxCapture(int slot, int generation, uint pipeWireNodeId, int widthHint, int heightHint)
        {
            StopSharedTexture(slot, _activeGenerations.TryGetValue(slot, out int oldGeneration) ? oldGeneration : 0, false);

            try
            {
                var textureSlot = new LinuxCaptureTextureSlot(pipeWireNodeId, widthHint, heightHint, _log);
                _activeSlots[slot] = textureSlot;
                _activeGenerations[slot] = generation;
                _bridgeIndexToSlot[SharedTextureBridgeProtocol.MagicIndexBase + slot] = textureSlot;
                if (textureSlot.TryBind())
                {
                    WriteRunning(slot, generation, textureSlot);
                    SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Linux capture slot={slot} gen={generation} node={pipeWireNodeId} running {textureSlot.Width}x{textureSlot.Height}");
                }
                else
                {
                    _pendingBinds.Add((slot, generation, textureSlot));
                    SharedTextureBridgePlugin.LogWarning($"[SharedTextureBridge] Linux capture slot={slot} node={pipeWireNodeId} pending bind");
                }
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError($"[SharedTextureBridge] Linux capture start failed slot={slot} gen={generation}", ex);
            }
        }

        private void WriteRunning(int slot, int generation, IBridgeTextureSlot textureSlot)
        {
            try
            {
                _messenger?.SendObject(SharedTextureBridgeProtocol.RunningMessageId, new SharedTextureRunningMessage
                {
                    SlotId = slot,
                    Generation = generation,
                    Width = textureSlot.Width,
                    Height = textureSlot.Height
                });
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogWarning($"Failed to send running ack for slot {slot}: {ex.Message}");
            }

            SharedTextureBridgePlugin.LogInfo($"Shared texture slot={slot} gen={generation} running: {textureSlot.Width}x{textureSlot.Height}");
        }

        private void WriteStopped(int slot, int generation)
        {
            try
            {
                _messenger?.SendObject(SharedTextureBridgeProtocol.StoppedMessageId, new SharedTextureStoppedMessage
                {
                    SlotId = slot,
                    Generation = generation
                });
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogWarning($"Failed to send stopped ack for slot {slot} gen={generation}: {ex.Message}");
            }

            SharedTextureBridgePlugin.LogInfo($"Shared texture slot={slot} gen={generation} stopped");
        }

        private void OnWarning(string message)
        {
            SharedTextureBridgePlugin.LogWarning($"[InterprocessLib] {message}");
        }

        private void OnFailure(Exception ex)
        {
            SharedTextureBridgePlugin.LogError("[InterprocessLib]", ex);
        }

        public void Dispose()
        {
            SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Disposing");
            foreach (var kv in _activeSlots)
            {
                try
                {
                    SharedTextureBridgePlugin.LogInfo($"[SharedTextureBridge] Disposing slot={kv.Key}");
                    kv.Value.Dispose();
                }
                catch (Exception ex)
                {
                    SharedTextureBridgePlugin.LogError($"[SharedTextureBridge] Slot dispose failed during bridge dispose slot={kv.Key}", ex);
                }
            }
            _activeSlots.Clear();
            _activeGenerations.Clear();
            _bridgeIndexToSlot.Clear();
            _pendingBinds.Clear();
            try
            {
                SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Messenger.Dispose START");
                _messenger?.Dispose();
                SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Messenger.Dispose DONE");
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogError("[SharedTextureBridge] Messenger.Dispose failed", ex);
            }
            _messenger = null;
            Messenger.OnWarning -= OnWarning;
            Messenger.OnFailure -= OnFailure;
            SharedTextureBridgePlugin.LogInfo("[SharedTextureBridge] Disposed");
        }
    }
}
