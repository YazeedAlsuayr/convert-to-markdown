# Convert to Markdown

A Windows 11 app that turns your documents into clean Markdown (`.md`): PDFs, Word, PowerPoint and Excel files, scans and pictures.
Right-click a file → **Convert to Markdown**, and the `.md` file appears next to it.

Everything runs on your PC. Your files are never uploaded.

## Who it's for

Anyone who wants the content of their files as plain, editable text: students and teachers, office and research work,
or anyone feeding documents to AI tools, note apps (Obsidian, Notion) or a search index. Works in English and Arabic.

## What it reads

- **Typed text** in reading order: headings, columns, lists, tables, footnotes, links and highlights
- **Handwritten pen notes** on PDFs, read by the built-in AI (or a quick built-in reader)
- **Text in pictures, screenshots and scans**, including charts, tables and forms with ticked or circled answers
- **Optional:** a short description of each page's layout and pictures
- Headers and footers repeated on every page are shown once; blank pages are marked

Supported files: `.pdf`, `.docx`, `.docm`, `.pptx`, `.pptm`, `.xlsx`, `.xlsm`, `.png`, `.jpg`, `.jpeg`, `.bmp`, `.tif`, `.tiff`, `.gif`.
Old `.doc`, `.ppt` and `.xls` files: save them in the new format first.

## Built-in AI (optional, offline)

Runs [llama.cpp](https://github.com/ggml-org/llama.cpp) on your graphics card. You choose the model on first start:

| Model | Download | Notes |
|---|---|---|
| Qwen3-VL 4B | about 3 GB | Recommended: faster, best at copying text exactly |
| Gemma 4 | about 5.2 GB | Slower, longer descriptions |
| No AI | nothing | Fastest; a quick built-in reader reads pictures and handwriting (less accurate) |

## Install

- **Microsoft Store:** [Get it from the Microsoft Store](https://apps.microsoft.com/detail/9PGF0LSJNXQ4)
- **From source:** install the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer), then double-click `Build.cmd`.
  It makes `App\Convert to Markdown Setup.exe`.

## Privacy

No data collection. See the [privacy policy](https://yazeedalsuayr.github.io/convert-to-markdown/).

## License

Open source under the [MIT License](LICENSE). Copyright © 2026 Yazeed Alsuayr.
Free to use, change and share; copies must keep this copyright line. Parts made by others are listed in [THIRD-PARTY NOTICES.txt](THIRD-PARTY%20NOTICES.txt).
