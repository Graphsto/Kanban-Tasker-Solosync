using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using KanbanTasker.Desktop;
using KanbanTasker.Localization;

namespace KanbanTasker.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("en")] [InlineData("de")] [InlineData("es")] [InlineData("fr")] [InlineData("it")]
    public void CatalogsHaveCompleteMessagesAndMatchingFormatArguments(string language)
    {
        var english = new TextCatalog("en").Messages;
        var localized = new TextCatalog(language).Messages;
        Assert.Equal(english.Keys.Order(), localized.Keys.Order());
        foreach (var (key, value) in localized)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.Equal(Arguments(key), Arguments(value));
            Assert.Equal(System.Text.CompositeFormat.Parse(key).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(value).MinimumArgumentCount);
        }
    }

    [Theory]
    [InlineData("de-DE", "de")] [InlineData("ES_mx", "es")] [InlineData("fr-CA", "fr")]
    [InlineData("it", "it")] [InlineData("unknown", "en")] [InlineData(null, "en")]
    public void LanguageCodesNormalizeAndUnknownCodesFallBack(string? code, string expected)
    {
        var catalog = new TextCatalog(code);
        Assert.Equal(expected, catalog.Language);
        Assert.Equal("New message: example", catalog.Get("New message: {0}", "example"));
    }

    [Fact]
    public void CoreDiagnosticsAreTranslatedWithoutChangingPathsOrOsDetails()
    {
        var catalog = new TextCatalog("de");
        var details = @"Access denied: C:\Users\François\日本語\Kanban.json";
        Assert.Equal("Datei nicht verfügbar; letzter gültiger Datenstand bleibt erhalten. " + details,
            catalog.TranslateDiagnostic("File unavailable; last valid data kept. " + details));
        Assert.Equal("Nicht in der Datendatei gespeichert: Der Aufgabentitel darf nicht leer sein.",
            catalog.TranslateDiagnostic("Not saved to the data file: Task title cannot be empty."));
        Assert.Equal("Diese Datei ist für arm64. Wähle das x64-Update für diesen PC.",
            catalog.TranslateDiagnostic("This file targets arm64. Select the x64 update for this PC."));
        Assert.Contains("0x80096010", catalog.TranslateDiagnostic("Windows could not verify the update signature (0x80096010). Nothing was started."));
        Assert.Equal(details, catalog.TranslateDiagnostic(details));
    }

    [Fact]
    public void FormattingUsesChosenLanguageWithoutChangingThreadCulture()
    {
        var before = CultureInfo.CurrentCulture;
        var date = new DateOnly(2026, 9, 19);
        Assert.Equal("Fällig am 19.09.2026", new TextCatalog("de").Get("Due {0:d}", date));
        Assert.Equal("Scadenza: 19/09/2026", new TextCatalog("it").Get("Due {0:d}", date));
        Assert.Same(before, CultureInfo.CurrentCulture);
        Assert.Equal("Supprimer « 日本語 — tarea » ?", new TextCatalog("fr").Get("Delete “{0}”?", "日本語 — tarea"));
    }

    [Fact]
    public void EveryExplicitUiMessageHasACatalogEntry()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "KanbanTasker.Desktop");
        var english = new TextCatalog("en").Messages;
        const string pattern = """
            \b(?:T|text\.Get)\("((?:\\.|[^"\\])*)"
            """;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        foreach (Match match in Regex.Matches(File.ReadAllText(file), pattern))
        {
            var key = JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"")!;
            Assert.True(english.ContainsKey(key), $"{Path.GetFileName(file)}: missing '{key}'");
        }
        foreach (var priority in new[] { "Low", "Medium", "High" })
        {
            Assert.Contains(priority, english.Keys);
            Assert.Contains(priority + " priority", english.Keys);
        }
    }

    [Fact]
    public void PreferencesPreserveLanguageAndOldProfilesDefaultToEnglish()
    {
        var previous = JsonSerializer.Deserialize<LocalPreferences>("""{"DeviceId":"500f566f-17ed-4ddf-a3fa-f21ec8b68a1e"}""")!;
        Assert.Equal("en", previous.Language);
        Assert.Equal("system", previous.Theme);
        previous.Language = "fr";
        previous.Theme = "darkBlue";
        previous.FilePath = @"C:\Nextcloud\Kanban.json";
        var reopened = JsonSerializer.Deserialize<LocalPreferences>(JsonSerializer.Serialize(previous))!;
        Assert.Equal("fr", reopened.Language);
        Assert.Equal("darkBlue", reopened.Theme);
        Assert.Equal(previous.FilePath, reopened.FilePath);
        Assert.Equal(previous.DeviceId, reopened.DeviceId);
    }

    private static string[] Arguments(string text) => Regex.Matches(text, @"\{\d+(?::[^}]+)?\}")
        .Select(x => x.Value).Order().ToArray();
    private static string RepositoryRoot([CallerFilePath] string path = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "KanbanTasker.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the repository containing KanbanTasker.sln.");
    }
}
