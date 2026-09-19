namespace KanbanTasker.Desktop;

internal sealed record AppearanceColors(string Page, string Surface, string Card, string Stroke, string SecondaryText)
{
    internal static string Normalize(string? theme) => theme is "light" or "dark" or "lightBlue" or "darkBlue" ? theme : "system";
    internal static bool IsDark(string? theme, bool systemIsDark) => Normalize(theme) switch
    {
        "dark" or "darkBlue" => true, "light" or "lightBlue" => false, _ => systemIsDark
    };
    internal static AppearanceColors? For(string? theme, bool highContrast) => highContrast ? null : Normalize(theme) switch
    {
        "lightBlue" => new("#E8F2FA", "#DCEBF6", "#F7FBFF", "#B8CDDF", "#42596E"),
        "darkBlue" => new("#122333", "#193348", "#23445C", "#42657F", "#BED0E0"),
        _ => null
    };
}
