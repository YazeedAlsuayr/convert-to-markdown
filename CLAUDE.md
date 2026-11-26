# CLAUDE.md — Convert to Markdown

Read this whole file before doing anything. Then read `CLAUDE.local.md` (private notes: the owner's
preferences, local paths and private test files; it is not on GitHub).

---

## 0. Start here (state on 26 Nov 2026, evening)

- **Branch:** work happens on **`dev`**. `main` = the safe copy (stage 1 only). Never commit to `main`.
- **Stage 3 (Excel)** is committed and pushed on `dev` (commit `3088263`).
- **Stage 4 (Word and PowerPoint)** is committed and pushed on `dev`.
- **Stage 5 (speed)** is committed and pushed on `dev`. Nothing uncommitted.
- **Next after that:** decide with the owner about merging `dev` → `main`, then stage 6.
- UI wording: never say "Windows OCR" / "Windows handwriting" to users; say "quick built-in reader" (owner's wish).
- **`Tests/` is in `.gitignore`**: the owner doesn't want any test file on GitHub. Never commit it.
- The owner may ask you to commit/push for him; then add the co-author line `Co-Authored-By: Claude …`.
- The owner is not a programmer. **Before doing anything, tell him in simple words what you will do and wait
  for "yes"** (details in section 1).

---

## 1. Rules for working with the owner (Yazeed)

**How to write to him and explain your work:** `talking-to-yazeed.local.md` (loaded automatically through
`CLAUDE.local.md`; private, not on GitHub). Follow it in every reply.

1. **Tell first, then wait.** Before changing files, running something that changes things, committing or
   pushing: say what you will do and why, in plain simple English, and wait for his OK.
2. **Never commit or push without being asked.** He commits himself in GitHub Desktop. When a stage is
   ready, give him the exact commit summary to type (e.g. `Stage 3: Excel number formats, hidden parts, charts`).
3. **Never work on `main`.** Check `git branch --show-current` says `dev` before editing.
4. **Test before calling anything done.** Build, run the test kit, grade against the key, compare with
   `Tests/baselines/` so nothing that passed before breaks. Report results as a short table.
5. **Keep his explanations simple**: short sentences, no jargon, step-by-step for GitHub Desktop and Windows.
   He writes fast with typos; read for meaning.
6. **Be honest about quality.** If a change is worse or only a small gain, say so with numbers.
7. **Don't control his screen or open his apps** unless he asks in that conversation.
8. **Line endings:** keep each file's existing line endings (most files are CRLF on disk; check with
   `git ls-files --eol`). Changing them makes GitHub Desktop show the whole file as changed.
9. The app must stay **general** (any document, any subject, any user): no medical or student-specific
   wording in the app, its AI prompts, README or Store text. Exception: `Vocabulary.cs` keeps its built-in
   medical word list (he asked to keep it).

---

## 2. What the app is

**Convert to Markdown** is a Windows 10/11 desktop app (C#, WPF, .NET) by Yazeed Alsuayr. Right-click a file
→ **Convert to Markdown** → a `.md` file appears next to it. Everything runs locally; files are never uploaded.

Supported: `.pdf .docx .docm .pptx .pptm .xlsx .xlsm .png .jpg .jpeg .bmp .tif .tiff .gif`.
Old `.doc .ppt .xls` are **not** supported on purpose (the owner decided; the app tells the user to re-save).

What it reads:
- Typed text from the file itself (exact): reading order, columns, lists, tables, footnotes, links, highlights.
- Pen/ink annotations on PDFs (handwriting) and highlighter strokes.
- Text in pictures/screenshots/scans: the built-in AI, or Windows OCR when the AI is off.
- Optional "Describe layout and pictures" (AI writes a short description; about 2x slower).

Built-in AI (optional, offline): **llama.cpp** (`llama-server.exe`) running a vision model on the GPU
(`-ngl 99`, falls back to CPU `-ngl 0`), context `-c 8192`, OpenAI-style API on `127.0.0.1:<free port>`.
The server only runs while converting and is tied to the app with a Windows Job Object (dies with the app).

| Model id | Name | Files (in `<DataDir>\ai\model`) | Size |
|---|---|---|---|
| `qwen` (default, recommended) | Qwen3-VL 4B | `Qwen3VL-4B-Instruct-Q4_K_M.gguf` + `mmproj-Qwen3VL-4B-Instruct-Q8_0.gguf` (from huggingface Qwen/Qwen3-VL-4B-Instruct-GGUF) | ~3 GB |
| `gemma` | Gemma 4 | `gemma-4-E4B-it-Q4_0.gguf` + `mmproj-gemma-4-E4B-it-Q8_0.gguf` (ggml-org/gemma-4-E4B-it-GGUF) | ~5.2 GB |

---

## 3. Where things are

**Repo:** https://github.com/YazeedAlsuayr/convert-to-markdown (MIT License, © 2026 Yazeed Alsuayr)
- Local clone (the only project folder): `C:\Users\iHero\Documents\GitHub\convert-to-markdown`
- Branches: `main` (stage 1, safe), `dev` (stage 1 + 2 + "make it general" + 3 + 4 + 5).
  `Tests/` is local only (git-ignored), never on GitHub.
- GitHub Pages serves `docs/index.html` = the privacy policy:
  https://yazeedalsuayr.github.io/convert-to-markdown/

**Microsoft Store:** published, version 2.0.0.0. Store ID **9PGF0LSJNXQ4**
(https://apps.microsoft.com/detail/9PGF0LSJNXQ4). Identity is in `StoreIdentity.txt`
(Name `YazeedAlsuayr.ConverttoMarkdown`, Publisher `CN=C919B301-E998-4955-B8B5-C1FBD6C9758D`,
PublisherName `Yazeed Alsuayr`). Raise `Version` for every upload (last number stays 0): next is `2.1.0.0`.

**App data folders** (settings.json, `ai\engine`, `ai\model`, `cache`, `last_run.log`):
- Dev/Setup build (what `Test.cmd` runs): `%LOCALAPPDATA%\ConvertToMarkdown\`
  → log: `C:\Users\iHero\AppData\Local\ConvertToMarkdown\last_run.log` (reset at every app start).
- Store build: `%LOCALAPPDATA%\Packages\YazeedAlsuayr.ConverttoMarkdown_75xwfy8d804g8\LocalState\`
- The Store version reuses models already at `%LOCALAPPDATA%\ConvertToMarkdown\ai\model` (copies them on
  setup) — the owner wants that folder kept as a backup for fast reinstall.

**Test kit and results:** `Tests/` in this repo (section 8). Private real-world files: see `CLAUDE.local.md`.

---

## 4. Code map

| File | What it does |
|---|---|
| `Program.cs` | Entry point. One window for many files (named pipe + mutex). Store detection (`IsStore`), `DataDir`, command-line flags (`--settings --setup-ai [qwen\|gemma] --remove-ai --check-ai --ai on\|off --ai-model --remove-model --install --uninstall -Embedding --trust-cert`), taskbar identity, `Log` class (`last_run.log`). |
| `MainWindow.cs` | The progress window (Windows 11 style). Runs conversions, shows errors. After success it closes itself after 8 s; on failure it stays open. |
| `Converter.cs` | **PDF and pictures.** PDF pipeline (below), annotations (highlights, ink, notes), handwriting (`ReadHandwritingAsync`, `InkPng`, `GrayPng`, `RecognizeInkAsync`), picture regions, AI/OCR calls, header/footer detection (`RepeatedEdges`), links (`AddLinks`), `Engines` (Windows OCR + ink recognizer). |
| `TextLayer.cs` | **PDF typed-text layout engine** (stage 1): glyphs → lines → blocks → reading order; columns, Q&A tables, grid tables (`RenderGrid`), lists (`RenderBlock`), super/subscripts (`MergeScripts`, `Joined`), drawn bullets (`AddDrawnBullets`), Arabic/RTL (`ToLogical`), ligatures (`Clean`), `WordsIn`. |
| `OfficeConverter.cs` | **Word, PowerPoint, Excel** read directly from the zip/XML (no Office needed). Pictures inside go to the AI (or OCR). Excel part rewritten in stage 3 (`ExcelAsync`, `SheetBlocks`, `RenderBlock`, `DrawingAsync`, `ChartText`, `Points`). |
| `ExcelFormat.cs` | (stage 3, new) Excel number formats → displayed text: dates, times, %, currency, thousands, `(negatives)`, scientific, `General` (0.30000000000000004 → 0.3), built-in format ids, 1900/1904 dates. |
| `Ai.cs` | llama.cpp server start/stop, GPU→CPU fallback, `AskAsync`, prompts (`DescribePrompt`, `PicturePrompt`, `PictureTextPrompt`, `FullPrompt`, `Symbols`), answer cleanup, model download/setup (`SetupAsync`), `Settings` (UseAi, Model, Describe in `settings.json`), `JobObject`. |
| `Vocabulary.cs` | Word list for handwriting "possible: …" suggestions: the document's own words + a built-in medical list (keep it). |
| `SetupWindow.cs` | First-run / Setup.exe welcome screens: choose AI model (Qwen / Gemma / No AI), Describe tick, download. |
| `SettingsWindow.cs` | Settings window (no file given): AI on/off, Describe, models (download/delete), "Convert files…". |
| `Installer.cs` | Non-Store install for the current user (`%LOCALAPPDATA%\Programs\ConvertToMarkdown`, Open with, Send to, Start menu, Settings > Apps uninstall). To be retired in stage 9. |
| `MenuCommand.cs`, `MenuPackage.cs`, `MakeMenuPackage.ps1` | Windows 11 top-level right-click entry for the non-Store build (COM `-Embedding` server + small signed MSIX). The Store package adds the menu itself. |
| `MakeStorePackage.ps1`, `Build Store package.cmd`, `Test Store package.cmd` | Store MSIX: stages files, includes the llama.cpp engine in the package (Store rule: no downloaded programs; cached in `engine-cache/`), runs makepri (`resources.pri`), makeappx; `-Test` makes a signed test package and installs it. Output: `StorePackage\`. |
| `Store/AppxManifest.xml`, `Package/AppxManifest.xml`, `Package/Assets/` (58 PNGs) | Store manifest (+ assets, `BackgroundColor="#005FB8"` to avoid an orange tile plate). |
| `Build.cmd` | Old self-contained single-file build + `Convert to Markdown Setup.exe` (to be retired). |
| `Test.cmd` | **Quick test:** drag files onto it → builds (`test-build.log`) → `dotnet run -c Release --no-build -- <files>`. |
| `Check AI.cmd`, `Set up AI.cmd`, `Set up Qwen.cmd`, `Use Qwen.cmd`, `Use Gemma.cmd`, `Remove Gemma.cmd`, `Settings.cmd` | Small helpers that call the app with flags. |
| `README.md`, `STORE LISTING v2.1.txt`, `STORE STEPS.txt`, `PRIVACY POLICY.txt`, `THIRD-PARTY NOTICES.txt`, `LICENSE`, `docs/index.html` | Public text. README and Store listing were made general on `dev`. |

Target: `net8.0-windows10.0.19041.0`, WPF, `RollForward=Major`, PdfPig 0.1.9 (NuGet), Windows APIs
(`Windows.Data.Pdf` rendering, `Windows.Media.Ocr`, `Windows.UI.Input.Inking`). Version in csproj: 2.0.0.
The owner's PC has the **.NET 10 SDK** (the .NET 8 SDK is gone and its download page no longer exists).

---

## 5. How conversion works (short)

**PDF (`Converter.PdfAsync`)**
1. First pass over all pages: `TextLayer.Build(page)` → lines (PdfPig letters → glyph rows → lines, with
   combining marks attached, diagonal watermark glyphs dropped, super/subscripts merged, drawn bullets added).
2. Header/footer lines repeated on ≥3 pages and ≥50% of pages (top/bottom 9%, digits normalised) are removed
   and written once at the top (`> Header on every page: …`).
3. Per page: `TextLayer.PageText` → reading order; `AddLinks` → `[text](url)`; annotations (highlights via
   QuadPoints → `### Highlights (exact)`, ink → handwriting, notes); pictures (`PictureRects`) → AI with
   `PictureTextPrompt` (or `PicturePrompt` when Describe is on) else Windows OCR; picture-only pages (scans)
   → `PicturePageAsync` with `FullPrompt`; blank pages → `*(blank page)*`.
4. Handwriting (stage 2, on `dev`): ink strokes → `SplitMarks` (lines/arrows/brackets vs letters; upright
   l/I/1 kept as letters) → `Cluster` (rows; tails of g/y and i-dots join their word) → 1–2 stroke notes that
   Windows ink says are marks are skipped → up to 8 notes drawn black-on-white, stacked, one PNG → AI
   (`NotesPrompt`: one line per note; mismatch → ask one by one with `NotePrompt`) → fallback Windows ink.
   Output: `### Handwriting (pen) - verify`, with "next to:" typed words, "possible:" spelling suggestions,
   `[highlighted]`, and pen-mark counts / "pen mark on: …".

**PDF page coordinates:** PdfPig y goes up; the code flips to y-down (`H - y`). `R` = rectangle record in TextLayer.cs.

**Office:** zip + XML. Word: styles → headings, numbering, tables, links, pictures. PowerPoint: slides in
order, titles, bullets/levels, tables, speaker notes, pictures (repeated logos shown once). Excel: section 8.3.

**AI answers** are cleaned (`Ai.Clean`: LaTeX → symbols, `<think>` removed, repeats cut).

---

## 6. Build, test, release

- **Build:** `dotnet build ConvertToMarkdown.csproj -c Release -nologo -v q`
- **Run a conversion:** `dotnet run -c Release --no-build -- "<file1>" "<file2>"` (opens the progress window;
  it closes 8 s after success, stays open after an error). Output `.md` is written next to each input file.
  Timing and AI calls: `%LOCALAPPDATA%\ConvertToMarkdown\last_run.log` (only the last app run).
- **Settings for the test kit:** AI on, model Qwen3-VL 4B, Describe off.
- **`Test.cmd`** does the same for drag-and-drop (writes `test-build.log`).
- **Store package (release):** bump `StoreIdentity.txt` Version (2.1.0.0) and csproj Version, run
  `Build Store package.cmd` → `StorePackage\*.msix` → the owner uploads it in Partner Center.
  `Test Store package.cmd` installs a signed test copy (uninstall the Setup version first).

---

## 7. Version 2.1 plan (9 stages) and status

Order agreed: 1 → 9. After each stage: owner runs tests (or you do), you grade, he commits to `dev`.
Merge `dev` → `main` only after stage 5 (speed), when times are compared and he agrees.

### Stage 1 — PDF layout ✅ done (on `main` and `dev`)
Done: columns (incl. 3 columns with unequal starts), Q&A tables, grid tables (spanning header in first
column it covers), nested/numbered lists, drawn bullets (incl. right-side bullets for Arabic, RTL indent
measured from the right), super/subscripts (strict: ≤4 chars, touching, not overlapping — student notes on
top of slide text must NOT become `^`/`_`), watermark skipped, ﬁ/ﬂ ligatures, exact highlights (QuadPoints),
links, header/footer once, Arabic `%10` → `10%`, tanween, blank pages, "—" not a bullet.
Known leftovers (lecture PDF, see CLAUDE.local.md): "Causes of AKI" slide — some Renal items land in the
Pre-renal list (columns too close, words merged into one line); KDIGO table squeezed into one column (same
cause: the line builder joins cells across columns); "••• – GFR <60" and duplicate "IMP!!"; Arabic heading
"7." moves to the end; P5.1 "Reference range" sits over Child instead of spanning Adult/Child.

### Stage 2 — handwriting read by the AI ✅ done (on `dev` only)
Challenge PDF page 11: 0/4 → 4/4 correct. Owner's real lecture notes: exact words ~30% → ~42%,
understandable ~70% → ~73%; lecture time 76 s → ~110 s (+45%). Messy handwriting is the limit.
Known issues: 2 notes lost on lecture page 7 (multi-line note merged; "Chronic kidney failure",
"Creatinine 3"); tiny stacked notes on page 12 still weak; occasional invented medical words.
**Decision:** after stage 5/7 make it a setting: "Read handwriting with: Windows (fast) / AI (more accurate)",
chosen automatically by the stage-7 setup window. Fix the lost-notes problem before merging to `main`.

### "Make it general" ✅ done (on `dev`)
Prompts, UI text, .md header, setup window, README made general; `STORE LISTING v2.1.txt` written
(update its "What's new" at release). `Vocabulary.cs` medical list kept on purpose.

### Stage 3 — Excel ✅ done (committed and pushed on `dev`, 26 Nov)
Kit: all X1–X7 checks pass (old code ~5 of 18). Owner's real Excel file converts well; a 2-column block whose
first row has a sentence on the right (> 30 chars or ends with ".") is a label/value list, not a table.
Implemented: number formats (`ExcelFormat.cs`), skip hidden sheets/rows/columns, sheet split into separate
tables (8-neighbour cell groups, cut where a new all-text header row with a different column span starts
right under a row with numbers), title/note rows above a table → `### title` + text, 2-column label/value
blocks → `- **label:** value`, single-row blocks → `a · b · c`, cell links `[text](url)`, cell notes
(comments) → `**Notes**` list, charts (title, kind, data ranges; values table only if data is on another
sheet), pictures in sheets → AI. Not done yet: side-by-side tables that share rows AND touch (gap of 0 cols).
Expected output for the kit: section 8.3.

### Stage 4 — Word and PowerPoint ✅ done (committed and pushed on `dev`, 26 Nov)
Also: `Load` keeps text pieces that are only a space (PowerPoint `<a:t> </a:t>` has no xml:space, so
"Diagnostic"+" "+"Criteria" became "DiagnosticCriteria"); only layout whitespace between tags is dropped.
Tested on the owner's real lecture `IBS 2026.pptx` (in `Tests/private/`): text 100% right; AI read "CAPSULES"
on the slide 1 drawing as "TOUCLES" (stage 8).
Word: list numbering is read from the paragraph style too (`StyleNumPr`, follows `basedOn`), so "List Number"
→ `1. 2. 3.` (real counters per list/level, `listCounters`) and "List Bullet 2" → indented one level (level from
the style name's digit) (W4 ✅); the `Title` style → `#` (W2 ✅). W1 already passed: headers/footers are not read,
so the header logo never reaches the AI (the body's brand logo is a real picture and is read).
PowerPoint: native charts → `ChartText(part, null)` (null = not Excel: always the values table, no cell ranges)
(S5 ✅); slides with `show="0"` skipped and one line at the end "*(Slide 9 hidden in the slide show, not
included.)*" (S9 ✅ ⭐); the 32×32 "Rx" logo is under the 40×25 px limit, so it never goes to the AI (S0 ✅).
Not done: nested numbered lists under a numbered parent use 2-space indent (CommonMark wants 3).

### Stage 5 — speed ✅ done (committed and pushed on `dev`, 26 Nov)
Owner's PC: **NVIDIA GTX 1070** (8 GB, Pascal). ~95% of conversion time is inside the AI; on this card the
Vulkan engine is slow at *reading the picture* (~100–200 picture tokens/s), answer writing ~50 tokens/s.
Measured "before": renal lecture 112 s (39 AI calls), challenge PDF 50 s, IBS pptx 31 s.
Done:
- AI starts in the background as soon as a file opens (`Converter.ConvertAsync`; Office only if it has
  `/media/` pictures, `OfficeConverter.HasPictures`). ~1–2 s.
- `AskOnceAsync`: exact same picture in one PDF asked once (SHA-256 of the PNG); almost-one-color pictures
  (< 1 in 2000 pixels differ, `IsPlainAsync`) skipped. 0 s on the 3 test files, helps logos/empty boxes.
- Handwriting retry: wrong line count → ask again in two halves (`ReadNotesAsync`), not one by one.
  Renal p4 became better aligned ("Dehydration →", "Bleeding → RePerfusion" now right).
- Setting **"Accurate handwriting reading (slower)"** (`Settings.HandwritingAi`, default on). Off = quick
  built-in reader (Windows ink): renal 111 s → 79 s (−29%), stage-1 handwriting quality. Stage 7 can pick it.
Tested and dropped: flash attention (`-fa` is `auto`; "not supported" on this Vulkan/GTX 1070, already off);
2 requests at once (`-np` auto = 4 slots; each answer ~half speed, no gain); smaller pictures (quality risk,
owner: quality first). Idea parked for stage 7: optional NVIDIA CUDA 12.4 engine (~750 MB, NVIDIA only;
CUDA 13 dropped Pascal) — not tested.
Results: renal 112 → 110 s (AI handwriting) / 79 s (fast handwriting); challenge PDF 50 → 49 s; IBS 31 → 31 s;
text identical except renal p4 handwriting (better). Office kit outputs identical.

### Stage 6 — clear error messages (todo)
Password PDF throws `COMException 0x80048040`, damaged PDF `0x80004005`; both show an empty reason today.
Show e.g. "This PDF is password protected" / "This file is damaged"; other files keep converting (E12, E13).

### Stage 7 — "Set up for your PC" window (todo)
PC check + short speed test; recommended settings preselected with time estimates; Speed: Auto / Faster /
More accurate; automatic step-down if the GPU fails or runs out of memory; reopenable from Settings;
choose where AI files live (default inside the app's data folder, or a shared folder the user picks).
The owner wants the app to run on every PC, low-end to high-end: reliability first, then speed.

### Stage 8 — compare AI models (todo)
Add Qwen3.5 4B as a candidate; compare with Qwen3-VL 4B and Gemma 4 on: Arabic in pictures (known error
"بحد" read as "بعد", "الباراسيتامول" misspelled), symbols (lecture slide: `~ ELECTROLYTE (+) (-)` — Qwen now
gets it), the lecture page 11 diagram (sometimes drops "Acute kidney disease (AKD)"), page 19 picture (Qwen
invents Arabic text), handwriting. Pick the default; Gemma may become "More accurate".
Also make the test kit less medical (add general content: invoice table, non-medical handwriting, chart).

### Stage 9 — testing setup and tidy-up (todo)
A "(Test)" named copy of the app so a test build doesn't clash with the installed one; `1 - Test update.cmd`;
`2 - Build Store package.cmd` with automatic version bump and an `Output\` folder; `Source\` and `Tools\`
folders; retire the Setup.exe/Installer path (Store only); move target to **.NET 10** (.NET 8 is out of
support); fix `Test.cmd`'s "no SDK" message (points to the dead .NET 8 page). Idea: a `--quiet` flag that
converts without showing the window and exits with a code (for automated tests).

### Release 2.1 (after stage 9)
Partner Center → new submission: upload the 2.1 MSIX, paste `STORE LISTING v2.1.txt`, set Privacy policy
URL to https://yazeedalsuayr.github.io/convert-to-markdown/ (currently points elsewhere).

---

## 8. Tests

### 8.1 Files
- `Tests/Challenge test kit/` — 14 test files + `00 TEST KEY.md` (check IDs P0–P15, X1–X7, W1–W9, S0–S9,
  I5–I11, E12–E14, T1–T3; ✅ = must pass, ⭐ = ideal). Password for file 12: `study1234`.
  Converted `.md` files land next to the inputs (git-ignored).
- `Tests/baselines/` — latest known outputs, to spot regressions:
  - `01 Challenge - PDF.md` = stage 2 + general (26 Nov 19:00). Best current PDF result.
  - `02 Challenge - Excel (before stage 3).md`, `03 …Word`, `04 …PowerPoint`, `05–11 pictures`,
    `14 Edge - blank page.md`, `kit-run before stage 1 - last_run.log` = the first full kit run (before stage 1;
    Word/PowerPoint/pictures haven't changed since).
- `Tests/generator/` — Python scripts that made the kit (Playwright HTML→PDF, pikepdf annotations, skeleton
  handwriting strokes, openpyxl + LibreOffice recalculation, python-docx, python-pptx) and `img/`. Only needed
  to make new test files.

### 8.2 Current grade (latest runs)
PDF (`01`): passes P0.1, P1.x, P2.1–2.2, P3.1–3.2, P4.1–4.3, P5.2–5.3, P6.1–6.2, P7.x, P8.1–8.3, P9.x,
P10.x, P11.1, P11.3–11.5 (P11.4 on `dev`), P12.x, P13.1, P14.1, P15.1. Partial: P5.1 (spanning header over
Child), P6.2 ⭐ (not a Markdown table), P6.3 (Arabic: "البراسيستامول", "يوميا"), P11.2 (strike-through only
reported as "pen mark on"). Check P0.2/T2 in the log (tiny logo must not reach the AI).
Excel: all X1–X7 pass (stage 3). Word: all W1–W9 pass (stage 4). PowerPoint: all S0–S9 pass (stage 4).
AI pictures vary a little between runs ("ACADEMY · EST." vs "ACADEMY . EST."). Pictures: I7 Arabic error "بعد أقصى", I10
decorative gives `[unclear]` (should be nothing). Edge: E12/E13 fail (stage 6), E14 passes.

### 8.3 Expected Excel output after stage 3 (from the Python prototype)
- **Read me:** `### Electrolyte workbook - challenge test`, the two text lines,
  `- **Guideline link:** [National Kidney Foundation](https://www.kidney.org/atoz/content/hyperkalemia)`,
  and the logo picture read by the AI ("MedNotes / ACADEMY · EST. 2019").
- **Formats:** `### Ward stock and costs`, the note line, then the table Item | Unit price | … | Formula total
  (no "Hidden column", no "Hidden row"): $4.50 / $25.75 / $0.28; 12.5% / 40.0% / 3.3%; 3 Nov 2026 /
  28 Oct 2026 / 1 Sep 2026; 12,500 / 1,800 / 2,550,000; (350) / 120 / (1,500); 2.50E-05 / 3.50E-04 / 8.40E-03;
  08:30 / 08:50 / 14:00; TRUE TRUE FALSE; $56,250 / $46,350 / $714,000. Then a list: Rounding check: 0.3 ·
  Divide by zero: #DIV/0! · Text with \| pipe: Line one<br>Line two · Code kept as text: 00123 · Arabic text ·
  Total of all items: $816,600. Then **Notes**: `- **A5** (Calcium gluconate 10%): Checked by Dr. Sara on 3 Nov`.
- **Side by side:** `### Two tables next to each other, and one below`, then 3 separate tables:
  Corridor|Freight (t/yr) (90,000,000 …), Operator|Stations, Year|Trucks|Revenue ($M).
- **Merged:** one table (Test | Reference range | … / Adult | Child …). **Secret** (hidden sheet): absent.
- **Chart:** the data table, `**Chart: Potassium by group** (column chart)` + `Data: Chart!$A$2:$A$5, Chart!$B$2:$B$5`,
  `**Chart: Sodium by group** (line chart)` + its ranges, then the doses-table picture read by the AI.
- **Wide:** 30 columns Day 1–30, 6.8 … 4.48. **Empty:** `(empty sheet)`.

### 8.4 How to grade
Convert, open the `.md`, check every ID of that file in the key, report pass/partial/fail in a table,
compare with `Tests/baselines/` for regressions, and read `last_run.log` for times and AI calls.
Also run the owner's real files listed in `CLAUDE.local.md` — they matter more than the kit.

---

## 9. Decisions already made (don't re-ask)
- Open source, **MIT License**, his name kept in the copyright line.
- Distribution: **Microsoft Store** (so no "Unknown publisher" warning). Setup.exe path will be retired.
- Built-in AI via llama.cpp, no Ollama dependency. Default model Qwen3-VL 4B; Gemma 4 optional.
- Uninstall deletes the AI (no pop-up asking). Keep backup models in `%LOCALAPPDATA%\ConvertToMarkdown\ai\model`.
- One project folder: the GitHub clone.
- No support for old `.doc/.ppt/.xls`.
- App, README and Store text are general (all users, all subjects).
- Stage 2 lives on `dev` until stage 5; later becomes a Windows-vs-AI handwriting setting.

## 10. Lessons learned (avoid repeating)
- `Buffer` is ambiguous (`System.Buffer` vs `Windows.Storage.Streams.Buffer`) — write `System.Buffer`.
- PdfPig 0.1.9: `QuadPointsQuadrilateral.Points` is a `ReadOnlySpan<PdfPoint>` (loop it, no LINQ);
  `page.ExperimentalAccess.Paths`, `page.GetHyperlinks()`, `Letter.StartBaseLine/GlyphRectangle/TextOrientation`.
- XML booleans in xlsx can be `"1"` or `"true"` (LibreOffice writes `true`/`false`).
- In `.cmd` files, no parentheses inside `echo` text within `if (...)` blocks (it breaks the script); use goto labels.
- Small vision models "tidy" symbols; the slide really had `~` and `*`, so don't "fix" them. Ask for the real
  picture before changing prompts.
- When unsure what handwriting says, render the ink and look — often the writing itself is unclear.
- Telling the AI to "copy letters exactly" stops invented words but gives raw misspellings; the "possible:"
  suggestions handle those.
- Superscript detection must not touch text that overlaps other text (student notes typed over slides).
