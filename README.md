# taskbar-monitor (AI-Assisted Fork)

> **This is an unofficial fork of [taskbar-monitor](https://github.com/leandrosa81/taskbar-monitor), originally created by [Leandro Lugarinho (@leandrosa81)](https://github.com/leandrosa81).**
>
> All credit for the original project belongs to the original developer. This fork is not affiliated with or endorsed by the original author.
>
> The improvements in this fork were developed with **AI-assisted code**. The code was built and tested locally, but it may still contain mistakes, regressions, or undocumented behavior.
>
> **Use at your own risk.** This software is provided without warranty. Review the source and back up your settings before installing or running it.

A Windows taskbar monitor that displays live graphs for CPU, memory, disk, network, GPU 3D, and GPU memory usage.

![Example 1](media/demo.png)

## What this fork adds

Version **0.5.6.1** includes:

- **Per-counter hover details** — hover a counter to see the top five resource users, grouped by executable name.
  - CPU: top processes by CPU usage.
  - MEM: top processes by working set.
  - DISK: top processes by process I/O (approximate).
  - NET: top network adapters by throughput.
  - GPU 3D: top processes by GPU engine utilization.
  - GPU MEM: top processes by GPU memory usage.
- A non-activating popup that does not steal focus from the current application.
- Popup placement for bottom, top, left, and right taskbars, including auto-hide and multi-monitor setups.
- Per-monitor DPI awareness and taskbar sizing fixes for high-DPI and high-resolution displays.
- Safer configuration loading and saving, including recovery from corrupt configuration files.
- Installer and startup fixes.
- Update checks now use this fork's GitHub releases, and the About tab links to this repository.
- Version labels show the full four-part version number.

### Known limitations

- DISK values are approximate because Windows process I/O counters include more than disk I/O.
- NET shows adapter totals. Reliable per-process network usage requires ETW.
- Per-process GPU data depends on the performance counters exposed by the GPU driver.

## Install

1. Make sure [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472) is installed.
2. Download **TaskbarMonitorInstaller.exe** from the [latest release](https://github.com/aungkyawswarzed/taskbarmonitor-AI-Assisted/releases/latest).
3. Run the installer as administrator and follow the prompts.

The installer writes to `C:\Program Files\TaskbarMonitor` and registers the app to start with Windows.

Tested on Windows 11. The original project also contains a Windows 10 DeskBand implementation, but this fork's released installer is intended for Windows 11.

## Uninstall

Use **Add or remove programs** and select **taskbar-monitor**, or run the uninstaller from the installation folder.

## Project status

This is a personal, unofficial fork maintained by [@aungkyawswarzed](https://github.com/aungkyawswarzed). It is not the upstream project. For the original, unmodified project, use [leandrosa81/taskbar-monitor](https://github.com/leandrosa81/taskbar-monitor).

Issues and changes specific to this fork should be reported in this repository.

## Credits

- **Original project and developer:** [Leandro Lugarinho (@leandrosa81)](https://github.com/leandrosa81) — [leandrosa81/taskbar-monitor](https://github.com/leandrosa81/taskbar-monitor)
- **DeskBand library:** [CSDeskBand](https://github.com/dsafa/CSDeskBand) by [dsafa](https://github.com/dsafa)
- **JSON library:** [Newtonsoft.Json](https://www.newtonsoft.com/json)
- **Fork maintenance and AI-assisted improvements:** [@aungkyawswarzed](https://github.com/aungkyawswarzed)

## License

This fork is distributed under the same **GNU General Public License v3.0** as the original project. See [LICENSE](LICENSE).

## Disclaimer

This project modifies Windows taskbar integration and runs with user-level access. It is not production software and comes with no support or guarantee of fitness for any purpose.

**Use it at your own risk.**
