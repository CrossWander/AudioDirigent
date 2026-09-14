# AudioDirigent

A portable Windows tray app (WPF, .NET 10) that keeps the sound on the device you actually
meant, shows a card when a device connects, and brings back devices that vanished after
hibernation. It knows nothing about any particular manufacturer: every decision it makes
comes from what Windows reports about the device itself. Interface in English, Ukrainian
and Russian.

## The problem

Windows picks the default audio device on its own, and its idea of "available" is not
yours. A virtual driver installed by a mixer or a headset utility registers endpoints that
stay alive whether or not anything is plugged in, and once the real device goes away, the
sound moves to one of those — a default device formally exists, and nothing plays.

A Bluetooth headset arrives twice: as a stereo device and as a hands-free one, mono and
16 kHz, the sound of a phone call. Make its hands-free microphone the default input and
Windows moves the *output* to the same profile, which is why music suddenly sounds like a
call.

And a wireless headset on a 2.4 GHz receiver is invisible in a different way: the receiver
stays an active audio device with the headset switched off, so Windows has nothing to
report and the sound goes into a headset nobody is wearing.

To see what your machine has: `AudioDirigent.exe --list`.

## How it decides

The app subscribes to `IMMNotificationClient` — Core Audio events for endpoints added,
removed and changed — and re-evaluates one rule on every change:

1. Take the **active** devices and drop the blocked ones.
2. Find the first match against the priority list in `config.json`.
3. Set it as the default (Console, Multimedia and Communications roles) **if** the current
   device is blocked, missing, or ranks lower.

Two preferences settle ties inside one priority, and both come from device properties
rather than from names:

- The Bluetooth **phone profile always yields** to the music profile of the same headset.
  The old way of doing this was a blocklist entry for `Hands-Free`, which silently does
  nothing on a non-English Windows — there the same device is called something else
  entirely. The property is the same in every language.
- **Software devices yield to real ones.** They are not blocked: whoever installed a mixer
  listens through that mixer, and an app that quietly removes it is simply broken.

A manual choice outside the rules is left alone — otherwise the app would fight the person
using it.

**Recording devices go through the same rule**, with their own priority and block lists
under `[input]`. That is not a bonus feature but the other half of the same problem: a
headset arrives with a microphone, and it is the microphone that drags the output into the
mono channel.

Changing the default endpoint goes through the undocumented `IPolicyConfig` COM interface.
Windows exposes no public API for this; every known tool takes the same route.

### Out of the box

The first run writes **a single priority: whatever is default right now**. Nothing else,
and no blocks. The app then does exactly one thing — holds the choice you already made and
returns to it when something tries to take over. Everything else in that list appears
because you put it there.

**A device you plug in plays immediately** and is written nowhere. To the app, plugging a
cable and picking a device by hand are the same event, and it already respects the second
one. Unplug it and the rule brings the sound back to the first priority.

**Bluetooth does not work that way**, because it connects by itself — the headset links up
when you take it out of the case, which is not a request to move the sound. Instead the card
above the tray offers **Make it the main device**, one click that switches the sound and
writes the device to the top of the priorities. Ignore the card and nothing changes.

## The card

When a device appears or disappears, a card rises from the bottom right corner of whichever
screen the cursor is on, holds four seconds, and says what happened. Hovering it stops the
clock — the button inside would otherwise be unreachable in the seconds it lives.

The picture comes from one set of drawings keyed by what the device is — headphones,
speakers, microphone, screen — rather than from the system icon of each device. A popup is
recognisable by being uniform; system icons are a lottery of stock renders and grey
rectangles.

**The battery line appears when there is a battery to show**, which is rarer than it
sounds. Windows fills that property for some Bluetooth devices and never for a headset on a
2.4 GHz receiver — to the system that is a generic HID device, and only the manufacturer's
own protocol knows the charge. So the line missing is the normal state, not a failure.

Balloons stay where they were, with their own switch: a failed repair or a taken hotkey is
not about a device, and a card with a picture of headphones does not suit it.

## The window

No system frame: the title bar is drawn by the app and doubles as its header, with its own
settings, minimise, maximise and close buttons. Dragging, resizing and snapping stay native
(`WindowChrome`), and a maximised window does not cover the taskbar.

The window follows the Windows theme unless told otherwise, and light and dark are one
markup: colours are named by role — canvas, surface, dimmed text, bad — and a palette
answers. The accent is the one already picked in Windows, nudged until it reads on the
ground it lands on, so the app looks like the rest of the desktop rather than like a brand.

Tray icon: a conductor's baton, colour when watching, grey when paused. Double-click opens
the window. Both the close button and minimise send it back to the tray — the app never
sits on the taskbar; exit is in the tray menu.

Inside:

- **Default device** — what is playing now, the state of the probe if one is set up, and
  the Watching/Paused badge. The badge is the pause switch.
- **Output / Input** — two tabs over one list: each direction has its own rules. Every
  endpoint, active ones first. On the left a badge: priority number, a red `✕` for blocked,
  or a `+` for a live device no rule covers yet — hovering it names the rule that put it
  there. Next to the state, a chip with the level the device is set to on a switch.
- **Buttons** — Prioritise / Block / Clear rule / Up / Down act on the selected row and
  write `config.json` immediately. Two more appear only when they apply: **Make it the main
  device** for a device outside the rules, and **Connect** / **Disconnect** for a Bluetooth
  headset. **Restore sound**, apart at the right end, acts on the system instead.
- **Settings** — theme, run at logon, USB port power, the connection card, balloons, and
  hotkeys. Below them, apart, everything about new versions: **Check for updates**, a daily
  check that can be switched on, and **Install** — the only times the app touches the
  network.
- **Log** — an expander with the switch history, trimmed to the last two days on every start.

### Connecting a Bluetooth headset

A paired headset that is not connected still sits in the list, and **Connect** raises the
link — the same thing the button in Windows settings does, and by the same route: down the
device topology from the endpoint to its KS filter, and a one-shot reconnect. The Bluetooth
API looks like the obvious way and is not: it installs and removes the profile driver
rather than raising the link, so "connecting" through it means a disable/enable round trip,
seconds of waiting and a device that blinks out of the list meanwhile.

The command goes to **every endpoint of the same physical device** — otherwise the music
profile comes up while the headset microphone stays down. **Disconnect** is the same call
with one constant changed, and it is there to hand the headset back to a phone without
opening Windows settings.

The command returns at once while the link takes a second or two, so the window updates on
the Core Audio event rather than by asking again.

## After hibernation

On waking, a USB device sometimes does not return at all: the PnP node is still there, the
audio endpoints are gone, replugging the cable changes nothing, and only a reboot used to
fix it — and the app was usually not even running by then, because a logon task does not
fire when a machine resumes.

**The wake is noticed.** `SystemEvents.PowerModeChanged` records which devices were alive
before sleep, so a headset that was simply left at home is never "repaired". On resume the
rule is recalculated after 5 seconds and then every 10, four times over, because USB
devices come back slowly and Core Audio may send no event at all.

**What did not come back is reinstalled.** The app takes the PnP node behind each missing
endpoint — read from the endpoint itself, not guessed from its name — climbs to its
composite parent and restarts that through `pnputil /restart-device`. The parent matters:
the sound of a headset sits on one interface of a composite device and its control HID on
another, and restarting only the first brings the sound back while the vendor's app goes on
insisting no headset is connected. Restarting the parent re-enumerates every interface at
once, which is exactly what pulling the cable out and back in does.

If that is not enough it restarts `AudioEndpointBuilder`, which rebuilds the entire endpoint
list; that is the part a reboot was doing. **Restore sound**, in the window and in the tray
menu, does the same on demand; from the console it is `--recover`.

**And the ports are kept awake.** "Allow the computer to turn off this device to save
power" is checked by default on USB hubs, and it is the likeliest reason a device does not
survive hibernation in the first place. The **Keep the ports awake** toggle unchecks it on
every USB node Windows offers the setting for. Only nodes where the box was still checked
are taken, and their names go into `config.json`; turning the toggle off restores exactly
those and nothing else.

Reinstalling a device and restarting a service both need administrator rights, and the app
never asks for them on its own. Enable **Run at logon** once from an elevated instance and
the task keeps those rights; otherwise recovery writes a line into the log saying it cannot
proceed.

## Install

Nothing to install: a single portable exe with the runtime inside. Everything it creates
lives next to it and nowhere else:

| File          | Contents                                                            |
|---------------|---------------------------------------------------------------------|
| `config.json` | everything the app remembers: rules, settings, language, USB nodes   |
| `log.txt`     | switch log, trimmed to the last two days on every start              |

Uninstalling means deleting the folder. Nothing is written to the registry or the user
profile. Two toggles are the exception, because they change Windows itself: **Run at logon**
creates a scheduler task, and **Keep the ports awake** unchecks a power setting on USB
nodes. Clearing each toggle undoes its own change, so clear them before deleting the folder.

On read-only media the app falls back to defaults: rules won't persist, but switching still
works.

### Updating

**Check for updates** asks GitHub about the latest release; a daily check can be switched
on, and it stays quiet unless there is something newer. **Install** downloads the new exe
next to the old one, leaves a short script behind and quits.

A program cannot replace its own file while it runs, so the script does it: it retries the
move until Windows releases the file — by the fact of the move succeeding, not by a timer,
because a single-file exe unpacks itself and holds its own file for a moment after the
process is gone — then starts the new version and deletes both the leftover download and
itself. Nothing of the old version stays in the folder.

If the folder is read-only — the usual case being Program Files — the download fails and
says so; the app is portable and belongs somewhere it can write.

Build it yourself:

```powershell
dotnet build -c Release                  # for development
.\tools\publish.ps1                      # self-contained exe into release\
.\tools\publish.ps1 -FrameworkDependent  # ~300 KB, needs .NET Desktop Runtime 10
```

The sources sit in eight places, one namespace for all of them:

| Folder      | Contents                                                                   |
|-------------|----------------------------------------------------------------------------|
| root        | `Program.cs` — the entry point, and nothing else                           |
| `Domain/`   | the ideas the app is about: an endpoint, a rule set, a pinned level        |
| `Audio/`    | the point of the app: the watcher loop, the rules, the volume              |
| `App/`      | its own housekeeping: the settings file, the journal, the build version    |
| `Interop/`  | the bindings: Core Audio, Bluetooth over KS, HID and SetupAPI              |
| `Platform/` | what those make the system do: PnP restart, scheduler task, USB power      |
| `Ui/`       | the window and its parts, the theme, the tray icon, the connection card    |
| `Lang/`     | language: one dictionary per language, and the code that switches them     |

There are no NuGet dependencies at all, and that is deliberate. Everything the app needs
from Windows is reached through hand-written P/Invoke: the battery property, the device
properties and the connection events are all the same PnP database WinRT would read, and
moving to a WinRT target framework costs six megabytes of projection in the published exe —
measured, not guessed — that trimming cannot remove, because WPF forbids trimming outright.

## Configuration

Normally edited in the window. Everything lives in one `config.json` next to the
executable, created on first run:

```json
{
  "language": "en",
  "notify": true,
  "popup": true,
  "hotkeys": false,
  "pauseHotkey": "Ctrl+Alt+P",
  "recoverHotkey": "Ctrl+Alt+R",
  "output": {
    "priority": [ "Speakers (Realtek(R) Audio)" ],
    "blocked": []
  },
  "input": {
    "priority": [ "Microphone (Realtek(R) Audio)" ],
    "blocked": []
  },
  "volume": [],
  "usbPower": false,
  "usbPowerHeld": []
}
```

A priority is a fragment of a device name, matched case-insensitively, so one line can
cover several devices — the badge in the list says when it does. Blocks outrank priorities.
A missing `output` or `input` is filled with the defaults on the next run; an empty one is
left empty — that is a decision, not an omission, and that direction is never touched.

`volume` is the level to set a device to at the moment the app switches to it — the first
entry whose `match` is part of the device name wins. Windows already remembers a volume per
endpoint, so the list is only for keeping a device at a fixed level; while it is empty the
app never touches the volume.

`usbPowerHeld` is written by the app itself: the USB nodes it took the power-saving
checkbox off, and the only way back.

The hotkeys can only be changed here. Modifiers are `Ctrl`, `Alt`, `Shift` and `Win` in any
combination plus one key; the names are WPF's (`P`, `F9`, `Oem3`, …).

Hand edits are read as written: comments and a trailing comma are tolerated, and the app
rewrites the file only when a setting changes.

### The probe, for a wireless headset on a receiver

Whether a wireless headset is switched on cannot be determined through Windows at all. The
USB audio driver builds its jack description statically from descriptors, and the state of
the radio link never reaches it: as far as the system is concerned the headset is connected
for as long as the receiver is plugged in, the stream opens, and the audio goes nowhere.

The only way is to ask the receiver directly over HID, in whatever protocol its
manufacturer chose. The app ships no protocols and polls nothing unless you describe one:

```json
"probe": {
  "vendors": [ "03F0", "0951" ],
  "usagePage": "01C0",
  "request": "0C 02 03 01 00 02",
  "statusByte": 6,
  "onValue": "02",
  "covers": "Cloud III"
}
```

Every number is hexadecimal, the way protocol analysers print them. `vendors` narrows the
search (empty means any), `usagePage` picks the control collection, `request` is what gets
written, `statusByte` is where the answer carries the state and `onValue` is what that byte
reads when the headset is on. `covers` is the fragment of the device name to hide while the
headset is off — that is what keeps the sound from going into a headset nobody is wearing.

The example above is real: it is the HyperX Cloud III receiver, whose protocol was
documented by
[platorp/HyperX-Cloud-III-3-S-Audio-Switcher](https://github.com/platorp/HyperX-Cloud-III-3-S-Audio-Switcher).
Use it as a shape to copy, not as something the app assumes.

To see what your machine exposes and what your receiver answers:
`AudioDirigent.exe --devices`.

## Autostart

The **Run at logon** toggle creates one task, `AudioDirigent`, that starts
`AudioDirigent.exe --tray` on two triggers: logon, and the Power-Troubleshooter wake event.
A logon trigger stays silent when a machine resumes, and if the app did not survive the
sleep nothing else would bring it back; a live instance turns the duplicate launch away
through its mutex.

The task is described by XML rather than by `schtasks` switches, because the defaults that
command applies are fatal here: a task is not started at all while the machine runs on
battery, is killed the moment the cable is pulled, and is stopped after three days. On a
laptop that means the app quietly never runs. All three are off in the XML.

```powershell
AudioDirigent.exe --autostart        # is the task there or not
AudioDirigent.exe --autostart on     # create it
AudioDirigent.exe --autostart off    # remove it
```

## Languages

Strings live in `Lang/Lang.en.xaml`, `Lang/Lang.uk.xaml` and `Lang/Lang.ru.xaml` —
`ResourceDictionary` files swapped wholesale at runtime. The language is switched in the
window footer and remembered in `config.json`; English is the default. Console modes and
log messages follow the same setting. Device names are not translated — Windows supplies
them.

To add a language: drop a `Lang.<code>.xaml` beside the others with the same keys and add
the code to `Localization.Codes`.

## Command line

| Argument                | What it does                                                    |
|-------------------------|-----------------------------------------------------------------|
| (none)                  | open the window, keep running in the tray                       |
| `--tray`                | start in the tray without a window — used by autostart           |
| `--once`                | apply the rule once and exit (console)                          |
| `--list`                | every device with its state, bus and kind, and the current default |
| `--test`                | self-check: the rule, the dictionaries, the card, the switching  |
| `--devices`             | HID interfaces and the probe's answer, if one is set up          |
| `--recover`             | bring vanished devices back                                      |
| `--autostart [on\|off]` | query, create or remove the scheduler task                       |

## Similar projects

All of them change the default device through the same `IPolicyConfig`:

- [Belphemur/SoundSwitch](https://github.com/Belphemur/SoundSwitch) — the most active
  project; hotkey switching, per-process profiles
- [m2jean/ToothTray](https://github.com/m2jean/ToothTray) and
  [PolarGoose/BluetoothDevicePairing](https://github.com/PolarGoose/BluetoothDevicePairing) —
  the KS route to connecting Bluetooth audio, and the reasoning behind it
- [SpriteOvO/AirPodsDesktop](https://github.com/SpriteOvO/AirPodsDesktop) and
  [timschneeb/GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) — the
  connection card, for AirPods and Galaxy Buds respectively
- [o0Zz/PeripheralBatteryMonitor](https://github.com/o0Zz/PeripheralBatteryMonitor) — the
  battery property and per-vendor protocols on top of it
- [sgiurgiu/DefaultAudioChanger](https://github.com/sgiurgiu/DefaultAudioChanger),
  [marcjoha/AudioSwitcher](https://github.com/marcjoha/AudioSwitcher),
  [yan0lovesha/AudioSwitch](https://github.com/yan0lovesha/AudioSwitch) — switching utilities

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Copyright 2026 Dykalo Pavlo. Forks and derivative works must keep the contents of `NOTICE`
and mark the files they changed (sections 4b and 4d of the license).

Grown out of [HyperXAudioGuard](https://github.com/crosswander/HyperXAudioGuard), which
solved the same problem for one manufacturer's hardware.

## Author

Developed by Dykalo Pavlo, 2026.
