using KanbanTasker.Desktop;

namespace KanbanTasker.Tests;

public sealed class AppearanceTests
{
    [Theory]
    [InlineData("system")] [InlineData("unknown")] [InlineData(null)]
    public void SystemAndUnsupportedPreferencesFollowBothWindowsModes(string? choice)
    {
        Assert.False(AppearanceColors.IsDark(choice, systemIsDark: false));
        Assert.True(AppearanceColors.IsDark(choice, systemIsDark: true));
    }

    [Theory]
    [InlineData("lightBlue", false)] [InlineData("darkBlue", true)]
    public void BlueThemesMeetNormalTextContrastAndYieldToHighContrast(string choice, bool dark)
    {
        var colors = AppearanceColors.For(choice, highContrast: false)!;
        foreach (var background in new[] { colors.Page, colors.Surface, colors.Card })
        {
            Assert.True(Contrast(dark ? "#FFFFFF" : "#000000", background) >= 4.5);
            Assert.True(Contrast(colors.SecondaryText, background) >= 4.5);
        }
        Assert.Null(AppearanceColors.For(choice, highContrast: true));
        Assert.Equal(dark, AppearanceColors.IsDark(choice, !dark));
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            double Channel(int index)
            {
                var c = Convert.ToByte(hex.Substring(index, 2), 16) / 255.0;
                return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
        }
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
