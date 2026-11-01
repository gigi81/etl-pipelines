using EtlPipelines.Agent;

namespace EtlPipelines.Agent.Tests;

/// <summary><see cref="AgentIdentity"/>: the id an agent keeps so a restart is the same agent, not a new one.</summary>
[Category("Agent")]
public class AgentIdentityTests
{
    [Test]
    public void There_is_no_id_before_one_has_been_saved_even_if_the_directory_does_not_exist()
    {
        using var directory = new TempDirectory();

        AgentIdentity.Load(directory.Path).Should().BeNull();
        AgentIdentity.Load(System.IO.Path.Combine(directory.Path, "missing")).Should().BeNull();
    }

    [Test]
    public void A_saved_id_is_loaded_again_and_saving_a_new_one_replaces_it()
    {
        //arrange
        using var directory = new TempDirectory();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        //act + assert
        AgentIdentity.Save(directory.Path, first).Should().BeTrue();
        AgentIdentity.Load(directory.Path).Should().Be(first);

        AgentIdentity.Save(directory.Path, second).Should().BeTrue();
        AgentIdentity.Load(directory.Path).Should().Be(second);

        // only the id file: the temporary one it was written through is gone
        Directory.GetFiles(directory.Path).Select(System.IO.Path.GetFileName).Should().Equal(AgentIdentity.FileName);
    }

    [Test]
    public void The_directory_is_created_when_it_does_not_exist_yet()
    {
        using var directory = new TempDirectory();
        var cache = System.IO.Path.Combine(directory.Path, "not", "yet");
        var id = Guid.NewGuid();

        AgentIdentity.Save(cache, id).Should().BeTrue();

        AgentIdentity.Load(cache).Should().Be(id);
    }

    [Test]
    [Arguments("")]
    [Arguments("not an id")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    public void A_file_that_does_not_hold_an_id_is_the_same_as_none(string content)
    {
        //arrange
        using var directory = new TempDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, AgentIdentity.FileName), content);

        //act + assert
        AgentIdentity.Load(directory.Path).Should().BeNull();
    }

    [Test]
    public void The_id_file_is_not_mistaken_for_an_installed_package_by_the_cache_sweep()
    {
        //arrange - a cache holding an id and nothing else, over its size cap
        using var directory = new TempDirectory();
        AgentIdentity.Save(directory.Path, Guid.NewGuid());

        //act
        CacheEvictor.Sweep(directory.Path, sizeCapBytes: 0);

        //assert
        AgentIdentity.Load(directory.Path).Should().NotBeNull("the sweep only removes installed package versions");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agent-identity-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
