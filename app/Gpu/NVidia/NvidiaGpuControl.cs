using GHelper.Helpers;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace GHelper.Gpu.NVidia;

// Undocumented NvAPI thermal channels (same interface HWiNFO/GPU-Z use): the public
// GetThermalSettings exposes only the core sensor on laptop GPUs, hotspot and memory
// junction live in NvAPI_GPU_ThermChannelGetStatus (0x65FE3AAD). Read-only.
// ABI verified empirically on RTX 4090 Laptop / driver 2026-07: struct v2 = 168 bytes
// (version, mask, 40 ints), channel temps land at ints [8..11] as fixed-point <<8:
// [8] core, [9] hotspot, [10] memory junction, [11] unknown (VRM?).
internal static class NvThermalChannels
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpusDelegate([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ThermalDelegate(IntPtr handle, ref ThermalInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct ThermalInfo
    {
        public uint Version;
        public uint Mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public int[] Temps;
    }

    const uint MASK_CORE_HOTSPOT = 0x3;
    const int CH_HOTSPOT = 9;

    static ThermalDelegate? _thermal;
    static IntPtr _handle;
    static bool _failed;

    static bool EnsureHandle()
    {
        if (_handle != IntPtr.Zero) return true;

        var initPtr = QueryInterface(0x0150E828);
        var enumPtr = QueryInterface(0xE5AC921F);
        if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero) return false;

        if (Marshal.GetDelegateForFunctionPointer<InitDelegate>(initPtr)() != 0) return false;

        IntPtr[] handles = new IntPtr[64];
        if (Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(enumPtr)(handles, out int count) != 0 || count == 0) return false;

        _handle = handles[0];
        return true;
    }

    public static int? GetHotSpot()
    {
        if (_failed) return null;

        try
        {
            if (_thermal is null)
            {
                var thermPtr = QueryInterface(0x65FE3AAD);
                if (thermPtr == IntPtr.Zero || !EnsureHandle()) { _failed = true; return null; }
                _thermal = Marshal.GetDelegateForFunctionPointer<ThermalDelegate>(thermPtr);
            }

            var info = new ThermalInfo
            {
                Version = (uint)Marshal.SizeOf<ThermalInfo>() | 0x20000,
                Mask = MASK_CORE_HOTSPOT,
                Temps = new int[40]
            };
            if (_thermal(_handle, ref info) != 0) return null;

            int hot = info.Temps[CH_HOTSPOT] >> 8;
            return (hot > 0 && hot < 130) ? hot : null;
        }
        catch
        {
            _failed = true; // driver without this interface — stop asking
            return null;
        }
    }

    // NvAPI_GPU_GetCurrentVoltage (0x465F9BCF): core voltage in uV. Read-only;
    // mobile RTX 40 reports it even though voltage CONTROL is locked.
    // ABI verified empirically on RTX 4090 Laptop, driver 2026-07: v1 = 76 bytes,
    // uV lands at offset 40, the rest reads back as zeros.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int VoltageDelegate(IntPtr handle, ref VoltageStatus status);

    [StructLayout(LayoutKind.Sequential)]
    struct VoltageStatus
    {
        public uint Version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public uint[] Unknown;
        public uint ValueUv;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
    }

    static VoltageDelegate? _voltage;
    static bool _voltageFailed;

    public static double? GetCoreVoltage()
    {
        if (_voltageFailed) return null;

        try
        {
            if (_voltage is null)
            {
                var ptr = QueryInterface(0x465F9BCF);
                if (ptr == IntPtr.Zero || !EnsureHandle()) { _voltageFailed = true; return null; }
                _voltage = Marshal.GetDelegateForFunctionPointer<VoltageDelegate>(ptr);
            }

            var status = new VoltageStatus
            {
                Version = (uint)Marshal.SizeOf<VoltageStatus>() | 0x10000,
                Unknown = new uint[9],
                Reserved = new uint[8]
            };
            if (_voltage(_handle, ref status) != 0) return null;

            double volts = status.ValueUv / 1_000_000.0;
            return (volts > 0.3 && volts < 1.6) ? volts : null;
        }
        catch
        {
            _voltageFailed = true;
            return null;
        }
    }
}

public class NvidiaGpuControl : IGpuControl
{

    public static int MaxCoreOffset = AppConfig.Get("max_gpu_core", 250);
    public static int MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 500);

    public static int MinCoreOffset = AppConfig.Get("min_gpu_core", -250);
    public static int MinMemoryOffset = AppConfig.Get("min_gpu_memory", -500);

    public static int MinClockLimit = AppConfig.Get("min_gpu_clock", 400);
    public const int MaxClockLimit = 3000;

    private static PhysicalGPU? _internalGpu;

    public NvidiaGpuControl()
    {
        _internalGpu = GetInternalDiscreteGpu();
        if (IsValid)
        {
            if (FullName.Contains("5080") || FullName.Contains("5090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 400);
                MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 1000);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
            if (FullName.Contains("5070 Ti") || FullName.Contains("4080") || FullName.Contains("4090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 300);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
        }
    }

    public bool IsValid => _internalGpu != null;

    public bool IsNvidia => IsValid;

    public string FullName => _internalGpu!.FullName;

    public int? _lastTemp;
    public int _lastTempTime = 0;

    private static bool verboseLog = false;

    private enum GpuState { Active, Asleep, Off }

    private GpuState _lastState = GpuState.Off;
    private long _lastStateTime = -StateCacheMs;
    private const int StateCacheMs = 500;

    private GpuState GetGpuState()
    {
        if (!IsValid) return GpuState.Off;
        if (Environment.TickCount64 - _lastStateTime < StateCacheMs) return _lastState;
        try
        {
            var perfState = GPUApi.GetCurrentPerformanceState(_internalGpu!.Handle);
            if (verboseLog) Logger.WriteLine($"GPU: {perfState}");
            _lastState = GpuState.Active;
        }
        catch (Exception ex)
        {
            if (verboseLog) Logger.WriteLine($"GPU: {ex.Message}");
            _lastState = ex.Message == "NVAPI_GPU_NOT_POWERED" ? GpuState.Asleep : GpuState.Off;
        }
        _lastStateTime = Environment.TickCount64;
        return _lastState;
    }

    public int? HotSpot { get; private set; }

    public bool IsGpuActive => GetGpuState() == GpuState.Active;

    public double? CoreVoltage => NvThermalChannels.GetCoreVoltage();

    // Silent one-shot poll for the tuning telemetry line: no Logger noise
    // (RunCMD logs every call and this runs every couple of seconds)
    public static (int coreMhz, int memMhz, double? watts)? ReadSmiTelemetry()
    {
        try
        {
            using var p = new Process();
            p.StartInfo.FileName = "nvidia-smi";
            p.StartInfo.Arguments = "--query-gpu=clocks.gr,clocks.mem,power.draw --format=csv,noheader,nounits";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            string line = p.StandardOutput.ReadLine() ?? "";
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }

            var parts = line.Split(',');
            if (parts.Length < 3) return null;

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out double core)) return null;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out double mem)) return null;
            double? watts = double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out double w) ? w : null;

            return ((int)core, (int)mem, watts);
        }
        catch
        {
            return null;
        }
    }

    public int? ReadCurrentTemperature(bool log = false)
    {
        if (!IsValid) return null;

        var thermalSettings = GPUApi.GetThermalSettings(_internalGpu!.Handle);
        if (thermalSettings.Sensors is null) return null;

        IThermalSensor? gpuSensor = thermalSettings.Sensors
            .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);

        HotSpot = gpuSensor is null ? null : NvThermalChannels.GetHotSpot();

        if (log || verboseLog) Logger.WriteLine($"GPU Temp: {gpuSensor?.CurrentTemperature}C" + (HotSpot is int h ? $" Hot: {h}C" : ""));
        return gpuSensor?.CurrentTemperature;
    }

    private Task<int?>? _readTask;

    public int? GetCurrentTemperature()
    {
        if (!IsValid) return null;

        var state = GetGpuState();
        if (state == GpuState.Off) return null;

        if ((_readTask?.IsCompleted ?? true) && (state == GpuState.Active || ShouldRefresh()))
        {
            _readTask = Task.Run(() =>
            {
                var temp = ReadCurrentTemperature();
                if (temp is not null)
                {
                    _lastTemp = temp;
                    _lastTempTime = Environment.TickCount;
                }
                return temp;
            });
        }

        _readTask?.Wait(500);

        return _lastTemp;
    }

    private bool ShouldRefresh()
    {
        const int minInterval = 5_000;
        const int maxInterval = 120_000;
        const float deltaMin = 5f;
        const float deltaMax = 20f;

        if (_lastTemp is null) return true;

        var cpuTemp = (float)HardwareControl.GetCPUTemp();
        var delta = _lastTemp.Value - cpuTemp;

        if (delta < deltaMin) return false;

        var t = Math.Clamp((delta - deltaMin) / (deltaMax - deltaMin), 0f, 1f);
        var interval = (int)(maxInterval - t * (maxInterval - minInterval));

        var refresh = Environment.TickCount > _lastTempTime + interval;
        if (verboseLog) Logger.WriteLine($"GPU Temp Refresh Interval: {interval}ms {refresh}");

        return refresh;
    }

    public void Dispose()
    {
        _internalGpu = null;
    }

    private static readonly HashSet<string> _systemProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "svchost", "fontdrvhost", "igfxem", "igfxhk", "igfxext",
        "nvcontainer", "nvdisplay.container", "nvsettings", "nvspcaps64",
        "nvsphelper64", "nvwmi64", "nvcplui", "atieclxx", "atiesrxx",
        "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost",
        "searchhost", "startmenuexperiencehost", "textinputhost",
        "applicationframehost", "systemsettings", "dllhost", "conhost",
        "audiodg", "ctfloader", "spoolsv", "wlanext", "msdtc",
    };

    public void KillGPUApps()
    {
        if (!IsValid) return;
        PhysicalGPU internalGpu = _internalGpu!;

        int currentPid = Process.GetCurrentProcess().Id;

        try
        {
            Process[] processes = internalGpu.GetActiveApplications();
            foreach (Process process in processes)
                try
                {
                    if (process.Id == currentPid) continue;
                    if (process.SessionId == 0) continue;
                    if (_systemProcessNames.Contains(process.ProcessName)) continue;

                    Logger.WriteLine("Kill:" + process.ProcessName);
                    ProcessHelper.KillByProcess(process);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }

        //GeneralApi.RestartDisplayDriver();
    }


    public bool GetClocks(out int core, out int memory)
    {
        PhysicalGPU internalGpu = _internalGpu!;

        //Logger.WriteLine(internalGpu.FullName);
        //Logger.WriteLine(internalGpu.ArchitectInformation.ToString());

        try
        {
            var temp = ReadCurrentTemperature(true); // Force wake up GPU for clock reading

            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(internalGpu.Handle);
            core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
            memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
            Logger.WriteLine($"GET GPU CLOCKS: {core}, {memory}");

            foreach (var delta in states.Voltages[PerformanceStateId.P0_3DPerformance])
            {
                Logger.WriteLine("GPU VOLT:" + delta.IsEditable + " - " + delta.ValueDeltaInMicroVolt.DeltaValue);
            }

            return true;

        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU CLOCKS:" + ex.Message);
            core = memory = 0;
            return false;
        }

    }


    private static bool RunPowershellCommand(string script, int timeoutMs = 0)
    {
        try
        {
            ProcessHelper.RunCMD("powershell", script, timeoutMs: timeoutMs);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.ToString());
            return false;
        }

    }

    public int GetMaxGPUCLock()
    {
        PhysicalGPU internalGpu = _internalGpu!;
        try
        {
            PrivateClockBoostLockV2 data = GPUApi.GetClockBoostLock(internalGpu.Handle);
            int limit = (int)data.ClockBoostLocks[0].VoltageInMicroV / 1000;
            Logger.WriteLine("GET CLOCK LIMIT: " + limit);
            return limit;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET CLOCK LIMIT: " + ex.Message);
            return -1;

        }
    }


    public int SetMaxGPUClock(int clock)
    {

        if (clock < MinClockLimit || clock >= MaxClockLimit) clock = 0;

        int _clockLimit = GetMaxGPUCLock();

        if (_clockLimit == clock) return 0;

        if (clock > 0) RunPowershellCommand($"nvidia-smi -lgc 0,{clock}");
        else RunPowershellCommand($"nvidia-smi -rgc");
        return 1;


    }

    public static void RestartNvContainer()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void RestartNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void StopNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Stop-Service -Name 'NvContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Stop-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
    }

    public int SetClocks(int core, int memory)
    {

        if (core < MinCoreOffset || core > MaxCoreOffset) return 0;
        if (memory < MinMemoryOffset || memory > MaxMemoryOffset) return 0;

        GetClocks(out int currentCore, out int currentMemory);

        // Nothing to set
        if (Math.Abs(core - currentCore) < 5 && Math.Abs(memory - currentMemory) < 5) return 0;

        PhysicalGPU internalGpu = _internalGpu!;

        var coreClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000));
        var memoryClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000));
        //var voltageEntry = new PerformanceStates20BaseVoltageEntryV1(PerformanceVoltageDomain.Core, new PerformanceStates20ParameterDelta(voltage));

        PerformanceStates20ClockEntryV1[] clocks = { coreClock, memoryClock };
        PerformanceStates20BaseVoltageEntryV1[] voltages = { };

        PerformanceState20[] performanceStates = { new PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, voltages) };

        var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);

        try
        {
            Logger.WriteLine($"SET GPU CLOCKS: {core}, {memory}");
            GPUApi.SetPerformanceStates20(internalGpu.Handle, overclock);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SET GPU CLOCKS: " + ex.Message);
            return -1;
        }

        return 1;
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            return PhysicalGPU
                .GetPhysicalGPUs()
                .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }


    public int? GetGpuUse()
    {
        if (!IsValid) return null;
         if (GetGpuState() != GpuState.Active) return null;

        PhysicalGPU internalGpu = _internalGpu!;
        IUtilizationDomainInfo? gpuUsage = GPUApi.GetUsages(internalGpu.Handle).GPU;

        return (int?)gpuUsage?.Percentage;

    }


    public float? GetGpuPower()
    {
        if (!IsValid) return null;
        var state = GetGpuState();
        if (state == GpuState.Off)
        {
            NvmlHelper.Shutdown();
            return null;
        }
        if (state != GpuState.Active) return 0f;
        return NvmlHelper.GetGpuPower() ?? 0f;
    }

    public (long usedMb, long totalMb)? GetVramInfo()
    {
        if (!IsValid) return null;
        if (GetGpuState() != GpuState.Active) return null;
        return NvmlHelper.GetMemoryInfo();
    }

}
