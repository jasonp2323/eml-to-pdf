using System.Net;
using System.Text;
using MsgReader.Outlook;
using PuppeteerSharp;

/// <summary>
/// Small, self-contained helpers for the conversion run: Chromium bootstrap,
/// output path naming, and turning a parsed .msg into a printable HTML document.
/// </summary>
internal static class Helpers
{
    // Long subjects easily blow past MAX_PATH once combined with the output dir.
    private const int MaxBaseNameLength = 100;

    private static readonly object PathGate = new();
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the browser used for rendering. An already-installed Edge/Chrome is
    /// strongly preferred: it is code-signed and already trusted by endpoint
    /// security, whereas a downloaded Chrome for Testing build is unsigned and
    /// runs from a user-writable directory, which tends to draw EDR attention.
    /// Falls back to downloading Chromium when no local browser is found.
    /// </summary>
    public static async Task<BrowserChoice> ResolveBrowserAsync()
    {
        foreach (var (name, path) in CandidateBrowsers())
        {
            if (File.Exists(path))
                return new BrowserChoice(path, $"{name} ({path})");
        }

        Console.WriteLine("No installed Edge/Chrome found; falling back to a downloaded Chromium.");
        return await DownloadChromiumAsync();
    }

    /// <summary>
    /// Well-known install locations, most-preferred first. Edge leads on Windows
    /// because it is present on every supported build.
    /// </summary>
    private static IEnumerable<(string Name, string Path)> CandidateBrowsers()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return ("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            yield return ("Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            yield return ("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            yield return ("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
            yield return ("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            yield return ("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
        }
        else
        {
            yield return ("Google Chrome", "/usr/bin/google-chrome");
            yield return ("Chromium", "/usr/bin/chromium");
            yield return ("Chromium", "/usr/bin/chromium-browser");
            yield return ("Microsoft Edge", "/usr/bin/microsoft-edge");
        }
    }

    /// <summary>
    /// Downloads Chromium on first use and returns its executable path. Throws
    /// with a readable message if that fails.
    /// </summary>
    private static async Task<BrowserChoice> DownloadChromiumAsync()
    {
        // Cache Chromium per-user rather than next to the binary, so a rebuild
        // (or a clean) doesn't trigger a fresh download.
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eml-to-pdf",
            "chromium");
        Directory.CreateDirectory(cacheDir);

        var fetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = cacheDir });
        var installed = fetcher.GetInstalledBrowsers().FirstOrDefault(b => b.Browser == fetcher.Browser);

        if (installed is null)
        {
            Console.WriteLine($"Downloading Chromium into '{cacheDir}' (first run only, several hundred MB)...");
            try
            {
                installed = await fetcher.DownloadAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not download Chromium into '{cacheDir}'. " +
                    $"Check network access/proxy settings and disk space. Reason: {ex.Message}", ex);
            }
        }

        var executable = fetcher.GetExecutablePath(installed.BuildId);
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"Chromium build '{installed.BuildId}' is registered but its executable is missing at '{executable}'. " +
                $"Delete '{cacheDir}' and re-run to force a fresh download.");
        }

        return new BrowserChoice(executable, $"Chrome for Testing {installed.BuildId} (downloaded, unsigned)");
    }

    /// <summary>
    /// Flattens an exception to its innermost message; MsgReader in particular
    /// tends to surface useless "type initializer threw an exception" wrappers.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        return inner == ex
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} -> {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>
    /// Strips characters that are illegal in Windows file names and truncates
    /// over-long names. Never returns an empty string.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length > MaxBaseNameLength)
            cleaned = cleaned[..MaxBaseNameLength];

        // Windows silently drops trailing dots and spaces, which breaks File.Exists checks.
        cleaned = cleaned.TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "message" : cleaned;
    }

    /// <summary>
    /// Returns an unused "&lt;outputDir&gt;/&lt;baseName&gt;.pdf", appending " (2)", " (3)", ...
    /// on collision. Reservations are tracked in-process so parallel workers can't
    /// pick the same name before either has written its file.
    /// </summary>
    public static string ReserveOutputPath(string outputDir, string baseName)
    {
        var safe = SanitizeFileName(baseName);

        lock (PathGate)
        {
            for (var i = 1; ; i++)
            {
                var candidate = Path.Combine(outputDir, i == 1 ? $"{safe}.pdf" : $"{safe} ({i}).pdf");
                if (!ReservedPaths.Contains(candidate) && !File.Exists(candidate))
                {
                    ReservedPaths.Add(candidate);
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Builds the HTML document handed to Chromium: the message header block
    /// followed by the HTML body, the plain-text body, or nothing at all.
    /// </summary>
    /// <summary>
    /// Rules injected into every message to undo two things Outlook/Word HTML does
    /// that survive fine in a mail client but fall apart when paginated into a PDF.
    /// Injected at the top of &lt;body&gt;, so it comes after the message's own
    /// &lt;head&gt; styles and wins on document order as well as on !important.
    /// </summary>
    private const string PrintNormalizeCss = """
        <style>
        /* Word exports declare a named page -- "@page WordSection1" plus
           "div.WordSection1 { page: WordSection1 }". Chromium honours named pages
           and breaks to a fresh sheet whenever the page name changes, which is why
           the header block ends up alone on page 1 with the body starting on page 2.
           Forcing every element back onto the default page removes that break. */
        * { page: auto !important; }

        /* Reply chains nest one quote wrapper per level, and the indent is
           cumulative. Twenty replies deep the text column is a few pixels wide and
           wraps one character per line. Flatten every level to the same shallow
           indent so depth costs nothing. */
        blockquote {
            margin-left: 0 !important;
            margin-right: 0 !important;
            padding-left: 0.7em !important;
            border-left: 2px solid #ddd !important;
        }

        /* Wrappers other clients use for the same job. */
        .gmail_quote, .gmail_quote_container, .protonmail_quote, .yahoo_quoted {
            margin-left: 0 !important;
            padding-left: 0 !important;
            border-left: 0 !important;
        }

        /* A quoted chain that nests layout tables collapses the same way. Let the
           innermost content keep a usable width rather than inheriting a squeezed
           one from its ancestors. */
        blockquote table, blockquote td, blockquote div {
            width: auto !important;
            min-width: 0 !important;
            margin-left: 0 !important;
        }

        /* Long URLs and unbroken tokens must not force a horizontal overflow that
           narrows everything else. */
        body { overflow-wrap: break-word; }
        </style>
        """;

    public static string BuildHtml(Storage.Message msg)
    {
        var header = BuildHeader(msg);

        var bodyHtml = msg.BodyHtml;
        if (!string.IsNullOrWhiteSpace(bodyHtml))
            return InjectHeader(bodyHtml, PrintNormalizeCss + header);

        var bodyText = msg.BodyText;
        var body = string.IsNullOrWhiteSpace(bodyText)
            ? "<p style=\"color:#888;font-style:italic\">(no message body)</p>"
            : $"<pre style=\"white-space:pre-wrap;overflow-wrap:break-word;font-family:Consolas,'Courier New',monospace;font-size:12px;margin:0\">{WebUtility.HtmlEncode(bodyText)}</pre>";

        return $"""
                <!DOCTYPE html>
                <html>
                <head><meta charset="utf-8"></head>
                <body style="font-family:'Segoe UI',Arial,sans-serif;font-size:13px;color:#000">
                {header}
                {body}
                </body>
                </html>
                """;
    }

    /// <summary>
    /// Puts the header block just inside the message's own &lt;body&gt; so the email's
    /// styling still applies to its content.
    /// </summary>
    private static string InjectHeader(string html, string header)
    {
        var bodyTag = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyTag >= 0)
        {
            var tagEnd = html.IndexOf('>', bodyTag);
            if (tagEnd >= 0)
                return html.Insert(tagEnd + 1, header);
        }

        // Fragment without a <body> of its own; Chromium will wrap it for us.
        return header + html;
    }

    private static string BuildHeader(Storage.Message msg)
    {
        var rows = new StringBuilder();
        AppendRow(rows, "From", FormatAddress(msg.Sender?.DisplayName, msg.Sender?.Email));
        AppendRow(rows, "To", FormatRecipients(msg, RecipientType.To));

        var cc = FormatRecipients(msg, RecipientType.Cc);
        if (cc.Length > 0)
            AppendRow(rows, "Cc", cc);

        AppendRow(rows, "Date", msg.SentOn?.ToLocalTime().ToString("f") ?? "");
        AppendRow(rows, "Subject", msg.Subject ?? "");

        return $"""
                <table style="width:100%;border-collapse:collapse;margin:0 0 16px 0;padding:0 0 10px 0;border-bottom:1px solid #bbb;font-family:'Segoe UI',Arial,sans-serif;font-size:12px;color:#000">
                {rows}</table>
                """;
    }

    private static void AppendRow(StringBuilder sb, string label, string value)
    {
        sb.Append("<tr><td style=\"vertical-align:top;padding:1px 8px 1px 0;white-space:nowrap;font-weight:600;color:#555\">")
          .Append(label)
          .Append(":</td><td style=\"vertical-align:top;padding:1px 0;overflow-wrap:break-word\">")
          .Append(WebUtility.HtmlEncode(value))
          .AppendLine("</td></tr>");
    }

    private static string FormatRecipients(Storage.Message msg, RecipientType type)
    {
        var recipients = msg.Recipients;
        if (recipients is null)
            return "";

        return string.Join("; ", recipients
            .Where(r => r.Type == type)
            .Select(r => FormatAddress(r.DisplayName, r.Email))
            .Where(s => s.Length > 0));
    }

    private static string FormatAddress(string? displayName, string? email)
    {
        displayName = displayName?.Trim() ?? "";
        email = email?.Trim() ?? "";

        if (displayName.Length > 0 && email.Length > 0 &&
            !displayName.Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            return $"{displayName} <{email}>";
        }

        return displayName.Length > 0 ? displayName : email;
    }
}

/// <summary>The browser chosen for rendering, plus a human-readable description of it.</summary>
/// <param name="ExecutablePath">Full path to the browser executable.</param>
/// <param name="Description">What to show the user, e.g. "Microsoft Edge (C:\...)".</param>
internal sealed record BrowserChoice(string ExecutablePath, string Description);
