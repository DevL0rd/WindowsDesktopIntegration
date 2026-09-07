using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;

namespace DesktopBuddy;

public sealed partial class WgcCapture
{

    internal static unsafe List<GpuAdapterInfo> EnumerateAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();
        if (DesktopBuddyPlatform.IsLinux)
            return adapters;

        var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero)
            return adapters;

        try
        {
            var vtable = *(IntPtr**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[IDXGIFactory_EnumAdapters];

            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                hr = enumAdapters(factory, i, &adapter);
                if (hr < 0) break;

                try
                {
                    DXGI_ADAPTER_DESC desc = GetAdapterDesc(adapter);
                    adapters.Add(new GpuAdapterInfo(
                        AdapterName(desc),
                        desc.VendorId,
                        desc.AdapterLuid,
                        (ulong)desc.DedicatedVideoMemory,
                        desc.VendorId == MicrosoftBasicRenderDriverVendorId));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return adapters;
    }

    private static unsafe DXGI_ADAPTER_DESC GetAdapterDesc(IntPtr adapter)
    {
        var adapterVtable = *(IntPtr**)adapter;
        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)adapterVtable[IDXGIAdapter_GetDesc];
        DXGI_ADAPTER_DESC desc;
        getDesc(adapter, &desc);
        return desc;
    }

    private static unsafe string AdapterName(DXGI_ADAPTER_DESC desc)
    {
        return new string(desc.Description).TrimEnd('\0');
    }

    private static void CacheAdapter(IntPtr adapter, DXGI_ADAPTER_DESC desc)
    {
        _cachedPreferredAdapter = adapter;
        _cachedPreferredAdapterVendorId = desc.VendorId;
        _cachedPreferredAdapterLuid = desc.AdapterLuid;
        if (_cachedPreferredAdapter != IntPtr.Zero)
            Marshal.AddRef(_cachedPreferredAdapter);
        _adapterCacheReady = true;
    }

    private static bool TryGetConfiguredGpuLuid(out long luid)
    {
        luid = 0;
        string raw = DesktopBuddyMod.Config?.GetValue(DesktopBuddyMod.PreferredGpuLuid)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out luid);

        return long.TryParse(raw, out luid);
    }

    private static unsafe IntPtr FindAdapterByLuid(long preferredLuid, out DXGI_ADAPTER_DESC matchedDesc)
    {
        matchedDesc = default;
        var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var vtable = *(IntPtr**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[IDXGIFactory_EnumAdapters];

            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                hr = enumAdapters(factory, i, &adapter);
                if (hr < 0) break;

                DXGI_ADAPTER_DESC desc = GetAdapterDesc(adapter);
                bool isBasic = desc.VendorId == MicrosoftBasicRenderDriverVendorId;
                if (!isBasic && desc.AdapterLuid == preferredLuid)
                {
                    matchedDesc = desc;
                    return adapter;
                }

                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return IntPtr.Zero;
    }

    private static unsafe IntPtr FindPreferredAdapter()
    {
        lock (_adapterCacheLock)
        {
            if (_adapterCacheReady)
            {
                if (_cachedPreferredAdapter != IntPtr.Zero)
                    Marshal.AddRef(_cachedPreferredAdapter);
                return _cachedPreferredAdapter;
            }

            if (TryGetConfiguredGpuLuid(out long preferredLuid))
            {
                IntPtr configuredAdapter = FindAdapterByLuid(preferredLuid, out DXGI_ADAPTER_DESC configuredDesc);
                if (configuredAdapter != IntPtr.Zero)
                {
                    CacheAdapter(configuredAdapter, configuredDesc);
                    Log.Msg($"[WgcCapture] Selected configured adapter: '{AdapterName(configuredDesc)}' VendorId=0x{configuredDesc.VendorId:X4} LUID=0x{configuredDesc.AdapterLuid:X16}");
                    return configuredAdapter;
                }

                Log.Msg($"[WgcCapture] Configured GPU LUID 0x{preferredLuid:X16} was not found; falling back to automatic adapter selection");
            }

            if (_rendererAdapterHintReady && _rendererAdapterHintLuid != 0)
            {
                IntPtr rendererAdapter = FindAdapterByLuid(_rendererAdapterHintLuid, out DXGI_ADAPTER_DESC rendererDesc);
                if (rendererAdapter != IntPtr.Zero)
                {
                    CacheAdapter(rendererAdapter, rendererDesc);
                    Log.Msg($"[WgcCapture] Selected renderer adapter: '{AdapterName(rendererDesc)}' VendorId=0x{rendererDesc.VendorId:X4} LUID=0x{rendererDesc.AdapterLuid:X16}");
                    return rendererAdapter;
                }

                Log.Msg($"[WgcCapture] Renderer adapter hint 0x{_rendererAdapterHintLuid:X16} was not found; falling back to automatic adapter selection");
            }

            var factory6Guid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
            int hr = CreateDXGIFactory1(ref factory6Guid, out IntPtr factory6);
            if (hr >= 0 && factory6 != IntPtr.Zero)
            {
                var factory6Vtable = *(IntPtr**)factory6;
                var enumByPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, IntPtr*, int>)factory6Vtable[IDXGIFactory6_EnumAdapterByGpuPreference];
                var adapterGuid = new Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");

                for (uint i = 0; ; i++)
                {
                    IntPtr adapter;
                    hr = enumByPreference(factory6, i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, &adapterGuid, &adapter);
                    if (hr < 0) break;

                    DXGI_ADAPTER_DESC desc = GetAdapterDesc(adapter);

                    string descStr = AdapterName(desc);
                    bool isBasic = desc.VendorId == MicrosoftBasicRenderDriverVendorId;
                    Log.Msg($"[WgcCapture] HighPerf adapter {i}: '{descStr}' VendorId=0x{desc.VendorId:X4} VRAM={desc.DedicatedVideoMemory / 1048576}MB LUID=0x{desc.AdapterLuid:X16}{(isBasic ? " [basic]" : "")}");

                    if (!isBasic)
                    {
                        CacheAdapter(adapter, desc);
                        Log.Msg($"[WgcCapture] Selected high-performance adapter: '{descStr}' VendorId=0x{desc.VendorId:X4} LUID=0x{desc.AdapterLuid:X16}");
                        Marshal.Release(factory6);
                        return adapter;
                    }

                    Marshal.Release(adapter);
                }

                Marshal.Release(factory6);
            }
            else
            {
                Log.Msg($"[WgcCapture] IDXGIFactory6 unavailable; falling back to dedicated VRAM adapter selection hr=0x{hr:X8}");
            }

            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
            if (hr < 0 || factory == IntPtr.Zero) { _adapterCacheReady = true; return IntPtr.Zero; }

            var vtable = *(IntPtr**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[IDXGIFactory_EnumAdapters];

            IntPtr bestAdapter = IntPtr.Zero;
            nuint bestDedicatedVideoMemory = 0;
            bool bestIsBasic = true;
            DXGI_ADAPTER_DESC bestDesc = default;
            uint bestVendorId = 0;

            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                hr = enumAdapters(factory, i, &adapter);
                if (hr < 0) break;

                DXGI_ADAPTER_DESC desc = GetAdapterDesc(adapter);

                bool isBasic = desc.VendorId == MicrosoftBasicRenderDriverVendorId;
                string descStr = AdapterName(desc);
                Log.Msg($"[WgcCapture] Adapter {i}: '{descStr}' VendorId=0x{desc.VendorId:X4} VRAM={desc.DedicatedVideoMemory / 1048576}MB LUID=0x{desc.AdapterLuid:X16}{(isBasic ? " [basic]" : "")}");

                bool better =
                    bestAdapter == IntPtr.Zero ||
                    (bestIsBasic && !isBasic) ||
                    (bestIsBasic == isBasic && desc.DedicatedVideoMemory > bestDedicatedVideoMemory);

                if (better)
                {
                    if (bestAdapter != IntPtr.Zero) Marshal.Release(bestAdapter);
                    bestAdapter = adapter;
                    bestVendorId = desc.VendorId;
                    bestDedicatedVideoMemory = desc.DedicatedVideoMemory;
                    bestIsBasic = isBasic;
                    bestDesc = desc;
                }
                else
                {
                    Marshal.Release(adapter);
                }
            }

            Marshal.Release(factory);

            if (bestAdapter != IntPtr.Zero)
            {
                CacheAdapter(bestAdapter, bestDesc);
                Log.Msg($"[WgcCapture] Selected max-VRAM adapter: '{AdapterName(bestDesc)}' VendorId=0x{bestVendorId:X4} VRAM={bestDedicatedVideoMemory / 1048576}MB LUID=0x{bestDesc.AdapterLuid:X16}");
            }
            else
            {
                _adapterCacheReady = true;
            }
            _adapterCacheReady = true;
            return bestAdapter;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private static unsafe int CallCreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice)
    {
        var lib = LoadLibraryW("d3d11.dll");
        var proc = GetProcAddress(lib, "CreateDirect3D11DeviceFromDXGIDevice");
        if (proc == IntPtr.Zero) { graphicsDevice = IntPtr.Zero; return -1; }

        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)proc;
        IntPtr result;
        int hr = fn(dxgiDevice, &result);
        graphicsDevice = result;
        return hr;
    }

    internal static uint SharedD3dAdapterVendorId
    {
        get
        {
            EnsurePreferredAdapterCached();
            return _preferredD3dAdapterVendorId;
        }
    }

    internal static bool PrewarmSharedDevice()
    {
        EnsurePreferredAdapterCached();
        return _adapterCacheReady;
    }

    internal static bool PrewarmCaptureFactory() => EnsureCaptureInterop();

    private static void EnsurePreferredAdapterCached()
    {
        IntPtr adapter = FindPreferredAdapter();
        if (adapter != IntPtr.Zero)
            Marshal.Release(adapter);

        _preferredD3dAdapterVendorId = _cachedPreferredAdapterVendorId;
    }

    private bool CreateD3dDevice()
    {
        if (_d3dDevice != IntPtr.Zero && _d3dContext != IntPtr.Zero && _winrtDevice != null)
            return true;

        lock (_d3dLock)
        {
            if (_d3dDevice != IntPtr.Zero && _d3dContext != IntPtr.Zero && _winrtDevice != null)
                return true;

            uint deviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            IntPtr preferredAdapter = FindPreferredAdapter();
            uint preferredVendorId = _cachedPreferredAdapterVendorId;
            long preferredLuid = _cachedPreferredAdapterLuid;
            int driverType = preferredAdapter != IntPtr.Zero ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE;
            int hr = D3D11CreateDevice(preferredAdapter, driverType, IntPtr.Zero,
                deviceFlags, IntPtr.Zero, 0, 7,
                out _d3dDevice, out _, out _d3dContext);
            if (preferredAdapter != IntPtr.Zero) Marshal.Release(preferredAdapter);
            if (hr < 0)
            {
                Log.Msg($"[WgcCapture] D3D11CreateDevice failed hr=0x{hr:X8}");
                ReleaseD3dDevice();
                return false;
            }

            var mtGuid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            if (Marshal.QueryInterface(_d3dDevice, in mtGuid, out IntPtr mtPtr) >= 0)
            {
                unsafe
                {
                    var vtable = *(IntPtr**)mtPtr;
                    var setProtFn = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)vtable[4];
                    setProtFn(mtPtr, 1, null);
                }
                Marshal.Release(mtPtr);
                Log.Msg("[WgcCapture] D3D11 multithread protection enabled");
            }

            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            hr = Marshal.QueryInterface(_d3dDevice, in dxgiGuid, out IntPtr dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] IDXGIDevice QueryInterface failed hr=0x{hr:X8}");
                ReleaseD3dDevice();
                return false;
            }

            hr = CallCreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            Marshal.Release(dxgiDevice);
            if (hr < 0 || inspectable == IntPtr.Zero)
            {
                Log.Msg($"[WgcCapture] CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}");
                ReleaseD3dDevice();
                return false;
            }

            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            _preferredD3dAdapterVendorId = preferredVendorId;
            Log.Msg($"[WgcCapture] D3D11 device ready 0x{_d3dDevice:X} vendor=0x{preferredVendorId:X4} LUID=0x{preferredLuid:X16}");
            return true;
        }
    }

    private void ReleaseD3dDevice()
    {
        _winrtDevice = null;

        if (_d3dContext != IntPtr.Zero)
        {
            try { Marshal.Release(_d3dContext); }
            catch { }
            _d3dContext = IntPtr.Zero;
        }

        if (_d3dDevice != IntPtr.Zero)
        {
            try { Marshal.Release(_d3dDevice); }
            catch { }
            _d3dDevice = IntPtr.Zero;
        }
    }
}
