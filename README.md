# TLOverlay

Real-time English -> Thai translation overlay for games running in **Windowed
Fullscreen (borderless)** on Windows 10 / 11.

โปรแกรม overlay แปลอังกฤษเป็นไทยแบบเรียลไทม์ สำหรับเกมที่รันแบบ Windowed Fullscreen
บน Windows 10/11 — ทำงานออฟไลน์ 100% ไม่ต้องต่อเน็ต ไม่ inject DLL เข้าเกม

## How it works

```
Windows.Graphics.Capture   capture the game's window only, never the whole screen
        v
ChangeDetector             skip frames that did not change (>90% of them)
        v
Windows.Media.Ocr          offline English OCR, ships with Windows
        v
TextAssembler              rebuild wrapped lines into whole sentences
        v
CachingTranslator          SQLite + LRU; repeat dialogue is instant
        v
llama.cpp on 127.0.0.1     local GGUF model, no network egress
        v
Overlay window             layered, click-through, excluded from capture
```

Three design decisions carry most of the weight:

- **No injection, no D3D hooking.** The overlay is an ordinary topmost window and
  capture goes through the public WGC API. That keeps it out of anti-cheat's way
  and off the list of things that crash games.
- **The overlay is excluded from capture** via `SetWindowDisplayAffinity`
  (`WDA_EXCLUDEFROMCAPTURE`). Without it, OCR reads back its own Thai output and
  the pipeline feeds on itself.
- **Text must hold still before we translate it.** Games reveal dialogue one
  character at a time; translating on first difference would fire a dozen times
  per line and show half-sentences.

## Requirements

- Windows 10 build 19041 (2004) or newer, or Windows 11
- .NET 8 SDK to build
- The game must run **Windowed Fullscreen / Borderless**. True exclusive
  fullscreen cannot be captured.
- English OCR language pack (present by default on virtually all installs)

## Build

```powershell
dotnet build tloverlay.sln -c Release
dotnet test tests/TLOverlay.Core.Tests/TLOverlay.Core.Tests.csproj
```

## Models

The translation model and `llama-server.exe` are **not** in this repository.

```powershell
pwsh tools/fetch-models.ps1
```

See [NOTICE.md](NOTICE.md) for model licensing - it matters, and not every
model here may be used commercially.

## Using it

1. `pwsh tools/fetch-models.ps1` once, to get the server and model.
2. `dotnet run --project src/TLOverlay.App`
3. Pick the game window from the list. The panel warns you if the window still
   has a border, which usually means the game is in exclusive fullscreen.
4. `Ctrl+Alt+R`, drag a box over the dialogue area, press Enter. The region is
   saved per game and reloads automatically next time.
5. `Ctrl+Alt+T` to start translating.

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+T` | Start / stop translating |
| `Ctrl+Alt+R` | Draw the capture region |
| `Ctrl+Alt+H` | Hide / show the overlay |
| `Ctrl+Alt+C` | Toggle click-through |

## Status

End to end and working: capture, OCR, translation, overlay, per-game profiles,
glossary, caching, hotkeys and the region editor. The control panel reports
average OCR and translation time plus the frame skip ratio, which is the number
that tells you whether change detection is doing its job - below roughly 80%
during play means a region is picking up animated scenery.

Not built yet: snip-once translation (`Ctrl+Alt+S` reports this), the NLLB ONNX
backend as a lighter alternative to the local LLM, and automatic region
detection.

## Known limits

- Exclusive fullscreen cannot be captured. Borderless only.
- Windows 10 builds before Windows 11 show a yellow capture border around the
  game that the OS will not let us turn off.
- Text baked into textures with heavily stylised fonts may not read.
- A local LLM competes with the game for VRAM. `gpuLayers` defaults to 0 (CPU)
  in `%AppData%\TLOverlay\settings.json` for that reason; raise it if you have
  headroom and want sub-second translations.
