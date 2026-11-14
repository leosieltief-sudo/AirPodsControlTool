# AirPods Control for Windows

**English** · [Русский](README.ru.md)

**Switch AirPods noise cancellation, Transparency, Adaptive and Off on Windows 10/11 — and set what the stems do when you
press and hold them. No Windows Test Mode, no Secure Boot change, no unsigned driver, no iPhone or Mac.**

> 🇷🇺 Переключение шумоподавления AirPods (ANC, Прозрачность, Адаптивный, Off) и настройка удержания ножки на Windows
> **без тестового режима и без отключения Secure Boot**. Описание на русском — [README.ru.md](README.ru.md).

A small tray app (.NET 10, WPF) that shows AirPods battery levels and controls the listening modes that Windows itself cannot
change. No browser, no cloud, no account, no telemetry.

![AirPods Control for Windows: battery and noise-control modes in the tray window](docs/screenshot.png)

> **Status: early, tested on one PC** (Windows 11 26200, MediaTek MT7922 Bluetooth, AirPods Pro 3). Other adapters and
> AirPods models are untested. Please read [Limitations](#limitations) before you install.

## No Test Mode — run it and set up once

Other ways to control AirPods modes on Windows need an unsigned kernel driver, which means turning Secure Boot off and
enabling Windows Test Mode. **AirPods Control does not.** It uses Windows' own WSL2 to talk to the AirPods:

1. Unpack the zip and run `AirPodsControl.exe` — nothing to install, no driver.
2. Click **Set up driverless mode** once. The wizard prepares everything by itself (you never install Linux by hand) and
   asks for administrator rights **one time**.
3. From then on: click a mode or press a hotkey. No administrator rights, no reboot, Secure Boot and Windows security settings
   stay as they are.

What you need beforehand: WSL2 installed (`wsl --install`), a USB Bluetooth adapter (most laptops' built-in Bluetooth is one),
and — for MediaTek adapters — the AirPods Control WSL kernel, which the current release does **not** include (see
[Limitations](#limitations)).

## Who is it for

- **AirPods owners on Windows** who just want to switch noise cancellation / Transparency / Off from the PC, or make the stems
  cycle through Off, ANC, Transparency and Adaptive without an iPhone.
- **Developers and researchers**: a working, documented way to speak Apple's AAP protocol from Windows without a kernel driver;
  protocol notes with sources in [docs/PROTOCOL.md](docs/PROTOCOL.md), driver research in
  [docs/WINDOWS-DRIVER-RESEARCH.md](docs/WINDOWS-DRIVER-RESEARCH.md), a CLI for scripting.
- **Companies and integrators**: MIT licence, no network services, logs stay on the machine.

## What works

| Feature | How | Verified on hardware |
|---|---|---|
| Find paired AirPods, model, connection state | Windows APIs, no setup | ✅ |
| Battery L / R / Case (10 % steps), charging, in-ear | BLE advertisements, no setup | ✅ |
| Switch ANC / Off / Transparency / Adaptive | WSL2 bridge (below) or MagicAAP driver | ✅ via WSL2 bridge (by ear); MagicAAP path not tested |
| Exact battery (1 %) | WSL2 bridge, on request | ✅ |
| Press-and-hold modes on the stems (incl. Off), written to the AirPods | WSL2 bridge, Settings → *Press and hold* | ✅ applied (by ear); kept after reconnect / case: **not verified** |
| Global hotkeys, autostart, tray menu | — | ✅ |

The AirPods send no confirmation over the bridge, so the app reports commands as **"sent"**, not "confirmed".

## Limitations

Known downsides of the current version, honestly:

- **Memory: about 900 MB of RAM while the bridge is active.** The WSL2 virtual machine with the AirPods Control kernel
  (`vmmemWSL` in Task Manager) stays running between commands. **This will be fixed in upcoming updates** (stopping the VM
  when idle and capping its memory). Until then, `wsl --shutdown` frees the memory; the next command starts it again.
- **Bluetooth pauses for about 12–15 s per command.** For each command the USB adapter is lent to WSL and handed back to
  Windows: audio stops and the AirPods reconnect. Quick repeated presses are merged into one pause.
- **Release without the MediaTek kernel.** The MediaTek firmware licence does not allow shipping it inside a Linux kernel, so
  the public zip has no kernel. MediaTek owners build it once with `kernel/build-kernel.sh` (≈30 min, see
  [docs/RELEASING.md](docs/RELEASING.md)). That kernel then applies to **all** your WSL distributions.
- **Adapters:** only MediaTek MT7921/MT7922/MT7925 tested (MT7922). Intel, Realtek and Broadcom: firmware is not provisioned
  yet — expect them not to work. Bluetooth must be on USB.
- **Tested on one PC and one model (AirPods Pro 3).** Other models with noise control should work but are unverified.
- **No confirmation from the AirPods:** results are "sent". Whether the press-and-hold set survives reconnecting and the case
  has not been verified.
- **One-time requirements:** WSL2, administrator rights once, internet during setup.
- **Not code-signed:** SmartScreen warns on first start (*More info → Run anyway*).
- Windows 10/11 x64 only.

## Why this is hard on Windows

AirPods are controlled with Apple's AAP protocol over a Bluetooth L2CAP channel (PSM 0x1001). On Windows only
kernel-mode drivers can open such channels: Winsock rejects L2CAP sockets (`WSAESOCKTNOSUPPORT`) and WinRT has no API for it.
Details and sources: [docs/PROTOCOL.md](docs/PROTOCOL.md).

There are two ways around it:

1. **Driverless mode (WSL2 bridge)** — recommended, no Test Mode, no Secure Boot change, no driver.
   For each command the app lends your USB Bluetooth adapter to a tiny Alpine Linux in WSL2 (via usbipd-win), Linux sends the
   AAP command, and the adapter is always handed back to Windows.
2. **MagicAAP driver** by the MagicPods author — instant, but unsigned: needs Secure Boot off and Windows Test Mode.
   The app never installs it; see [docs/WINDOWS-DRIVER-RESEARCH.md](docs/WINDOWS-DRIVER-RESEARCH.md).

## Requirements (driverless mode)

- Windows 10/11 x64 with virtualization enabled in the BIOS/UEFI.
- **WSL2 installed.** If it is not, run `wsl --install` as administrator and reboot — the app does not do that for you.
- A Bluetooth adapter connected over **USB** (built-in laptop Bluetooth usually is).
- AirPods paired with Windows (*Settings → Bluetooth & devices*).
- Internet access during the one-time setup.

## Install

1. Download `AirPodsControl-x.y.z-win-x64-portable.zip` from [Releases](../../releases) and unpack it anywhere.
2. Run `AirPodsControl.exe`. Windows SmartScreen may warn because the exe is not code-signed (*More info → Run anyway*).
3. Click **Set up driverless mode (WSL2)** (or *Settings → Driverless setup*). The wizard:
   - creates a separate WSL distribution `airpods-bridge` from Alpine minirootfs (~4 MB, SHA-256 checked) and installs
     BlueZ + Python (~40 MB) into it;
   - installs [usbipd-win](https://github.com/dorssel/usbipd-win) via `winget` if missing;
   - asks **once** for administrator rights to share the adapter with usbipd and to copy **only these AirPods'** pairing key
     into that distribution;
   - on MediaTek adapters, offers to switch WSL to the AirPods Control kernel (see below);
   - tests the adapter and the AirPods connection.

   You never install Linux by hand. After setup no administrator rights are needed.
4. **Remove driverless mode** in the wizard deletes the distribution (with the key), un-shares the adapter and removes the kernel line.

### The WSL kernel (MediaTek only) — read this

Microsoft's WSL kernel lacks MediaTek Bluetooth support. The AirPods Control kernel is the same Microsoft kernel version with
MediaTek Bluetooth enabled and the MT7921/MT7922/MT7925 firmware built in ([kernel/](kernel/)). It is set in
`%UserProfile%\.wslconfig` (a backup is kept) and **applies to all your WSL distributions** (Ubuntu, Docker Desktop, …).

> **Release note:** the MediaTek firmware licence allows redistribution but *not as part of the Linux kernel*. Until that is
> resolved, public releases ship **without** the kernel; build it yourself with `kernel/build-kernel.sh`
> (≈30 min) — see [docs/RELEASING.md](docs/RELEASING.md).

## Use

- **Mode buttons / hotkeys** (`Ctrl+Alt+1` ANC, `Ctrl+Alt+2` Off, `Ctrl+Alt+3` Transparency; configurable). Presses during a
  running switch are merged into it (the latest wins) instead of starting another Bluetooth interruption.
- **Battery:** *Refresh battery* waits for a fresh BLE advertisement (no interruption, ±10 %). *Exact (AAP)…* reads 1 % values
  through the bridge after a warning. Old values stay visible, marked with their time — never replaced by 0.
- **Press and hold (Settings):** tick the modes the stems should switch between (at least two; Off also sends "Allow Off")
  and click **Apply to AirPods**. The checkboxes are your *desired* set; the line below shows what the AirPods actually
  reported or confirmed — usually "Current AirPods set: unknown", because they report nothing over the bridge.
  The stems' Siri / noise-control assignment is never changed.
- **Bluetooth mouse/keyboard protection:** if a Bluetooth mouse or keyboard is connected through the same adapter (or the app
  cannot tell), every hand-over is blocked until you explicitly confirm (default: No).

### CLI `airpodsctl.exe`

```
airpodsctl diag                       Bluetooth, paired AirPods, control channel
airpodsctl battery 20                 BLE battery for 20 s
airpodsctl bridge-status              driverless mode readiness
airpodsctl bridge-check               which adapter would be handed over, which input devices it serves (changes nothing)
airpodsctl bridge-mode anc|off|transparency|adaptive[,mode…] [--trace]   switch via the bridge, print the operation trace
airpodsctl bridge-battery             exact battery over AAP
airpodsctl bridge-hold-cycle --modes off,anc,transparency[,adaptive]     write the press-and-hold modes
airpodsctl bridge-enable-off (--modes anc,… | --keep-current)            same, Off always added
airpodsctl kernel-status | kernel-install | kernel-remove                MediaTek WSL kernel
  --allow-input-loss                  explicitly allow losing a Bluetooth mouse/keyboard (a general --yes never does)
```

## FAQ

**How do I turn on AirPods noise cancellation on Windows?** Windows has no setting for it. Install AirPods Control, run the
driverless setup once, then click *Noise Cancellation* or press `Ctrl+Alt+1`.

**Can I switch AirPods Pro to Transparency mode or Off from a Windows PC without an iPhone?** Yes — that is what this app
does, including Adaptive on models that have it.

**Do I need Test Mode, a disabled Secure Boot or a custom driver?** No. Driverless mode uses WSL2 and usbipd-win. The optional
MagicAAP driver path does need them; the app never installs it.

**Can I make the stem's press-and-hold include Off without a Mac or iPhone?** Yes: *Settings → Press and hold*, tick the modes,
*Apply to AirPods*.

**Why does my audio stop for a few seconds?** The adapter is briefly lent to WSL for each command — see
[Limitations](#limitations).

**Why does `vmmemWSL` use ~900 MB of RAM?** The WSL VM stays running between commands. A fix is planned; `wsl --shutdown`
frees it now.

**Does it work with AirPods Pro 2 / AirPods 4 / AirPods Max?** Untested. Models with noise control should work if your
Bluetooth adapter is supported — reports are welcome in Issues.

## Build from source

Requirements: .NET 10 SDK; Python 3 for the bridge tests (optional); Inno Setup 6 for an installer (optional).

```
.\build.ps1             # tests + self-contained single-file win-x64 build + zip in .\dist
.\build.ps1 -NoKernel   # same, without the MediaTek WSL kernel
```

The kernel is not stored in git. Build it with `kernel/build-kernel.sh` (as root in any Alpine WSL distribution) into
`assets\wsl-kernel\`; `build.ps1` then adds it to the zip. Tests: `dotnet test tests\AirPodsControl.Tests` (154) and
`python -m unittest discover -s tests/bridge` (61).

## Privacy

- No telemetry, no accounts. Network access only during setup (Alpine image, Alpine packages, usbipd-win via winget).
- The AirPods' pairing key is copied only into the local `airpods-bridge` WSL distribution and deleted with it.
- Logs (`%LOCALAPPDATA%\AirPodsControl\logs`, 7 days) contain the AirPods' Bluetooth address; settings are in
  `%APPDATA%\AirPodsControl`.

## Documentation

- [docs/WSL-BRIDGE.md](docs/WSL-BRIDGE.md) — how the bridge works · [docs/BRIDGE-OPERATIONS.md](docs/BRIDGE-OPERATIONS.md) — one
  operation = one hand-over, traces, input protection, press-and-hold modes · [docs/PROTOCOL.md](docs/PROTOCOL.md) — AAP and BLE
  formats with sources · [docs/RELEASING.md](docs/RELEASING.md) — release checklist. Most docs are in Russian.

## Licences and credits

- AirPods Control: [MIT](LICENSE).
- WSL kernel (only if bundled): GPL-2.0 — source is Microsoft's
  [WSL2-Linux-Kernel](https://github.com/microsoft/WSL2-Linux-Kernel) (tag in `BUILDINFO`) plus [kernel/patches](kernel/patches);
  embedded MediaTek firmware: [kernel/LICENCE.mediatek](kernel/LICENCE.mediatek).
- usbipd-win (GPL-3.0) and Alpine/BlueZ packages are downloaded by the setup from their official sources, not bundled.
- Protocol knowledge: [LibrePods](https://github.com/librepods-org/librepods) (AAP),
  [AirPodsDesktop](https://github.com/SpriteOvO/AirPodsDesktop) (BLE format, model IDs),
  [AirPodsWindows](https://github.com/YimingZhanshen/AirPodsWindows) (MagicAAP interface), [MagicPods](https://magicpods.app/magicaap/).

"AirPods" is a trademark of Apple Inc. This project is not affiliated with Apple.

<sub>Keywords: AirPods Windows, AirPods Pro Windows 11, AirPods noise cancellation Windows, AirPods ANC PC, AirPods transparency
mode Windows, AirPods Adaptive mode Windows, AirPods Off mode, AirPods stem press and hold, AirPods battery Windows, AirPods
without iPhone, AAP protocol, Apple Accessory Protocol, L2CAP, WSL2 Bluetooth, usbipd, no Test Mode, no driver ·
шумоподавление AirPods Windows, режим прозрачности AirPods на ПК, AirPods на Windows без тестового режима.</sub>
