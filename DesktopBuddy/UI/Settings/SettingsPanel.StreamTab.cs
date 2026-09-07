using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{

    private static void BuildStreamTab(UIBuilder ui, SettingsPanelState state, DesktopSession session)
    {
        AddSectionHeader(ui, "Stream");
        int currentResolution = NormalizeStreamResolution(RuntimeMaxStreamResolution);
        int currentFps = NormalizeStreamFps(RuntimeStreamFps);
        AddOptionRow(ui, state, "Resolution", currentResolution.ToString(CultureInfo.InvariantCulture),
            StreamResolutionOptions.Select(option => (option.Value.ToString(CultureInfo.InvariantCulture), option.Label)).ToArray(),
            value =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected))
                    return;
                selected = NormalizeStreamResolution(selected);
                SaveConfigValue(MaxStreamResolution, selected);
                SaveConfigValue(Bitrate, RecommendedBitrateMbps(selected, currentFps));
                RequestStreamEncoderRestart(session, "stream resolution");
            }, preferredColumns: 4, cellWidth: 108f);
        AddOptionRow(ui, state, "FPS", currentFps.ToString(CultureInfo.InvariantCulture),
            StreamFpsOptions.Select(option => (option.Value.ToString(CultureInfo.InvariantCulture), option.Label)).ToArray(),
            value =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected))
                    return;
                selected = NormalizeStreamFps(selected);
                SaveConfigValue(StreamFps, selected);
                SaveConfigValue(Bitrate, RecommendedBitrateMbps(currentResolution, selected));
                RequestStreamEncoderRestart(session, "stream FPS");
            }, preferredColumns: 4, cellWidth: 108f);

        int currentBitrate = Math.Clamp(RuntimeBitrateMbps, 1, 200);
        AddFloatSlider(ui, state, "Bitrate Mbps", currentBitrate, 4f, 80f,
            value =>
            {
                SaveConfigValue(Bitrate, Math.Clamp((int)MathF.Round(value), 1, 200));
                RequestStreamEncoderRestart(session, "stream bitrate");
            }, commitOnReleaseOnly: true, wholeNumbers: true);

        AddSectionHeader(ui, "Encoder");
        AddOptionRow(ui, state, "Preference", RuntimeEncoderPreference,
            new[]
            {
                ("auto", "Auto"), ("hevc_nvenc", "HEVC NVENC"), ("h264_nvenc", "H264 NVENC"),
                ("hevc_amf", "HEVC AMF"), ("h264_amf", "H264 AMF"), ("hevc_qsv", "HEVC QSV"),
                ("h264_qsv", "H264 QSV"), ("libx264", "libx264"), ("libx265", "libx265")
            },
            value =>
            {
                SaveConfigValue(EncoderPreference, value);
                RequestStreamEncoderRestart(session, "encoder preference");
            });

        // Preferred GPU is selected by DXGI adapter LUID, which only exists on Windows.
        if (DesktopBuddyPlatform.IsLinux)
            return;

        string currentLuid = Config?.GetValue(PreferredGpuLuid)?.Trim() ?? "";
        var gpus = WgcCapture.EnumerateAdapters()
            .Where(g => !g.IsBasicRenderDriver && !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => NormalizeGpuDisplayName(g.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var gpuOptions = new List<(string Value, string Label)> { ("", "Auto") };
        gpuOptions.AddRange(gpus.Select(gpu => ("0x" + gpu.Luid.ToString("X16", CultureInfo.InvariantCulture), NormalizeGpuDisplayName(gpu.Name))));
        AddOptionRow(ui, state, "Preferred GPU", currentLuid, gpuOptions.ToArray(),
            value =>
            {
                SaveConfigValue(PreferredGpuLuid, value ?? "");
                RequestStreamEncoderRestart(session, "preferred GPU");
            }, cellWidth: 220f);
    }

    private static string NormalizeGpuDisplayName(string name)
    {
        return string.Join(" ", (name ?? "Unnamed GPU").Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int NormalizeStreamResolution(int value)
    {
        return StreamResolutionOptions
            .OrderBy(option => Math.Abs(option.Value - value))
            .First().Value;
    }

    private static int NormalizeStreamFps(int value)
    {
        return StreamFpsOptions
            .OrderBy(option => Math.Abs(option.Value - value))
            .First().Value;
    }

    private static int RecommendedBitrateMbps(int longEdge, int fps)
    {
        longEdge = NormalizeStreamResolution(longEdge);
        fps = NormalizeStreamFps(fps);
        float width = longEdge;
        float height = longEdge * 9f / 16f;
        float mbps = width * height * fps * 0.11f / 1_000_000f;
        return Math.Clamp((int)MathF.Round(mbps), 4, 80);
    }

}
