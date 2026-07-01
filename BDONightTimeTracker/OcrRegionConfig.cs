using System.IO;
using System.Text.Json;

namespace BDONightTimeTracker;

/// <summary>
/// Defines the capture region for reading the BDO clock via OCR.
/// Coordinates are relative to the BDO window, not the screen —
/// so they work correctly in both fullscreen and windowed mode.
///
/// The capture width is intentionally generous — it may also include the
/// player's family name tag, which BDO renders just left of the clock.
/// OcrService automatically isolates the clock from that wider capture
/// (see CropToRightmostTextBlock), so Width does not need per-player tuning
/// for name length; it only needs to be wide enough to contain the name tag,
/// the gap, and the clock together.
///
/// HOW TO CALIBRATE:
/// If the clock is still not read correctly, adjust the values in:
///   %APPDATA%\BDONightTimeTracker\ocr_region.json
/// Then restart the widget.
///
/// TIPS:
/// - Use the Windows Snipping Tool (Win+Shift+S) in "window" mode,
///   then measure the clock position from the top-right corner.
/// - RightOffset: pixels between the right edge of the BDO window and
///   the right edge of the clock area.
/// - TopOffset: pixels between the top edge of the BDO window and
///   the top edge of the clock area.
/// - Increase Width if the capture doesn't reach far enough left to include
///   a gap before the name tag (rare); increase Height if characters are clipped.
/// </summary>
public class OcrRegionConfig
{
    // Pixels from the RIGHT edge of the BDO window to the right edge of the clock region.
    public int RightOffset { get; set; } = 8;

    // Pixels from the TOP edge of the BDO window to the top edge of the clock region.
    public int TopOffset { get; set; } = 8;

    // Width of the capture region. Must be wide enough to fit "PM 12 : 59"
    // AND any family name tag + gap that may sit to its left (see class doc).
    public int Width { get; set; } = 220;

    // Height of the capture region.
    public int Height { get; set; } = 26;

    // Upscale factor before OCR. Tesseract is much more accurate on larger images.
    // Height * UpscaleFactor should be at least 60px.
    public int UpscaleFactor { get; set; } = 3;

    private static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BDONightTimeTracker", "ocr_region.json");

    public static OcrRegionConfig LoadOrDefault()
    {
        if (!File.Exists(ConfigFilePath)) return new OcrRegionConfig();
        try
        {
            return JsonSerializer.Deserialize<OcrRegionConfig>(
                       File.ReadAllText(ConfigFilePath)) ?? new OcrRegionConfig();
        }
        catch
        {
            return new OcrRegionConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
        File.WriteAllText(ConfigFilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
