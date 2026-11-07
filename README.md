# KL Win Health

A Windows PC health desktop app with a ROG-style dark green UI. It covers virus checks, ASUS/ROG and Windows service cleanup, browser cleanup, storage cleanup, wallpaper and lock screen, and desktop widgets.

Built with C# .NET 8 WPF (MVVM), x64, and runs as administrator.

---

## Build

Requirements:
- Windows 10 or 11 (x64).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bat
build.bat
```

The script produces `publish\KLWinHealth.exe`, a self-contained single file (about 75 MB). It runs on any x64 Windows 10/11 PC without installing .NET.

The exact command `build.bat` runs:

```bat
dotnet publish KLWinHealth\KLWinHealth.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o publish
```

For development, use `dotnet build KLWinHealth\KLWinHealth.csproj` and run `KLWinHealth\bin\Debug\net8.0-windows\win-x64\KLWinHealth.exe`.

### Administrator rights: UAC only once

The app needs administrator rights, but it asks for them **only the first time**:

1. **First run** of `KLWinHealth.exe` (from anywhere): Windows shows one UAC prompt. The elevated app then
   - copies itself to `%ProgramFiles%\KL Win Health\KLWinHealth.exe` (a folder only administrators can write to),
   - registers an on-demand Task Scheduler task `KLWinHealth\Launch` that runs it with highest privileges,
   - creates **Start menu** and **Desktop** shortcuts and an **Apps & features** entry.
2. **Every later launch** (shortcut, or the exe itself) starts unelevated, runs the `KLWinHealth\Launch` task and exits. The task opens the app as administrator **with no UAC prompt**.
3. **Updating**: run a newly built `publish\KLWinHealth.exe`. It sees that it is different from the installed copy, asks UAC once, and replaces the installed copy. Exit the running app (tray → Exit) first.
4. **Uninstall**: Settings → Apps → *KL Win Health* → Uninstall, or `"%ProgramFiles%\KL Win Health\KLWinHealth.exe" --uninstall`. This removes the program, tasks and shortcuts. The data in `%ProgramData%\KLWinHealth\` is kept.

If the UAC prompt is cancelled, the app opens in limited mode. This works for accounts that are administrators. A standard user who types an admin password gets the UAC prompt every time.

The weekly auto-clean and **Settings → Start with Windows** also use highest-privilege tasks, so they never prompt. Start with Windows is **on by default** (desktop widgets and the slideshow only run while the app is in the tray); turning it off in Settings is remembered.

## Command line

| Argument | What it does |
|---|---|
| `--minimized` | Start in the tray (used by the Start with Windows task) |
| `--page=Virus\|Rog\|Windows\|Browser\|Storage\|Wallpaper\|Widgets\|Settings` | Open a module directly |
| `--autoclean` | Headless weekly clean of **Safe** storage categories only, then exit (used by the weekly task) |

## Data folder: `%ProgramData%\KLWinHealth\`

| Path | Contents |
|---|---|
| `settings.json` | All settings. The VirusTotal key is stored DPAPI-encrypted for the current user. |
| `backups\` | One JSON file per change, plus a wallpaper image copy. **Restore** reads these. |
| `logs\` | Daily Serilog logs (`klwinhealth-YYYYMMDD.log`), kept for 30 days |
| `quarantine\` | Quarantined files (`<id>.quarantine`) with metadata (`<id>.json`) |
| `config\` | Editable rule lists (see below) |
| `lockscreen\` | Copy of the image used as the lock screen |

### Editable rules (`config\*.json`)

- **`rog_services.json`**: ASUS/ROG service rules (Required / Safe / Caution) and detection patterns.
- **`windows_services.json`**: Windows service rules (Protected groups / Safe / Caution).
- **`cleanup_categories.json`**: Clean storage categories (paths, minimum file age, level).

These files are written on first run from copies embedded in the exe. You can edit them; comments are allowed. A newer app version with a higher `"version"` number replaces them.

## Safety model

- **A backup is made before every change.** This covers service startup type and state, registry and policy values, and the wallpaper. **Restore** is on every page that changes something, and **Settings → Restore everything** puts back the *original* state.
- **Every change asks for confirmation** and lists exactly what will happen.
- **Locked items are enforced in code**, not only in the config files:
  - Windows Defender (`WinDefend`, `WdNisSvc`, `Sense`, `SecurityHealthService`, `wscsvc`, …), the firewall, RPC and other core services can never be stopped.
  - ASUS services needed for fans and performance modes, Fn keys and backlight, the battery charge limit and GPU switching are locked on **both** service pages.
- **Personal folders are never cleaned automatically.** This covers Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive and Prefetch, among others. The large-file finder and the Downloads review only move files *you tick* to the **Recycle Bin**.
- Long tasks run in the background with progress and **Cancel**. Errors are logged and shown as plain messages; the app does not crash on them.

## Modules

| Module | Highlights |
|---|---|
| **Virus check** | Drag & drop files or folders, or **Scan entire PC** (Defender quick or full scan, plus local checks of Downloads, Desktop, Temp, AppData, Startup and ProgramData). Each file goes through a Defender scan (MpCmdRun), heuristics (disguised extensions, RLO trick, Office macros, PDF JavaScript, script patterns, entropy, signature) and an optional VirusTotal hash lookup. You get a risk score, can quarantine or restore files, and can export results to CSV. |
| **Clean ROG service** | Finds ASUS/ROG services by rules, name patterns or publisher. Required ones are locked; **Clean recommended** stops and disables only the Safe ones and shows the RAM freed. |
| **Clean Windows service** | Shows all services as Critical (locked), Safe, Caution or Not classified, with search and filters. Each service has Stop, Disable, Manual and Restore actions. |
| **Browser cleaner** | Supports Chrome, Edge, Brave and Firefox:<br>• **Processes** are labelled from their command line (main, tab renderer, GPU, network, extension, …); only Safe ones can be ended.<br>• **Extensions** are listed per profile, with a shortcut to the browser's extensions page.<br>• **Memory policies**: Background mode off, Memory Saver / Sleeping tabs on, Edge Startup Boost off.<br>• **Cache clearing** closes the browser first and never touches cookies or passwords.<br>• A Shift+Esc tip points to the browser's own Task Manager. |
| **Clean storage** | Drive usage bars, then **Analyze** and **Clean** the selected categories (temp files, Recycle Bin, thumbnails, error reports, crash dumps, shader cache, Delivery Optimization, logs, Windows Update cache, Windows.old, DISM). Also a large-file finder and a review of old downloads. |
| **Wallpaper** | Preview with Fill/Fit/Stretch/Center/Tile/Span, set the desktop or lock screen image, recent history, and a folder slideshow that keeps running from the tray (**Next wallpaper** in the tray menu). **Restore original** and **Restore lock screen** undo the changes. |
| **Desktop widgets** | Clock, Calendar, System monitor (CPU/RAM/GPU/disk), Network speed, Battery and Text widgets. Each has its own font, colours, opacity, corner style and size.<br>• **Edit mode**: drag to move (snaps to the grid), drag the corner handles to resize, right-click for more options.<br>• **Locked**: widgets sit on the desktop layer behind all windows and are click-through. |
| **Settings** | Start with Windows, minimize to tray, VirusTotal key, weekly auto-clean, the list of backups with Restore / Restore everything, and buttons to open the logs and quarantine folders. |

## How the desktop widgets attach

The app sends Explorer the undocumented `0x052C` message to Progman, which creates the `WorkerW` desktop layer. It then finds the window that hosts the desktop icons:
- **Classic layout** (Windows 10, Windows 11 before 24H2): a top-level `WorkerW` hosts `SHELLDLL_DefView`.
- **24H2+ layout**: `SHELLDLL_DefView` and `WorkerW` are children of `Progman`.

Each widget is a layered, transparent window **owned** by that desktop host. This keeps it above the wallpaper and below every app window, and it stays visible with Win+D. In locked mode the widget is `WS_EX_TRANSPARENT` (click-through), `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` (not in Alt+Tab).

If Explorer restarts, the widgets are re-attached when Windows sends `TaskbarCreated`. A 5-second watchdog also re-creates any widget that Explorer took down with it.

## Testing checklist (per phase)

1. **Shell / Home / Settings**
   - The window is 1100×720 with a custom title bar and a red close button on hover.
   - The stat cards update every 2 s, the 8 tiles animate in, and every tile opens its module.
   - The tray menu has Open, Widgets edit/locked and Exit.
   - Closing the window hides it to the tray when that setting is on.
   - Start with Windows creates the `KLWinHealth\Startup` task (check with `schtasks /query /tn KLWinHealth\Startup`).
2. **Virus check**
   - Drop a harmless `.txt` file: the result is Clean.
   - Drop the [EICAR test file](https://www.eicar.org/download-anti-malware-testfile/): Defender reports a threat and the verdict is Dangerous.
   - Rename an `.exe` to `.pdf.exe`: Suspicious.
   - Quarantine a file, then restore it.
   - Export the results to CSV.
3. **Clean ROG service**: on an ASUS PC, Required services show a lock. **Clean recommended** lists only Safe services, and Restore brings them back.
4. **Clean Windows service**
   - Defender, the firewall and RPC show as Critical, and their actions are disabled.
   - Stop a Safe service (e.g. `Fax`), then click **Restore**.
5. **Browser cleaner**
   - Processes show labels, PIDs and memory; only Safe rows can be ended.
   - **Optimize settings → Apply** creates the policy. Open `edge://policy` or `chrome://policy` to see it, then click **Undo**.
   - **Clear cache** closes the browser first.
6. **Clean storage**
   - **Analyze** shows sizes, and **Clean** asks first ("These files can't be restored").
   - The free space on Home updates afterwards.
   - The large-file finder and Old downloads only move ticked files to the Recycle Bin.
7. **Wallpaper**
   - Preview the fit modes, **Set as desktop**, then **Restore original**.
   - Start a slideshow with a 1 min interval and use **Next wallpaper** in the tray.
   - **Set as lock screen** shows the image on Win+L; **Restore lock screen** removes it.
8. **Desktop widgets**
   - Add a Clock: the app switches to edit mode. Drag it and resize it from a corner.
   - Click **Locked**: the widget becomes click-through and stays behind all windows, including after Win+D.
   - Restart Explorer (`taskkill /f /im explorer.exe & start explorer`): the widgets come back within about 5 s.
9. **Release**
   - Run `build.bat`, copy `publish\KLWinHealth.exe` to another PC and run it.
   - Check `%ProgramData%\KLWinHealth\logs` for the log files.

## Notes and limitations

- **Browser policies** make the browser show *"Managed by your organization"*. This is how Chromium shows any policy. Restoring removes the policies and the message.
- **Lock screen** uses the `PersonalizationCSP` policy values. While it is set, the lock screen picture can't be changed in Windows Settings. Use **Restore lock screen** to give control back.
- **VirusTotal** is optional. The free API key allows 4 lookups per minute, and the app spaces requests automatically. Only file **hashes** are sent; files are never uploaded.
- Without administrator rights the app runs in **Limited mode** (shown in the title bar). Click the badge to restart elevated.
- GPU, disk and network usage are sampled only while a System monitor or Network widget exists, which keeps idle CPU use low.

## Project layout

```
KLWinHealth/
  App.xaml(.cs), App.Modules.cs       startup, DI registrations, background features (tray, slideshow, widgets)
  MainWindow.xaml(.cs)                shell: title bar, page host, toasts, drag & drop (works while elevated)
  Themes/RogGreen.xaml                colours, fonts, all control styles
  Controls/                           AngledBorder, ClippedTile, StatCard, GlowButton, TitleBar, BusyBar, PageHeader
  Converters/                         value converters + formatting helpers
  Models/                             settings, backups, scan results, services, browsers, widgets
  Services/                           system access: services, registry, backups, scheduler, storage, browsers, wallpaper
  Services/VirusScan/                 Defender, heuristics, signatures, VirusTotal, quarantine
  Services/Widgets/                   desktop layer (WorkerW), widget models, widget manager
  ViewModels/  Views/                 one page per module
  Config/                             default rule lists (embedded)
  Assets/                             app icon, Rajdhani font (OFL)
build.bat                             release build
```
