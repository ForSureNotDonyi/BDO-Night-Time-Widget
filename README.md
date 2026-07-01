# BDO Night Time Tracker

A tiny always-on-top widget for **Black Desert Online** that shows the current
in-game day/night phase and counts down to the next transition — so you don't
have to alt-tab or squint at the corner of your screen to know when night
(and its bonuses) starts or ends.

![Widget screenshot](screenshots/bdo_clock.png)

## Features

- **Live phase indicator** — ☀ for day, ☾ for night, with a countdown to the
  next transition (e.g. `Night in 42m 10s`).
- **Automatic sync via OCR** — reads the in-game clock straight off your
  screen (top-right corner of the BDO window) using Windows' built-in OCR
  engine. No manual input needed in most cases.
- **Auto show/hide** — the widget appears only while BDO is the focused
  window, and disappears when BDO is minimized or closed, so it never
  clutters your desktop when you're not playing.
- **Click-through by default** — the widget doesn't block clicks to the game
  underneath it. Hold **Ctrl** to interact with it (move it, click its
  buttons).
- **System tray icon** — lives quietly in the tray with an Exit option.
- **Remembers its position** between sessions.
- **Single instance** — launching it twice just focuses/no-ops instead of
  spawning duplicates.

## Requirements

- Windows 10/11 (uses the built-in `Windows.Media.Ocr` engine — no external
  OCR install required)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Black Desert Online running as `BlackDesert64.exe` (the standard 64-bit
  client)

## How it works

BDO's in-game clock runs on a fixed cycle: a full day/night cycle takes
**4 real hours**, split into **3h20m of in-game day** and **40m of in-game
night**. Once the widget reads the clock once (via OCR), it can calculate
the current phase and time-to-transition on its own — no need to re-read the
clock constantly.

On top of a single read, the widget uses an **edge-triggered calibration**:
it watches for the exact moment the in-game clock's minute ticks over, which
pins the sync down to a much tighter window than a single OCR snapshot
would allow.

If OCR fails to read the clock (e.g. unusual UI scaling), you can also force
a re-sync manually with the ↺ button on the widget.

## Usage

1. Launch BDO and the widget.
2. Bring BDO into focus — the widget will automatically try to sync with the
   in-game clock a few seconds later.
3. Once synced, the widget shows the current phase and countdown.
4. Hold **Ctrl** and drag the widget to reposition it; release Ctrl to go
   back to click-through mode.
5. Click ↺ (while holding Ctrl) to force a manual re-sync if the countdown
   ever looks wrong.
6. Click ✕ or use the tray icon to close the widget.

### If OCR doesn't read the clock correctly

The OCR capture region can be tuned via:
```
%APPDATA%\BDONightTimeTracker\ocr_region.json
```
Adjust `RightOffset` / `TopOffset` / `Width` / `Height` to match where the
clock sits in your UI, then restart the widget. A debug capture
(`ocr_debug.png`) is saved to the same folder whenever OCR runs, which is
useful for checking what region is actually being captured.

## Building from source

```
git clone https://github.com/ForSureNotDonyi/BDO-Night-Time-Widget.git
cd BDO-Night-Time-Widget
dotnet build
```

Or open `BDONightTimeTracker.slnx` in Visual Studio 2022+ and run from
there.

## Contributing

Contributions are welcome via fork + pull request — see [LICENSE](LICENSE)
for the exact terms. In short: you're free to fork and modify the code to
prepare a pull request back to this repository, but redistributing your own
copies or using the code commercially isn't permitted without permission.

## License

See [LICENSE](LICENSE).
