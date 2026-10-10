# MyRPA.Documents

PDF activities (Phase 7.1, [ADR-0043](../../docs/adr/0043-more-enterprise-integrations.md)). A product plugin: it
references the Automation SDK and [PdfPig](https://github.com/UglyToad/PdfPig) (NuGet `PdfPig` 0.1.16, Apache-2.0),
which only this plugin may reference. It runs in-process and is fully trusted (ADR-0015); its load context is not a
sandbox.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `fileRoot` | the host's working directory | The only folder tree the activities read (the SDK's `FileRootPolicy`: no paths outside it, no links) |
| `maxFileBytes` | 52428800 (50 MB) | The largest PDF read; larger files are refused before parsing |
| `maxPages` | 2000 | The most pages one activity reads |
| `maxTextChars` | 10000000 | The most characters of text one `Pdf.ReadText` returns |

## Activities

| Type | Properties | Outputs | Errors |
|---|---|---|---|
| `Pdf.ReadText` | `path`, `pages` (such as `1-3, 5` or `2-`; default all), `password` (**secret**) | `result`: the text in reading order, pages separated by a blank line; `pageTexts`: a List with each page's text | `InvalidDocument`, `EncryptedDocument`, `InvalidInput` for a bad page selection, `TooManyItems`, `FileTooLarge` |
| `Pdf.GetInfo` | `path`, `password` (**secret**) | `result`: `{ pages, title, author, subject, keywords, creator, producer, created, modified, encrypted, version }` (missing values null) | `InvalidDocument`, `EncryptedDocument` |

## Security and limits

- A PDF is untrusted input parsed by a third-party library. Every parser failure becomes `InvalidDocument`, with the
  cause kept as the inner exception; cancellation still stops the run between pages.
- `password` must name an argument or variable (validation `MYRPA1066`). It goes only to the parser and never appears
  in messages or logs. Without it, or with a wrong one, an encrypted PDF fails with `EncryptedDocument`.
- Scanned PDFs hold images, not text, and give empty text; OCR is not part of 7.1.
- Messages name the path as the workflow gave it, never the absolute root.
