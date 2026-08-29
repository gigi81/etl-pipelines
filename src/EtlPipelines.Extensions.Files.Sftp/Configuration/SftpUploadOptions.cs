using EtlPipelines.Extensions.Files.Configuration;

namespace EtlPipelines.Extensions.Files.Sftp.Configuration;

/// <summary>Settings for uploading one or more files over SFTP.</summary>
/// <remarks>
/// Derives from <see cref="FileSelectionOptions"/>, not just <see cref="FileWriteOptions"/>, because
/// the pattern-based overload selects local files the same way <see cref="EtlPipelines.Extensions.Files.Configuration.FileCopyOptions"/> does.
/// </remarks>
public sealed class SftpUploadOptions : FileSelectionOptions
{
}
