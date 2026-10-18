using System.Text.Json;
using System.Text.Json.Serialization;

namespace GifCapture;

/// <summary>A saved capture size, optionally with a fixed screen position.</summary>
sealed record SizePreset(string Name, int Width, int Height, int? X = null, int? Y = null)
{
    [JsonIgnore] public bool HasPosition => X.HasValue && Y.HasValue;
}

/// <summary>User preferences persisted to %AppData%\GifCapture\settings.json.</summary>
sealed class AppSettings
{
    public string Language { get; set; } = "ko";

    public int Fps { get; set; } = 15;
    public bool DrawCursor { get; set; } = true;
    public bool ShowClicks { get; set; }
    public bool ShowKeys { get; set; }
    public bool FollowWindow { get; set; } = true;
    public bool Countdown { get; set; } = true;
    /// <summary>True: save straight to the folder after recording. False: open the editor.</summary>
    public bool QuickSave { get; set; }
    public bool CopyToClipboard { get; set; } = true;
    public List<SizePreset> Presets { get; set; } = [];

    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GifCapture");
    public int Quality { get; set; } = 90;
    public int ScalePercent { get; set; } = 100;
    public ResampleMode Resample { get; set; }
    public bool LimitFps { get; set; }
    public int MaxFps { get; set; } = 15;
    public bool MergeSimilar { get; set; } = true;
    public int PlayCount { get; set; }
    public int TargetSizeMb { get; set; } = 15;

    public static SizePreset[] BuiltInPresets =>
    [
        new("360p", 640, 360),
        new("480p", 854, 480),
        new("720p", 1280, 720),
        new("1080p", 1920, 1080),
        new("4:3", 800, 600),
        new(L.T("정사각형"), 500, 500),
        new(L.T("세로 4:5"), 480, 600),
        new(L.T("세로 9:16"), 360, 640),
    ];

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GifCapture", "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged or locked settings file must never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
