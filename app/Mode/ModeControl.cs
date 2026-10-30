using GHelper.Gpu;
using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using PawnIO;

namespace GHelper.Mode
{
    public class ModeControl
    {

        static SettingsForm settings = Program.settingsForm;

        private static bool customFans = false;
        private static int customPower = 0;

        private int _cpuUV = 0;
        private int _igpuUV = 0;
        private int _cpuTemp = CpuInfo.DefaultTemp;
        private bool _ryzenPower = false;

        private static RyzenSmuService? _smu;
        private static readonly object _smuLock = new();

        private static RyzenSmuService? GetSmu()
        {
            lock (_smuLock)
            {
                if (_smu != null && _smu.IsInitialized) return _smu;
                _smu?.Dispose();
                _smu = new RyzenSmuService();
                if (!_smu.Initialize(System.Reflection.Assembly.GetExecutingAssembly()))
                {
                    _smu.Dispose();
                    _smu = null;
                }
                else
                {
                    Logger.WriteLine($"SMU Init: {_smu.CpuCodeName} ({_smu.Family}), SMU v{_smu.SmuVersion >> 16}.{(_smu.SmuVersion >> 8) & 0xFF}.{_smu.SmuVersion & 0xFF}");
                }
                return _smu;
            }
        }

        public static bool IsPawnAvailable()  => GetSmu() != null;
        public static bool IsPawnInstalled()   => RyzenSmuService.IsPawnInstalled();

        static System.Timers.Timer? reapplyTimer;
        static System.Timers.Timer modeToggleTimer = default!;
        static CancellationTokenSource _modeCts = new();
        static Task _modeTask = Task.CompletedTask;

        // dGPU/EC sync (on by default, "dgpu_ec_sync": 0 disables): tracks when G-Helper is writing mode/fan settings to the EC,
        // so a dGPU power-on landing in that window (whose "dGPU on" EC notification the BIOS may drop) can be repaired.
        static int _ecBurstDepth = 0;
        static DateTime _ecBurstEnd = DateTime.MinValue;
        static DateTime _lastDgpuResync = DateTime.MinValue;
        const int DgpuWakeToleranceMs = 750;   // watcher polls every 500 ms
        const int DgpuSettleMs = 2000;
        const int DgpuResyncCooldownS = 60;

        // Custom build: the BIOS gives up on an EC mailbox message after ~20 ms and still reports success for
        // mode changes and fan-curve "select fan" messages. Space G-Helper's EC writes and re-send once when quiet.
        static int EcWriteGapMs => Math.Clamp(AppConfig.Get("ec_write_gap", 150), 0, 2000);   // between fan-curve writes
        static int EcModeGapMs => Math.Clamp(AppConfig.Get("ec_mode_gap", 300), 100, 3000);   // mode write -> fan curves
        static System.Timers.Timer? quietReapplyTimer;

        /// <summary>
        /// Re-send mode, fan curves and power limits once, ec_reapply_delay seconds (default 10, 0 = off) after
        /// startup, wake, charger change or GPU mode switch. The current mode can't be read back from the EC,
        /// so re-sending is the only way to make sure it took. Debounced: a new trigger restarts the countdown.
        /// </summary>
        public void ScheduleQuietReapply(string reason)
        {
            int delay = AppConfig.Get("ec_reapply_delay", 10);
            if (delay <= 0) return;

            if (quietReapplyTimer is null)
            {
                quietReapplyTimer = new System.Timers.Timer { AutoReset = false };
                quietReapplyTimer.Elapsed += QuietReapply_Elapsed;
            }

            quietReapplyTimer.Stop();
            quietReapplyTimer.Interval = delay * 1000;
            quietReapplyTimer.Start();
            Logger.WriteLine($"Quiet re-apply in {delay}s ({reason})");
        }

        private void QuietReapply_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Still writing to the EC (e.g. a mode change in progress): try again shortly
            if (Volatile.Read(ref _ecBurstDepth) > 0)
            {
                quietReapplyTimer!.Interval = 2000;
                quietReapplyTimer.Start();
                return;
            }

            Logger.WriteLine("Quiet re-apply: re-sending mode, fans, power and battery limit");
            SetPerformanceMode();

            // The battery limit goes through the same EC mailbox; BatteryControl waits for the mode writes above
            GHelper.Battery.BatteryControl.ReapplyLimit();
        }

        /// <summary>
        /// Custom build: for other EC mailbox writes (e.g. battery limit). Waits until G-Helper's mode/fan writes
        /// are done (up to maxMs), then marks the write as an EC burst so the dGPU watcher accounts for it.
        /// </summary>
        public static void RunEcWrite(Action write, int maxMs = 5000)
        {
            for (int waited = 0; Volatile.Read(ref _ecBurstDepth) > 0 && waited < maxMs; waited += 50) Thread.Sleep(50);

            BeginEcBurst();
            try { write(); }
            finally { EndEcBurst(); }
        }

        public ModeControl()
        {
            int reapplyTime = AppConfig.Get("reapply_time", IsReapplyTempRequired() ? 30 : 0);
            if (reapplyTime > 0)
            {
                reapplyTimer = new System.Timers.Timer(reapplyTime * 1000);
                reapplyTimer.Elapsed += ReapplyTimer_Elapsed;
            }

            if (AppConfig.IsNotFalse("dgpu_ec_sync"))
            {
                DgpuPowerWatcher.Woke += OnDgpuWoke;
                DgpuPowerWatcher.Start();
            }
        }

        private static void BeginEcBurst() => Interlocked.Increment(ref _ecBurstDepth);

        private static void EndEcBurst()
        {
            _ecBurstEnd = DateTime.Now;
            Interlocked.Decrement(ref _ecBurstDepth);
        }

        private void OnDgpuWoke(DateTime wakeTime)
        {
            // Only Standard (Optimus) mode powers the dGPU on and off at runtime
            if (AppConfig.Get("gpu_mode") != AsusACPI.GPUModeStandard) return;

            bool inBurst = Volatile.Read(ref _ecBurstDepth) > 0;
            bool justAfterBurst = (wakeTime - _ecBurstEnd).TotalMilliseconds <= DgpuWakeToleranceMs;
            if (!inBurst && !justAfterBurst) return;

            if ((DateTime.Now - _lastDgpuResync).TotalSeconds < DgpuResyncCooldownS)
            {
                Logger.WriteLine("dGPU woke during EC writes, resync skipped (cooldown)");
                return;
            }
            _lastDgpuResync = DateTime.Now;

            Logger.WriteLine("dGPU woke during EC writes, re-sending mode and fans after it settles");

            Task.Run(async () =>
            {
                // Let the current mode/fan writes finish, then give the dGPU power-on time to complete
                for (int i = 0; i < 100 && Volatile.Read(ref _ecBurstDepth) > 0; i++) await Task.Delay(100);
                await Task.Delay(DgpuSettleMs);
                SetPerformanceMode();
            });
        }

        // Cezanne/Rembrandt (Renoir) + Phoenix/HawkPoint (Mobile) silently reset temp limit under load.
        private static bool IsReapplyTempRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Renoir or CpuFamily.Mobile;
        }

        private static bool IsReapplyRyzenRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Raphael;
        }

        private static void SetReapplyEnabled(bool enabled)
        {
            // Custom build: keep re-applying while fPPT is set through the SMU, even with UV off
            enabled = enabled || _fastSmu;

            if (reapplyTimer == null && enabled && _fastSmu)
            {
                reapplyTimer = new System.Timers.Timer(AppConfig.Get("reapply_time", 30) * 1000);
                reapplyTimer.Elapsed += (s, e) => Program.modeControl.ReapplyTimer_Elapsed(s, e);
            }

            if (reapplyTimer != null) reapplyTimer.Enabled = enabled;
        }


        private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Timer used to run only with UV applied; it can now also run for SMU fPPT alone
            if (AppConfig.IsApplyUV()) SetCPUTemp(AppConfig.GetMode("cpu_temp"));
            SetRyzenPower();
            if (_fastSmu) SetFastSmu();
        }

        // Custom build: fPPT through the SMU (same command RyzenAdj/UXTU use) when the BIOS has no fPPT ACPI method.
        // ASUS firmware overwrites SMU limits on mode/charger changes, so this is re-applied by the reapply timer.
        static bool _fastSmu = false;

        public static bool IsFastSmuAvailable()
            => CpuInfo.IsAMD && !Program.acpi.IsSupported(AsusACPI.PPT_APUC1) && !Program.acpi.IsAllAmdPPT() && IsPawnInstalled();

        private SmuStatus? SetFastSmu(bool log = false)
        {
            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);
            if (limit_fast < AsusACPI.MinTotal || limit_fast > AsusACPI.MaxTotal) return null;

            var smu = GetSmu();
            if (smu == null) return null;

            var status = smu.SetFastLimit(limit_fast);
            if (log) Logger.WriteLine($"FAST (SMU): {limit_fast}W {status}");
            return status;
        }

        public void WaitForApply()
        {
            try { _modeTask.Wait(5000); } catch { }
        }

        public void AutoPerformance(bool powerChanged = false)
        {
            int mode = AppConfig.Get("performance_" + Program.PerformanceKey());
            Logger.WriteLine($"{Program.currentSource} Performance Mode: {Modes.GetName(mode == -1 ? Modes.GetCurrent() : mode)}");

            if (mode != -1)
                SetPerformanceMode(mode, powerChanged);
            else
                SetPerformanceMode(Modes.GetCurrent());
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            Program.acpi.SetPerformanceMode(Modes.GetCurrentBase());

            // Default power mode
            AppConfig.RemoveMode("powermode");
            PowerNative.SetPowerMode(Modes.GetCurrentBase());
        }

        public void Toast()
        {
            Program.toast.RunToast(Modes.GetCurrentName(), SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? ToastIcon.Charger : ToastIcon.Battery);
        }

        public void SetPerformanceMode(int mode = -1, bool notify = false)
        {

            int oldMode = Modes.GetCurrent();
            if (mode < 0) mode = oldMode;

            if (!Modes.Exists(mode)) mode = 0;

            settings.ShowMode(mode);

            Modes.SetCurrent(mode);


            _modeCts.Cancel();
            _modeCts = new CancellationTokenSource();
            var ct = _modeCts.Token;

            _modeTask = Task.Run(async () =>
            {
                BeginEcBurst();
                bool burstOpen = true;
                try
                {
                    bool reset = AppConfig.IsResetRequired() && (Modes.GetBase(oldMode) == Modes.GetBase(mode)) && customPower > 0 && !AppConfig.IsApplyPower();

                    customFans = false;
                    customPower = 0;

                    SetModeLabel();

                    // Workaround for not properly resetting limits on G14 2024
                    if (reset)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, (Modes.GetBase(oldMode) != 1) ? AsusACPI.PerformanceTurbo : AsusACPI.PerformanceBalanced, "ModeReset");
                        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                    }

                    ct.ThrowIfCancellationRequested();

                    if (AppConfig.Is("status_mode")) Program.acpi.DeviceSet(AsusACPI.StatusMode, [0x00, Modes.GetBase(mode) == AsusACPI.PerformanceSilent ? (byte)0x02 : (byte)0x03], "StatusMode");
                    Program.acpi.SetPerformanceMode(AppConfig.IsManualModeRequired() ? AsusACPI.PerformanceManual : Modes.GetBase(mode));

                    // With dgpu_ec_sync, GPU clocks (NVAPI, which can power the dGPU on) are applied after the
                    // EC writes below instead of in the middle of them
                    bool deferClocks = DgpuPowerWatcher.IsRunning;
                    if (!deferClocks) SetGPUClocks();

                    // Custom build: give the EC time to finish the mode switch before fan-curve writes
                    // (the BIOS drops EC mailbox messages that wait more than ~20 ms)
                    await Task.Delay(TimeSpan.FromMilliseconds(EcModeGapMs), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoFans();
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoPower();

                    if (deferClocks)
                    {
                        EndEcBurst();
                        burstOpen = false;
                        await Task.Delay(TimeSpan.FromMilliseconds(DgpuWakeToleranceMs + 250), ct);
                        SetGPUClocks();
                    }

                    var command = AppConfig.GetModeString("mode_command");
                    if (command is not null)
                    {   Logger.WriteLine("Running mode command: " + command);
                        RestrictedProcessHelper.RunAsRestrictedUser(command);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.WriteLine($"SetPerformanceMode cancelled (mode {mode})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"SetPerformanceMode failed (mode {mode}): {ex.Message}");
                }
                finally
                {
                    if (burstOpen) EndEcBurst();
                }
            }, ct);

            if (notify) Toast();

            if (!AppConfig.Is("skip_powermode"))
            {
                // Windows power mode
                if (AppConfig.GetModeString("powermode") is not null)
                    PowerNative.SetPowerMode(AppConfig.GetModeString("powermode"));
                else
                    PowerNative.SetPowerMode(Modes.GetBase(mode));

                if (AppConfig.IsAutoASPM()) PowerNative.SetBalancedASPM();
                if (AppConfig.IsAutoStandbyNetworking()) PowerNative.SetConnectivityInStandby();
            }

            // CPU Boost setting override
            if (AppConfig.GetMode("auto_boost") != -1)
                    PowerNative.SetCPUBoost(AppConfig.GetMode("auto_boost"));

            settings.FansInit();
        }


        private void ModeToggleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            modeToggleTimer.Stop();
            Logger.WriteLine($"Hotkey mode: {Modes.GetCurrent()}");
            SetPerformanceMode();

        }

        public void CyclePerformanceMode(bool back = false)
        {
            int delay = AppConfig.Get("mode_delay", 1000);

            if (modeToggleTimer is null)
            {
                modeToggleTimer = new System.Timers.Timer(delay);
                modeToggleTimer.Elapsed += ModeToggleTimer_Elapsed;
            }

            modeToggleTimer.Stop();
            modeToggleTimer.Start();
            Modes.SetCurrent(Modes.GetNext(back));
            Toast();
        }

        // Custom build: fan-curve writes can now come from background threads (fan chart, mode changes,
        // re-applies). Run them one set at a time so the spacing between EC writes holds, and count them
        // as an EC write window for the dGPU watcher.
        static readonly object _fansLock = new();

        public void AutoFans(bool force = false)
        {
            lock (_fansLock)
            {
                BeginEcBurst();
                try { AutoFansCore(force); }
                finally { EndEcBurst(); }
            }
        }

        private void AutoFansCore(bool force)
        {
            customFans = false;

            if (AppConfig.IsApplyFans() || force)
            {

                bool xgmFan = false;
                if (AppConfig.Is("xgm_fan"))
                {
                    XGM.SetFan(AppConfig.GetFanConfig(AsusFan.XGM));
                    xgmFan = Program.acpi.IsXGConnected();
                }

                // Custom build: each curve write is "select fan" + "data" EC messages, and the BIOS doesn't check
                // the select. Space the writes so each one finds the EC mailbox free.
                int cpuResult = Program.acpi.SetFanCurve(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                Thread.Sleep(EcWriteGapMs);
                int gpuResult = Program.acpi.SetFanCurve(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                if (AppConfig.Is("mid_fan"))
                {
                    Thread.Sleep(EcWriteGapMs);
                    Program.acpi.SetFanCurve(AsusFan.Mid, AppConfig.GetFanConfig(AsusFan.Mid));
                }


                // Alternative way to set fan curve
                if (cpuResult != 1 || gpuResult != 1)
                {
                    Thread.Sleep(EcWriteGapMs);
                    cpuResult = Program.acpi.SetFanRange(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                    Thread.Sleep(EcWriteGapMs);
                    gpuResult = Program.acpi.SetFanRange(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                    // Something went wrong, resetting to default profile
                    if (cpuResult != 1 || gpuResult != 1)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Reset Mode");
                        settings.LabelFansResult("Model doesn't support custom fan curves");
                    }
                }
                else
                {
                    settings.LabelFansResult("");
                    customFans = true;
                }

                Thread.Sleep(EcWriteGapMs);
                int hystUp = AppConfig.GetMode("hysteresis_up");
                int hystDown = AppConfig.GetMode("hysteresis_down");
                if (hystUp > 0 && hystDown > 0)
                    Program.acpi.SetFanHysteresis(hystUp, hystDown);

                // force set PPTs for missbehaving bios on FX507/517 series
                if ((AppConfig.IsPowerRequired() || xgmFan) && !AppConfig.IsApplyPower())
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, 80, "PowerLimit Fix A0");
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, 80, "PowerLimit Fix A3");
                    });
                }

            } else
            {
                XGM.Reset();
            }

            SetModeLabel();

        }

        public void AutoPower(bool launchAsAdmin = false)
        {

            customPower = 0;

            bool applyPower = AppConfig.IsApplyPower();
            bool applyFans = AppConfig.IsApplyFans();

            // Custom build: stop re-applying SMU fPPT once power limits are no longer applied
            if (!applyPower && _fastSmu)
            {
                _fastSmu = false;
                SetReapplyEnabled(AppConfig.IsApplyUV());
            }

            if (applyPower && !applyFans && AppConfig.IsFanRequired())
            {
                AutoFans(true);
                Thread.Sleep(500);
            }

            if (applyPower) SetPower(launchAsAdmin);

            Thread.Sleep(500);
            SetGPUPower();
            if (applyPower) SetCrossPower();
            AutoRyzen();

            if (IsReapplyRyzenRequired())
                Task.Delay(5000).ContinueWith(_ => { AutoRyzen(); ReadRyzenLimits(); });

        }

        public void SetModeLabel()
        {
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + Modes.GetCurrentName() + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
        }

        public void SetRyzenPower(bool init = false)
        {
            if (init) _ryzenPower = true;

            if (!_ryzenPower) return;
            if (!AppConfig.IsApplyPower()) return;

            var smu = GetSmu();
            if (smu == null) return;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            smu.SetAllLimits(limit_total, limit_fast, limit_slow,
                out SmuStatus stapm, out SmuStatus fast, out SmuStatus slow);
            if (init) Logger.WriteLine($"STAPM: {limit_total}W {stapm} | SLOW: {limit_slow}W {slow} | FAST: {limit_fast}W {fast}");
        }

        public void SetPower(bool launchAsAdmin = false)
        {

            bool allAMD = Program.acpi.IsAllAmdPPT();
            bool isAMD = CpuInfo.IsAMD;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_cpu = AppConfig.GetMode("limit_cpu");
            int limit_slow = AppConfig.GetMode("limit_slow");
            int limit_fast = AppConfig.GetMode("limit_fast");

            if (limit_slow < 0 || allAMD) limit_slow = limit_total;

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            if (limit_cpu > AsusACPI.MaxCPU) return;
            if (limit_cpu < AsusACPI.MinCPU) return;

            if (limit_fast > AsusACPI.MaxTotal) return;
            if (limit_fast < AsusACPI.MinTotal) return;

            if (limit_slow > AsusACPI.MaxTotal) return;
            if (limit_slow < AsusACPI.MinTotal) return;

            // SPL and SPPT
            if (Program.acpi.IsSupported(AsusACPI.PPT_APUA0))
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, limit_total, "PowerLimit A3");
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, limit_slow, "PowerLimit A0");
                customPower = limit_total;
            }
            else if (isAMD)
            {
                if (ProcessHelper.IsUserAdministrator())
                {
                    SetRyzenPower(true);
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }
            }

            if (allAMD) // CPU limit all amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, limit_cpu, "PowerLimit B0");
                customPower = limit_cpu;
            }
            else if (isAMD && Program.acpi.IsSupported(AsusACPI.PPT_APUC1)) // FPPT boost for non all-amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, limit_fast, "PowerLimit C1");
            }
            else if (IsFastSmuAvailable()) // Custom build: no fPPT in BIOS, set it through the SMU
            {
                if (ProcessHelper.IsUserAdministrator())
                {
                    _fastSmu = SetFastSmu(true) == SmuStatus.OK;
                    SetReapplyEnabled(AppConfig.IsApplyUV());
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }
            }

            SetModeLabel();

        }

        public void SetCrossPower()
        {
            int gpucpu = AppConfig.GetMode("limit_gpucpu");
            int crossload = AppConfig.GetMode("limit_crossload");
            int cputemp = AppConfig.GetMode("limit_cputemp");

            if (gpucpu >= AsusACPI.MinGPUtoCPU && gpucpu <= AsusACPI.MaxGPUtoCPU && Program.acpi.IsSupported(AsusACPI.PPT_GPUCPU9C))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUCPU9C, gpucpu, "PowerLimit 9C (GPU to CPU)");

            if (crossload >= AsusACPI.MinCrossLoad && crossload <= AsusACPI.MaxCrossLoad && Program.acpi.IsSupported(AsusACPI.PPT_CROSS9F))
                Program.acpi.DeviceSet(AsusACPI.PPT_CROSS9F, crossload, "PowerLimit 9F (Cross Loading)");

            if (cputemp >= AsusACPI.MinCPUTemp && cputemp <= AsusACPI.MaxCPUTemp && Program.acpi.IsSupported(AsusACPI.PPT_TEMP9E))
                Program.acpi.DeviceSet(AsusACPI.PPT_TEMP9E, cputemp, "PowerLimit 9E (CPU Temp)");
        }

        public void SetGPUClocks(bool launchAsAdmin = true, bool reset = false)
        {
            Task.Run(() =>
            {

                int core = AppConfig.GetMode("gpu_core");
                int memory = AppConfig.GetMode("gpu_memory");
                int clock_limit = AppConfig.GetMode("gpu_clock_limit");

                if (reset) core = memory = clock_limit = 0;

                if (core == -1 && memory == -1 && clock_limit == -1) return;
                //if ((gpu_core > -5 && gpu_core < 5) && (gpu_memory > -5 && gpu_memory < 5)) launchAsAdmin = false;

                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Clocks: Eco"); return; }
                if (HardwareControl.GpuControl is null) { Logger.WriteLine("Clocks: NoGPUControl"); return; }
                if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Clocks: NotNvidia"); return; }

                NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
                try
                {
                    int statusClocks = nvControl.SetClocks(core, memory);
                    int statusLimit = nvControl.SetMaxGPUClock(clock_limit);
                    if ((statusLimit != 0 || statusClocks != 0) && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Clocks Error:" + ex.ToString());
                }

                settings.GPUInit();
            });
        }

        public void SetGPUPower()
        {

            int gpu_boost = AppConfig.GetMode("gpu_boost");
            int gpu_temp = AppConfig.GetMode("gpu_temp");
            int gpu_power = AppConfig.GetMode("gpu_power");

            int boostResult = -1;

            if (gpu_power >= AsusACPI.MinGPUPower && gpu_power <= AsusACPI.MaxGPUPower && Program.acpi.IsSupported(AsusACPI.GPU_POWER))
                Program.acpi.DeviceSet(AsusACPI.GPU_POWER, gpu_power, "PowerLimit TGP (GPU VAR)");

            if (gpu_boost >= AsusACPI.MinGPUBoost && gpu_boost <= AsusACPI.MaxGPUBoost && Program.acpi.IsSupported(AsusACPI.PPT_GPUC0))
                boostResult = Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0 (GPU BOOST)");

            if (gpu_temp >= AsusACPI.MinGPUTemp && gpu_temp <= AsusACPI.MaxGPUTemp && Program.acpi.IsSupported(AsusACPI.PPT_GPUC2))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC2, gpu_temp, "PowerLimit C2 (GPU TEMP)");

            // Fallback
            if (boostResult == 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0");

        }

        public SmuStatus? SetCPUTemp(int cpuTemp, bool log = false)
        {
            if (cpuTemp < CpuInfo.MinTemp || cpuTemp > CpuInfo.DefaultTemp) return null;
            if (cpuTemp == CpuInfo.DefaultTemp && _cpuTemp == CpuInfo.DefaultTemp) return null;

            var smu = GetSmu();
            if (smu == null) return null;
            SmuStatus status = smu.SetThm(cpuTemp);
            if (log) Logger.WriteLine($"CPU Temp: {cpuTemp}°C {status}");
            if (status == SmuStatus.OK) _cpuTemp = cpuTemp;
            return status;
        }

        public void SetUV(int cpuUV)
        {
            if (!CpuInfo.IsSupportedUV()) return;

            if (cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoAll(cpuUV);
                Logger.WriteLine($"UV: {cpuUV} {status}");
                if (status == SmuStatus.OK) _cpuUV = cpuUV;
            }
        }

        public void SetUViGPU(int igpuUV)
        {
            if (!CpuInfo.IsSupportedUViGPU()) return;

            if (igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoGfx(igpuUV);
                Logger.WriteLine($"iGPU UV: {igpuUV} {status}");
                if (status == SmuStatus.OK) _igpuUV = igpuUV;
            }
        }

        public string SetRyzen(bool launchAsAdmin = false)
        {
            if (!ProcessHelper.IsUserAdministrator())
            {
                if (launchAsAdmin) ProcessHelper.RunAsAdmin("uv");
                return string.Empty;
            }

            var smu = GetSmu();
            if (smu == null) return string.Empty;

            var lines = new System.Text.StringBuilder();
            try
            {
                int cpuUV   = AppConfig.GetMode("cpu_uv",   0);
                int igpuUV  = AppConfig.GetMode("igpu_uv",  0);
                int cpuTemp = AppConfig.GetMode("cpu_temp");
                string? cpuUVCores = AppConfig.GetModeString("cpu_uv_cores");

                if (CpuInfo.IsSupportedUV() && cpuUVCores is not null)
                {
                    int core = 0;
                    foreach (var token in cpuUVCores.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token, out int uv) && -uv >= CpuInfo.MinCPUUV && -uv <= CpuInfo.MaxCPUUV)
                        {
                            SmuStatus s = smu.SetCoPer(core, -uv);
                            Logger.WriteLine($"UV core {core}: {-uv} {s}");
                            if (s == SmuStatus.OK && uv != 0) _cpuUV = -uv;
                        }
                        core++;
                    }
                    lines.AppendLine($"CPU UV cores {cpuUVCores}");
                }
                else if (CpuInfo.IsSupportedUV() && cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
                {
                    SmuStatus s = smu.SetCoAll(cpuUV);
                    Logger.WriteLine($"UV: {cpuUV} {s}");
                    if (s == SmuStatus.OK) _cpuUV = cpuUV;
                    lines.AppendLine($"CPU UV {cpuUV}: {s}");
                }

                if (CpuInfo.IsSupportedUViGPU() && igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
                {
                    SmuStatus s = smu.SetCoGfx(igpuUV);
                    Logger.WriteLine($"iGPU UV: {igpuUV} {s}");
                    if (s == SmuStatus.OK) _igpuUV = igpuUV;
                    lines.AppendLine($"iGPU UV {igpuUV}: {s}");
                }

                SmuStatus? tempStatus = SetCPUTemp(cpuTemp, true);
                if (tempStatus.HasValue) lines.AppendLine($"CPU Temp {cpuTemp}°C: {tempStatus}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            SetReapplyEnabled(AppConfig.IsApplyUV());
            return lines.ToString().TrimEnd();
        }

        public string ReadRyzenLimits()
        {
            var smu = GetSmu();
            if (smu == null) return string.Empty;

            try
            {
                PowerLimits? lim = smu.GetPowerLimits();
                if (lim == null) return string.Empty;

                string line = $"SPL: {lim.Stapm:F1}W | sPPT {lim.Slow:F1}W | fPPT {lim.Fast:F1}W";
                if (lim.ApuSlow.HasValue) line += $" | APU {lim.ApuSlow.Value:F1}W";
                line += $", Temp: {lim.TctlTemp:F0}°C";
                Logger.WriteLine("Ryzen Limits: " + line);
                return line;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ReadRyzenLimits Error: " + ex.ToString());
                return string.Empty;
            }
        }

        public void ResetRyzen()
        {
            if (_cpuUV != 0) SetUV(0);
            if (_igpuUV != 0) SetUViGPU(0);
            if (_cpuTemp != CpuInfo.DefaultTemp) SetCPUTemp(CpuInfo.DefaultTemp, true);
            SetReapplyEnabled(false);
        }

        public void AutoRyzen()
        {
            if (!CpuInfo.IsAMD) return;

            if (AppConfig.IsApplyUV()) SetRyzen();
            else ResetRyzen();
        }

        public void AutoCPUTemp()
        {
            if (!CpuInfo.IsAMD) return;
            if (!AppConfig.IsApplyUV()) return;
            if (!ProcessHelper.IsUserAdministrator()) return;

            try
            {
                SetCPUTemp(AppConfig.GetMode("cpu_temp"), true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AutoCPUTemp Error: " + ex.Message);
            }
        }

        public void ShutdownReset()
        {
            if (!AppConfig.IsShutdownReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode,AsusACPI.PerformanceBalanced, "Mode Reset");
        }

        public void SleepReset()
        {
            if (!AppConfig.IsSleepReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Sleep Reset");
        }

    }
}
