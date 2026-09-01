using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MsgReader.Outlook;
using PuppeteerSharp;
using PuppeteerSharp.Media;

// MsgReader needs the legacy code pages (windows-125x etc.) that .NET Core drops by default.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length < 2 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("Usage: eml-to-pdf <input-dir> <output-dir> [--recurse] [--parallel N] [--no-remote-content]");
    Console.WriteLine();
    Console.WriteLine("  --recurse             also convert .msg files in sub-directories");
    Console.WriteLine("  --parallel N          messages converted at once (default: processor count)");
    Console.WriteLine("  --no-remote-content   block all http(s) requests while rendering, so messages");
    Console.WriteLine("                        cannot fetch tracking pixels or remote images. Use this");
    Console.WriteLine("                        for suspicious or quarantined mail.");
    return 1;
}

var inputDir = args[0];
var outputDir = args[1];
var recurse = false;
var parallelism = Environment.ProcessorCount;
var blockRemoteContent = false;

for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--recurse":
            recurse = true;
            break;

        case "--no-remote-content":
            blockRemoteContent = true;
            break;

        case "--parallel":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out parallelism) || parallelism < 1)
            {
                Console.Error.WriteLine("--parallel requires a positive integer.");
                return 1;
            }
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Input directory not found: {inputDir}");
    return 1;
}

Directory.CreateDirectory(outputDir);

var files = Directory.GetFiles(
    inputDir,
    "*.msg",
    recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

if (files.Length == 0)
{
    Console.WriteLine($"No .msg files found in '{inputDir}'.");
    return 0;
}

Console.WriteLine($"Found {files.Length} .msg file(s). Converting with {parallelism} worker(s)...");

BrowserChoice renderer;
try
{
    renderer = await Helpers.ResolveBrowserAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No usable browser, cannot render PDFs.{Environment.NewLine}{ex.Message}");
    return 1;
}

Console.WriteLine($"Renderer: {renderer.Description}");
if (blockRemoteContent)
    Console.WriteLine("Remote content blocked: messages cannot reach the network.");

var processed = 0;
var succeeded = 0;
var failed = 0;
var failures = new ConcurrentBag<string>();
var stopwatch = Stopwatch.StartNew();

// One browser for the whole run; each message gets its own short-lived page.
await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = true,
    ExecutablePath = renderer.ExecutablePath,
    // Talk to the browser over a pipe rather than a remote-debugging port. A
    // listening CDP port is how infostealers hijack browsers, so endpoint
    // security watches for it; a pipe does the same job without opening one.
    Pipe = true,
    // NOTE: the browser sandbox is deliberately left ON. This renders untrusted
    // HTML straight from email, which is exactly what the sandbox is for.
    Args = ["--disable-dev-shm-usage"]
});

await Parallel.ForEachAsync(
    files,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (file, ct) =>
    {
        try
        {
            var pdfPath = await ConvertAsync(browser, file, outputDir, blockRemoteContent);
            Interlocked.Increment(ref succeeded);
            Report(file, Path.GetFileName(pdfPath));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failed);
            failures.Add($"{file}\t{ex.GetType().Name}: {ex.Message}");
            Report(file, $"FAILED - {ex.Message}");
        }
    });

await browser.CloseAsync();
stopwatch.Stop();

var failuresLog = Path.Combine(outputDir, "failures.log");
if (!failures.IsEmpty)
    await File.WriteAllLinesAsync(failuresLog, failures.OrderBy(line => line, StringComparer.OrdinalIgnoreCase));

Console.WriteLine();
Console.WriteLine($"Processed: {processed}");
Console.WriteLine($"Succeeded: {succeeded}");
Console.WriteLine($"Failed:    {failed}");
Console.WriteLine($"Elapsed:   {stopwatch.Elapsed.TotalSeconds:F1}s");

if (!failures.IsEmpty)
    Console.WriteLine($"Failure details written to {failuresLog}");

return failed == 0 ? 0 : 1;

// Parses one .msg and renders it to a PDF in the output directory.
static async Task<string> ConvertAsync(IBrowser browser, string msgPath, string outputDir, bool blockRemoteContent)
{
    string html;
    using (var msg = new Storage.Message(msgPath))
    {
        html = Helpers.BuildHtml(msg);
    }

    var pdfPath = Helpers.ReserveOutputPath(outputDir, Path.GetFileNameWithoutExtension(msgPath));

    await using var page = await browser.NewPageAsync();

    if (blockRemoteContent)
    {
        await page.SetRequestInterceptionAsync(true);
        page.Request += BlockRemoteRequests;
    }

    // Emails are authored for screens; print stylesheets often hide content.
    await page.EmulateMediaTypeAsync(MediaType.Screen);

    await page.SetContentAsync(html, new SetContentOptions
    {
        WaitUntil = [WaitUntilNavigation.Load],
        Timeout = 30_000
    });

    await page.PdfAsync(pdfPath, new PdfOptions
    {
        Format = PaperFormat.Letter,
        PrintBackground = true,
        MarginOptions = new MarginOptions
        {
            Top = "0.5in",
            Bottom = "0.5in",
            Left = "0.5in",
            Right = "0.5in"
        }
    });

    return pdfPath;
}

void Report(string msgPath, string outcome)
{
    var n = Interlocked.Increment(ref processed);
    Console.WriteLine($"[{n}/{files.Length}] {Path.GetFileName(msgPath)} -> {outcome}");
}

// Lets the page build itself from the HTML we supplied, but refuses every trip
// to the network, so a message can't phone home or pull a tracking pixel.
static async void BlockRemoteRequests(object? sender, RequestEventArgs e)
{
    try
    {
        var url = e.Request.Url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await e.Request.AbortAsync();
        }
        else
        {
            await e.Request.ContinueAsync();
        }
    }
    catch
    {
        // The page may already be closed or the request already handled.
        // Either way there is nothing useful to do here.
    }
}
