# eml-to-pdf

A small .NET console app that bulk-converts Outlook `.msg` files to PDF.

**No Outlook, no Word, no Office install required.** Messages are parsed
directly with [MsgReader](https://github.com/Sicos1977/MSGReader) and rendered
by a headless browser driven by
[PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp).

> **Note on the name:** the project is called `eml-to-pdf`, but it currently
> converts **`.msg`** files only. `.eml` files are not picked up.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` should report `10.x`)
- Microsoft Edge or Google Chrome installed (standard on Windows). If neither is
  present the app downloads its own Chromium — see [Which browser it uses](#which-browser-it-uses).

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
eml-to-pdf <input-dir> <output-dir> [--recurse] [--parallel N] [--no-remote-content]
```

| Argument              | Required | Description                                                       |
| --------------------- | -------- | ----------------------------------------------------------------- |
| `<input-dir>`         | yes      | Folder to scan for `.msg` files. Must already exist.               |
| `<output-dir>`        | yes      | Where the PDFs are written. Created automatically if missing.      |
| `--recurse`           | no       | Also convert `.msg` files in sub-directories.                      |
| `--parallel N`        | no       | How many messages to convert at once. Defaults to your CPU count.  |
| `--no-remote-content` | no       | Block every http(s) request while rendering. See [Remote content](#remote-content). |

Run with `--help` (or no arguments) to print the same summary.

### Examples

```bash
# Top-level folder only, default parallelism
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs"

# Whole directory tree
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs" --recurse

# Throttle to 4 at a time (useful on a low-memory machine)
dotnet run --project eml-to-pdf -- "D:\Export" "D:\PDFs" --recurse --parallel 4

# Suspicious or quarantined mail: never let a message touch the network
dotnet run --project eml-to-pdf -- "D:\Quarantine" "D:\PDFs" --no-remote-content
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
- **Illegal characters** in the subject are replaced with `_`, and long names are
  truncated to 100 characters so the full path stays under Windows' `MAX_PATH`
  limit.

---

## Which browser it uses

Rendering needs a browser engine. On startup the app picks one, in this order:

1. **Microsoft Edge**, then **Google Chrome**, from their standard install paths.
2. If neither is found, it **downloads Chromium** (Chrome for Testing) into
   `%LOCALAPPDATA%\eml-to-pdf\chromium` on Windows, or
   `~/.local/share/eml-to-pdf/chromium` elsewhere.

It prints which one it chose:

```
Renderer: Microsoft Edge (C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe)
```

**Prefer the installed browser.** Edge ships with every supported version of
Windows and is code-signed and already trusted by endpoint security. The
downloaded Chrome for Testing build is *not* code-signed, occupies roughly
700 MB, and runs from a user-writable directory — "download an executable into
AppData, then run it" is a pattern EDR products watch closely. On a managed
endpoint you almost certainly want that fallback never to fire.

Whichever is used, the browser launches with a fresh temporary profile. It never
reads or writes your real Edge/Chrome profile, cookies, or history.

### Security posture

Three deliberate choices, because this tool renders untrusted HTML that arrived
by email:

- **The browser sandbox is left enabled.** No `--no-sandbox`.
- **Control happens over a pipe, not a debugging port** (`Pipe = true`). A
  listening CDP port is a well-known browser-hijacking vector, so no TCP port is
  opened at all.
- **`--no-remote-content` is available** for mail you do not trust.

<a name="remote-content"></a>

### Remote content

By default a message that references remote images will fetch them, exactly as a
mail client would. For ordinary archiving that is what you want — the PDF looks
like the email did.

For **suspicious, quarantined, or phishing mail, pass `--no-remote-content`.**
Without it, converting such a message pulls content from attacker-controlled
infrastructure, loads tracking pixels, and confirms to the sender that the
mailbox is live. The flag aborts every `http://` and `https://` request during
rendering: remote images show as broken, and nothing leaves the machine.

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
| `1`       | Bad arguments, missing input directory, no usable browser, or at least one message failed |

---

## Deploying to another endpoint

### What the target machine needs

| Requirement | Notes |
| ----------- | ----- |
| **Edge or Chrome installed** | Default on Windows 10/11, so normally free. Without one, the app downloads ~700 MB of unsigned Chromium — see [Which browser it uses](#which-browser-it-uses). |
| **.NET 10 runtime** | Only for the framework-dependent build; the self-contained build bundles it. |
| Read access to the `.msg` files | |
| Write access to the output directory | |
| No administrator rights | The app writes only to the output directory. |

No internet connection is needed provided a browser is already installed.

### Build it

```bash
# A) Framework-dependent: ~9 MB, needs the .NET 10 runtime on the target
dotnet publish eml-to-pdf -c Release -o publish

# B) Self-contained single file: one ~79 MB .exe, no runtime needed
dotnet publish eml-to-pdf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-single
```

Option B is usually right for a one-off run on someone else's machine. Both
output folders are git-ignored.

### Get it past endpoint security

The published executable is **unsigned**, which matters more than its size:

```powershell
Get-AuthenticodeSignature .\publish-single\eml-to-pdf.exe    # -> NotSigned
```

1. **Record the hash before copying it anywhere.** This is what a security team
   needs in order to allowlist it, and it changes with every rebuild:

   ```powershell
   Get-FileHash .\publish-single\eml-to-pdf.exe -Algorithm SHA256
   ```

2. **Clear Mark-of-the-Web on the target.** A file copied via browser, email, or
   some network shares is flagged, which triggers SmartScreen:

   ```powershell
   Unblock-File .\eml-to-pdf.exe
   ```

3. **Expect SmartScreen** ("Windows protected your PC") on first run of an
   unsigned binary that has no reputation.

4. **Application control may block it outright** under AppLocker or WDAC if run
   from a user-writable path such as `Downloads` or `Temp`. Run it from an
   allowlisted location, have it allowlisted by hash, or sign it with a
   code-signing certificate your organisation already trusts.

5. **Tell whoever runs your EDR before a large batch.** Reading thousands of mail
   files and writing thousands of new files within a few minutes is, in the
   abstract, the shape of a mass-file-operation heuristic. Far easier to mention
   in advance than to explain from the alert queue.

### Run it

```powershell
.\eml-to-pdf.exe "D:\Export" "D:\PDFs" --recurse --no-remote-content
```

Check the first lines of output to confirm it found the local browser rather
than reaching for the download:

```
Renderer: Microsoft Edge (C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe)
Remote content blocked: messages cannot reach the network.
```

---

## Project layout

```
eml-to-pdf.sln
eml-to-pdf/
  eml-to-pdf.csproj   NuGet references: MsgReader, PuppeteerSharp
  Program.cs          Argument parsing, browser startup, the parallel loop, summary
  Helpers.cs          Browser resolution, filename sanitizing/collisions, HTML building
```

---

## Known limitations

- **Attachments are out of scope.** They are neither extracted nor listed in the
  PDF. Embedded images referenced by `cid:` will therefore appear broken.
- **`.eml` files are ignored.** MsgReader can parse them, so adding support is a
  small change to the file search and the parsing call.
- **Remote images** are fetched from the internet if the message links to them,
  unless you pass `--no-remote-content`. Page loads time out after 30 seconds.
- Messages that are not really emails (contacts, appointments, or files that
  merely have a `.msg` extension) will fail and be logged.

---

## Troubleshooting

**`The type initializer for 'MsgReader.Rtf.Font' threw an exception`**
The legacy Windows code pages are not registered. `Program.cs` handles this with
`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup — if
you see this, that line has gone missing.

**It says it is downloading Chromium instead of using Edge**
No Edge or Chrome was found at a standard install path. Check that the browser
is installed for all users rather than in an unusual location. If you do need
the download, set the standard proxy variables (`HTTPS_PROXY`) when behind a
corporate proxy.

**"Windows protected your PC", or the exe will not start on another machine**
The published binary is unsigned and has no reputation. Run `Unblock-File` on
it, and see [Deploying to another endpoint](#deploying-to-another-endpoint) for
application-control considerations.

**Conversions are slow or the machine bogs down**
Lower `--parallel`. Each concurrent message is a live browser page, so the
default of one-per-CPU-core can be heavy on large HTML emails.
