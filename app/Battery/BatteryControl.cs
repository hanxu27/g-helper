using GHelper.Helpers;
using Microsoft.Win32;
using System.Diagnostics;

namespace GHelper.Battery
{
    public static class BatteryControl
    {

        static bool _chargeFull = AppConfig.Is("charge_full");
        public static bool chargeFull
        {
            get
            {
                return _chargeFull;
            }
            set
            {
                AppConfig.Set("charge_full", value ? 1 : 0);
                _chargeFull = value;
            }
        }

        public static void ToggleBatteryLimitFull()
        {
            if (chargeFull) SetBatteryChargeLimit();
            else SetBatteryLimitFull();
        }

        // Custom build: the battery limit goes through the EC mailbox, which the BIOS gives up on after ~20 ms.
        // G-Helper used to re-write it several times around resume/screen-on/charger events, right alongside
        // mode/fan writes and dGPU power-ons. Writes now run in the background, skip a value that was written
        // successfully within battery_dedupe seconds (default 10), wait for other EC writes, and always send
        // the most recently requested value.
        static readonly object _limitLock = new();
        static volatile int _pendingLimit = -1;
        static int _lastLimitWritten = -1;
        static long _lastLimitTime = 0;

        static void QueueBatteryLimit(int limit, bool force = false)
        {
            _pendingLimit = limit;
            Task.Run(() =>
            {
                lock (_limitLock)
                {
                    int target = _pendingLimit;
                    if (target < 0) return;

                    long now = Environment.TickCount64;
                    int dedupeMs = AppConfig.Get("battery_dedupe", 10) * 1000;
                    if (!force && target == _lastLimitWritten && now - _lastLimitTime < dedupeMs)
                    {
                        Logger.WriteLine($"BatteryLimit = {target} : skipped (already set)");
                        return;
                    }

                    int result = 0;
                    GHelper.Mode.ModeControl.RunEcWrite(() => result = Program.acpi.DeviceSet(AsusACPI.BatteryLimit, target, "BatteryLimit"));

                    // Only remember writes the EC accepted, so a dropped one is retried next time
                    if (result == 1) { _lastLimitWritten = target; _lastLimitTime = Environment.TickCount64; }
                    else _lastLimitWritten = -1;
                }
            });
        }

        /// <summary>Custom build: force one re-send of the current target, used by the quiet re-apply.</summary>
        public static void ReapplyLimit()
        {
            int limit = chargeFull ? 100 : AppConfig.Get("charge_limit");
            if (limit < 40 || limit > 100) return;
            QueueBatteryLimit(limit, force: true);
        }

        public static void SetBatteryLimitFull()
        {
            chargeFull = true;
            QueueBatteryLimit(100);
            Program.settingsForm.VisualiseBatteryFull();
        }

        public static void UnSetBatteryLimitFull()
        {
            chargeFull = false;
            Logger.WriteLine("Battery fully charged");
            Program.settingsForm.Invoke(Program.settingsForm.VisualiseBatteryFull);
        }

        public static void AutoBattery(bool init = false)
        {
            if (chargeFull && !init) SetBatteryLimitFull();
            else SetBatteryChargeLimit();
        }

        public static void SetAsusChargeLimit(int value)
        {
            if (!ProcessHelper.IsUserAdministrator()) return;
            const string keyPath = @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.SetValue("ChargingRate", value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to set ChargingRate: {ex.Message}");
            }
        }

        public static void SetBatteryChargeLimit(int setLimit = -1)
        {
            int limit = setLimit;
            if (limit < 0) limit = AppConfig.Get("charge_limit");
            if (limit < 40 || limit > 100) return;

            if (AppConfig.IsChargeLimit6080())
            {
                if (limit > 85) limit = 100;
                else if (limit >= 80) limit = 80;
                else if (limit < 60) limit = 60;
            }

            if (setLimit > 0) SetAsusChargeLimit(limit);

            QueueBatteryLimit(limit);

            AppConfig.Set("charge_limit", limit);
            chargeFull = false;

            Program.settingsForm.VisualiseBattery(limit);
        }

        public static void BatteryReport()
        {
            var reportDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                var cmd = new Process();
                cmd.StartInfo.WorkingDirectory = reportDir;
                cmd.StartInfo.UseShellExecute = false;
                cmd.StartInfo.CreateNoWindow = true;
                cmd.StartInfo.FileName = "powershell";
                cmd.StartInfo.Arguments = "powercfg /batteryreport; explorer battery-report.html";
                cmd.Start();
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }

    }
}
