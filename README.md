# eml-to-pdf

A small .NET console app that bulk-converts Outlook `.msg` files to PDF.

**No Outlook, no Word, no Office install required.** Messages are parsed
directly with [MsgReader](https://github.com/Sicos1977/MSGReader) and rendered
by a headless Chromium browser driven by
[PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp).

> **Note on the name:** the project is called `eml-to-pdf`, but it currently
> converts **`.msg`** files only. `.eml` files are not picked up.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` should report `10.x`)
- An internet connection **on first run only**, so Chromium can be downloaded (see below)

Primarily tested on Windows. The code has no Windows-only dependencies and
should run on Linux/macOS.

---

## Quick start

```bash
# from the repository root
dotnet build

# convert every .msg in a folder
dotnet run --project eml-to-pdf -- "C:\Mail\Export" "C:\Mail\PDFs"
```

The `--` matters: everything **before** it is an argument to `dotnet run`,
everything **after** it is passed to the application.

---

## Usage

```
eml-to-pdf <input-dir> <output-dir> [--recurse] [--parallel N]
```

| Argument       | Required | Description                                                        |
| -------------- | -------- | ------------------------------------------------------------------ |
| `<input-dir>`  | yes      | Folder to scan for `.msg` files. Must already exist.                |
| `<output-dir>` | yes      | Where the PDFs are written. Created automatically if missing.       |
| `--recurse`    | no       | Also convert `.msg` files in sub-directories.                       |
| `--parallel N` | no       | How many messages to convert at once. Defaults to your CPU count.   |

Run with `--help` (or no arguments) to print the same summary.

### Examples

```bash
# Top-level folder only, default parallelism
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs"

# Whole directory tree
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs" --recurse

# Throttle to 4 at a time (useful on a low-memory machine)
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs" --recurse --parallel 4
```

Wrap paths in quotes if they contain spaces.

---

## What you get

Each `Some Subject.msg` becomes `Some Subject.pdf` in the output directory:

- **Page setup** — US Letter, 0.5 inch margins, backgrounds printed.
- **Header block** — a table of `From` / `To` / `Cc` / `Date` / `Subject` is
  prepended to the message. `Cc` is omitted when the message has none.
- **Body** — the HTML body is used when present; otherwise the plain-text body
  is rendered in a monospace block. A message with neither still produces a PDF
  containing just the header, rather than being silently skipped.

### File naming

- **Collisions** are resolved by appending a counter: `Report.pdf`,
  `Report (2).pdf`, `Report (3).pdf`. This is expected — Outlook exports
  routinely produce many files with the same subject line. It also applies when
  you re-run into an output folder that already has results in it.
- **Illegal characters** (`\ / : * ? " < > |` and control characters) are
  replaced with `_`, and long names are truncated to 100 characters so the full
  path stays under Windows' `MAX_PATH` limit.

---

## First run: the Chromium download

Rendering needs a browser. On startup the app checks for a local Chromium and
downloads one if it is missing. Expect it to occupy roughly 700 MB on disk once extracted:

```
No local Chromium found in 'C:\Users\you\AppData\Local\eml-to-pdf\chromium'. Downloading (first run only)...
```

It is cached per-user at:

| OS            | Location                                          |
| ------------- | ------------------------------------------------- |
| Windows       | `%LOCALAPPDATA%\eml-to-pdf\chromium`              |
| Linux / macOS | `~/.local/share/eml-to-pdf/chromium`              |

Because the cache lives outside the project folder, rebuilding or running
`dotnet clean` will **not** trigger a re-download. If the download fails the app
stops immediately with an explanatory message rather than failing once per
message.

One Chromium process is launched for the entire run and reused; each message
gets its own short-lived page.

---

## Errors and exit codes

A message that fails to convert does **not** stop the run. The error is printed,
counted, and the next message is processed:

```
[3/812] Broken export.msg -> FAILED - FileFormatException: Invalid header signature.
```

At the end you get a summary:

```
Processed: 812
Succeeded: 809
Failed:    3
Elapsed:   94.2s
Failure details written to D:\PDFs\failures.log
```

`failures.log` is written into the output directory only when something failed,
one tab-separated line per failure: the source path, then the error.

| Exit code | Meaning                                                             |
| --------- | ------------------------------------------------------------------- |
| `0`       | Everything converted (or there were no `.msg` files to convert)      |
| `1`       | Bad arguments, missing input directory, Chromium unavailable, or at least one message failed |

---

## Building a standalone executable

To run the tool on a machine without the .NET SDK installed:

```bash
# Framework-dependent: small, but the target machine needs the .NET 10 runtime
dotnet publish eml-to-pdf -c Release -o publish

# Self-contained single file: one large .exe, no runtime needed on the target
dotnet publish eml-to-pdf -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish-single
```

Both output folders are git-ignored. Note that Chromium is *not* bundled — the
first run on a new machine still downloads it.

---

## Project layout

```
eml-to-pdf.sln
eml-to-pdf/
  eml-to-pdf.csproj   NuGet references: MsgReader, PuppeteerSharp
  Program.cs          Argument parsing, Chromium startup, the parallel loop, summary
  Helpers.cs          Chromium bootstrap, filename sanitizing/collisions, HTML building
```

---

## Known limitations

- **Attachments are out of scope.** They are neither extracted nor listed in the
  PDF. Embedded images referenced by `cid:` will therefore appear broken.
- **`.eml` files are ignored.** MsgReader can parse them, so adding support is a
  small change to the file search and the parsing call.
- **Remote images** are fetched from the internet if the message links to them.
  Page loads time out after 30 seconds.
- Messages that are not really emails (contacts, appointments, or files that
  merely have a `.msg` extension) will fail and be logged.

---

## Troubleshooting

**`The type initializer for 'MsgReader.Rtf.Font' threw an exception`**
The legacy Windows code pages aren't registered. `Program.cs` handles this with
`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup —
if you see this, that line has gone missing.

**The Chromium download fails behind a corporate proxy**
Set the standard proxy environment variables (`HTTPS_PROXY`) before running, or
copy an already-populated cache folder from another machine.

**Conversions are slow or the machine bogs down**
Lower `--parallel`. Each concurrent message is a live browser page, so the
default of one-per-CPU-core can be heavy on large HTML emails.
