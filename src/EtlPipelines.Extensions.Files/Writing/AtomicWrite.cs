using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Files.Writing;

/// <summary>Whether <see cref="AtomicWrite.WriteAsync"/> actually wrote its target.</summary>
public enum FileWriteOutcome
{
    /// <summary>The target was written and promoted.</summary>
    Written,

    /// <summary>The target already existed and <see cref="OverwritePolicy.Skip"/> left it alone.</summary>
    Skipped,
}

/// <summary>
/// The only place in these packages that promotes a file into its final name. Generalises
/// <c>EtlPipelines.Extensions.Csv.CsvSink</c>'s temp-sibling-then-rename: stream into a temporary file beside the
/// target, close the handle, then rename it into place. Applies to a downloaded file, an uploaded-then-
/// verified copy, each extracted archive entry, and the finished archive a compress stage produces.
/// </summary>
/// <remarks>
/// <para>
/// A rename is only atomic within one volume, which is why the temporary file is a sibling of the
/// target rather than living in the system temp directory - that is usually a different volume. The
/// handle is closed before the rename because an open one makes the move fail on Windows. On any
/// failure the temporary file is never promoted and is left in place for inspection; the previous
/// good target, if any, is untouched.
/// </para>
/// <para>
/// Public, rather than internal to this package, because the Http and Sftp satellite packages write
/// files the same way and share this instead of reimplementing it.
/// </para>
/// </remarks>
public static class AtomicWrite
{
    /// <summary>
    /// Applies <paramref name="overwrite"/>, then, unless it says to skip, writes through
    /// <paramref name="write"/> and promotes the result to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// The existence check and the <see cref="OverwritePolicy.Fail"/> decision happen before anything
    /// is written, so a caller checking the whole set of targets up front (as <c>Fail</c> is meant to
    /// be used) never has some already written when the failure is reported.
    /// </remarks>
    public static async ValueTask<ErrorOr<FileWriteOutcome>> WriteAsync(
        IFileInfo target,
        OverwritePolicy overwrite,
        bool createTargetDirectory,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(write);

        if (CheckOverwrite(target, overwrite) is { } settled)
        {
            return settled;
        }

        var fileSystem = target.FileSystem;

        if (createTargetDirectory)
        {
            target.Directory?.Create();
        }

        var directory = target.DirectoryName ?? fileSystem.Path.GetPathRoot(target.FullName) ?? ".";
        var temp = fileSystem.FileInfo.New(fileSystem.Path.Combine(directory, $".{Guid.NewGuid():N}.tmp"));

        await using (var stream = temp.Create())
        {
            await write(stream, cancellationToken).ConfigureAwait(false);
        }

        // Closed by the `await using` above before the rename runs, not after: an open handle makes
        // the move fail on Windows, and renaming a file that has not finished flushing would promote
        // a partial one anywhere.
        temp.Refresh();
        temp.MoveTo(target.FullName, overwrite: true);

        return FileWriteOutcome.Written;
    }

    /// <summary>
    /// Applies <paramref name="overwrite"/> against whether <paramref name="target"/> currently
    /// exists. Returns <see langword="null"/> when the caller should proceed to write; a settled
    /// result otherwise - <see cref="FileWriteOutcome.Skipped"/> or a
    /// <see cref="OverwritePolicy.Fail"/> error. Shared by <see cref="WriteAsync"/> and by a plain
    /// rename, which has no write callback of its own to route through it.
    /// </summary>
    internal static ErrorOr<FileWriteOutcome>? CheckOverwrite(IFileInfo target, OverwritePolicy overwrite)
    {
        target.Refresh();

        if (!target.Exists)
        {
            return null;
        }

        return overwrite switch
        {
            OverwritePolicy.Skip => FileWriteOutcome.Skipped,
            OverwritePolicy.Fail => Error.Failure("files.write.exists", $"'{target.FullName}' already exists."),
            _ => null,
        };
    }
}
