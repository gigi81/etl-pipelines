using System.IO.Compression;
using System.Text;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>Builds the bytes of a minimal <c>.nupkg</c>: a zip with a nuspec, which is all the Server reads.</summary>
internal static class TestPackage
{
    /// <summary>A package with the given identity; a .NET tool unless <paramref name="dotnetTool"/> is false.</summary>
    public static byte[] Build(string id = "Contoso.Pipelines.Orders", string version = "1.2.3", bool dotnetTool = true)
    {
        var packageTypes = dotnetTool ? """<packageTypes><packageType name="DotnetTool" /></packageTypes>""" : string.Empty;
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>tests</authors>
                <description>A test package.</description>
                {packageTypes}
              </metadata>
            </package>
            """;

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{id}.nuspec");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(nuspec);
        }

        return stream.ToArray();
    }
}
