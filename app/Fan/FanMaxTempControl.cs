namespace GHelper.Fan
{
    // EC firmware runs each fan off its own sensor (CPU fan - CPU temp, GPU fan - GPU zone).
    // Two optional dynamic-curve features share the 3s timer below.
    //
    // 1) Sync to hottest sensor (fan_sync_max_temp): since the heatsink is shared, raises the
    //    floor of every custom curve to the speed its own curve prescribes at the hottest
    //    sensor, so no fan idles while another component is hot. When the floor is substantial
    //    (>= START_ASSIST) the curve start is also pulled down to START_TEMP so EC actually
    //    spins up a stopped fan (its own sensor may still sit below the first curve point).
    //
    // 2) Stop hysteresis (fan_hyst): EC applies an audible start impulse (~2700+ RPM) on every
    //    fan start regardless of the target duty, so instead of letting a fan stop and restart
    //    around the curve start point, once a fan is observed spinning its curve start is
    //    extended down to a per-fan stop temperature (fan_hyst_{cpu|gpu|mid}_{mode}) at
    //    >= MIN_SUSTAIN duty; the fan then keeps running until its own sensor cools below the
    //    stop point. While the fan is stopped the unmodified curve sits in EC, so the start
    //    point stays exactly where the user drew it. Exception - GPU fan idle park: while
    //    the dGPU sleeps, a stopped GPU fan gets its start raised to curve[1]-1, because
    //    with the dGPU down the zone input re-heats from the shared heatsink past low
    //    drawn starts in seconds and EC would kick the fan in an endless loop.
    //
    // Lifecycle: started by ModeControl.AutoFans after custom curves are successfully applied,
    // stopped by AutoFans otherwise and by every path that resets EC curves behind its back
    // (calibration, factory reset, sleep/shutdown reset).
    public static class FanMaxTempControl
    {
        const int TICK_MS = 3000;
        const int TEMP_STEP = 2;
        const int NO_TEMP = -1000;
        const byte MIN_SUSTAIN = 3;        // duty that reliably sustains a spinning fan
        const byte START_ASSIST = 10;      // sync floor at which a stopped fan is force-started
        const byte START_TEMP = 20;        // curve start temp used when a fan must be running

        public const int HYST_MIN_GAP = 3; // stop point sits at least this far below curve start

        static readonly System.Timers.Timer timer = new(TICK_MS);
        static readonly object syncLock = new();

        const int GPU_ACTIVE_CONFIRM = 3;  // ticks of dGPU uptime before its drawn start returns

        // Hybrid stock GPU fan (fan_gpu_stock): stock EC logic at idle (spins through dGPU
        // wake/sleep flips, no kicks), the custom curve engages when the sync floor says the
        // fan is genuinely needed (hot CPU - stock reacts to its slowly-warming zone only)
        // or the GPU itself heats past the drawn start; back to stock after a calm minute.
        const byte GPU_ENGAGE_FLOOR = 5;   // sync floor that engages the custom curve
        const byte GPU_RELEASE_FLOOR = 3;  // floor at/below which the need is considered gone
        const int GPU_PARK_TICKS = 5;      // calm ticks (~15 s) after which the keep-alive is
                                           // dropped and the fan is allowed to stop (parked):
                                           // a spinning engaged fan gets PWM-cut blips every
                                           // ~minute from brief dGPU wakes - audible in the
                                           // quiet cooldown, so the fan must not linger there
        const int GPU_RELEASE_TEMP = 56;   // stock takes over only once the heatsink has
                                           // really cooled: handing a warm zone to stock makes
                                           // it start/stop the fan on the sensor rebound
                                           // (observed: handoff at CPU 60 pulses, at 55 clean)

        static bool gpuEngaged;
        static int gpuCalmTicks;

        public enum GpuPhase { Custom, Stock, Engaged, Parked }

        // Live phase of the hybrid for the UI: which algorithm owns the GPU fan right now
        public static GpuPhase GpuFanPhase
        {
            get
            {
                if (!AppConfig.IsMode("fan_gpu_stock")) return GpuPhase.Custom;
                if (!gpuEngaged) return GpuPhase.Stock;
                return gpuCalmTicks >= GPU_PARK_TICKS ? GpuPhase.Parked : GpuPhase.Engaged;
            }
        }

        static int lastTemp = NO_TEMP;
        static readonly byte[]?[] lastWritten = new byte[3][];
        static readonly bool[] spinning = new bool[3];
        static int gpuActiveTicks;

        static FanMaxTempControl()
        {
            timer.Elapsed += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Logger.WriteLine("FanSync: " + ex.Message); }
            };
        }

        public static bool IsEnabled => AppConfig.Is("fan_sync_max_temp");
        public static bool IsHystEnabled => AppConfig.Is("fan_hyst");
        // fan_gpu_stock keeps the timer alive on its own: the hybrid state machine must
        // be able to finish an engagement even with sync and hysteresis both unchecked
        public static bool IsAnyEnabled => IsEnabled || IsHystEnabled || AppConfig.IsMode("fan_gpu_stock");

        public static string FanName(AsusFan device) => device switch
        {
            AsusFan.GPU => "gpu",
            AsusFan.Mid => "mid",
            _ => "cpu",
        };

        // Per-fan per-mode stop temperature, clamped to a sane band below the curve start
        public static int HystStop(AsusFan device, int firstPointTemp)
        {
            int stop = AppConfig.GetMode("fan_hyst_" + FanName(device), firstPointTemp - 10);
            return Math.Max(START_TEMP, Math.Min(firstPointTemp - HYST_MIN_GAP, stop));
        }

        public static void Start()
        {
            lock (syncLock)
            {
                lastTemp = NO_TEMP;
                Array.Clear(lastWritten);
                Array.Clear(spinning);
                // gpuEngaged deliberately survives restarts: if EC still holds the engaged
                // keep-alive curve (curve edits / checkbox toggles restart the engine
                // without touching EC), the state machine must keep owning it until a
                // proper release hands the fan back to stock
                if (timer.Enabled) return;
                timer.Start();
            }
            Logger.WriteLine("FanSync: started");
        }

        public static void Stop()
        {
            lock (syncLock) // waits for an in-flight tick, so no write lands after Stop returns
            {
                if (!timer.Enabled) return;
                timer.Stop();
            }
            Logger.WriteLine("FanSync: stopped");
        }

        static void Tick()
        {
            lock (syncLock)
            {
                if (!timer.Enabled) return;

                bool sync = IsEnabled, hyst = IsHystEnabled;
                if (!sync && !hyst) return;

                if (sync || AppConfig.IsMode("fan_gpu_stock"))
                {
                    int temp = -1;
                    float? cpu = HardwareControl.GetCPUTemp();
                    float? gpu = HardwareControl.GetGPUTemp();
                    if (cpu is > 0 and < 120) temp = (int)cpu;
                    if (gpu is > 0 and < 120 && (int)gpu > temp) temp = (int)gpu;
                    if (temp > 0 && Math.Abs(temp - lastTemp) >= TEMP_STEP) lastTemp = temp;
                }

                bool gpuActive = HardwareControl.GpuControl is Gpu.NVidia.NvidiaGpuControl nv && nv.IsGpuActive;
                gpuActiveTicks = gpuActive ? gpuActiveTicks + 1 : 0;

                Apply(AsusFan.CPU, sync, hyst);
                if (AppConfig.IsMode("fan_gpu_stock")) HybridGpu(sync, hyst);
                else Apply(AsusFan.GPU, sync, hyst);
                if (AppConfig.Is("mid_fan")) Apply(AsusFan.Mid, sync, hyst);
            }
        }

        // Stock at idle / custom under demand for the GPU fan. The stock EC algorithm keeps
        // the fan alive through dGPU wake/sleep flips, but reacts only to its own slowly
        // warming zone sensor - a hot CPU leaves it parked for many minutes. The custom
        // curve (with floors and forced start) takes over while there is real demand.
        static void HybridGpu(bool sync, bool hyst)
        {
            byte[] curve = AppConfig.GetFanConfig(AsusFan.GPU);
            if (AsusACPI.IsInvalidCurve(curve)) return;

            byte floor = (sync && lastTemp != NO_TEMP) ? EvalCurve(curve, lastTemp) : (byte)0;
            // Sustained activity only: a brief RTD3 wake reads a warm-ish diode (~45-50)
            // and must not engage the custom curve (that is a start kick for nothing)
            float? gpuTemp = HardwareControl.GetGPUTemp();
            bool gpuHot = gpuTemp is > 0 && (int)gpuTemp >= curve[0] && gpuActiveTicks >= GPU_ACTIVE_CONFIRM;

            if (!gpuEngaged)
            {
                if (floor < GPU_ENGAGE_FLOOR && !gpuHot) return; // stock rules
                gpuEngaged = true;
                gpuCalmTicks = 0;
                Logger.WriteLine($"GpuStock: engaging custom curve (floor {floor}, gpu {gpuTemp})");
            }
            else
            {
                bool calm = floor <= GPU_RELEASE_FLOOR && !gpuHot;
                gpuCalmTicks = calm ? gpuCalmTicks + 1 : 0;

                // Parked (fan already stopped) and the heatsink is cool: hand over to stock
                if (gpuCalmTicks >= GPU_PARK_TICKS && lastTemp != NO_TEMP && lastTemp < GPU_RELEASE_TEMP)
                {
                    gpuEngaged = false;
                    Logger.WriteLine("GpuStock: returning GPU fan to stock (mode reset)");
                    // EC has no per-fan "back to stock": reset the whole mode, then put the
                    // still-custom curves back. Apply (not raw writes) keeps the current
                    // hysteresis extension and floors, so a spinning CPU fan is not cut.
                    Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Mode.Modes.GetCurrentBase(), "GpuStock");
                    Array.Clear(lastWritten);
                    Apply(AsusFan.CPU, sync, hyst);
                    if (AppConfig.Is("mid_fan")) Apply(AsusFan.Mid, sync, hyst);
                    // The mode rewrite also reverted PPT/GPU limits to BIOS defaults - the
                    // dedupe in DynPL would silently skip them, so a full power re-apply
                    // runs off-thread (it sleeps internally)
                    Mode.DynamicPowerLimitControl.Invalidate();
                    Task.Run(() => Program.modeControl.AutoPower());
                    return;
                }
            }

            // Keep-alive only while there is demand; after GPU_PARK_TICKS of calm the fan
            // stops once (hysteresis blow-down) and the park blocks restarts - a stopped fan
            // has nothing for the dGPU wake blips to cut
            Apply(AsusFan.GPU, sync, hyst, assist: gpuCalmTicks < GPU_PARK_TICKS);
        }

        static void Apply(AsusFan device, bool sync, bool hyst, bool assist = false)
        {
            byte[] curve = AppConfig.GetFanConfig(device);
            if (AsusACPI.IsInvalidCurve(curve)) return;

            byte floor = (sync && lastTemp != NO_TEMP) ? EvalCurve(curve, lastTemp) : (byte)0;

            bool spin = false;
            if (hyst)
            {
                int i = (int)device;
                // The moment the fan stops, the base curve must go back to EC: the extended
                // curve's first point is not just a stop threshold, EC also RESTARTS the fan
                // there, and the sensor cooled by the fan's own airflow oscillates around it
                // (observed: 0->2700 pulsing at the stop line). Restart belongs to the base
                // start point only, so no stop-confirm delay here.
                spin = Program.acpi.GetFan(device) > 0;

                if (spin != spinning[i])
                {
                    spinning[i] = spin;
                    Logger.WriteLine($"FanHyst: {device} {(spin ? "spinning" : "stopped")}");
                }

                if (spin)
                {
                    int stop = HystStop(device, curve[0]);
                    if (stop < curve[0]) curve[0] = (byte)stop;
                    for (int k = 8; k < 16; k++) curve[k] = Math.Max(curve[k], MIN_SUSTAIN);
                }
            }

            // GPU fan idle park: while the dGPU is powered down, EC's input for this fan
            // is blown below any stop line in seconds by the fan's own airflow, and once
            // the fan stops it re-heats from the shared heatsink past low curve starts in
            // ~10 s - an unbreakable limit cycle for starts drawn at/below that equilibrium
            // (observed at 54-60 with CPU at 64-68). So a stopped GPU fan is parked with
            // its start raised to just under the second point; the drawn start returns
            // after the dGPU has been awake for GPU_ACTIVE_CONFIRM ticks (real use, not a
            // brief telemetry wake). Start assist below still overrides the park.
            if (device == AsusFan.GPU && hyst && !spin
                && gpuActiveTicks < GPU_ACTIVE_CONFIRM
                && HardwareControl.GpuControl is Gpu.NVidia.NvidiaGpuControl
                && curve[1] > curve[0] + 1)
                curve[0] = (byte)(curve[1] - 1);

            // A hybrid-engaged GPU fan runs on a keep-alive curve for the WHOLE engagement:
            // the zone sensor never reads below room temperature, so EC neither stops the
            // fan (blow-down would cross any hysteresis stop line in seconds) nor re-kicks
            // it; the one stop happens after the controlled release back to stock.
            if (assist && floor >= MIN_SUSTAIN) curve[0] = START_TEMP;
            // Start assist is for a STOPPED fan whose own sensor sits below the curve start;
            // a spinning fan gets the floor via speeds alone (keeps the curve shape stable)
            else if (floor >= START_ASSIST && curve[0] > START_TEMP && !spin) curve[0] = START_TEMP;
            if (floor > 0)
                for (int k = 8; k < 16; k++) curve[k] = Math.Max(curve[k], floor);

            if (lastWritten[(int)device] is byte[] prev && prev.SequenceEqual(curve)) return;

            byte[] written = (byte[])curve.Clone(); // SetFanCurve mutates the array (fan_scale)
            lastWritten[(int)device] = Program.acpi.SetFanCurve(device, curve) == 1 ? written : null;
        }

        // Curve speed (%) at a given temperature, linearly interpolated between the 8 points.
        // Below the first point the fan is OFF by EC semantics (proven on GU604): returning
        // the first speed there would fabricate floors/engagements at cold idle for curves
        // whose first speed is above the thresholds.
        static byte EvalCurve(byte[] curve, int temp)
        {
            if (temp < curve[0]) return 0;
            if (temp == curve[0]) return curve[8];

            for (int i = 1; i < 8; i++)
            {
                if (temp <= curve[i])
                {
                    int t0 = curve[i - 1], t1 = curve[i];
                    int s0 = curve[i + 7], s1 = curve[i + 8];
                    if (t1 <= t0) return (byte)Math.Max(s0, s1);
                    return (byte)(s0 + (s1 - s0) * (temp - t0) / (t1 - t0));
                }
            }

            return curve[15];
        }
    }
}
