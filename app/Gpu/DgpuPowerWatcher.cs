using System.Runtime.InteropServices;

namespace GHelper.Gpu
{
    /// <summary>
    /// Watches the NVIDIA dGPU's device power state (D0 = on, D3 = off) without waking it,
    /// using the PnP manager's cached DEVPKEY_Device_PowerData.
    ///
    /// Why: on some ROG BIOSes (e.g. GA503QS) the ACPI power-on method for the dGPU tells the
    /// EC "dGPU is on" through the same EC mailbox that performance-mode and fan-curve writes
    /// use. If the mailbox is still busy (~20 ms), the BIOS silently drops that notification and
    /// the EC keeps treating the dGPU as off: fans don't react to GPU load, then the EC falls
    /// into its over-temperature fallback (fans 100%, periodic PROCHOT).
    ///
    /// ModeControl marks the window in which G-Helper is writing to the EC. If the dGPU powers on
    /// inside that window, the mode and fan curves are re-sent once the GPU has settled.
    /// On by default; disable with config key "dgpu_ec_sync": 0.
    /// </summary>
    public static class DgpuPowerWatcher
    {
        const string GUID_DEVCLASS_DISPLAY = "{4D36E968-E325-11CE-BFC1-08002BE10318}";
        const uint CM_GETIDLIST_FILTER_CLASS = 0x00000200;
        const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;

        const int PowerDeviceD0 = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // DEVPKEY_Device_PowerData -> CM_POWER_DATA
        static readonly DEVPROPKEY DEVPKEY_Device_PowerData = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 32 };

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, in DEVPROPKEY PropertyKey, out uint PropertyType, byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

        static System.Threading.Timer? _timer;
        static string? _deviceId;
        static int _lastState = -1;
        static int _busy = 0;

        /// <summary>Raised (on a thread-pool thread) when the dGPU goes from a low-power state to D0.</summary>
        public static event Action<DateTime>? Woke;

        public static DateTime LastWake { get; private set; } = DateTime.MinValue;

        public static bool IsRunning => _timer is not null;

        /// <summary>Fresh read of whether the dGPU is in D0 right now. Does not wake the device.</summary>
        public static bool IsPoweredOn => ReadPowerState() == PowerDeviceD0;

        public static void Start(int intervalMs = 500)
        {
            if (_timer is not null) return;

            _deviceId = FindNvidiaDevice();
            if (_deviceId is null)
            {
                Logger.WriteLine("dGPU watcher: NVIDIA device not found");
                return;
            }

            _lastState = ReadPowerState();
            Logger.WriteLine($"dGPU watcher: {_deviceId} initial D{StateName(_lastState)}");
            _timer = new System.Threading.Timer(_ => Poll(), null, intervalMs, intervalMs);
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        static void Poll()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                int state = ReadPowerState();
                if (state <= 0 || state == _lastState) return;

                Logger.WriteLine($"dGPU power: D{StateName(_lastState)} -> D{StateName(state)}");

                bool woke = state == PowerDeviceD0 && _lastState > PowerDeviceD0;
                _lastState = state;

                if (woke)
                {
                    LastWake = DateTime.Now;
                    Woke?.Invoke(LastWake);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("dGPU watcher error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        static string StateName(int state) => state > 0 ? (state - 1).ToString() : "?";

        static string? FindNvidiaDevice()
        {
            uint flags = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out uint len, GUID_DEVCLASS_DISPLAY, flags) != 0 || len <= 1) return null;

            var buffer = new char[len];
            if (CM_Get_Device_ID_ListW(GUID_DEVCLASS_DISPLAY, buffer, len, flags) != 0) return null;

            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (id.StartsWith("PCI\\VEN_10DE", StringComparison.OrdinalIgnoreCase)) return id;

            return null;
        }

        /// <summary>Returns DEVICE_POWER_STATE (1 = D0 ... 4 = D3), or -1 if unavailable. Does not wake the device.</summary>
        static int ReadPowerState()
        {
            if (_deviceId is null || CM_Locate_DevNodeW(out uint devInst, _deviceId, 0) != 0) return -1;

            var data = new byte[64];
            uint size = (uint)data.Length;
            if (CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_PowerData, out _, data, ref size, 0) != 0 || size < 8) return -1;

            // CM_POWER_DATA: ULONG PD_Size; DEVICE_POWER_STATE PD_MostRecentPowerState; ...
            return BitConverter.ToInt32(data, 4);
        }
    }
}
