using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Windows.Globalization;

namespace BDONightTimeTracker;

/// <summary>
/// Reads the BDO in-game clock via Windows.Media.Ocr (built-in Windows 10/11 OCR).
///
/// PIPELINE:
///   1. GetWindowRect → compute capture coordinates relative to BDO window top-right
///   2. CopyFromScreen → raw bitmap of the clock region (deliberately wide — may
///      include the player's family name tag, which sits immediately left of the clock)
///   3. Binarize, then crop to the right-most isolated text block (the clock),
///      discarding anything further left across a real UI gap (e.g. the name tag)
///   4. Save to temp PNG → load as SoftwareBitmap via BitmapDecoder
///   5. OcrEngine.RecognizeAsync → raw string
///   6. Regex parse for BDO format "PM 6 : 39" → convert to 24-hour int (hour, minute)
///
/// No external files required — Windows.Media.Ocr is built into Windows 10/11.
/// </summary>
public partial class OcrService
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Matches BDO clock format: "PM 6 : 39", "AM 11 : 05", etc.
    // The colon is often misread by OCR as ".", "-", "-.", etc., so accept any
    // combination of those characters as the hour/minute separator.
    [GeneratedRegex(@"(AM|PM)\s+(\d{1,2})\s*[:\.\-]+\s*(\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex BdoTimePattern();

    private readonly OcrRegionConfig _region;
    private readonly OcrEngine?      _ocrEngine;

    public OcrService(OcrRegionConfig region)
    {
        _region = region;

        // OcrEngine.TryCreateFromUserProfileLanguages() uses the system language pack.
        // Fall back to English explicitly if the profile language is not English.
        _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
    }

    /// <summary>
    /// true if the Windows OCR engine was initialised successfully.
    /// Always true on Windows 10/11 with an English language pack installed.
    /// </summary>
    public bool IsOcrAvailable => _ocrEngine is not null;

    /// <summary>
    /// Runs the full OCR pipeline against the top-right corner of the BDO window.
    /// Must be awaited; do NOT call from Task.Run (WinRT async internally).
    /// </summary>
    /// <returns>
    /// (true, hour24, minute) on success; (false, 0, 0) on any failure.
    /// </returns>
    public async Task<(bool success, int hour, int minute)> ReadGameTimeAsync(IntPtr bdoHwnd)
    {
        if (_ocrEngine is null)         return (false, 0, 0);
        if (bdoHwnd == IntPtr.Zero)     return (false, 0, 0);
        if (!GetWindowRect(bdoHwnd, out RECT rect)) return (false, 0, 0);

        int captureX = rect.Right - _region.RightOffset - _region.Width;
        int captureY = rect.Top   + _region.TopOffset;

        var log = new System.Text.StringBuilder();
        log.AppendLine($"[OCR] hwnd=0x{bdoHwnd:X} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
        log.AppendLine($"[OCR] captureX={captureX} captureY={captureY} w={_region.Width} h={_region.Height} scale={_region.UpscaleFactor}");

        try
        {
            using Bitmap raw = CaptureScreen(captureX, captureY, _region.Width, _region.Height);
            log.AppendLine($"[OCR] captured {raw.Width}x{raw.Height}");
            SaveDebugBitmap(raw, "ocr_debug.png");

            using Bitmap upscaled   = Upscale(raw, _region.UpscaleFactor);
            using Bitmap binarized  = Binarize(upscaled);
            using Bitmap clockOnly  = CropToRightmostTextBlock(binarized);
            using Bitmap padded     = AddPadding(clockOnly, 30);
            log.AppendLine($"[OCR] upscaled={upscaled.Width}x{upscaled.Height} binarized={binarized.Width}x{binarized.Height} clockOnly={clockOnly.Width}x{clockOnly.Height} padded={padded.Width}x{padded.Height}");
            SaveDebugBitmap(padded, "ocr_debug_upscaled.png");

            SoftwareBitmap swBmp = await BitmapToSoftwareBitmapAsync(padded);
            log.AppendLine($"[OCR] SoftwareBitmap {swBmp.PixelWidth}x{swBmp.PixelHeight} fmt={swBmp.BitmapPixelFormat} alpha={swBmp.BitmapAlphaMode}");

            OcrResult result = await _ocrEngine.RecognizeAsync(swBmp);
            log.AppendLine($"[OCR] lines={result.Lines.Count} text={JsonQuote(result.Text)}");

            string text = result.Text;
            var (ok, h, m) = ParseBdoTime(text);
            log.AppendLine($"[OCR] parse ok={ok} h={h} m={m}");
            SaveOcrText(log.ToString());

            return (ok, h, m);
        }
        catch (Exception ex)
        {
            log.AppendLine($"[OCR] EXCEPTION {ex.GetType().Name}: {ex.Message}");
            SaveOcrText(log.ToString());
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// Reads the game clock, then keeps polling until the displayed minute
    /// changes ("edge-triggered" calibration). The BDO clock only shows
    /// hour:minute (no seconds), so a single read is only accurate to within
    /// the minute — up to ~59s of ambiguity. By instead detecting the exact
    /// real-time moment the minute ticks over, we pin the anchor to that
    /// transition (which corresponds to :00 seconds of the new minute),
    /// cutting the ambiguity down to roughly one poll interval.
    ///
    /// The expected wait for a transition is bounded by the day/night
    /// compression ratio: ~13.3 real seconds per game-minute during the day,
    /// ~4.4 real seconds per game-minute at night. If no transition is seen
    /// within a safety-margined timeout (OCR miss, BDO minimized, etc.), this
    /// falls back to the plain single-read anchor (same as ReadGameTimeAsync).
    /// </summary>
    /// <returns>(true, hour24, minute, anchorRealTime) on success; (false, 0, 0, default) on failure.</returns>
    public async Task<(bool success, int hour, int minute, DateTime anchorRealTime)> CalibrateAnchorAsync(
        IntPtr bdoHwnd, CancellationToken ct = default)
    {
        DateTime t0 = DateTime.Now;
        var (ok0, h0, m0) = await ReadGameTimeAsync(bdoHwnd);
        if (!ok0) return (false, 0, 0, default);

        bool   isNight              = h0 >= 22 || h0 < 7;
        double realSecPerGameMinute = 60.0 / (isNight ? 13.5 : 4.5);
        var    pollInterval         = TimeSpan.FromMilliseconds(750);
        DateTime deadline           = DateTime.Now + TimeSpan.FromSeconds(realSecPerGameMinute + 5);

        DateTime lastOldReadTime = t0;

        while (DateTime.Now < deadline)
        {
            await Task.Delay(pollInterval, ct);

            DateTime readTime = DateTime.Now;
            var (ok, h, m) = await ReadGameTimeAsync(bdoHwnd);
            if (!ok) continue; // transient OCR miss — keep polling until deadline

            if (h != h0 || m != m0)
            {
                // True transition instant lies somewhere between the last confirmed
                // "still old minute" read and this "new minute" read — use the midpoint.
                DateTime midpoint = lastOldReadTime + TimeSpan.FromTicks((readTime - lastOldReadTime).Ticks / 2);
                return (true, h, m, midpoint);
            }

            lastOldReadTime = readTime;
        }

        // No transition observed in time (e.g. OCR kept failing) — fall back to the
        // initial read anchored at the moment we obtained it, same as a plain read.
        return (true, h0, m0, t0);
    }

    /// <summary>
    /// Captures only the configured region and saves it as-is (no preprocessing).
    /// Useful for calibrating ocr_region.json without needing a successful OCR.
    /// Saved to %APPDATA%\BDONightTimeTracker\ocr_debug.png
    /// </summary>
    public string SaveDebugCapture(IntPtr bdoHwnd)
    {
        string debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BDONightTimeTracker", "ocr_debug.png");

        if (bdoHwnd == IntPtr.Zero) return debugPath;
        if (!GetWindowRect(bdoHwnd, out RECT rect)) return debugPath;

        int captureX = rect.Right - _region.RightOffset - _region.Width;
        int captureY = rect.Top   + _region.TopOffset;

        try
        {
            using Bitmap bmp = CaptureScreen(captureX, captureY, _region.Width, _region.Height);
            SaveDebugBitmap(bmp, "ocr_debug.png");
        }
        catch { }

        return debugPath;
    }

    // ─── Screenshot ───────────────────────────────────────────────────────────

    private static Bitmap CaptureScreen(int x, int y, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    // ─── Upscale ──────────────────────────────────────────────────────────────
    // Windows.Media.Ocr needs the text to be large enough to recognise (roughly
    // 20+ px tall). NearestNeighbor keeps pixel edges crisp for pixel-art fonts.

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        if (factor <= 1) return new Bitmap(src);
        var dst = new Bitmap(src.Width * factor, src.Height * factor, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // ─── System.Drawing.Bitmap → WinRT SoftwareBitmap ────────────────────────
    //
    // Windows.Media.Ocr requires a SoftwareBitmap. We encode the GDI bitmap to
    // PNG bytes and feed them through InMemoryRandomAccessStream so BitmapDecoder
    // can produce a properly-formatted SoftwareBitmap without any WinRT interop
    // wrappers (AsRandomAccessStream can throw at runtime in .NET 8).

    private static async Task<SoftwareBitmap> BitmapToSoftwareBitmapAsync(Bitmap bmp)
    {
        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            pngBytes = ms.ToArray();
        }

        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
        }
        ras.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    // ─── Debug helpers ────────────────────────────────────────────────────────

    private static void SaveDebugBitmap(Bitmap bmp, string filename)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BDONightTimeTracker");
            Directory.CreateDirectory(dir);
            bmp.Save(Path.Combine(dir, filename), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }

    private static void SaveOcrText(string text)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BDONightTimeTracker");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ocr_last_output.txt"), text);
        }
        catch { }
    }

    // ─── Padding ──────────────────────────────────────────────────────────────
    // Windows OCR often misses text that touches the image border. A solid white
    // margin gives it context and significantly improves recognition rate.

    private static Bitmap AddPadding(Bitmap src, int px)
    {
        var dst = new Bitmap(src.Width + px * 2, src.Height + px * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(WinFormsColor.White);
        g.DrawImage(src, px, px, src.Width, src.Height);
        return dst;
    }

    // ─── Binarize ─────────────────────────────────────────────────────────────
    // BDO clock: bright (white/yellow) text on a dark background.
    // Windows OCR expects dark text on a light background, so invert:
    // luminance > threshold → black (text), otherwise → white (background).

    private static unsafe Bitmap Binarize(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);

        BitmapData srcData = src.LockBits(
            new Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dstData = dst.LockBits(
            new Rectangle(0, 0, dst.Width, dst.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte* pSrc = (byte*)srcData.Scan0;
        byte* pDst = (byte*)dstData.Scan0;
        int   bytes = srcData.Stride * src.Height;

        for (int i = 0; i < bytes; i += 4)
        {
            int  gray = (int)(pSrc[i + 2] * 0.299 + pSrc[i + 1] * 0.587 + pSrc[i] * 0.114);
            byte fill = gray > 140 ? (byte)0 : (byte)255;
            pDst[i]     = fill;
            pDst[i + 1] = fill;
            pDst[i + 2] = fill;
            pDst[i + 3] = 255;
        }

        src.UnlockBits(srcData);
        dst.UnlockBits(dstData);
        return dst;
    }

    // ─── Crop to clock text ───────────────────────────────────────────────────
    // The capture region is intentionally wide enough to also catch the
    // player's family name tag, which BDO renders immediately to the left of
    // the clock. Rather than requiring per-player pixel calibration to exclude
    // it, we isolate the clock automatically: scan the binarized image column
    // by column from the right edge (the clock is always right-aligned), and
    // stop as soon as we cross a real gap between separate UI elements — much
    // wider than the letter/word spacing inside "AM 8 : 10" itself. Everything
    // left of that gap (name tag, channel indicator, etc.) is discarded.
    private static unsafe Bitmap CropToRightmostTextBlock(Bitmap binarized)
    {
        int width = binarized.Width, height = binarized.Height;

        BitmapData data = binarized.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var colHasInk = new bool[width];
        byte* p = (byte*)data.Scan0;
        int   stride = data.Stride;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (p[y * stride + x * 4] < 128) { colHasInk[x] = true; break; }
            }
        }
        binarized.UnlockBits(data);

        int rightEdge = width - 1;
        while (rightEdge >= 0 && !colHasInk[rightEdge]) rightEdge--;
        if (rightEdge < 0) return new Bitmap(binarized); // blank capture, nothing to crop

        int gapThreshold = Math.Max(15, height / 2);
        int leftBoundary = rightEdge;
        int gapRun = 0;
        for (int x = rightEdge; x >= 0; x--)
        {
            if (colHasInk[x])
            {
                leftBoundary = x;
                gapRun = 0;
            }
            else if (++gapRun >= gapThreshold)
            {
                break;
            }
        }

        var cropRect = new Rectangle(leftBoundary, 0, rightEdge - leftBoundary + 1, height);
        return binarized.Clone(cropRect, binarized.PixelFormat);
    }

    private static string JsonQuote(string s) =>
        "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    // ─── Parse "PM 6 : 39" → (hour24, minute) ────────────────────────────────

    private static (bool, int, int) ParseBdoTime(string text)
    {
        Match m = BdoTimePattern().Match(text);
        if (!m.Success) return (false, 0, 0);

        string period = m.Groups[1].Value.ToUpperInvariant();
        if (!int.TryParse(m.Groups[2].Value, out int h12) || h12 < 1 || h12 > 12) return (false, 0, 0);
        if (!int.TryParse(m.Groups[3].Value, out int min) || min < 0 || min > 59) return (false, 0, 0);

        int h24 = (period, h12) switch
        {
            ("AM", 12) => 0,
            ("PM", 12) => 12,
            ("PM", _)  => h12 + 12,
            _          => h12,
        };

        return (true, h24, min);
    }
}
