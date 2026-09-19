using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private readonly UISettings systemUi = new();
    private ThemeSettings? themeSettings;
    private readonly Dictionary<string, Brush> appearanceBrushes = [];
    private ContentDialog? settingsDialog;
    private bool appearanceRefreshQueued;

    private void InitializeAppearance()
    {
        Root.ActualThemeChanged += (_, _) => QueueAppearanceRefresh();
        systemUi.ColorValuesChanged += SystemColorsChanged;
        themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        themeSettings.Changed += HighContrastChanged;
        Closed += (_, _) =>
        {
            systemUi.ColorValuesChanged -= SystemColorsChanged;
            themeSettings.Changed -= HighContrastChanged;
        };
        ApplyAppearance();
    }
    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!closed) ApplyAppearance(); });
    private void HighContrastChanged(ThemeSettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!closed) ApplyAppearance(); });
    private void ApplyAppearance()
    {
        if (closed) return;
        CancelBoardDrag();
        // Windows exposes white foreground for dark apps, black foreground for light apps.
        var foreground = systemUi.GetColorValue(UIColorType.Foreground);
        var systemIsDark = 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        Root.RequestedTheme = AppearanceColors.IsDark(preferences.Theme, systemIsDark) ? ElementTheme.Dark : ElementTheme.Light;
        RefreshAppearanceSurfaces();
        // ThemeResource bindings settle with the XAML theme-change pass.
        QueueAppearanceRefresh();
    }
    private void QueueAppearanceRefresh()
    {
        if (appearanceRefreshQueued || closed) return;
        appearanceRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            appearanceRefreshQueued = false;
            if (!closed) RefreshAppearanceSurfaces();
        });
    }
    private void RefreshAppearanceSurfaces()
    {
        appearanceBrushes.Clear();
        if (AppearanceColors.For(preferences.Theme, themeSettings?.HighContrast == true) is { } colors)
        {
            appearanceBrushes["ApplicationPageBackgroundThemeBrush"] = ColorBrush(colors.Page);
            appearanceBrushes["LayerFillColorDefaultBrush"] = ColorBrush(colors.Surface);
            appearanceBrushes["CardBackgroundFillColorDefaultBrush"] = ColorBrush(colors.Card);
            appearanceBrushes["CardStrokeColorDefaultBrush"] = ColorBrush(colors.Stroke);
            appearanceBrushes["TextFillColorSecondaryBrush"] = ColorBrush(colors.SecondaryText);
        }
        Root.Background = Brush("ApplicationPageBackgroundThemeBrush");
        TaskPane.PaneBackground = Brush("LayerFillColorDefaultBrush");
        if (EditorLoaded) EditorSurface.Background = TaskPane.PaneBackground;
        PathText.Foreground = Brush("TextFillColorSecondaryBrush");
        if (settingsDialog is not null) ApplyDialogAppearance(settingsDialog);
        ApplyTitleBarTheme();
        if (loaded) RenderBoard();
    }
    private Brush Brush(string key)
    {
        if (appearanceBrushes.TryGetValue(key, out var custom)) return custom;
        // Resolve through elements in this window's theme scope, not Application.Resources,
        // whose theme may differ from an explicit Light/Dark selection.
        return key switch
        {
            "ApplicationPageBackgroundThemeBrush" => ThemePage.Background,
            "LayerFillColorDefaultBrush" => ThemeSurface.Background,
            "CardBackgroundFillColorDefaultBrush" => ThemeCard.Background,
            "CardStrokeColorDefaultBrush" => ThemeStroke.Background,
            "TextFillColorSecondaryBrush" => ThemeSecondaryText.Background,
            "TextFillColorPrimaryBrush" => ThemePrimaryText.Background,
            "SystemFillColorCriticalBrush" => ThemeCritical.Background,
            "SystemFillColorCautionBrush" => ThemeCaution.Background,
            "SystemFillColorSuccessBrush" => ThemeSuccess.Background,
            "AccentFillColorDefaultBrush" => ThemeAccent.Background,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown theme brush.")
        };
    }
    private static SolidColorBrush ColorBrush(string hex) => new(Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
    private void ApplyDialogAppearance(ContentDialog dialog)
    {
        dialog.RequestedTheme = Root.ActualTheme;
        // LayerFillColorDefaultBrush is translucent in dark mode. Dialogs need
        // the opaque platform base (including high contrast), or our solid blue surface.
        dialog.Background = appearanceBrushes.TryGetValue("LayerFillColorDefaultBrush", out var surface)
            ? surface : ThemeDialog.Background;
    }
}
