using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed class LinuxNativeBridgeRenderer : IDisposable
    {
        private const uint OpPoll = 2;
        private const uint OpStop = 3;
        private const uint OpStartNode = 6;
        private const uint OpCopyFrame = 7;
        private const uint OpCloseFrame = 8;
        private const uint OpLoad = 9;

        private static readonly object LoadLock = new object();
        private static IntPtr SharedModule;
        private static DesktopBuddyLinuxBridgeCallDelegate SharedCall;

        private DesktopBuddyLinuxBridgeCallDelegate _call;
        private ulong _captureId;

        internal bool TryLoad()
        {
            if (_call != null) return true;

            lock (LoadLock)
            {
                if (SharedCall == null)
                {
                    string path = ResolveBridgePath();
                    SharedModule = LoadLibraryA(path);
                    if (SharedModule == IntPtr.Zero)
                    {
                        SharedTextureBridgePlugin.LogWarning($"[LinuxBridge] LoadLibrary failed path={path} err=0x{Marshal.GetLastWin32Error():X8}");
                        return false;
                    }

                    IntPtr proc = GetProcAddress(SharedModule, "DesktopBuddyLinuxBridgeCall");
                    if (proc == IntPtr.Zero)
                    {
                        SharedTextureBridgePlugin.LogWarning($"[LinuxBridge] GetProcAddress failed err=0x{Marshal.GetLastWin32Error():X8}");
                        FreeSharedModule();
                        return false;
                    }

                    SharedCall = (DesktopBuddyLinuxBridgeCallDelegate)Marshal.GetDelegateForFunctionPointer(proc, typeof(DesktopBuddyLinuxBridgeCallDelegate));
                    SharedTextureBridgePlugin.LogInfo($"[LinuxBridge] Loaded {path}");
                }

                _call = SharedCall;
            }
            return true;
        }

        /// <summary>
        /// Performs the native library load that every other op would otherwise do lazily on
        /// first use. Splitting it out means a renderer that dies on the first capture attempt
        /// tells us whether it died pulling PipeWire into the Wine process or connecting to it.
        /// </summary>
        internal int LoadNative()
        {
            if (!TryLoad()) return -1;
            var call = new DbLinuxBridgeCall { Op = OpLoad };
            return _call(ref call);
        }

        internal int StartCapture(uint nodeId)
        {
            if (!TryLoad()) return -1;
            var call = new DbLinuxBridgeCall { Op = OpStartNode, Arg0 = nodeId };
            int status = _call(ref call);
            if (status == 0)
                _captureId = call.Arg0;
            return status;
        }

        internal int PollFrame(out DbLinuxFrame frame)
        {
            frame = default;
            if (_call == null || _captureId == 0) return -1;
            var call = new DbLinuxBridgeCall { Op = OpPoll, Arg0 = _captureId };
            int status = _call(ref call);
            frame = call.Frame;
            return status;
        }

        internal int CopyFrameBytes(DbLinuxFrame frame, byte[] destination)
        {
            if (_call == null || destination == null || destination.Length == 0)
                return -1;

            var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                var call = new DbLinuxBridgeCall
                {
                    Op = OpCopyFrame,
                    Arg0 = checked((ulong)destination.LongLength),
                    Frame = frame,
                    Buffer = (ulong)handle.AddrOfPinnedObject().ToInt64()
                };
                return _call(ref call);
            }
            finally { handle.Free(); }
        }

        internal void DiscardFrame(DbLinuxFrame frame)
        {
            if (_call == null || frame.Fd < 0)
                return;

            var call = new DbLinuxBridgeCall { Op = OpCloseFrame, Frame = frame };
            _call(ref call);
        }

        internal void Stop()
        {
            if (_call == null || _captureId == 0) return;
            var call = new DbLinuxBridgeCall { Op = OpStop, Arg0 = _captureId };
            _call(ref call);
            _captureId = 0;
        }

        private static string ResolveBridgePath()
        {
            string dir = Path.GetDirectoryName(typeof(SharedTextureBridgePlugin).Assembly.Location) ?? string.Empty;
            return Path.Combine(dir, "DesktopBuddyLinuxBridge.so");
        }

        public void Dispose()
        {
            Stop();
            _call = null;
        }

        private static void FreeSharedModule()
        {
            if (SharedModule == IntPtr.Zero)
                return;

            FreeLibrary(SharedModule);
            SharedModule = IntPtr.Zero;
            SharedCall = null;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DesktopBuddyLinuxBridgeCallDelegate(ref DbLinuxBridgeCall call);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibraryA(string fileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
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
    internal struct DbLinuxBridgeCall
    {
        public uint Op;
        public int Status;
        public ulong Arg0;
        public DbLinuxFrame Frame;
        public ulong Buffer;
    }
}
