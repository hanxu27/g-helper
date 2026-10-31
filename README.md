# G-Helper — dgpufix build

An unofficial fork of [G-Helper](https://github.com/seerge/g-helper), built for the **ASUS ROG Zephyrus G15 (GA503QS, Ryzen 5000 "Cezanne" + NVIDIA)**. Its main target is a bug where **both fans get stuck at 100% and the CPU throttles for about a second every 5–10 seconds**. The bug usually shows up after a cold start or after waking from sleep, especially when a 3D app is launched right away.

The version shows as **0.290-dgpufix**. It's based on upstream G-Helper 0.290. For general G-Helper features and documentation, see the [original README](docs/README.md) and the [upstream wiki](https://github.com/seerge/g-helper/wiki).

> This is not an official G-Helper release and is not supported by the G-Helper author. It's tested only on a GA503QS. Use at your own risk.

## The problem it works around

Analysis of the GA503QS BIOS (versions 416 and 418) found that the BIOS talks to the laptop's embedded controller (EC), the chip that runs the fans, through a shared message slot. The BIOS **gives up on a message if the slot stays busy for more than about 20 ms, and usually doesn't report the failure.** The messages affected include:

- **"NVIDIA GPU is on/off."** If this is dropped, the EC keeps treating the GPU as off. The fans don't react to GPU heat until the EC's over-temperature fallback kicks in: fans at 100% and PROCHOT throttling.
- **Performance mode changes.** These always report success, and the current mode can't be read back.
- **Fan curve writes.** The "which fan" part isn't checked, so a curve can land on the wrong fan.

Bursts of mode and fan writes from G-Helper (or Armoury Crate) at startup and wake make these collisions much more likely. The root cause is in ASUS's firmware. This fork changes G-Helper so it stops causing the collisions and repairs the damage when one happens anyway.

## What's different from upstream G-Helper

**EC reliability**
- **GPU wake detection (opt-in, see Settings).** Watches the NVIDIA GPU's power state without waking it. If the GPU powers on while G-Helper is writing to the EC, G-Helper re-sends your mode and fan curves once the GPU settles.
- **Spaced-out EC writes.** Waits 300 ms between a mode change and the fan curves, and 150 ms between each fan-curve write, so each message finds the slot free. Fan curves are also applied one set at a time.
- **Quiet re-apply.** About 10 seconds after startup, wake, a charger change, or an Eco/Standard switch, G-Helper re-sends mode, fan curves, power limits, and battery charge limit once.
- **The GPU stays asleep.** G-Helper no longer wakes a sleeping NVIDIA GPU just to read its temperature. It shows the EC's GPU temperature, or blank, while the GPU sleeps.
- **Fewer battery limit writes.** Duplicate charge-limit writes around wake, charger, and lid events are skipped, and the remaining ones wait for other EC writes to finish.

**Features**
- **fPPT slider through the SMU.** The GA503QS BIOS exposes SPL and sPPT but not fPPT. This build shows an **"fPPT (CPU short boost, SMU)"** slider in Fans + Power → CPU and sets it directly on the CPU's power controller (the SMU), the same way Universal x86 Tuning Utility and RyzenAdj do. It's re-applied after mode and charger changes and every 30 seconds. Requires the PawnIO driver (G-Helper offers to install it) and admin rights.

**Removed or disabled**
- **No self-update.** This build never checks for or installs official G-Helper releases, so it won't be replaced by one. New versions come from this fork's Releases page.
- **No automatic BIOS/driver checks.** The BIOS & Driver Updates window doesn't contact ASUS until you click **Refresh**. Downloads still happen only when you click a driver.
- **No Donate button.** If you find G-Helper useful, please consider [supporting the original author](https://g-helper.com/support).

## Install

1. Download `GHelper.exe` from this fork's [Releases](../../releases/latest) page.
2. **Quit any other G-Helper** (right-click its tray icon, then Quit) and close Universal x86 Tuning Utility or similar tools. Only one tool should write to the EC.
3. **Disable Armoury Crate's background services.** They write modes and fan curves on their own, without this build's spacing.
4. Put `GHelper.exe` in a folder of your choice and run it. The build isn't code-signed, so Windows SmartScreen will warn you. Click **More info**, then **Run anyway**.
5. In Settings, turn **Run on Startup** off and on again so Windows launches this copy at login.

Your settings live in `%AppData%\GHelper\config.json` and are shared with the official G-Helper. Switching back and forth is safe.

## Settings

Add these to `%AppData%\GHelper\config.json` (inside the `{ }`, separated by commas), then restart G-Helper.

| Key | Default | What it does |
|---|---|---|
| `dgpu_ec_sync` | off | Set to `1` to turn on GPU wake detection. Only active in Standard (Optimus) GPU mode. **Recommended on GA503QS.** |
| `ec_mode_gap` | 300 | Milliseconds between a mode change and the fan-curve writes |
| `ec_write_gap` | 150 | Milliseconds between individual fan-curve writes |
| `ec_reapply_delay` | 10 | Seconds before the quiet re-apply. `0` turns it off |
| `battery_dedupe` | 10 | Seconds within which a repeated, already-accepted battery limit write is skipped |
| `gpu_temp_wake` | off | Set to `1` to let G-Helper wake a sleeping GPU to read its temperature (upstream behavior) |
| `reapply_time` | 30 | Seconds between re-applies of SMU settings (fPPT, undervolt, CPU temp limit) |

Example:
```json
{
  "dgpu_ec_sync": 1,
  ...your existing settings...
}
```

## Checking that it works

Open `%AppData%\GHelper\log.txt`, or in PowerShell:
```powershell
Select-String "dGPU|Quiet re-apply|FAST \(SMU\)|BatteryLimit" $env:APPDATA\GHelper\log.txt | Select-Object -Last 20
```

| Log line | Meaning |
|---|---|
| `dGPU watcher: PCI\VEN_10DE... initial D3` | GPU wake detection started and found the NVIDIA GPU |
| `dGPU power: D3 -> D0` | It saw the GPU wake up. If this never appears when you start a game, detection doesn't work on your system, but the other fixes still do. |
| `dGPU woke during EC writes, re-sending mode and fans...` | A collision happened and was repaired |
| `Quiet re-apply: re-sending mode, fans, power and battery limit` | The quiet re-apply ran |
| `FAST (SMU): 45W OK` | fPPT was set through the SMU |
| `BatteryLimit = 80 : skipped (already set)` | A duplicate battery limit write was skipped |

## Known limitations

- **If G-Helper starts in Eco mode,** GPU wake detection can't find the GPU (it's disconnected). After switching to Standard, restart G-Helper.
- **Mode switches take about half a second longer** because of the spacing.
- **Re-applies repeat everything a mode switch does,** including the Windows power mode, CPU boost, undervolt, and any per-mode `mode_command`. After startup or wake this can happen up to twice.
- **GPU temperature may be blank or stale** while the NVIDIA GPU is asleep. That's expected.
- **Don't push fPPT too high.** The slider goes to 150 W, but large short bursts of heat are exactly what trips the EC's over-temperature fallback.
- **On other models:** the EC reliability changes are harmless elsewhere, and the fPPT-through-SMU slider also appears on other AMD laptops whose BIOS lacks fPPT. Both are tested only on a GA503QS.
- **If fans still get stuck at 100%** with no `dGPU woke during EC writes` or `Quiet re-apply` line near that moment in the log, the cause is likely in the EC firmware itself. Switching to **Ultimate** GPU mode (dGPU always on) avoids the GPU on/off path entirely.

## Building from source

Requires Git and the [.NET 10 SDK](https://dotnet.microsoft.com/download).
```powershell
git clone <this fork's URL>
cd g-helper
git checkout dgpu-ec-sync
dotnet publish app/GHelper.sln --configuration Release --runtime win-x64 -p:PublishSingleFile=true --no-self-contained
```
The exe ends up in `app\bin\x64\Release\net10.0-windows\win-x64\publish\GHelper.exe`.

## Credits and license

G-Helper is created by [seerge](https://github.com/seerge) and contributors, and licensed under GPL-3.0 (see [LICENSE](LICENSE)). This fork is distributed under the same license, with all changes available in this repository's history.
