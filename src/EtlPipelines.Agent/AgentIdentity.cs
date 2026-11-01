namespace EtlPipelines.Agent;

/// <summary>
/// The id the server issued this agent the first time it registered, kept in a file in the agent's
/// cache directory so every later start registers as the same agent - the same tags, pipeline
/// assignments and run history - instead of a new one each time. In Docker the cache directory is a
/// volume, so a recreated container keeps it.
/// </summary>
public static class AgentIdentity
{
    /// <summary>The file in <see cref="AgentOptions.CacheDirectory"/> the id is kept in.</summary>
    public const string FileName = "agent-id";

    /// <summary>The id kept in <paramref name="cacheDirectory"/>; <see langword="null"/> when there is none yet (or the file is unreadable).</summary>
    public static Guid? Load(string cacheDirectory)
    {
        try
        {
            var path = Path.Combine(cacheDirectory, FileName);
            return File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) && id != Guid.Empty ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not being able to read it only costs a new registration - never worth failing the agent over.
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="agentId"/> for the next start. Written to a temporary file and moved into
    /// place, so a crash cannot leave half an id behind. Best effort: an agent that cannot save its id
    /// still works, it is just a new agent each time it starts.
    /// </summary>
    /// <returns>Whether it was saved.</returns>
    public static bool Save(string cacheDirectory, Guid agentId)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.Combine(cacheDirectory, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, agentId.ToString());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
