# OpenRouter Export Viewer 0.1.2

A portable Windows native application for OpenRouter `orpg.3.0` chat exports.

## License

Project-authored code is distributed under the **[OREV Community License 1.0](LICENSE)**.
Its eligibility, territorial, recipient, permitted-use and redistribution
conditions are part of the license grant. See [LICENSING.md](LICENSING.md) for
scope, provenance and third-party treatment. This is a distinct restricted
license; it is not the unmodified Apache License 2.0. Include LICENSE and NOTICE
with copies and modifications. Separately licensed dependencies keep their own terms.

## Run

Download the Windows x64 ZIP from [Releases](https://github.com/c933103/OREV/releases).

Extract the ZIP and double-click **OpenRouter Export Viewer.exe**. No installer, Python, browser, API key, or separately installed .NET runtime is needed. This build targets **Windows 10/11, x64**. The single executable extracts its bundled runtime/native libraries into the normal .NET temporary cache on first launch.

The native file picker opens immediately. Choose a bulk or individual JSON export. Cancelling the picker leaves a Choose file button. Files can also be opened from the toolbar, dragged onto the app, or passed on its command line.

## Browse

- The left pane lists conversations and message counts. Filter by title.
- Click a conversation to read its messages. Parent links determine the reply order and indentation; model names identify parallel replies. “Go to parent” navigates to the parent message, including across pages.
- Messages use native controls with basic Markdown formatting. Text, reasoning and tool bodies are visible by default. Reasoning/tool sections can be collapsed. Nested tool queries, results, source titles, URLs and errors are rendered as readable fields. HTML is never executed.
- Embedded base64 raster images render **automatically inline**, in a background queue. PNG, JPEG, GIF's first frame, WebP and other formats supported by the bundled Skia decoder are supported. Base64 is streamed to a temporary file, then a bounded preview is decoded; the original stays unchanged. Temporary decoded files use delete-on-close.
- Image previews are fitted to 1200 × 800 or smaller. Very large images requiring over 64 million decoded pixels are left as saveable attachments. SVG is preserved and saveable, without executing or rendering its active content. Audio/video/documents can be saved; this version does not embed media players or document viewers. Animated images show their first frame.
- Metadata opens a collapsible right panel. Click a message's Metadata link for its fields; click the conversation heading for the room fields. Tree values expand lazily in groups of 100. Selecting a value shows a bounded text preview.
- Long text previews are limited to 65,536 characters. Each page holds at most 25 messages. Export always includes complete original content.
- Remote image and website URLs are not fetched during loading. Clicking an external link opens it in your browser.

## Export

Check several conversations and click **Export checked**. Alternatively, select rows with Ctrl/Shift and use **Export selected** when no boxes are checked. Check all applies to the current filtered list; previous checks remain selected across filtering.

Choose an output folder. Each selected room becomes an individual JSON file with `version: "orpg.3.0"`, `title`, `characters`, `messages`, `items`, all artifact maps, and any additional room fields. Unknown fields, model catalog snapshots, IDs, parent links, reasoning, tools and embedded attachment strings remain intact. Existing filenames receive numeric suffixes. Incomplete output files are removed after cancellation or error; already completed files remain.

This transformation matches the single-room export structure. Actual OpenRouter import was not completed in this environment: its temporary browser file input disappeared during the automated file-picker step. Full field equality and embedded bytes were verified locally. No user conversation was uploaded to OpenRouter during testing.

## Large files and privacy

The scanner indexes byte ranges instead of allocating base64 strings or a DOM of all text. Export copies the original JSON values directly from the source file. No network client, telemetry, API integration or automatic remote media fetching is included. Keep the original JSON available while browsing: the app reads values from it on demand and holds it read-locked on Windows.

Index memory depends on the number of JSON fields and messages, rather than the byte size of attachment strings. JSON nesting is limited to 128 levels and the index to eight million nodes. The parser supports UTF-8 JSON and an optional UTF-8 BOM.

## Build from source

Install the .NET 8 SDK, then run from the repository root:

```powershell
./build.ps1
```

The result is `out/win-x64/OpenRouter Export Viewer.exe`. `./build.ps1 -Runtime win-arm64` can build an ARM64 variant; that variant is not included or tested here.

Core checks, with the included synthetic fixture or a supplied OpenRouter sample:

```powershell
dotnet run --project Tests/Tests.csproj
dotnet run --project Tests/Tests.csproj -- "C:\path\bulk.json" --large
```

The checks derive the expected room/message counts and fields from each supplied export, so they accept different test cases. The included synthetic fixture contains three rooms and no user data. `--large` creates and deletes a synthetic 1 GiB base64 archive and verifies its split payload using SHA-256.

Image decoder checks are in `ImageTests`. This project includes the Linux native decoder for testing in the build environment; the Windows application packages the Windows decoder.

## Verification limits

The Windows x64 application was cross-compiled successfully with no compiler warnings. The JSON engine and image renderer were exercised on Linux. Version 0.1.1 replaces nested auto-sized message cards with an explicitly sized vertical layout after missing bodies were reported. Presentation checks now exercise the same text/reasoning/tool extraction path that the GUI uses. The native Windows GUI regression passed on GitHub Actions in [run 43604013791](https://github.com/c933103/OREV/actions/runs/43604013791), checking four nonempty text/reasoning/tool controls, parent bounds, and collapse/expand heights. A Windows GUI regression runner is included in `GuiTests`; it verifies nonempty, visible text controls, parent clipping, and collapse/expand heights, and produces a screenshot and geometry report.

This is an unsigned build. Third-party license notices are included in `notices`.

To exercise the native GUI on Windows from the repository root:

```powershell
dotnet run --project GuiTests/GuiTests.csproj -- Tests/fixtures/presentation.json
```

The fixture includes user text, an assistant answer, reasoning, and nested tool results. The test writes its screenshot, geometry report and result to the normal temporary folder. It does not invoke any model or network tool.
