using System.Reflection;

namespace PawnIO
{
    public sealed class IntelMsr : IDisposable
    {
        private const uint MSR_RAPL_POWER_UNIT   = 0x606;
        private const uint MSR_PKG_ENERGY_STATUS = 0x611;

        private readonly PawnIOWrapper _io = new();
        private bool _init;
        private double _energyUnit;
        private double _powerUnit;
        private double _timeUnit;
        private uint _lastEnergy;
        private long _lastTick;

        public bool IsInitialized => _init;

        public bool Initialize(Assembly assembly)
        {
            string name = assembly.GetName().Name + ".IntelMSR.bin";
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Initialize(ms.ToArray());
        }

        public bool Initialize(byte[] moduleData)
        {
            if (_init) return true;
            if (_io.Connect() != PawnIOWrapper.ConnectResult.OK || !_io.LoadModule(moduleData)) return false;

            if (!ReadMsr(MSR_RAPL_POWER_UNIT, out ulong unit)) return false;
            int esu = (int)((unit >> 8) & 0x1F);   // energy status units, bits [12:8]
            _energyUnit = 1.0 / (1UL << esu);
            _powerUnit = 1.0 / (1UL << (int)(unit & 0xF));          // bits [3:0]
            _timeUnit = 1.0 / (1UL << (int)((unit >> 16) & 0x1F));  // bits [20:16]

            _init = true;
            return true;
        }

        public float? GetPackagePower()
        {
            if (!_init || !ReadMsr(MSR_PKG_ENERGY_STATUS, out ulong raw)) return null;

            uint energy = (uint)raw;
            long tick = Environment.TickCount64;

            if (_lastTick == 0) { _lastEnergy = energy; _lastTick = tick; return null; }

            double seconds = (tick - _lastTick) / 1000.0;
            if (seconds < 0.05) return null;

            double joules = unchecked(energy - _lastEnergy) * _energyUnit; 
            _lastEnergy = energy;
            _lastTick = tick;

            return (float)(joules / seconds);
        }

        // MSR_PKG_POWER_LIMIT: [14:0] PL1, [15] PL1 enable, [16] clamp, [23:17] PL1 time
        // window, [46:32] PL2, [47] PL2 enable, [63] lock. The RAPL limit is enforced by
        // the CPU itself, so it holds regardless of what the EC programs through ACPI.
        private const uint MSR_PKG_POWER_LIMIT = 0x610;

        public readonly record struct PowerLimits(double Pl1, double Pl2, double Pl1Tau, bool Locked);

        /// <summary>Current package power limits, or null when the MSR is unavailable.</summary>
        public PowerLimits? GetPowerLimits()
        {
            if (!_init || !ReadMsr(MSR_PKG_POWER_LIMIT, out ulong v)) return null;
            return new PowerLimits(
                (v & 0x7FFF) * _powerUnit,
                ((v >> 32) & 0x7FFF) * _powerUnit,
                DecodeTau((int)((v >> 17) & 0x7F)),
                (v & (1UL << 63)) != 0);
        }

        /// <summary>Raw register value, for saving state before the loop starts trimming.</summary>
        public ulong? GetPowerLimitRaw()
            => _init && ReadMsr(MSR_PKG_POWER_LIMIT, out ulong v) ? v : null;

        /// <summary>Restores a value captured by <see cref="GetPowerLimitRaw"/>.</summary>
        public bool SetPowerLimitRaw(ulong raw)
            => _init && WriteMsr(MSR_PKG_POWER_LIMIT, raw);

        /// <summary>
        /// Sets PL1 and PL2 (and optionally the PL1 time window), leaving every other
        /// field alone. Zero pl2 or tau means "keep the current value". Returns false
        /// when the register is locked or a value is out of range.
        /// </summary>
        public bool SetLimits(double pl1, double pl2 = 0, double tauSeconds = 0)
        {
            if (!_init || !ReadMsr(MSR_PKG_POWER_LIMIT, out ulong v)) return false;
            if ((v & (1UL << 63)) != 0) return false;   // locked by firmware

            ulong raw1 = (ulong)Math.Round(pl1 / _powerUnit);
            if (raw1 == 0 || raw1 > 0x7FFF) return false;

            ulong updated = (v & ~0x7FFFUL) | raw1 | (1UL << 15);   // value + PL1 enable
            if (pl2 > 0)
            {
                ulong raw2 = (ulong)Math.Round(pl2 / _powerUnit);
                if (raw2 > 0x7FFF) return false;
                updated = (updated & ~(0x7FFFUL << 32)) | (raw2 << 32) | (1UL << 47);
            }
            if (tauSeconds > 0)
                updated = (updated & ~(0x7FUL << 17)) | ((ulong)EncodeTau(tauSeconds) << 17);

            return WriteMsr(MSR_PKG_POWER_LIMIT, updated);
        }

        // Time window is stored as t = 2^Y * (1 + Z/4) * timeUnit, Y = bits[4:0], Z = bits[6:5]
        private double DecodeTau(int field)
            => Math.Pow(2, field & 0x1F) * (1 + ((field >> 5) & 3) / 4.0) * _timeUnit;

        private int EncodeTau(double seconds)
        {
            double ticks = seconds / _timeUnit;
            int best = 0;
            double bestErr = double.MaxValue;
            for (int y = 0; y < 32; y++)
                for (int z = 0; z < 4; z++)
                {
                    double err = Math.Abs(Math.Pow(2, y) * (1 + z / 4.0) - ticks);
                    if (err < bestErr) { bestErr = err; best = y | (z << 5); }
                }
            return best;
        }

        private bool ReadMsr(uint msr, out ulong value)
        {
            value = 0;
            var output = new ulong[1];
            if (!_io.Execute("ioctl_read_msr", new ulong[] { msr }, output)) return false;
            value = output[0];
            return true;
        }

        private bool WriteMsr(uint msr, ulong value)
            => _io.Execute("ioctl_write_msr", new ulong[] { msr, value }, Array.Empty<ulong>());

        public void Dispose() => _io.Dispose();
    }
}
