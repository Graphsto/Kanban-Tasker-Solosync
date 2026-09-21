using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using System.Xml;

namespace KanbanTasker.Updates;

internal enum UpdateDisposition { Install, Update, AlreadyInstalled, OlderVersion }
internal sealed record UpdateFile(Version Version, string Architecture, bool IsSetup);

internal static class UpdateFiles
{
    public static string Identity => Distribution.AppDistribution.Identity;
    public static string Publisher => Distribution.AppDistribution.Publisher;

    public static UpdateDisposition Compare(Version? installed, Version candidate) => installed is null ? UpdateDisposition.Install :
        candidate > installed ? UpdateDisposition.Update : candidate == installed ? UpdateDisposition.AlreadyInstalled : UpdateDisposition.OlderVersion;

    public static Version ParseVersion(string? text)
    {
        if (!Version.TryParse(text, out var version) || version.Build < 0 || version.Revision < 0 ||
            new[] { version.Major, version.Minor, version.Build, version.Revision }.Any(x => x > ushort.MaxValue))
            throw new InvalidDataException("The update has an invalid four-part version number.");
        return version;
    }

    public static UpdateFile Read(string path, string architecture)
    {
        if (Path.GetExtension(path).Equals(".msix", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException("The selected file is not an app package.");
            using var stream = entry.Open();
            return ReadManifest(stream, architecture);
        }
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a Kanban Tasker Setup.exe or MSIX file.");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.OriginalFilename, "KanbanTasker.Setup.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected executable is not a Kanban Tasker installer.");
        using var exe = File.OpenRead(path);
        using var reader = new BinaryReader(exe);
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid setup executable.");
        exe.Position = 0x3C; var peOffset = reader.ReadUInt32();
        if (peOffset > exe.Length - 6) throw new InvalidDataException("Invalid setup executable.");
        exe.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Invalid setup executable.");
        var machine = reader.ReadUInt16();
        var nativeArchitecture = machine switch { 0x8664 => "x64", 0xAA64 => "arm64", _ => "unsupported" };
        EnsureArchitecture(nativeArchitecture, architecture);
        return new(ParseVersion(info.FileVersion), nativeArchitecture, true);
    }

    internal static UpdateFile ReadManifest(Stream stream, string architecture)
    {
        XDocument xml;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024, CloseInput = false
            });
            xml = XDocument.Load(reader);
        }
        catch (XmlException ex) { throw new InvalidDataException("The update manifest is invalid or too large.", ex); }
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = xml.Root?.Element(ns + "Identity");
        if ((string?)identity?.Attribute("Name") != Identity || (string?)identity?.Attribute("Publisher") != Publisher)
            throw new InvalidDataException("The package belongs to a different app or publisher.");
        var target = (string?)identity.Attribute("ProcessorArchitecture") ?? "";
        EnsureArchitecture(target, architecture);
        return new(ParseVersion((string?)identity.Attribute("Version")), target, false);
    }

    private static void EnsureArchitecture(string target, string current)
    {
        if (target != current || target is not ("x64" or "arm64"))
            throw new InvalidDataException($"This file targets {target}. Select the {current} update for this PC.");
    }
}
