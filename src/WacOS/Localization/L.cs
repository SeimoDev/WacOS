using System.Globalization;

namespace WacOS;

/// <summary>
/// User-interface localisation. The English text is the key; <see cref="Strings.Table"/> holds the translations.
/// The language follows the system by default and can be fixed in Settings → General.
/// </summary>
public static class L
{
    /// <summary>Language codes offered in Settings, with their native names ("auto" follows the system).</summary>
    public static readonly (string code, string name)[] Languages =
    {
        ("en", "English"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"),
    };

    /// <summary>Set by the --lang command-line switch: overrides the saved setting for this run.</summary>
    public static string? Override { get; set; }

    /// <summary>The language actually in use: "en", "zh-Hans", "zh-Hant" or "ja".</summary>
    public static string Current
    {
        get
        {
            string pref = Override ?? App.Settings.Current.Language ?? "auto";
            if (Languages.Any(l => l.code == pref)) return pref;
            return FromCulture(CultureInfo.CurrentUICulture);
        }
    }

    private static string FromCulture(CultureInfo c)
    {
        string n = c.Name;
        if (n.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (n.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool traditional = n.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) || n.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) || n.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh-Hant" : "zh-Hans";
        }
        return "en";
    }

    /// <summary>Translates an English source string; unknown strings come back unchanged.</summary>
    public static string T(string text)
    {
        int col = Current switch { "zh-Hans" => 0, "zh-Hant" => 1, "ja" => 2, _ => -1 };
        if (col < 0) return text;
        return Strings.Table.TryGetValue(text, out var row) && !string.IsNullOrEmpty(row[col]) ? row[col] : text;
    }

    /// <summary>Translates a format string and fills in its arguments.</summary>
    public static string F(string format, params object?[] args) => string.Format(T(format), args);
}
