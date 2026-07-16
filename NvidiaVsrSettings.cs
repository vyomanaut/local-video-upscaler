using System.Runtime.InteropServices;

namespace RtxLocalVideo;

internal sealed record NvidiaVsrStatus(bool IsAvailable, int CurrentLevel, int MaximumLevel, string? Error);

internal sealed class TemporaryNvidiaVsrLevel : IDisposable
{
    private readonly int originalLevel;
    private readonly int appliedLevel;
    private bool disposed;

    internal TemporaryNvidiaVsrLevel(int originalLevel, int appliedLevel)
    {
        this.originalLevel = originalLevel;
        this.appliedLevel = appliedLevel;
    }

    public string? RestoreWarning { get; private set; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (originalLevel == appliedLevel) return;

        // Do not overwrite a setting the user changed in NVIDIA App during an export.
        var current = NvidiaVsrSettings.Probe();
        if (!current.IsAvailable || current.CurrentLevel != appliedLevel) return;

        if (!NvidiaVsrSettings.TrySetAndVerify(originalLevel, out var error))
            RestoreWarning = $"The previous NVIDIA VSR setting could not be restored: {error}";
    }
}

internal static class NvidiaVsrSettings
{
    private const uint InitializeId = 0x0150E828;
    private const uint UnloadId = 0xD22BDD7E;
    private const uint EnumDisplayId = 0x9ABDD40D;
    private const uint GetVideoStateExId = 0x0B6EF8B9;
    private const uint SetVideoStateExId = 0x9321CA5B;
    private const uint SuperResolutionComponent = 0x1D;

    private static readonly object Sync = new();

    public static NvidiaVsrStatus Probe()
    {
        lock (Sync)
        {
            try
            {
                using var api = NativeApi.Open(requireSetter: true);
                var state = GetState(api);
                var supported = (state.Flags & 1) != 0 && state.MinimumLevel == 0 && state.MaximumLevel >= 4;
                return supported
                    ? new NvidiaVsrStatus(true, (int)state.CurrentLevel, (int)state.MaximumLevel, null)
                    : new NvidiaVsrStatus(false, (int)state.CurrentLevel, (int)state.MaximumLevel,
                        "This NVIDIA driver does not expose programmable RTX VSR quality levels.");
            }
            catch (Exception ex)
            {
                return new NvidiaVsrStatus(false, 0, 0, ex.Message);
            }
        }
    }

    public static TemporaryNvidiaVsrLevel ApplyTemporary(int level)
    {
        if (level is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(level), "The VSR level must be 1–4 or Auto.");

        lock (Sync)
        {
            var before = Probe();
            if (!before.IsAvailable)
                throw new InvalidOperationException(
                    $"This driver cannot be controlled through the NVIDIA VSR quality API. {before.Error}");
            if (level > before.MaximumLevel)
                throw new InvalidOperationException($"This driver supports VSR values only through {before.MaximumLevel}.");

            if (before.CurrentLevel != level && !TrySetAndVerify(level, out var error))
                throw new InvalidOperationException($"NVIDIA VSR quality could not be set to {FormatLevel(level)}. {error}");

            return new TemporaryNvidiaVsrLevel(before.CurrentLevel, level);
        }
    }

    internal static bool TrySetAndVerify(int level, out string? error)
    {
        lock (Sync)
        {
            try
            {
                using var api = NativeApi.Open(requireSetter: true);
                var state = new SetVideoStateComponentEx
                {
                    Version = MakeVersion(0x40, 1),
                    Component = SuperResolutionComponent,
                    DeviceIndex = 0,
                    Enable = 1,
                    SetToAlgorithm = 1,
                    Value = (uint)level,
                    ReservedValue = 0
                };

                var status = api.SetVideoState!(api.Display, ref state);
                if (status != 0)
                {
                    error = $"NvAPI_SetVideoStateEx returned {status}.";
                    return false;
                }

                var verified = GetState(api);
                if (verified.CurrentLevel != level)
                {
                    error = $"The driver accepted the request but reported level {verified.CurrentLevel}.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public static string FormatLevel(int level) => level == 5 ? "Auto" : $"level {level}";

    private static GetVideoStateComponentEx GetState(NativeApi api)
    {
        var state = new GetVideoStateComponentEx
        {
            Version = MakeVersion(0x80, 1),
            Component = SuperResolutionComponent,
            DeviceIndex = 0
        };
        var status = api.GetVideoState(api.Display, ref state);
        if (status != 0)
            throw new InvalidOperationException($"NvAPI_GetVideoStateEx returned {status}.");
        return state;
    }

    private static uint MakeVersion(uint size, uint version) => size | (version << 16);

    [StructLayout(LayoutKind.Explicit, Size = 0x80)]
    private struct GetVideoStateComponentEx
    {
        [FieldOffset(0x00)] public uint Version;
        [FieldOffset(0x04)] public uint Component;
        [FieldOffset(0x08)] public uint DeviceIndex;
        [FieldOffset(0x0C)] public uint Flags;
        [FieldOffset(0x18)] public uint MinimumLevel;
        [FieldOffset(0x1C)] public uint MaximumLevel;
        [FieldOffset(0x58)] public uint CurrentLevel;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    private struct SetVideoStateComponentEx
    {
        [FieldOffset(0x00)] public uint Version;
        [FieldOffset(0x04)] public uint Component;
        [FieldOffset(0x08)] public uint DeviceIndex;
        [FieldOffset(0x0C)] public uint Enable;
        [FieldOffset(0x10)] public uint SetToAlgorithm;
        [FieldOffset(0x14)] public uint Value;
        [FieldOffset(0x18)] public ulong ReservedValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumDisplayDelegate(uint index, out IntPtr display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVideoStateDelegate(IntPtr display, ref GetVideoStateComponentEx state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetVideoStateDelegate(IntPtr display, ref SetVideoStateComponentEx state);

    private sealed class NativeApi : IDisposable
    {
        private readonly IntPtr module;
        private readonly UnloadDelegate? unload;

        private NativeApi(
            IntPtr module,
            IntPtr display,
            GetVideoStateDelegate getVideoState,
            SetVideoStateDelegate? setVideoState,
            UnloadDelegate? unload)
        {
            this.module = module;
            Display = display;
            GetVideoState = getVideoState;
            SetVideoState = setVideoState;
            this.unload = unload;
        }

        public IntPtr Display { get; }
        public GetVideoStateDelegate GetVideoState { get; }
        public SetVideoStateDelegate? SetVideoState { get; }

        public static NativeApi Open(bool requireSetter)
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var module))
                throw new InvalidOperationException("nvapi64.dll is unavailable.");

            try
            {
                var queryAddress = NativeLibrary.GetExport(module, "nvapi_QueryInterface");
                var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryAddress);
                var initialize = Resolve<InitializeDelegate>(query, InitializeId, required: true)!;
                var unload = Resolve<UnloadDelegate>(query, UnloadId, required: false);
                var enumDisplay = Resolve<EnumDisplayDelegate>(query, EnumDisplayId, required: true)!;
                var getVideoState = Resolve<GetVideoStateDelegate>(query, GetVideoStateExId, required: true)!;
                var setVideoState = Resolve<SetVideoStateDelegate>(query, SetVideoStateExId, requireSetter);

                var status = initialize();
                if (status != 0)
                    throw new InvalidOperationException($"NvAPI_Initialize returned {status}.");
                status = enumDisplay(0, out var display);
                if (status != 0 || display == IntPtr.Zero)
                {
                    unload?.Invoke();
                    throw new InvalidOperationException($"No NVIDIA display handle is available ({status}).");
                }

                return new NativeApi(module, display, getVideoState, setVideoState, unload);
            }
            catch
            {
                NativeLibrary.Free(module);
                throw;
            }
        }

        public void Dispose()
        {
            unload?.Invoke();
            NativeLibrary.Free(module);
        }

        private static T? Resolve<T>(QueryInterfaceDelegate query, uint id, bool required) where T : Delegate
        {
            var address = query(id);
            if (address != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            if (required)
                throw new InvalidOperationException($"Required NVAPI function 0x{id:X8} is unavailable.");
            return null;
        }
    }
}
