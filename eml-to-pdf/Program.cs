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
    Console.WriteLine("Usage: eml-to-pdf <input-dir> <output-dir> [--recurse] [--parallel N]");
    Console.WriteLine();
    Console.WriteLine("  --recurse     also convert .msg files in sub-directories");
    Console.WriteLine("  --parallel N  messages converted at once (default: processor count)");
    return 1;
}

var inputDir = args[0];
var outputDir = args[1];
var recurse = false;
var parallelism = Environment.ProcessorCount;

for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--recurse":
            recurse = true;
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

string chromiumPath;
try
{
    chromiumPath = await Helpers.EnsureChromiumAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Chromium is unavailable, cannot render PDFs.\n{ex.Message}");
    return 1;
}

var processed = 0;
var succeeded = 0;
var failed = 0;
var failures = new ConcurrentBag<string>();
var stopwatch = Stopwatch.StartNew();

// One browser for the whole run; each message gets its own short-lived page.
await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = true,
    ExecutablePath = chromiumPath,
    // Keeps Chromium happy in containers and other low-/dev/shm environments.
    Args = ["--no-sandbox", "--disable-dev-shm-usage"]
});

await Parallel.ForEachAsync(
    files,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (file, ct) =>
    {
        try
        {
            var pdfPath = await ConvertAsync(browser, file, outputDir);
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
static async Task<string> ConvertAsync(IBrowser browser, string msgPath, string outputDir)
{
    string html;
    using (var msg = new Storage.Message(msgPath))
    {
        html = Helpers.BuildHtml(msg);
    }

    var pdfPath = Helpers.ReserveOutputPath(outputDir, Path.GetFileNameWithoutExtension(msgPath));

    await using var page = await browser.NewPageAsync();

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
