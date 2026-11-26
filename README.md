# Convert to Markdown

A Windows 11 app that turns lecture PDFs, Word, PowerPoint and Excel files, and pictures into Markdown (`.md`).
Right-click a file → **Convert to Markdown**, and the `.md` file appears next to it.

Everything runs on your PC. Your files are never uploaded.

## What it reads

- **Typed text**, headings, tables and highlights, including Arabic
- **Handwritten pen notes** on PDFs (Windows handwriting recognition)
- **Text in pictures and screenshots**: Windows OCR, or the built-in AI
- **Optional:** a description of each page's layout and pictures

Supported files: `.pdf`, `.docx`, `.docm`, `.pptx`, `.pptm`, `.xlsx`, `.xlsm`, `.png`, `.jpg`, `.jpeg`, `.bmp`, `.tif`, `.tiff`, `.gif`.
Old `.doc`, `.ppt` and `.xls` files: save them in the new format first.

## Built-in AI (optional, offline)

Runs [llama.cpp](https://github.com/ggml-org/llama.cpp) on your graphics card. You choose the model on first start:

| Model | Download | Notes |
|---|---|---|
| Qwen3-VL 4B | about 3 GB | Recommended: faster, best at copying text exactly |
| Gemma 4 | about 5.2 GB | Slower, longer descriptions |
| No AI | nothing | Fastest; Windows OCR reads pictures |

## Install

- **Microsoft Store:** (https://apps.microsoft.com/detail/9PGF0LSJNXQ4.)
- **From source:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download), then double-click `Build.cmd`.
  It makes `App\Convert to Markdown Setup.exe`.

## Privacy

No data collection. See the [privacy policy](https://yazeedalsuayr.github.io/convert-to-markdown/).

## License

Open source under the [MIT License](LICENSE). Copyright © 2026 Yazeed Alsuayr.
Free to use, change and share; copies must keep this copyright line. Parts made by others are listed in [THIRD-PARTY NOTICES.txt](THIRD-PARTY%20NOTICES.txt).
