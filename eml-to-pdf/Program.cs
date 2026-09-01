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
    Console.WriteLine("                  [--timeout SECONDS] [--no-remote-content] [--skip-existing]");
    Console.WriteLine();
    Console.WriteLine("  --recurse             also convert .msg files in sub-directories");
    Console.WriteLine("  --parallel N          messages converted at once (default: processor count)");
    Console.WriteLine("  --timeout SECONDS     how long one message may spend loading remote content");
    Console.WriteLine("                        and rendering (default: 30). Raise it for messages with");
    Console.WriteLine("                        many or large images; the PDF is still produced from");
    Console.WriteLine("                        whatever finished loading.");
    Console.WriteLine("  --no-remote-content   block all http(s) requests while rendering, so messages");
    Console.WriteLine("                        cannot fetch tracking pixels or remote images. Images");
    Console.WriteLine("                        attached to the message still render. Use this for");
    Console.WriteLine("                        suspicious or quarantined mail.");
    Console.WriteLine("  --skip-existing       leave messages already converted by an earlier run");
    Console.WriteLine("                        alone, so an interrupted batch can be resumed.");
    Console.WriteLine("  --recycle-after N     restart the browser every N messages (default: 15).");
    Console.WriteLine("                        Chromium never returns the memory these documents");
    Console.WriteLine("                        use, so a long-lived instance eventually wedges.");
    Console.WriteLine("                        Lower this if conversions start timing out.");
    return 1;
}

var inputDir = args[0];
var outputDir = args[1];
var recurse = false;
var parallelism = Environment.ProcessorCount;
var blockRemoteContent = false;
var timeoutMs = 30_000;
var skipExisting = false;
var recycleAfter = 15;

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

        case "--skip-existing":
            skipExisting = true;
            break;

        case "--recycle-after":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out recycleAfter) || recycleAfter < 1)
            {
                Console.Error.WriteLine("--recycle-after requires a positive integer.");
                return 1;
            }
            break;

        case "--timeout":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out var timeoutSeconds) || timeoutSeconds < 1)
            {
                Console.Error.WriteLine("--timeout requires a positive number of seconds.");
                return 1;
            }
            timeoutMs = timeoutSeconds * 1000;
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

Helpers.PrepareOutputDirectory(outputDir, skipExisting);

var processed = 0;
var succeeded = 0;
var skipped = 0;
var failed = 0;
var failures = new ConcurrentBag<string>();
var stopwatch = Stopwatch.StartNew();

// Claim output names up front, single-threaded. Numbering then follows file order
// instead of whichever worker got there first, and a skipped message never has to
// wait for a browser to start.
var work = new List<(string MsgPath, string PdfPath)>();
foreach (var file in files)
{
    var pdfPath = Helpers.ReserveOutputPath(outputDir, Path.GetFileNameWithoutExtension(file));
    if (pdfPath is null)
    {
        skipped++;
        Report(file, "skipped - already converted");
    }
    else
    {
        work.Add((file, pdfPath));
    }
}

// Chromium's memory climbs steadily as it renders these documents and is never
// handed back -- roughly 1.5 GB by the tenth message and 7 GB by the eightieth.
// One long-lived instance therefore wedges partway through a large batch and every
// remaining message times out. Work through the batch in chunks, each with a fresh
// browser, so memory is reclaimed by process exit before it can become a problem.
var chunkIndex = 0;
foreach (var chunk in work.Chunk(recycleAfter))
{
    if (chunkIndex++ > 0)
        Console.WriteLine($"-- restarting browser (every {recycleAfter} messages) --");

    await using var browser = await LaunchBrowserAsync(renderer, timeoutMs);

    await Parallel.ForEachAsync(
        chunk,
        new ParallelOptions { MaxDegreeOfParallelism = parallelism },
        async (item, ct) =>
        {
            try
            {
                await ConvertAsync(browser, item.MsgPath, item.PdfPath, blockRemoteContent, timeoutMs);
                Interlocked.Increment(ref succeeded);
                Report(item.MsgPath, Path.GetFileName(item.PdfPath));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                failures.Add($"{item.MsgPath}\t{ex.GetType().Name}: {ex.Message}");
                Report(item.MsgPath, $"FAILED - {ex.Message}");
            }
        });

    await browser.CloseAsync();
}

stopwatch.Stop();

var failuresLog = Path.Combine(outputDir, "failures.log");
if (!failures.IsEmpty)
    await File.WriteAllLinesAsync(failuresLog, failures.OrderBy(line => line, StringComparer.OrdinalIgnoreCase));

Console.WriteLine();
Console.WriteLine($"Processed: {processed}");
Console.WriteLine($"Succeeded: {succeeded}");
if (skipped > 0)
    Console.WriteLine($"Skipped:   {skipped} (already converted)");
Console.WriteLine($"Failed:    {failed}");
Console.WriteLine($"Elapsed:   {stopwatch.Elapsed.TotalSeconds:F1}s");

if (!failures.IsEmpty)
    Console.WriteLine($"Failure details written to {failuresLog}");

return failed == 0 ? 0 : 1;

// Parses one .msg and renders it to a PDF in the output directory.
static async Task ConvertAsync(
    IBrowser browser, string msgPath, string pdfPath, bool blockRemoteContent, int timeoutMs)
{
    string html;
    using (var msg = new Storage.Message(msgPath))
    {
        html = Helpers.BuildHtml(msg);
    }

    await using var page = await browser.NewPageAsync();

    if (blockRemoteContent)
    {
        await page.SetRequestInterceptionAsync(true);
        page.Request += BlockRemoteRequests;
    }

    // Emails are authored for screens; print stylesheets often hide content.
    await page.EmulateMediaTypeAsync(MediaType.Screen);

    // Parse the document but do NOT wait on subresources here. Mail is full of
    // tracking pixels and hosted images, and a proxy that drops blocked requests
    // instead of refusing them leaves each one hanging until it times out. Waiting
    // for "load" makes a perfectly good render hostage to every one of them.
    await page.SetContentAsync(html, new SetContentOptions
    {
        WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
        Timeout = timeoutMs
    });

    // Give remote images a bounded chance to arrive, then print regardless. A slow
    // or unreachable image costs us that image, never the whole message.
    if (!blockRemoteContent)
    {
        try
        {
            await page.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
            {
                IdleTime = 500,
                Timeout = timeoutMs
            });
        }
        catch (Exception)
        {
            // Something never finished loading. Print what did render.
        }
    }

    await page.PdfAsync(pdfPath, new PdfOptions
    {
        Timeout = timeoutMs,
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
}

// A fresh browser per chunk. See the recycling note above the run loop.
static Task<IBrowser> LaunchBrowserAsync(BrowserChoice renderer, int timeoutMs) =>
    Puppeteer.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        ExecutablePath = renderer.ExecutablePath,
        // Talk to the browser over a pipe rather than a remote-debugging port. A
        // listening CDP port is how infostealers hijack browsers, so endpoint
        // security watches for it; a pipe does the same job without opening one.
        Pipe = true,
        // Surface an unresponsive browser promptly instead of sitting on Puppeteer's
        // three-minute default, but stay clear of the per-message budget so a long
        // --timeout can't trip the protocol timeout before the page has had its turn.
        ProtocolTimeout = Math.Max(60_000, timeoutMs * 2),
        // NOTE: the browser sandbox is deliberately left ON. This renders untrusted
        // HTML straight from email, which is exactly what the sandbox is for.
        Args = ["--disable-dev-shm-usage"]
    });

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
