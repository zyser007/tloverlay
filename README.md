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

**To run**

- Windows 10 build 19041 (2004) or newer, or Windows 11
- The game must run **Windowed Fullscreen / Borderless**. True exclusive
  fullscreen cannot be captured.
- English OCR language pack (present by default on virtually all installs)
- Nothing else. Release builds are self-contained, so no .NET runtime install.

**To build**

- .NET 8 SDK
- **Windows.** This does not cross-compile: WPF and the WinRT projections have no
  Linux or macOS build, so `dotnet build` fails outside Windows no matter the
  runtime identifier.

## Using it

1. `dotnet run --project src/TLOverlay.App`
2. First run opens the setup screen. Download the model, or point it at one you
   already have. This happens once.
3. Pick the game window from the list. The panel warns you if the window still
   has a border, which usually means the game is in exclusive fullscreen.
4. `Ctrl+Alt+R`, drag a box over the dialogue area, press Enter. The editor opens
   showing the regions you already have; dragging adds another, clicking one
   selects it and Delete removes it. Regions are saved per game and reload
   automatically next time.
5. `Ctrl+Alt+T` to start translating.

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+T` | Start / stop translating |
| `Ctrl+Alt+R` | Draw the capture region |
| `Ctrl+Alt+H` | Show / hide the translated text |
| `Ctrl+Alt+G` | Show / hide the capture region outlines |
| `Ctrl+Alt+C` | Toggle click-through |

## Models

The translation model and `llama-server.exe` are **not** in this repository, and
you do not need a terminal to get them. Run the app: if either is missing it
opens a setup screen that downloads both, with a progress bar, a resume if the
connection drops, and a Browse button for files you already have. Reachable
later from the control panel as **ตั้งค่าโมเดล**, which is also how you switch
model or move the work between CPU and GPU.

If you would rather script it, `tools/fetch-models.ps1` does the same job, but
it needs PowerShell 7 (`pwsh`), which Windows does not ship by default.

See [NOTICE.md](NOTICE.md) for model licensing - it matters, and not every
model here may be used commercially. The setup screen shows each model's licence
next to it in the dropdown.

## Build

```powershell
dotnet build tloverlay.sln -c Release
dotnet test tests/TLOverlay.Core.Tests/TLOverlay.Core.Tests.csproj
dotnet run --project src/TLOverlay.App
```

The projects compile against `net8.0-windows10.0.22621.0` but declare
`SupportedOSPlatformVersion` 10.0.19041.0. The newer projection is only needed at
compile time, for `GraphicsCaptureSession.IsBorderRequired`; that property is
probed with `ApiInformation` before use, so the app still runs on Windows 10
2004 - it just keeps the yellow capture border there.

## Release

### Producing a build

```powershell
dotnet publish src/TLOverlay.App/TLOverlay.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  --output artifacts/TLOverlay-win-x64
```

Three of those flags are load-bearing, and one option must stay off:

- `--self-contained true` - the audience is players, not developers. Asking them
  to install the .NET Desktop Runtime before they can read their game is a step
  most will not get past. The cost is size: the release zip is around 75 MB.
  Single-file packing means it really is one `TLOverlay.exe`, nothing beside it.
- `-p:IncludeNativeLibrariesForSelfExtract=true` - `Microsoft.Data.Sqlite` needs
  the native `e_sqlite3.dll`. Without this it never leaves the bundle and the
  translation cache throws on the first lookup, at runtime, on the user's
  machine.
- `--runtime win-x64` - required for a self-contained build, and the only
  architecture the capture interop targets.
- **Never `-p:PublishTrimmed=true`.** WPF does not support trimming. The build
  succeeds and the executable crashes on startup, which is the worst possible
  place to find out.

### What ships, and what does not

The zip contains `TLOverlay.exe`, `NOTICE.md` and `README.md`.

It does **not** contain `models/` or `runtime/`. Those are several gigabytes, and
their licences differ per model - some are non-commercial. The app fetches them
itself on first run, which is the whole reason the setup screen exists.

### Cutting a release

1. Bump `<Version>` in `Directory.Build.props`.
2. Commit that on the default branch.
3. `git tag v0.2.0 && git push origin v0.2.0`

The tag is what triggers `.github/workflows/release.yml`: it runs the tests,
publishes, zips, and creates the GitHub Release with generated notes. Nothing
else publishes a release, so a tag always corresponds to something that shipped.

CI runs the same publish command on every push and uploads the result as a build
artifact. A publish that has stopped working is therefore caught on the commit
that broke it, not while cutting a tag.

### Note on signing

The executable is unsigned, so Windows SmartScreen shows a warning the first time
a user runs it ("More info" then "Run anyway"). Removing that needs a code
signing certificate, which this project does not have. Worth saying plainly in
release notes rather than leaving people to wonder.

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
