using System.IO.Abstractions;

namespace EtlPipelines.Files.Http;

/// <summary>One file to download and where to put it - the general shape for downloading several URLs at once.</summary>
public sealed record HttpDownload(Uri Url, IFileInfo Target);
