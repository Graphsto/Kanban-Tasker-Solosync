using System.Text.Json;

namespace KanbanTasker.Desktop;

public sealed class LocalPreferences
{
    public static string DirectoryPath =>
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.DirectoryPath;
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KanbanTasker.Revived");
#endif
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "system";
    public Guid? SelectedBoard { get; set; }
    public HashSet<Guid> CollapsedColumns { get; set; } = [];
    public static LocalPreferences Load()
    {
        try
        {
            var path = Path.Combine(DirectoryPath, "preferences.json");
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(path));
                if (settings is not null && settings.DeviceId != Guid.Empty) return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "preferences.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }
}
