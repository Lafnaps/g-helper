using GHelper.Helpers;
using PawnIO;

namespace GHelper.Mode
{
    // Software CPU temperature target for platforms without a hardware knob (Intel has no
    // equivalent of the Ryzen SMU thermal limit): a slow closed loop that trims PL1/PL2
    // below the mode's configured values while the CPU runs hotter than cpu_temp, and
    // restores them back as it cools.
    //
    // Two backends. When PawnIO is available the limit goes straight into the CPU's RAPL
    // register (MSR 0x610): 0.125 W steps instead of 1 W, a controllable averaging window,
    // and the written value can be read back. Otherwise it falls back to the ASUS ACPI PPT
    // endpoints, which is what upstream uses.
    //
    // Deliberately conservative to avoid hunting with the CPU's own governors (turbo,
    // TjMax throttling, NV temp target): reacts only to sustained heat (CONFIRM_TICKS),
    // steps down fast but up slow, and has a temperature dead band. Runtime-only: the
    // configured limits in config.json are never touched.
    //
    // Lifecycle mirrors FanMaxTempControl: (re)started by ModeControl.SetPower after the
    // base limits are applied, stopped by paths that reset power state behind its back.
    public static class DynamicPowerLimitControl
    {
        const int TICK_MS = 3000;
        const int CONFIRM_TICKS = 2; // sustained heat, not a spike

        // Loop tuning lives in hidden config keys: deliberate no-UI knobs for tinkerers,
        // defaults are chosen for stability against the CPU's own governors
        static int StepDown => Math.Max(1, AppConfig.Get("pl_dyn_step_down", 5)); // W per tick while too hot
        static int StepUp => Math.Max(1, AppConfig.Get("pl_dyn_step_up", 2));     // W per tick while cool enough
        static int Band => Math.Max(1, AppConfig.Get("pl_dyn_band", 4));          // °C below target before restoring
        static int MinPl => Math.Max(10, Math.Min(60, AppConfig.GetMode("pl_dyn_min", 20))); // trim floor, per mode

        // RAPL averaging window used while trimming. The firmware default here is ~56 s,
        // far too slow for a loop that ticks every 3 s - the cap would not bite before the
        // next decision. 0 keeps whatever the firmware set.
        static double Tau => AppConfig.Get("pl_dyn_tau", 2);

        static readonly System.Timers.Timer timer = new(TICK_MS);
        static readonly object plLock = new();

        static int offset;                     // watts currently shaved off the configured limits
        static int hotTicks;
        static int applied = int.MinValue;     // last written PL1, to dedupe writes
        static ulong? msrOriginal;             // raw MSR 0x610 captured before the first trim

        // Snapshot for the UI (mode label, Fans window), refreshed every tick
        public static bool IsRunning => timer.Enabled;
        public static bool IsTrimming { get; private set; }
        public static int Current { get; private set; }
        public static int Base { get; private set; }

        static DynamicPowerLimitControl()
        {
            timer.Elapsed += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Logger.WriteLine("DynPL: " + ex.Message); }
            };
        }

        public static bool IsEnabled =>
            !CpuInfo.IsAMD
            && AppConfig.IsMode("auto_apply_power")
            && AppConfig.GetMode("cpu_temp") > 0
            && AppConfig.GetMode("cpu_temp") < CpuInfo.DefaultTemp;

        public static void Start()
        {
            lock (plLock)
            {
                offset = 0;
                hotTicks = 0;
                applied = int.MinValue;
                if (timer.Enabled) return;
                timer.Start();
            }
            Logger.WriteLine("DynPL: started");
        }

        public static void Stop()
        {
            bool wasTrimming;
            lock (plLock) // waits for an in-flight tick, so no write lands after Stop returns
            {
                if (!timer.Enabled) return;
                timer.Stop();
                wasTrimming = IsTrimming;
                IsTrimming = false;

                // The trim must not outlive the loop: on app quit nothing else would ever
                // restore the limits and the CPU would keep running at the shaved watts.
                // On mode changes this is a harmless double-write before the new limits.
                if (msrOriginal is ulong raw)
                {
                    // Put PL1, its window and every other field back exactly as found
                    HardwareControl.IntelMsrSession()?.SetPowerLimitRaw(raw);
                    Logger.WriteLine("DynPL: RAPL register restored on stop");
                    msrOriginal = null;
                }

                if (offset > 0)
                {
                    int baseTotal = AppConfig.GetMode("limit_total");
                    int baseSlow = AppConfig.GetMode("limit_slow", baseTotal);
                    if (baseTotal >= AsusACPI.MinTotal && baseTotal <= AsusACPI.MaxTotal
                        && Program.acpi.IsSupported(AsusACPI.PPT_APUA0))
                    {
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, baseTotal, null);
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, baseSlow, null);
                        Logger.WriteLine($"DynPL: base {baseTotal}W restored on stop");
                    }
                    offset = 0;
                }
            }
            Logger.WriteLine("DynPL: stopped");
            if (wasTrimming) Program.modeControl.SetModeLabel();
        }

        // External EC resets (mode rewrite behind our back) invalidate the dedupe cache:
        // the next tick rewrites the current value even if the offset has not changed
        public static void Invalidate()
        {
            lock (plLock) applied = int.MinValue;
        }

        /// <summary>
        /// Applies a trimmed limit, preferring the CPU's own RAPL register and falling back
        /// to the ASUS ACPI endpoints. Returns false when neither backend accepted it.
        /// </summary>
        static bool WriteLimit(int total, int slow)
        {
            // Gate on the same opt-in as the base limits: mixing backends would leave the
            // configured ceiling in ACPI while the trim lives in RAPL
            if (ModeControl.UsesMsrPower())
            {
                var msr = HardwareControl.IntelMsrSession()!;
                // Capture the untouched register once, so Stop can put it back verbatim
                msrOriginal ??= msr.GetPowerLimitRaw();
                if (msr.SetLimits(total, slow, Tau)) return true;
                Logger.WriteLine("DynPL: MSR write rejected, using ACPI");
            }

            if (!Program.acpi.IsSupported(AsusACPI.PPT_APUA0)) return false;
            Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, total, null);
            Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, slow, null);
            return true;
        }

        static void Tick()
        {
            lock (plLock)
            {
                if (!timer.Enabled) return;

                int target = AppConfig.GetMode("cpu_temp");
                bool enabled = IsEnabled;

                float? t = HardwareControl.GetCPUTemp();
                if (t is not > 0) return;

                int baseTotal = AppConfig.GetMode("limit_total");
                int baseSlow = AppConfig.GetMode("limit_slow", baseTotal);
                if (baseTotal < AsusACPI.MinTotal || baseTotal > AsusACPI.MaxTotal) return;

                int minPl = MinPl;
                int maxOffset = Math.Max(0, baseTotal - minPl);

                if (enabled && t > target)
                {
                    if (++hotTicks >= CONFIRM_TICKS) offset = Math.Min(maxOffset, offset + StepDown);
                }
                else
                {
                    hotTicks = 0;
                    // slider moved back to Default (or apply unchecked) — release the trim entirely
                    if (!enabled) offset = 0;
                    else if (t < target - Band && offset > 0) offset = Math.Max(0, offset - StepUp);
                }

                int total = Math.Max(minPl, baseTotal - offset);
                int slow = Math.Max(minPl, baseSlow - offset);

                Base = baseTotal;
                Current = total;
                IsTrimming = offset > 0;

                if (total == applied) return;
                applied = total;

                if (WriteLimit(total, slow))
                    Logger.WriteLine($"DynPL: {baseTotal}W -> {total}W (CPU {(int)t}°C, target {target}°C)");
            }

            Program.modeControl.SetModeLabel(); // reflect the new value in the mode header
        }
    }
}
