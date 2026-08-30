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

## Status

Core pipeline, profiles, glossary, caching and the local translation backend are
implemented and unit-tested. Capture, OCR and the overlay window are being built
on top of them.
