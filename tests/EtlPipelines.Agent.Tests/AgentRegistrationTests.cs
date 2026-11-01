using EtlPipelines.Agent;
using EtlPipelines.Agent.Tests.Fakes;
using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent.Tests;

/// <summary><see cref="AgentRegistration"/>: staying connected - and the same agent - through whatever happens to the server.</summary>
[Category("Agent")]
public class AgentRegistrationTests
{
    [Test]
    public async Task The_agent_connects_again_when_the_stream_fails_and_is_the_same_agent()
    {
        //arrange - the first stream dies, as when the server restarts
        using var cache = new TempDirectory();
        var server = new FakeAgentService
        {
            OnSubscribe = (number, _) => new ScriptedStream([], number == 1 ? ScriptedStream.Ending.Fails : ScriptedStream.Ending.StaysOpen),
        };
        var processor = new RecordingProcessor();

        //act
        await using var running = Start(server, processor, cache.Path);
        await WorkSchedulerTests.Eventually(() => server.RegistrationCount >= 2);

        //assert
        var registrations = server.Registrations.ToList();
        registrations[0].AgentId.Should().BeEmpty("a first start has no id yet");
        registrations[1].AgentId.Should().NotBeEmpty("the next connection brings the id it was issued");
        AgentIdentity.Load(cache.Path)!.Value.ToString().Should().Be(registrations[1].AgentId);
    }

    [Test]
    public async Task The_agent_registers_with_the_id_it_kept_from_an_earlier_start()
    {
        //arrange
        using var cache = new TempDirectory();
        var kept = Guid.NewGuid();
        AgentIdentity.Save(cache.Path, kept);
        var server = new FakeAgentService();

        //act
        await using var running = Start(server, new RecordingProcessor(), cache.Path);
        await WorkSchedulerTests.Eventually(() => server.RegistrationCount >= 1);

        //assert
        server.Registrations[0].AgentId.Should().Be(kept.ToString());
    }

    [Test]
    public async Task Work_items_are_handed_over_and_one_received_before_the_connection_was_lost_is_not_dropped()
    {
        //arrange
        using var cache = new TempDirectory();
        var first = new WorkItem { WorkItemId = "one", InstallPackage = new InstallPackageWorkItem { PackageId = "A", Version = "1" } };
        var second = new WorkItem { WorkItemId = "two", InstallPackage = new InstallPackageWorkItem { PackageId = "B", Version = "1" } };
        var server = new FakeAgentService
        {
            OnSubscribe = (number, _) => number == 1
                ? new ScriptedStream([first], ScriptedStream.Ending.Fails)
                : new ScriptedStream([second], ScriptedStream.Ending.StaysOpen),
        };
        var processor = new RecordingProcessor();

        //act
        await using var running = Start(server, processor, cache.Path);
        await WorkSchedulerTests.Eventually(() => processor.Received.Count >= 2);

        //assert - in order, for the same agent, and the token is the agent's own, not a connection's
        processor.Received.Select(r => r.Item.WorkItemId).Should().Equal("one", "two");
        processor.Received.Select(r => r.AgentId).Distinct().Should().ContainSingle();
        processor.Received.Select(r => r.Token).Distinct().Should().ContainSingle();
    }

    [Test]
    public async Task A_server_that_does_not_know_the_agent_makes_it_register_again_as_itself()
    {
        //arrange - the first heartbeat is told the agent is unknown (removed, or lost in a restart)
        using var cache = new TempDirectory();
        var heartbeats = 0;
        var server = new FakeAgentService
        {
            OnHeartbeat = _ => Interlocked.Increment(ref heartbeats) == 1
                ? FakeAgentService.Failure<Ack>(StatusCode.NotFound, "unknown")
                : FakeAgentService.Unary(new Ack()),
        };

        //act
        await using var running = Start(server, new RecordingProcessor(), cache.Path);
        await WorkSchedulerTests.Eventually(() => server.RegistrationCount >= 2);

        //assert
        server.Registrations[1].AgentId.Should().Be(server.Issued[0], "same id, so the same agent once the server has it again");
    }

    [Test]
    public async Task One_failed_heartbeat_is_not_a_reason_to_leave()
    {
        //arrange
        using var cache = new TempDirectory();
        var calls = 0;
        var server = new FakeAgentService
        {
            OnHeartbeat = _ => Interlocked.Increment(ref calls) == 1
                ? FakeAgentService.Failure<Ack>(StatusCode.Unavailable)
                : FakeAgentService.Unary(new Ack()),
        };

        //act
        await using var running = Start(server, new RecordingProcessor(), cache.Path);
        await WorkSchedulerTests.Eventually(() => Volatile.Read(ref calls) >= 6);

        //assert
        server.RegistrationCount.Should().Be(1);
    }

    [Test]
    public async Task Heartbeats_that_keep_failing_mean_the_link_is_dead_and_the_agent_connects_again()
    {
        //arrange - the stream stays open, as on a half-dead connection; only the heartbeats show it
        using var cache = new TempDirectory();
        var server = new FakeAgentService { OnHeartbeat = _ => FakeAgentService.Failure<Ack>(StatusCode.Unavailable) };

        //act
        await using var running = Start(server, new RecordingProcessor(), cache.Path);
        await WorkSchedulerTests.Eventually(() => server.RegistrationCount >= 2);

        //assert
        server.Registrations[1].AgentId.Should().Be(server.Issued[0]);
    }

    [Test]
    public async Task A_server_that_cannot_be_reached_is_tried_again_until_it_can_be()
    {
        //arrange - registration is refused twice, as when the server is still starting
        using var cache = new TempDirectory();
        var server = new FakeAgentService { RefuseRegistration = attempt => attempt <= 2 };

        //act
        await using var running = Start(server, new RecordingProcessor(), cache.Path);
        await WorkSchedulerTests.Eventually(() => server.SubscribeCount >= 1);

        //assert
        server.RegistrationCount.Should().Be(3, "two refused, then one through");
        server.Issued.Should().ContainSingle();
    }

    [Test]
    public async Task Stopping_the_agent_waits_for_the_work_still_running()
    {
        //arrange
        using var cache = new TempDirectory();
        var processor = new RecordingProcessor();
        var running = Start(new FakeAgentService(), processor, cache.Path);
        await Task.Delay(100);
        processor.Busy = new TaskCompletionSource();

        //act
        var stopping = running.StopAsync();
        await Task.Delay(300);

        //assert
        stopping.IsCompleted.Should().BeFalse("a run is still going");
        processor.Busy.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await running.DisposeAsync();
    }

    private static Running Start(FakeAgentService server, RecordingProcessor processor, string cacheDirectory)
    {
        var registration = new AgentRegistration(
            server.Client,
            processor,
            Options.Create(new AgentOptions
            {
                CacheDirectory = cacheDirectory,
                Name = "agent-under-test",
                HeartbeatInterval = TimeSpan.FromMilliseconds(30),
                ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(40),
            }),
            NullLogger<AgentRegistration>.Instance);

        registration.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new Running(registration);
    }

    /// <summary>A started <see cref="AgentRegistration"/>, stopped when disposed.</summary>
    private sealed class Running(AgentRegistration registration) : IAsyncDisposable
    {
        public Task StopAsync() => registration.StopAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await registration.StopAsync(CancellationToken.None);
            registration.Dispose();
        }
    }

    private sealed class RecordingProcessor : IWorkItemProcessor
    {
        public List<(string AgentId, WorkItem Item, CancellationToken Token)> Received { get; } = [];

        public TaskCompletionSource? Busy { get; set; }

        public void Process(string agentId, WorkItem workItem, CancellationToken cancellationToken)
        {
            lock (Received)
            {
                Received.Add((agentId, workItem, cancellationToken));
            }
        }

        public Task WhenIdleAsync() => Busy?.Task ?? Task.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agent-registration-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
