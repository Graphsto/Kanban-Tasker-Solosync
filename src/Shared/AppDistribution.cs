using System.Reflection;

namespace KanbanTasker.Distribution;

internal static class AppDistribution
{
    private static readonly IReadOnlyDictionary<string, string?> Metadata = typeof(AppDistribution).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value);
    private static string Get(string name) => Metadata.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
        ? value : throw new InvalidOperationException("Missing distribution setting: " + name);
    internal static bool IsStore => Get("DistributionChannel") == "Store";
    internal static string Identity => Get("PackageIdentity");
    internal static string Publisher => Get("PackagePublisher");
    internal static string DisplayName => Get("AppDisplayName");
    internal static string ProfileDirectory => Get("ProfileDirectory");
    internal static Version ProductVersion => typeof(AppDistribution).Assembly.GetName().Version!;
    internal static Uri StoreUri => new("ms-windows-store://pdp/?ProductId=" + Get("StoreId"));
    internal static Uri StoreWebUri => new("https://apps.microsoft.com/detail/" + Get("StoreId"));
    internal static Uri PrivacyUri => new(Get("PrivacyUrl"));
}
