using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public static class PackageAssemblyInspection
{
    public static Dictionary<string, string> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var values = new Dictionary<string, string> { ["Version"] = assembly.Version.ToString() };
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid assembly attribute.");
            values.Add(blob.ReadSerializedString(), blob.ReadSerializedString());
        }
        foreach (var handle in reader.ManifestResources)
            values.Add("Resource:" + reader.GetString(reader.GetManifestResource(handle).Name), "true");
        foreach (var handle in reader.TypeDefinitions)
            if (reader.GetString(reader.GetTypeDefinition(handle).Name) is "SmokeProfile" or "ShowcaseProfile")
                throw new InvalidDataException("A test harness was included in the package.");
        return values;
    }
}
