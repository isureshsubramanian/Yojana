using System.Collections.Concurrent;
using System.Globalization;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Yojana.Serialisation.Contracts;

namespace Yojana.RenderWorker;

/// <summary>Bounds live pages with independent slots; each slot owns and periodically recycles one browser process.</summary>
public sealed class BrowserPool : IAsyncDisposable
{
    private readonly ConcurrentQueue<BrowserSlot> available = new();
    private readonly SemaphoreSlim capacity;
    private readonly List<BrowserSlot> slots;
    private readonly TimeSpan timeout;

    public BrowserPool(RenderWorkerOptions options, BrowserInstallation installation, ILoggerFactory loggerFactory)
    {
        if (options.MaximumConcurrentPages is < 1 or > 32) throw new InvalidOperationException("MaximumConcurrentPages must be between 1 and 32.");
        if (options.RecycleAfterRenders < 1) throw new InvalidOperationException("RecycleAfterRenders must be at least 1.");
        if (options.RenderTimeoutSeconds < 1) throw new InvalidOperationException("RenderTimeoutSeconds must be at least 1.");
        timeout = TimeSpan.FromSeconds(options.RenderTimeoutSeconds);
        slots = Enumerable.Range(0, options.MaximumConcurrentPages)
            .Select(_ => new BrowserSlot(installation.ExecutablePath, options.RecycleAfterRenders, options.RenderTimeoutSeconds, loggerFactory)).ToList();
        foreach (var slot in slots) available.Enqueue(slot);
        capacity = new SemaphoreSlim(slots.Count, slots.Count);
    }

    public async Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await capacity.WaitAsync(deadline.Token);
        if (!available.TryDequeue(out var slot))
        {
            capacity.Release();
            throw new InvalidOperationException("Browser pool capacity accounting failed.");
        }
        try
        {
            return await slot.RenderAsync(request, deadline.Token);
        }
        finally
        {
            available.Enqueue(slot);
            capacity.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in slots) await slot.DisposeAsync();
        capacity.Dispose();
    }

    private sealed class BrowserSlot(string executablePath, int recycleAfter, int timeoutSeconds, ILoggerFactory loggerFactory)
        : IAsyncDisposable
    {
        private IBrowser? browser;
        private int renderCount;

        public async Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken cancellationToken)
        {
            browser ??= await Puppeteer.LaunchAsync(new LaunchOptions
            {
                ExecutablePath = executablePath,
                Headless = true,
                Args = ["--disable-background-networking", "--disable-component-update", "--disable-sync",
                    "--disable-extensions", "--no-first-run", "--no-default-browser-check"],
            }, loggerFactory).WaitAsync(cancellationToken);
            var page = await browser.NewPageAsync().WaitAsync(cancellationToken);
            try
            {
                await page.SetJavaScriptEnabledAsync(false).WaitAsync(cancellationToken);
                await page.SetRequestInterceptionAsync(true).WaitAsync(cancellationToken);
                page.AddRequestInterceptor(intercepted => intercepted.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? intercepted.ContinueAsync() : intercepted.AbortAsync());
                await page.SetContentAsync(request.Html, new SetContentOptions
                {
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                    Timeout = timeoutSeconds * 1_000,
                    CancellationToken = cancellationToken,
                }).WaitAsync(cancellationToken);
                var options = PdfOptions(request.Page, timeoutSeconds);
                return await page.PdfDataAsync(options).WaitAsync(cancellationToken);
            }
            finally
            {
                await page.CloseAsync().WaitAsync(CancellationToken.None);
                renderCount++;
                if (renderCount >= recycleAfter)
                {
                    await browser.CloseAsync();
                    browser.Dispose();
                    browser = null;
                    renderCount = 0;
                }
            }
        }

        private static PdfOptions PdfOptions(PdfPageSpec page, int timeoutSeconds)
        {
            var header = page.HeaderHtml ?? "<span></span>";
            var footer = page.FooterHtml ?? "<span></span>";
            return new PdfOptions
            {
                Format = Paper(page.Size),
                Landscape = page.Orientation.Equals("landscape", StringComparison.OrdinalIgnoreCase),
                MarginOptions = new MarginOptions
                {
                    Top = Mm(page.MarginTopMm), Right = Mm(page.MarginRightMm),
                    Bottom = Mm(page.MarginBottomMm), Left = Mm(page.MarginLeftMm),
                },
                DisplayHeaderFooter = page.HeaderHtml is not null || page.FooterHtml is not null,
                HeaderTemplate = header,
                FooterTemplate = footer,
                PrintBackground = true,
                PreferCSSPageSize = true,
                Tagged = true,
                Timeout = timeoutSeconds * 1_000,
            };
        }

        private static PaperFormat Paper(string value) => value.Trim().ToUpperInvariant() switch
        {
            "A3" => PaperFormat.A3, "A5" => PaperFormat.A5, "LETTER" => PaperFormat.Letter,
            "LEGAL" => PaperFormat.Legal, _ => PaperFormat.A4,
        };
        private static string Mm(decimal value) => Math.Max(0, value).ToString("0.##", CultureInfo.InvariantCulture) + "mm";

        public async ValueTask DisposeAsync()
        {
            if (browser is null) return;
            await browser.CloseAsync();
            browser.Dispose();
            browser = null;
        }
    }
}
