# Third-party notices

TLOverlay itself ships no model weights. What you download with
`tools/fetch-models.ps1` carries its own terms, and they are not all alike.
Read this before redistributing anything you build.

## llama.cpp

MIT License. Bundling `llama-server.exe` is fine, including commercially.
https://github.com/ggml-org/llama.cpp

## Translation models

| Model | Licence | Commercial use |
|---|---|---|
| Typhoon 2 (SCB10X) | See the model card on Hugging Face | Check the card - terms differ by release |
| Gemma 3 (Google) | Gemma Terms of Use | Permitted, with use restrictions |
| NLLB-200 (Meta) | **CC-BY-NC 4.0** | **Not permitted** |

NLLB-200 is the one to watch. It is a strong, small, fast translation model and
a reasonable default for personal use, but its non-commercial licence means a
build that bundles it cannot be sold or used commercially. The app surfaces the
active model's licence in its About screen for this reason.

## Windows components

`Windows.Graphics.Capture` and `Windows.Media.Ocr` are part of Windows and used
through their public APIs. No redistribution is involved.

## Fonts

If a Thai font is bundled for the overlay, its licence (SIL OFL for Sarabun and
Noto Sans Thai) applies and the licence file must ship alongside it.
