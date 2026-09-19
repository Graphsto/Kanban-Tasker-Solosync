using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KanbanTasker.Localization;

internal sealed record AppLanguage(string Code, string Name);

/// <summary>UTF-8 catalogs with English message IDs, culture-aware formatting and English fallback.
/// Stored workspace values and core diagnostics stay language-neutral; only presentation is translated.</summary>
internal sealed class TextCatalog
{
    public static IReadOnlyList<AppLanguage> Languages { get; } = Array.AsReadOnly<AppLanguage>(
        [new("en", "English"), new("de", "Deutsch"), new("es", "Español"), new("fr", "Français"), new("it", "Italiano")]);
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = Languages.ToDictionary(x => x.Code, x => Load(x.Code));
    public string Language { get; }
    public CultureInfo Culture { get; }
    public IReadOnlyDictionary<string, string> Messages => Catalogs[Language];

    public TextCatalog(string? language)
    {
        Language = NormalizeLanguage(language);
        Culture = CultureInfo.GetCultureInfo(Language switch { "de" => "de-DE", "es" => "es-ES", "fr" => "fr-FR", "it" => "it-IT", _ => "en-US" });
    }
    public static string NormalizeLanguage(string? language)
    {
        var code = language?.Split('-', '_')[0].ToLowerInvariant();
        return Languages.Any(x => x.Code == code) ? code! : "en";
    }
    public string Get(string message, params object?[] args)
    {
        var value = Catalogs[Language].GetValueOrDefault(message) ?? Catalogs["en"].GetValueOrDefault(message) ?? message;
        return args.Length == 0 ? value : string.Format(Culture, value, args);
    }

    // Adapt the existing core/update diagnostic contract without localizing serialized data,
    // paths or OS-supplied details. Only these explicitly listed message templates are matched.
    private static readonly (string Key, bool Nested)[] DiagnosticTemplates =
    [
        ("File unavailable; showing the last valid local recovery. {0}", true),
        ("Saved in local recovery; the data file could not be updated. Retrying automatically. {0}", true),
        ("Not saved to the data file: {0}", true),
        ("File unavailable; last valid data kept. {0}", true),
        ("Missing workspace property: {0}.", false), ("Invalid {0}.", false),
        ("This file targets {0}. Select the {1} update for this PC.", false),
        ("Windows could not verify the update signature (0x{0}). Nothing was started.", false)
    ];
    public string TranslateDiagnostic(string message)
    {
        if (Catalogs["en"].ContainsKey(message)) return Get(message);
        foreach (var (key, nested) in DiagnosticTemplates)
        {
            var pattern = "^" + Regex.Escape(key).Replace(@"\{0}", "(?<a>.+)").Replace(@"\{1}", "(?<b>.+)") + "$";
            var match = Regex.Match(message, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) continue;
            var first = match.Groups["a"].Value;
            return Get(key, nested ? TranslateDiagnostic(first) : first, match.Groups["b"].Value);
        }
        return message;
    }
    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream($"KanbanTasker.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing language catalog: {language}.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
