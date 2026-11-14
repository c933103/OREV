Portable Windows x64 build of OpenRouter Export Viewer 0.1.1. Extract the ZIP and launch **OpenRouter Export Viewer.exe**; no installer or separately installed .NET runtime is required.

- Opens bulk and single-room `orpg.3.0` JSON exports, with byte-range indexing for large base64 payloads.
- Shows conversation branches, metadata, readable text, reasoning, tool fields and embedded image previews.
- Exports multiple selected conversations as separate single-room JSON files while preserving original fields and attachments.
- Version 0.1.1 replaces message-card sizing and shows reasoning/tool bodies by default after missing content was reported.

Data, presentation and image-decoding checks passed on two supplied exports. The Windows release is built by GitHub Actions after the data/image checks and native GUI regression pass. OpenRouter's live importer has not yet been verified.

The ZIP includes source and third-party notices. It does not include any uploaded user conversation exports.
