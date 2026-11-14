# OpenRouter Export Viewer 0.1.1 verification

## Display repair

The user reported that version 0.1.0 did not display text, reasoning or tool use. The earlier data/attachment checks did not exercise native card visibility.

Version 0.1.1:

- Replaces automatically sized nested flow cards with a vertical message panel that assigns child bounds and computes its full height explicitly.
- Makes text, reasoning and tool bodies visible initially; reasoning/tool headers can collapse and expand their bodies.
- Moves the GUI content extraction path into MessageContent so actual presentation values are exercised by tests.
- Renders nested tool fields, including queries, source titles/URLs, results and errors; the previous tool description displayed object/array counts.
- Keeps a plain-text fallback when Markdown yields no readable content.
- Retains automatic embedded image previews and full original single-room export values.

## Completed checks

- Windows x64 portable application rebuilt successfully.
- Four-room supplied export: 148 checks passed, including text/reasoning values from the GUI presentation path and full export equality.
- Three-room supplied export: 129 checks passed.
- Synthetic fixture: 93 checks passed.
- Dedicated presentation fixture checks user text, assistant text, reasoning text and nested tool query/source/title/URL values.
- GUI regression runner compiled successfully with no warnings. It is designed to assert nonempty native text controls, visibility within parent bounds, and collapse/expand card heights, and writes a screenshot and geometry report.

## Remaining verification

The native Windows GUI has not been successfully executed in this Linux environment. A local Wine attempt terminated during Wine startup, before the viewer could launch; this does not validate or invalidate viewer behaviour. The Windows regression runner was compiled but not run. OpenRouter's live importer also remains unverified because the previous browser file-picker attempt failed.

Previous index/export tests passed on a synthetic 1 GiB base64 input. The index/export implementation is unchanged in 0.1.1.
