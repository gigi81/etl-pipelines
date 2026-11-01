namespace EtlPipelines.Server.Catalog;

/// <summary>Why <see cref="PackageUploader"/> refused a package.</summary>
public enum PackageUploadError
{
    /// <summary>The file is not a pipeline package: not a valid <c>.nupkg</c>, or not a .NET tool.</summary>
    Invalid,

    /// <summary>The feed already has that exact version of the package.</summary>
    AlreadyExists,

    /// <summary>The feed could not be reached, or refused the push for another reason.</summary>
    FeedUnavailable,
}
