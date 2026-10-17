namespace GifCapture;

/// <summary>
/// UI text lookup. Source strings are Korean and double as keys; other languages map from them.
/// Missing translations fall back to Korean, so an untranslated string is never blank.
/// </summary>
static partial class L
{
    public static readonly string[] Languages = ["ko", "ja", "en"];

    public static string Language { get; private set; } = "ko";

    public static void Init(string? language) => Language = Languages.Contains(language) ? language! : "ko";

    /// <summary>Translates <paramref name="ko"/> into the current language.</summary>
    public static string T(string ko) => In(Language, ko);

    /// <summary>Translates <paramref name="ko"/> into a specific language.</summary>
    public static string In(string language, string ko)
    {
        var table = language switch { "ja" => Ja, "en" => En, _ => null };
        return table != null && table.TryGetValue(ko, out var text) ? text : ko;
    }

    /// <summary>Translates a composite format string, then fills it.</summary>
    public static string F(string ko, params object?[] args) => string.Format(T(ko), args);
}
