# InsomniaWin

**Keep your Windows laptop awake. Give the session an end time.**

[![License: MIT](https://img.shields.io/badge/License-MIT-536D78?style=flat-square)](LICENSE)
![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-303336?style=flat-square)
![.NET 10 Native WPF](https://img.shields.io/badge/.NET-10%20WPF-A6BBC3?style=flat-square)

**InsomniaWin** is a lightweight, native Windows 10/11 system tray application for **timed closed-lid awake sessions**—built for people running **AI coding agents, local LLMs, browser automation, downloads, or long builds** on a laptop.

Inspired by [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia) for macOS, InsomniaWin brings the same safety-first, journaled transaction design natively to Windows using Win32 Power APIs, Core Audio, WMI, and a Task Scheduler recovery backstop—**with no Administrator privileges required**.

> **Ventilation & Safety Notice:** Always leave a running closed laptop on a stable, hard, well-ventilated surface—never inside a closed backpack or padded sleeve. Hardware thermal throttling, crash recovery, and battery cutoffs are protective layers, not absolute guarantees.

---

## Why InsomniaWin Exists

Normally, closing your laptop lid immediately puts Windows to sleep, killing active builds, local models, or autonomous coding agents. On the other hand, permanently changing *"When I close the lid -> Do nothing"* in Windows Control Panel is dangerous because it is too easy to forget to switch sleep back on—causing your laptop to overheat in a bag or drain flat overnight.

**InsomniaWin treats staying awake with the lid closed as a reversible, timed transaction:**
1. Set an exact duration (`30m` to `3d`, or custom `Days / Hours / Minutes`).
2. Before changing anything, InsomniaWin records your original Windows settings to an on-disk recovery journal.
3. When the timer expires (or you hold the `×` ring to end early, or battery/heat crosses a safety cutoff), every change is undone automatically—and if the lid is still closed, Windows puts the laptop right back to sleep.

---

## Quick Start (End Users)

1. Download **`InsomniaWin-v1.0-win-x64.zip`** from the [Releases](../../releases) page (it is self-contained—no separate `.NET` installation required).
2. Extract the `.zip` folder and double-click **`InsomniaWin.exe`**.
   - *First-run Windows SmartScreen note:* Because the open-source binary is not signed with a paid commercial Authenticode certificate, Windows may show *"Windows protected your PC"*. Click **More info → Run anyway** (or build directly from source below).
3. Look for the **eye icon** in the bottom-right Windows System Tray (or inside the `^` overflow chevron):
   - **Closed eye** = idle (normal Windows lid-sleep behavior)
   - **Open eye** = awake session running
4. **Left-click** the eye to choose a preset (`30m`, `1h`, `2h`, `4h`, `8h`, `12h`, `24h`, `3d`) or enter a custom duration.
5. To stop early, press and **hold the `×` ring for 0.6 seconds** (accidental taps cancel safely) or right-click the eye → **End Session**.

---

## Key Features

### 1. System Tray Modes & Controls
- **Tray Eye + Popover (Default):** Monochrome open/closed eye in the notification area with a dark charcoal flyout showing remaining time, quick extend chips (`+30m`, `+1h`, `+2h`), live hardware badges, warnings, and **Settings…**.
- **Floating Pill:** Optional compact always-on-top translucent pill displaying the eye, live countdown, and hold-to-end ring (remembers screen position).
- **Minimal Eye Ring:** Circular progress arc rendered directly around the tray icon; left-click starts or extends by your default preset.
- **Keep Screen On & Unlocked (Optional):** Off by default. Enable in Settings (`SetThreadExecutionState` with `ES_DISPLAY_REQUIRED`) when running GUI/computer-use agents that need an active unlocked desktop while the lid is open.

### 2. Clamshell-Aware Reversible Lid-Close Actions
Using `QueryDisplayConfig`, InsomniaWin checks whether an external monitor is attached before taking lid-close actions. If you are docked at a desk with an external display, it leaves screen, audio, and apps alone and simply keeps counting down. When closed standalone:
- **Display Darkening:** Saves current panel brightness (`WmiMonitorBrightness`), dims to `0`, and sends `SC_MONITORPOWER` off; restores brightness and wakes the display when opened.
- **Per-Device Audio Mute:** Saves each active render endpoint's volume and mute state via Core Audio (`IAudioEndpointVolume`) and mutes them; restores each device on lid open (and keeps disconnected USB/Bluetooth outputs saved until they reconnect or you click *Stop waiting*).
- **Optional Screen Lock:** Calls native `LockWorkStation()` on lid close if enabled in Settings.
- **Low Power / Energy Saver:** Automatically enables Windows Energy Saver (`ESBATTTHRESHOLD`) and Better Battery overlay while the lid is closed, restoring your exact prior values on open.
- **App Freezing with Kernel Verification:** Pauses configured background battery hogs (defaults: `Slack`, `WhatsApp`, `Discord`) via `NtSuspendProcess` and resumes them via `NtResumeProcess` ONLY after verifying **PID + kernel creation timestamp + boot session ID** (`GetProcessTimes` + `Win32_OperatingSystem.LastBootUpTime`) so a recycled PID from an unrelated app is never touched.
  - Built-in protection keeps coding agents, terminals, IDEs (`VS Code`, `Cursor`, `Antigravity`, `Zed`, JetBrains, `WindowsTerminal`, `pwsh`, `wsl`), local LLMs (`Ollama`, `LM Studio`), browsers, and meeting/recording tools (`Zoom`, `Teams`, `Webex`, `OBS`, `Loom`) running—even if *"Freeze all other desktop apps"* is enabled.
- **Optional Idle Docker Desktop Rule:** Checks `docker ps -q` twice before pausing `Docker Desktop` UI (cancelling immediately if the lid reopens mid-check).

### 3. Battery & Thermal Safety Floors
- **Battery Floors (Default):** Enables Low Power Mode below **40%** on battery, and automatically ends the session (sleeping the laptop if the lid is shut) below **10%**—or after **2 consecutive unreadable battery readings** while discharging.
- **Thermal Protection (Default):** Samples Windows thermal zones (`Win32_PerfFormattedData_Counters_ThermalZoneInformation`) when exposed by OEM firmware—enabling Low Power Mode at **82 °C** (with hysteresis holding until cooled below **72 °C**) and ending the session at **92 °C**.

### 4. Crash-Recovery Journal & 1-Minute Task Scheduler Backstop
- Every change is written ahead to `%LOCALAPPDATA%\InsomniaWin\state.json` under an exclusive OS file lock (`recovery.lock`).
- Starting a session registers a user-level Windows Task Scheduler job (`InsomniaWinBackstop`) that invokes `InsomniaWin.exe --backstop` once per minute.
- If the app ever crashes or is force-closed past `endsAt`, the backstop safely restores `LIDACTION`, Energy Saver, and verified suspended processes—while leaving valid unexpired sessions untouched.
- Corrupt `session.json` files are quarantined (`session.json.unreadable-<timestamp>`) rather than discarded, and quitting is blocked if non-audio dirty state remains unresolved.

### 5. CLI & Coding Agent Integration (`--session`)
Drive the running tray app directly from scripts, CI runners, or AI coding agents over a scoped Named Pipe:

```powershell
InsomniaWin.exe --session start 7200    # Start a 2-hour session
InsomniaWin.exe --session extend 1800   # Extend by 30 minutes
InsomniaWin.exe --session status        # Print current state (works even if app is closed)
InsomniaWin.exe --session end           # End session and restore Windows power settings
```

Exit codes: `0` = success, `1` = refused / app not running, `64` = invalid CLI usage.

### 6. Chromium Browser Unthrottling
Detects running `Chrome`, `Edge`, `Brave`, `Arc`, or `Chromium` instances missing `--disable-backgrounding-occluded-windows` or `--disable-renderer-backgrounding` and offers a one-click, profile-preserving relaunch from the popover.

---

## Architecture

```text
InsomniaWin/
├── InsomniaWin.sln
├── src/
│   ├── InsomniaWin.Core/          # Cross-layer logic, models, store & Win32/COM/WMI adapters
│   │   ├── Core/                  # SessionManager, LidActions, FloorRules, FreezePlanner, BackstopRunner, SessionIpc
│   │   ├── Models/                # Config, Session, RuntimeState, PowerStatus, EndReason
│   │   ├── Store/                 # AppPaths, atomic JsonStore, RecoveryLock, RollingLogger
│   │   └── System/                # Win32 powrprof, SetThreadExecutionState, Core Audio COM, NtSuspendProcess, WMI
│   ├── InsomniaWin/               # Native WPF + System Tray UI (Popover, FloatingPill, Settings, EditApps)
│   └── InsomniaWin.Tests/         # xUnit test suite (27 tests using isolated temp INSOMNIA_HOME & fakes)
└── scripts/
    ├── uninstall.ps1              # Safety-checked uninstaller (Task Scheduler + HKCU Run + optional -Purge)
    ├── simulate-lid.ps1           # Simulated lid close/open trigger for Debug / INSOMNIA_LID_SIMULATION=1
    └── publish-github.ps1         # Helper to push repository & v1.0.0 release to GitHub via gh CLI
```

---

## Building from Source (Developers)

Requires **.NET 10 SDK** on Windows 10/11:

```powershell
# Restore & build Release
dotnet build InsomniaWin.sln -c Release

# Run the xUnit test suite (completely safe: uses fakes & temp directories, never touches host power settings)
dotnet test InsomniaWin.sln -c Release

# Run directly from source
dotnet run --project src\InsomniaWin\InsomniaWin.csproj -c Release

# Publish a framework-dependent single-file binary (~770 KB, requires .NET 10 Desktop Runtime)
dotnet publish src\InsomniaWin\InsomniaWin.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

# Publish a self-contained single-file binary (zero prerequisites for distribution)
dotnet publish src\InsomniaWin\InsomniaWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish-standalone
```

### Testing Lid Actions Without Physically Closing Your Laptop
In `Debug` builds (or Release builds launched with `$env:INSOMNIA_LID_SIMULATION = "1"`), trigger simulated lid events:

```powershell
.\scripts\simulate-lid.ps1 closed
.\scripts\simulate-lid.ps1 open
```

---

## Uninstalling

The uninstaller runs `--backstop` and verifies no unresolved power or process changes remain in `state.json` before removing recovery machinery:

```powershell
.\scripts\uninstall.ps1          # Removes Task Scheduler backstop & startup entry
.\scripts\uninstall.ps1 -Purge   # Also deletes %LOCALAPPDATA%\InsomniaWin (config & logs)
```

---

## License

[MIT License](LICENSE) — inspired by [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia).
