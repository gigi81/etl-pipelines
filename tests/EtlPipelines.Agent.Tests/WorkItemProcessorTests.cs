using EtlPipelines.Agent;
using EtlPipelines.Agent.Tests.Fakes;
using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent.Tests;

/// <summary><see cref="WorkItemProcessor"/>: several runs at once, and telling the server how they went even when it is briefly gone.</summary>
[Category("Agent")]
public class WorkItemProcessorTests
{
    [Test]
    public async Task Runs_go_at_the_same_time_up_to_the_limit_and_every_one_is_reported()
    {
        //arrange - three runs of an installed package, room for two
        using var cache = new TempCache();
        var server = new FakeAgentService();
        var (running, mostAtOnce) = (0, 0);
        var exited = new List<string>();
        server.OnReport = request =>
        {
            lock (exited)
            {
                switch (request.Status)
                {
                    case ReportExecutionStatusRequest.Types.Status.Started:
                        mostAtOnce = Math.Max(mostAtOnce, ++running);
                        break;
                    case ReportExecutionStatusRequest.Types.Status.Exited:
                        running--;
                        exited.Add(request.SessionId);
                        break;
                }
            }

            return FakeAgentService.Unary(new Ack());
        };
        var processor = Create(server, cache, maxConcurrentRuns: 2);

        //act - handed over one after the other, which returns at once
        for (var i = 0; i < 3; i++)
        {
            processor.Process("agent", Run($"session-{i}"), CancellationToken.None);
        }

        await processor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        //assert
        mostAtOnce.Should().Be(2, "two at a time: more than one, and no more than the limit");
        exited.Should().BeEquivalentTo("session-0", "session-1", "session-2");
    }

    [Test]
    public async Task Telling_the_server_a_run_ended_is_tried_again_while_it_is_unreachable()
    {
        //arrange - the server is away for the first two attempts at reporting the exit, as in a restart
        using var cache = new TempCache();
        var server = new FakeAgentService();
        var exitAttempts = 0;
        var reportedExit = new TaskCompletionSource<int>();
        server.OnReport = request =>
        {
            if (request.Status != ReportExecutionStatusRequest.Types.Status.Exited)
            {
                return FakeAgentService.Unary(new Ack());
            }

            if (Interlocked.Increment(ref exitAttempts) <= 2)
            {
                return FakeAgentService.Failure<Ack>(StatusCode.Unavailable, "restarting");
            }

            reportedExit.TrySetResult(request.ExitCode);
            return FakeAgentService.Unary(new Ack());
        };
        var processor = Create(server, cache, maxConcurrentRuns: 1, exitCode: 7);

        //act
        processor.Process("agent", Run("session"), CancellationToken.None);
        var exitCode = await reportedExit.Task.WaitAsync(TimeSpan.FromSeconds(60));

        //assert
        exitCode.Should().Be(7);
        exitAttempts.Should().Be(3, "two refused, then through");
    }

    [Test]
    public async Task A_run_that_cannot_start_is_reported_as_failed_rather_than_left_silent()
    {
        //arrange - the package is not in the cache and the work item carries no feed to install it from
        using var cache = new TempCache();
        var server = new FakeAgentService();
        var reported = new TaskCompletionSource<ReportExecutionStatusRequest>();
        server.OnReport = request =>
        {
            if (request.Status == ReportExecutionStatusRequest.Types.Status.Exited)
            {
                reported.TrySetResult(request);
            }

            return FakeAgentService.Unary(new Ack());
        };
        var processor = Create(server, cache, maxConcurrentRuns: 1, installed: false);

        //act
        processor.Process("agent", Run("session"), CancellationToken.None);
        var exit = await reported.Task.WaitAsync(TimeSpan.FromSeconds(30));

        //assert
        exit.ExitCode.Should().Be(-1);
    }

    private static WorkItem Run(string sessionId) =>
        new()
        {
            WorkItemId = sessionId,
            ExecutePipeline = new ExecutePipelineWorkItem
            {
                SessionId = sessionId,
                PackageId = "Pkg",
                PackageVersion = "1.0.0",
                PipelineName = "pipeline",
                ServerUrl = "http://localhost:5000",
            },
        };

    private static WorkItemProcessor Create(FakeAgentService server, TempCache cache, int maxConcurrentRuns, int exitCode = 0, bool installed = true)
    {
        if (installed)
        {
            FakeShim.Write(Path.Combine(cache.Path, "Pkg", "1.0.0"), exitCode);
        }

        var options = Options.Create(new AgentOptions
        {
            CacheDirectory = cache.Path,
            MaxConcurrentRuns = maxConcurrentRuns,
            ResourceSampleInterval = TimeSpan.FromMilliseconds(200),
            ReportRetryDelay = TimeSpan.FromMilliseconds(10),
        });

        return new WorkItemProcessor(server.Client, new PackageInstaller(options), options, NullLogger<WorkItemProcessor>.Instance);
    }

    private sealed class TempCache : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("work-item-processor-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
