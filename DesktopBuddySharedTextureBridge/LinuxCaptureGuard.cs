using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;

namespace DesktopBuddySharedTextureBridge
{
    /// <summary>
    /// Crash-loop breaker for the Linux native capture start.
    ///
    /// Starting a PipeWire capture runs unmanaged code inside the Wine renderer. If that
    /// call faults, the renderer dies outright and FrooxEngine follows it down with a
    /// FORCE CRASH, so there is no managed exception to catch and no way to recover in
    /// process. Instead we record that a start was in flight, on disk, before making the
    /// call. A start that never completes leaves the marker behind, and after enough
    /// consecutive losses we stop attempting the native path at all: the desktop panel
    /// stays blank, but the session survives.
    ///
    /// The marker also carries the stage the start had reached. Unity buffers its log, so an
    /// abort can swallow the last lines written before it; a file write is a side effect that
    /// survives, which is what makes the stage trustworthy after the fact.
    /// </summary>
    internal static class LinuxCaptureGuard
    {
        private const int DisableThreshold = 2;
        private const string GuardFileName = "linux-capture.guard";

        /// <summary>Loading the native library, which pulls PipeWire into the Wine process.</summary>
        internal const string StageLoad = "load";

        /// <summary>Connecting the capture to its PipeWire node.</summary>
        internal const string StageConnect = "connect";

        private static ManualLogSource _log;
        private static string _path;
        private static int _failures;
        private static string _lastStage;

        /// <summary>True when repeated crashes have taken the native capture path out of service.</summary>
        internal static bool NativeCaptureDisabled { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;

            try
            {
                string dir = Path.GetDirectoryName(typeof(LinuxCaptureGuard).Assembly.Location);
                if (string.IsNullOrEmpty(dir))
                    return;

                _path = Path.Combine(dir, GuardFileName);
                _failures = ReadMarker(out _lastStage);

                if (_failures <= 0)
                    return;

                string stage = _lastStage != null ? $" while it was in the '{_lastStage}' stage" : string.Empty;

                if (_failures >= DisableThreshold)
                {
                    NativeCaptureDisabled = true;
                    _log?.LogError(
                        $"[LinuxCapture] Native capture disabled: the renderer died during capture start {_failures} times in a row{stage}. " +
                        $"Desktop panels will stay blank this session. Delete '{_path}' to re-enable.");
                }
                else
                {
                    _log?.LogWarning(
                        $"[LinuxCapture] Previous renderer session died during capture start{stage} (strike {_failures} of {DisableThreshold}); retrying.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Guard init failed, continuing unguarded: {ex.Message}");
            }
        }

        /// <summary>Records that a native start is about to run. Returns false if the path is out of service.</summary>
        internal static bool BeginNativeStart(uint nodeId)
        {
            if (NativeCaptureDisabled)
            {
                _log?.LogError($"[LinuxCapture] Refusing native capture start for node={nodeId}: path disabled after repeated crashes.");
                return false;
            }

            WriteMarker(_failures + 1, StageLoad);
            return true;
        }

        /// <summary>
        /// Updates the in-flight marker to name the stage now running, so a renderer that dies
        /// here is attributed to that stage rather than to the start as a whole.
        /// </summary>
        internal static void MarkStage(string stage)
        {
            WriteMarker(_failures + 1, stage);
        }

        /// <summary>Clears the in-flight marker once the native call has returned, however it returned.</summary>
        internal static void EndNativeStart()
        {
            // Surviving the call is what matters here, not the status code it produced:
            // a clean failure is reported through normal channels and is not a crash.
            _failures = 0;
            try
            {
                if (_path != null && File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Could not clear guard marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the marker, which holds a failure count optionally followed by ':' and the stage
        /// that was in flight. Markers written before the stage was recorded are still just a
        /// count, so the stage is reported as unknown rather than treated as a parse failure.
        /// </summary>
        private static int ReadMarker(out string stage)
        {
            stage = null;
            try
            {
                if (_path == null || !File.Exists(_path))
                    return 0;

                string text = File.ReadAllText(_path).Trim();
                int separator = text.IndexOf(':');
                if (separator >= 0)
                {
                    string suffix = text.Substring(separator + 1).Trim();
                    if (suffix.Length > 0)
                        stage = suffix;
                    text = text.Substring(0, separator);
                }

                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0
                    ? count
                    : 1;
            }
            catch
            {
                stage = null;
                return 0;
            }
        }

        private static void WriteMarker(int count, string stage)
        {
            try
            {
                if (_path == null)
                    return;

                File.WriteAllText(_path, $"{count.ToString(CultureInfo.InvariantCulture)}:{stage}");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[LinuxCapture] Could not write guard marker: {ex.Message}");
            }
        }
    }
}
