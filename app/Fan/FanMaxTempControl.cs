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
    //    point stays exactly where the user drew it.
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

        static int lastTemp = NO_TEMP;
        static readonly byte[]?[] lastWritten = new byte[3][];
        static readonly bool[] spinning = new bool[3];

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
        public static bool IsAnyEnabled => IsEnabled || IsHystEnabled;

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

                if (sync)
                {
                    int temp = -1;
                    float? cpu = HardwareControl.GetCPUTemp();
                    float? gpu = HardwareControl.GetGPUTemp();
                    if (cpu is > 0 and < 120) temp = (int)cpu;
                    if (gpu is > 0 and < 120 && (int)gpu > temp) temp = (int)gpu;
                    if (temp > 0 && Math.Abs(temp - lastTemp) >= TEMP_STEP) lastTemp = temp;
                }

                Apply(AsusFan.CPU, sync, hyst);
                Apply(AsusFan.GPU, sync, hyst);
                if (AppConfig.Is("mid_fan")) Apply(AsusFan.Mid, sync, hyst);
            }
        }

        static void Apply(AsusFan device, bool sync, bool hyst)
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

            // Start assist is for a STOPPED fan whose own sensor sits below the curve start;
            // a spinning fan gets the floor via speeds alone (keeps the curve shape stable)
            if (floor >= START_ASSIST && curve[0] > START_TEMP && !spin) curve[0] = START_TEMP;
            if (floor > 0)
                for (int k = 8; k < 16; k++) curve[k] = Math.Max(curve[k], floor);

            if (lastWritten[(int)device] is byte[] prev && prev.SequenceEqual(curve)) return;

            byte[] written = (byte[])curve.Clone(); // SetFanCurve mutates the array (fan_scale)
            lastWritten[(int)device] = Program.acpi.SetFanCurve(device, curve) == 1 ? written : null;
        }

        // Curve speed (%) at a given temperature, linearly interpolated between the 8 points
        static byte EvalCurve(byte[] curve, int temp)
        {
            if (temp <= curve[0]) return curve[8];

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
