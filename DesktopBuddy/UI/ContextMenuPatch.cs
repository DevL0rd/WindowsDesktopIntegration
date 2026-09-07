using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using FrooxEngine;
using Elements.Core;
using Elements.Assets;

namespace DesktopBuddy;

public static class ContextMenuPatch
{
    private const int PAGE_SIZE = 8;
    private const string DesktopIconFileName = "icon_transparent.png";
    private const string PlusIconFileName = "plus.png";

    private static readonly ConcurrentDictionary<IntPtr, Uri> _iconCache = new();
    private static readonly ConcurrentDictionary<IntPtr, byte> _iconCacheRequests = new();

    private static Uri _desktopIconUri;
    private static bool _desktopIconLoaded;

    private static readonly string[] IgnoredSubstrings = { "vrmonitor", "SteamVR Status", "rainmeter" };

    private enum MenuOptions
    {
        Default,
        Locomotion,
        Grabbing,
        LaserGrab,
        HandGrab
    }

    private static bool ShouldIgnore(string title)
    {
        if (WindowEnumerator.IsResoniteWindow(title)) return true;
        foreach (var sub in IgnoredSubstrings)
            if (title.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly FieldInfo _itemsRootField = typeof(ContextMenu)
        .GetField("_itemsRoot", BindingFlags.NonPublic | BindingFlags.Instance);

    private static void ClearMenu(ContextMenu menu)
    {
        var itemsRoot = _itemsRootField?.GetValue(menu) as SyncRef<Slot>;
        itemsRoot?.Target?.DestroyChildren();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private record MonitorInfo(IntPtr Handle, string Name, int Width, int Height);

    private static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(hMon, ref info);
            int w = info.rcMonitor.Right - info.rcMonitor.Left;
            int h = info.rcMonitor.Bottom - info.rcMonitor.Top;
            monitors.Add(new MonitorInfo(hMon, info.szDevice, w, h));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    internal static StaticTexture2D GetDesktopIconTexture(Engine engine, Slot slot)
    {
        try
        {
            var tex = TextureProviderSettings.ClampWrap(slot.AttachComponent<StaticTexture2D>());

            if (_desktopIconLoaded && _desktopIconUri != null)
            {
                tex.URL.Value = _desktopIconUri;
                DesktopBuddyMod.Msg("[Icon] Using cached desktop icon");
                return tex;
            }

            var iconPath = Path.Combine(Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty, DesktopIconFileName);
            if (!File.Exists(iconPath))
            {
                DesktopBuddyMod.Msg($"[Icon] Desktop icon file not found: {iconPath}");
                return tex;
            }

            var capturedTex = tex;

            Task.Run(async () =>
            {
                try
                {
                    var bitmap = Bitmap2D.Load(iconPath, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                    {
                        _desktopIconUri = uri;
                        _desktopIconLoaded = true;
                        DesktopBuddyMod.Msg($"[Icon] Desktop icon saved: {uri}");
                        capturedTex.World.RunInUpdates(0, () =>
                        {
                            if (!capturedTex.IsDestroyed)
                                capturedTex.URL.Value = uri;
                        });
                    }
                }
                catch (Exception ex)
                {
                    DesktopBuddyMod.Msg($"[Icon] Desktop icon save error: {ex.Message}");
                }
            });
            return tex;
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Icon] Desktop icon error: {ex.Message}");
            return null;
        }
    }

    internal static StaticTexture2D GetIconTexture(IntPtr hwnd, Engine engine, Slot slot)
    {
        try
        {
            if (_iconCache.TryGetValue(hwnd, out var cached))
            {
                var tex = TextureProviderSettings.ClampWrap(slot.AttachComponent<StaticTexture2D>());
                tex.URL.Value = cached;
                return tex;
            }

            var capturedHwnd = hwnd;
            if (!_iconCacheRequests.TryAdd(capturedHwnd, 0))
                return null;

            Task.Run(async () =>
            {
                try
                {
                    var iconData = WindowIconExtractor.GetIconRGBA(capturedHwnd, out int w, out int h);
                    if (iconData == null || w <= 0 || h <= 0)
                        return;

                    var bitmap = new Bitmap2D(iconData, w, h,
                        Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                        _iconCache[capturedHwnd] = uri;
                }
                catch (Exception ex)
                {
                    DesktopBuddyMod.Msg($"[Icon] Save error: {ex.Message}");
                }
            });
            return null;
        }
        catch (Exception ex)
        {
            DesktopBuddyMod.Msg($"[Icon] Error for hwnd={hwnd}: {ex.Message}");
            return null;
        }
    }
    private static void ShowPickerPage(ContextMenu menu, int page)
    {
        if (DesktopBuddyPlatform.IsLinux)
        {
            ShowLinuxPickerPage(menu);
            return;
        }

        DesktopBuddyMod.Msg($"[ContextMenu] ShowPickerPage page={page}");
        ClearMenu(menu);
        var world = menu.World;
        var engine = world.Engine;

        var entries = new List<(string label, colorX color, Action action, IntPtr hwnd)>();

        var monitors = GetMonitors();
        DesktopBuddyMod.Msg($"[ContextMenu] Found {monitors.Count} monitors");
        for (int i = 0; i < monitors.Count; i++)
        {
            var mon = monitors[i];
            int idx = i;
            entries.Add(($"Monitor {idx + 1} ({mon.Width}x{mon.Height})",
                new colorX(0.1f, 0.25f, 0.4f, 1f),
                () => { menu.Close(); DesktopBuddyMod.SpawnStreaming(world, IntPtr.Zero, $"Monitor {idx + 1}", mon.Handle, monitorIndex: idx, startPrivate: DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.NewWindowsStartPrivate) ?? true); },
                IntPtr.Zero));
        }

        var allWindows = WindowEnumerator.GetOpenWindows();
        DesktopBuddyMod.Msg($"[ContextMenu] Found {allWindows.Count} windows");
        foreach (var win in allWindows)
        {
            if (ShouldIgnore(win.Title)) continue;
            var handle = win.Handle;
            var title = win.Title;
            string display = title.Length > 30 ? title[..27] + "..." : title;
            entries.Add((display,
                new colorX(0.15f, 0.15f, 0.25f, 1f),
                () => { menu.Close(); DesktopBuddyMod.SpawnStreaming(world, handle, title, startPrivate: DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.NewWindowsStartPrivate) ?? true); },
                handle));
        }

        int totalPages = (entries.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        int start = page * PAGE_SIZE;
        int end = Math.Min(start + PAGE_SIZE, entries.Count);

        DesktopBuddyMod.Msg($"[ContextMenu] Showing entries {start}-{end} of {entries.Count} (page {page + 1}/{totalPages})");

        for (int i = start; i < end; i++)
        {
            var entry = entries[i];
            LocaleString lbl = entry.label;
            colorX? c = entry.color;
            var act = entry.action;

            StaticTexture2D iconTex = null;
            if (entry.hwnd != IntPtr.Zero)
                iconTex = GetIconTexture(entry.hwnd, engine, menu.Slot);

            ContextMenuItem mi;
            if (iconTex != null)
                mi = menu.AddItem(in lbl, (IAssetProvider<ITexture2D>)iconTex, in c);
            else
                mi = menu.AddItem(in lbl, (Uri)null!, in c);
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => act();
        }

        if (page > 0)
        {
            LocaleString lbl = $"< Prev (Page {page}/{totalPages})";
            colorX? c = new colorX(0.3f, 0.3f, 0.1f, 1f);
            var mi = menu.AddItem(in lbl, (Uri)null!, in c);
            int prev = page - 1;
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => ShowPickerPage(menu, prev);
        }
        if (page < totalPages - 1)
        {
            LocaleString lbl = $"Next > (Page {page + 2}/{totalPages})";
            colorX? c = new colorX(0.3f, 0.3f, 0.1f, 1f);
            var mi = menu.AddItem(in lbl, (Uri)null!, in c);
            int next = page + 1;
            mi.Button.LocalPressed += (IButton b, ButtonEventData d) => ShowPickerPage(menu, next);
        }
    }

    private sealed class LinuxSharedSource
    {
        public string Label;
        public string RestoreToken;
        public bool IsMonitor;
        /// <summary>
        /// Position of the monitor in compositor coordinates. Two monitors of the same size
        /// are indistinguishable by label alone, so this is what gives each saved screen its
        /// own identity rather than collapsing them into one entry.
        /// </summary>
        public int PositionX;
        public int PositionY;
    }

    private static readonly object _linuxSourcesLock = new();
    private static readonly List<LinuxSharedSource> _linuxSources = new();
    private static bool _linuxSourcesLoaded;
    private static readonly ConcurrentDictionary<string, Uri> _fileIconUris = new();

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty));
    private static string UnB64(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return string.Empty; }
    }

    private static void LoadLinuxSourcesOnce()
    {
        if (_linuxSourcesLoaded) return;
        _linuxSourcesLoaded = true;
        try
        {
            string serialized = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.LinuxSharedSources);
            if (string.IsNullOrWhiteSpace(serialized)) return;
            var legacyTokens = new List<string>();
            lock (_linuxSourcesLock)
            {
                _linuxSources.Clear();
                foreach (var line in serialized.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;
                    string token = UnB64(parts[2]);
                    if (string.IsNullOrEmpty(token)) continue;

                    // Entries written before per-screen support hold a RemoteDesktop token,
                    // which a ScreenCast session cannot restore: the portal ignores it and
                    // shows the picker instead, so the dial entry silently stops meaning
                    // anything. Drop those and revoke the grant they left behind. The extra
                    // position fields are what distinguishes the new format.
                    if (parts.Length < 5)
                    {
                        legacyTokens.Add(token);
                        continue;
                    }

                    int px = int.TryParse(parts[3], out int parsedX) ? parsedX : 0;
                    int py = int.TryParse(parts[4], out int parsedY) ? parsedY : 0;
                    _linuxSources.Add(new LinuxSharedSource
                    {
                        IsMonitor = parts[0] == "1",
                        Label = UnB64(parts[1]),
                        RestoreToken = token,
                        PositionX = px,
                        PositionY = py,
                    });
                }
            }
            DesktopBuddyMod.Msg($"[ContextMenu] Loaded {_linuxSources.Count} saved Linux source(s)");

            if (legacyTokens.Count > 0)
            {
                DesktopBuddyMod.Msg($"[ContextMenu] Dropped {legacyTokens.Count} saved source(s) from the pre-per-screen format");
                SaveLinuxSources();
                Task.Run(() =>
                {
                    try
                    {
                        using var bridge = new LinuxNativeBridge();
                        foreach (string legacy in legacyTokens)
                            bridge.PortalRevokeToken("remote-desktop", legacy);
                    }
                    catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Legacy revoke error: {ex.Message}"); }
                });
            }
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Load sources error: {ex.Message}"); }
    }

    private static void SaveLinuxSources()
    {
        try
        {
            var sb = new StringBuilder();
            lock (_linuxSourcesLock)
            {
                foreach (var s in _linuxSources)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s.IsMonitor ? '1' : '0').Append('|')
                      .Append(B64(s.Label)).Append('|')
                      .Append(B64(s.RestoreToken)).Append('|')
                      .Append(s.PositionX).Append('|')
                      .Append(s.PositionY);
                }
            }
            DesktopBuddyMod.Config?.Set(DesktopBuddyMod.LinuxSharedSources, sb.ToString());
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Save sources error: {ex.Message}"); }
    }

    private static StaticTexture2D GetFileIconTexture(Engine engine, Slot slot, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var tex = TextureProviderSettings.ClampWrap(slot.AttachComponent<StaticTexture2D>());
            if (_fileIconUris.TryGetValue(path, out var cached))
            {
                tex.URL.Value = cached;
                return tex;
            }

            var capturedTex = tex;
            Task.Run(async () =>
            {
                try
                {
                    var bitmap = Bitmap2D.Load(path, false);
                    var uri = await engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                    if (uri != null)
                    {
                        _fileIconUris[path] = uri;
                        capturedTex.World.RunInUpdates(0, () =>
                        {
                            if (!capturedTex.IsDestroyed)
                                capturedTex.URL.Value = uri;
                        });
                    }
                }
                catch (Exception ex) { DesktopBuddyMod.Msg($"[Icon] App icon load error: {ex.Message}"); }
            });
            return tex;
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[Icon] App icon error: {ex.Message}"); return null; }
    }

    private static void ShowLinuxPickerPage(ContextMenu menu)
    {
        DesktopBuddyMod.Msg("[ContextMenu] Showing Linux source list");
        LoadLinuxSourcesOnce();
        ClearMenu(menu);

        var engine = menu.World.Engine;

        LocaleString addLabel = "Share a desktop";
        colorX? addColor = new colorX(0.1f, 0.35f, 0.35f, 1f);
        string plusPath = Path.Combine(Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? string.Empty, PlusIconFileName);
        StaticTexture2D addIcon = GetFileIconTexture(engine, menu.Slot, plusPath);
        var add = addIcon != null
            ? menu.AddItem(in addLabel, (IAssetProvider<ITexture2D>)addIcon, in addColor)
            : menu.AddItem(in addLabel, (Uri)null!, in addColor);
        add.Button.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            menu.Close();
            OpenLinuxPortalPickerThenSpawn(menu.World, null);
        };

        List<LinuxSharedSource> sources;
        lock (_linuxSourcesLock)
            sources = new List<LinuxSharedSource>(_linuxSources);

        foreach (var source in sources)
        {
            LocaleString lbl = source.Label;
            colorX? c = new colorX(0.1f, 0.25f, 0.4f, 1f);

            StaticTexture2D iconTex = GetDesktopIconTexture(engine, menu.Slot);

            ContextMenuItem item = iconTex != null
                ? menu.AddItem(in lbl, (IAssetProvider<ITexture2D>)iconTex, in c)
                : menu.AddItem(in lbl, (Uri)null!, in c);

            var captured = source;
            item.Button.LocalPressed += (IButton b, ButtonEventData d) =>
            {
                menu.Close();
                OpenLinuxPortalPickerThenSpawn(menu.World, captured);
            };
        }

        if (sources.Count == 0)
            return;

        LocaleString clearLabel = "Clear saved screens";
        colorX? clearColor = new colorX(0.45f, 0.15f, 0.15f, 1f);
        var clear = menu.AddItem(in clearLabel, (Uri)null!, in clearColor);
        clear.Button.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            ClearLinuxSources();
            // Redraw in place rather than closing, so the list is visibly empty.
            ShowLinuxPickerPage(menu);
        };
    }

    /// <summary>
    /// Forgets every saved source and revokes the portal grants behind them, so clearing the
    /// list also stops those grants lingering in the desktop's remembered permissions.
    /// </summary>
    private static void ClearLinuxSources()
    {
        List<string> tokens;
        lock (_linuxSourcesLock)
        {
            tokens = new List<string>();
            foreach (var s in _linuxSources)
                if (!string.IsNullOrEmpty(s.RestoreToken)) tokens.Add(s.RestoreToken);
            _linuxSources.Clear();
        }

        SaveLinuxSources();
        DesktopBuddyMod.Msg($"[ContextMenu] Cleared {tokens.Count} saved Linux source(s)");

        if (tokens.Count == 0) return;

        Task.Run(() =>
        {
            try
            {
                using var bridge = new LinuxNativeBridge();
                int revoked = 0;
                foreach (string token in tokens)
                    if (bridge.PortalRevokeToken("screencast", token)) revoked++;
                DesktopBuddyMod.Msg($"[ContextMenu] Revoked {revoked}/{tokens.Count} cleared portal grant(s)");
            }
            catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Clear revoke error: {ex.Message}"); }
        });
    }

    private static void OpenLinuxPortalPickerThenSpawn(World world, LinuxSharedSource reshare)
    {
        string tokenIn = reshare?.RestoreToken;
        DesktopBuddyMod.Msg(tokenIn == null
            ? "[ContextMenu] Opening Linux portal picker"
            : "[ContextMenu] Re-sharing saved Linux source (no dialog)");
        Task.Run(() =>
        {
            try
            {
                using var bridge = new LinuxNativeBridge();

                // Input first: one shared RemoteDesktop session serves every panel, and it is
                // what makes absolute pointer injection possible at all.
                ulong inputSession = LinuxInputSessionManager.Ensure(out int workspaceW, out int workspaceH);

                // Capture is a separate ScreenCast session so the picker offers each monitor
                // individually instead of one whole-workspace entry.
                ulong captureSession = bridge.ScreencastStart(tokenIn, out var selection, out var newToken, out var isMonitor);

                // A restore token that no longer resolves stays broken forever, so drop it and
                // fall back to the picker instead of leaving a dead entry on the dial.
                void DropStaleSourceAndReopenPicker()
                {
                    if (reshare == null) return;
                    ForgetLinuxSource(reshare);
                    DesktopBuddyMod.Msg("[ContextMenu] Dropped stale saved Linux source; reopening portal picker");
                    OpenLinuxPortalPickerThenSpawn(world, null);
                }

                if (captureSession == 0 || selection.NodeId == 0)
                {
                    string err = bridge.GetInputLastError();
                    DesktopBuddyMod.Msg($"[ContextMenu] Linux capture session failed captureSession={captureSession} node={selection.NodeId} error={err ?? "(none)"}");
                    DropStaleSourceAndReopenPicker();
                    return;
                }

                // A stale token can also resolve to a session and still hand back a node that is
                // already gone. That case used to reach the renderer, which faults inside PipeWire
                // with no managed exception to catch and takes the session down with it, so the
                // node is confirmed here while falling back to the picker is still possible.
                if (!WaitForNode(bridge, selection.NodeId))
                {
                    DesktopBuddyMod.Msg($"[ContextMenu] Linux capture node={selection.NodeId} never appeared in PipeWire; treating source as stale");
                    bridge.ScreencastStop(captureSession);
                    DropStaleSourceAndReopenPicker();
                    return;
                }

                int width = selection.Width > 0 ? checked((int)selection.Width) : 1280;
                int height = selection.Height > 0 ? checked((int)selection.Height) : 720;

                DesktopBuddyMod.Msg($"[ContextMenu] Linux capture session={captureSession} input session={inputSession} node={selection.NodeId}");

                string title = RememberLinuxSource(reshare, newToken, isMonitor, width, height,
                    selection.PositionX, selection.PositionY);
                LinuxPortalSelectionStore.Set(new LinuxPortalSelection(
                    selection.NodeId, width, height, inputSession, captureSession,
                    selection.PositionX, selection.PositionY, workspaceW, workspaceH));
                DesktopBuddyMod.Msg($"[ContextMenu] Linux portal selected node={selection.NodeId} size={width}x{height} " +
                    $"pos=({selection.PositionX},{selection.PositionY}) workspace={workspaceW}x{workspaceH} monitor={isMonitor} token={(newToken != null ? "yes" : "no")}");
                world.RunInUpdates(0, () => DesktopBuddyMod.SpawnStreaming(world, IntPtr.Zero, title, startPrivate: DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.NewWindowsStartPrivate) ?? false));
            }
            catch (Exception ex)
            {
                DesktopBuddyMod.Msg($"[ContextMenu] Linux portal picker error: {ex}");
            }
        });
    }

    /// <summary>
    /// Confirms a PipeWire node is live, tolerating the short gap between the portal handing back
    /// a session and its stream node reaching the registry. A single miss is not evidence of a
    /// dead source; only a node still absent at the end of the window is treated as one.
    /// </summary>
    private static bool WaitForNode(LinuxNativeBridge bridge, uint nodeId)
    {
        const int Attempts = 10;
        const int DelayMs = 50;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (bridge.NodeExists(nodeId)) return true;
            Thread.Sleep(DelayMs);
        }
        return false;
    }

    private static void ForgetLinuxSource(LinuxSharedSource source)
    {
        if (source == null) return;
        string token;
        lock (_linuxSourcesLock)
        {
            token = source.RestoreToken;
            _linuxSources.Remove(source);
        }
        SaveLinuxSources();
        RevokeLinuxToken(token);
    }

    /// <summary>
    /// Drops a portal grant we no longer reference. Without this the grant persists in the
    /// desktop's remembered-permissions list indefinitely, one entry per share.
    /// </summary>
    private static void RevokeLinuxToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            using var bridge = new LinuxNativeBridge();
            // Saved sources are ScreenCast grants now that capture has its own session, so
            // they live in the "screencast" table rather than "remote-desktop".
            if (bridge.PortalRevokeToken("screencast", token))
                DesktopBuddyMod.Msg("[ContextMenu] Revoked superseded Linux portal grant");
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Revoke token error: {ex.Message}"); }
    }

    /// <summary>
    /// Builds the dial label for a captured source. Screens are commonly identical in size,
    /// so dimensions alone cannot tell two of them apart; the compositor-space position is the
    /// only stable discriminator the portal gives us, and it is appended once more than one
    /// screen is in play.
    /// </summary>
    private static string BuildLinuxSourceLabel(bool isMonitor, int width, int height, int posX, int posY)
    {
        if (!isMonitor)
            return $"Window ({width}×{height})";

        // Prefer the compositor's connector name; fall back to position, which is the only
        // other thing that tells two identically sized screens apart.
        string name = null;
        try
        {
            using var bridge = new LinuxNativeBridge();
            name = bridge.KWinOutputName(posX, posY, width, height);
        }
        catch (Exception ex) { DesktopBuddyMod.Msg($"[ContextMenu] Output name lookup failed: {ex.Message}"); }

        if (!string.IsNullOrEmpty(name))
            return $"Screen ({name}; {width}×{height}) @ {posX},{posY}";

        return $"Screen ({width}×{height}) @ {posX},{posY}";
    }

    private static string RememberLinuxSource(LinuxSharedSource existing, string token, bool isMonitor,
        int width, int height, int posX = 0, int posY = 0)
    {
        string label = BuildLinuxSourceLabel(isMonitor, width, height, posX, posY);

        if (string.IsNullOrEmpty(token))
            return existing?.Label ?? label;

        LinuxSharedSource entry;
        string supersededToken = null;
        lock (_linuxSourcesLock)
        {
            entry = (existing != null && _linuxSources.Contains(existing)) ? existing : null;
            if (entry == null)
            {
                // Match on the source's actual identity, not its label. Two 1920x1200 screens
                // share a label under the old scheme and would collapse into one entry,
                // revoking each other's grant.
                foreach (var s in _linuxSources)
                    if (s.RestoreToken == token ||
                        (s.IsMonitor == isMonitor && s.PositionX == posX && s.PositionY == posY && s.Label == label))
                    { entry = s; break; }
            }

            if (entry != null)
            {
                // The portal mints a fresh grant per start, so the token we are replacing is
                // now unreferenced and has to be revoked rather than simply overwritten.
                if (!string.IsNullOrEmpty(entry.RestoreToken) && entry.RestoreToken != token)
                    supersededToken = entry.RestoreToken;
                entry.RestoreToken = token;
                entry.Label = label;
                entry.IsMonitor = isMonitor;
                entry.PositionX = posX;
                entry.PositionY = posY;
            }
            else
            {
                entry = new LinuxSharedSource
                {
                    Label = label,
                    RestoreToken = token,
                    IsMonitor = isMonitor,
                    PositionX = posX,
                    PositionY = posY,
                };
                _linuxSources.Add(entry);
            }
        }

        SaveLinuxSources();
        RevokeLinuxToken(supersededToken);

        return label;
    }

    [HarmonyPatch(typeof(InteractionHandler), "OpenContextMenu")]
    private class ContextMenuOpenMenuPatch
    {
        public static void Postfix(InteractionHandler __instance, MenuOptions options)
        {
            try
            {
                if (__instance == null || !__instance.IsOwnedByLocalUser)
                    return;

                ContextMenu ctx = __instance.ContextMenu;
                if (ctx == null)
                    return;

                if (options == MenuOptions.Default)
                {
                    if (DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.ShowContextMenuItem) == false)
                        return;

                    DesktopBuddyMod.Msg("[ContextMenu] Postfix fired, adding Desktop item");
                    LocaleString label = "Desktop";
                    colorX? color = colorX.Cyan;

                    var engine = __instance.World.Engine;
                    var iconTex = GetDesktopIconTexture(engine, __instance.Slot);

                    ContextMenuItem item;
                    if (iconTex != null)
                        item = ctx.AddItem(in label, (IAssetProvider<ITexture2D>)iconTex, in color);
                    else
                        item = ctx.AddItem(in label, (Uri)null!, in color);

                    item.Button.LocalPressed += (IButton btn, ButtonEventData data) =>
                    {
                        try
                        {
                            DesktopBuddyMod.Msg("[ContextMenu] Desktop item LocalPressed entered");
                            if (DesktopBuddyMod.ShowSetupNoticeFromDesktopClick(__instance.World))
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Setup notice shown from Desktop item");
                                ctx.Close();
                                return;
                            }

                            if (DesktopBuddyPlatform.IsLinux)
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Linux Desktop item pressed, showing source list");
                                ShowLinuxPickerPage(ctx);
                            }
                            else
                            {
                                DesktopBuddyMod.Msg("[ContextMenu] Desktop item pressed, showing picker");
                                ShowPickerPage(ctx, 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            DesktopBuddyMod.Msg($"[ContextMenu] Desktop item pressed error: {ex}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                DesktopBuddyMod.Msg($"[ContextMenu] Postfix error: {ex}");
            }
        }
    }
}
