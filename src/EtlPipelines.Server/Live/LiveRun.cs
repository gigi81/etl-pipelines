using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Live.Events;

namespace EtlPipelines.Server.Live;

/// <summary>
/// One run as <see cref="LiveState"/> holds it: its summary, the stages it has completed, and its
/// latest resource samples. Mutable and thread-safe - gRPC calls from the pipeline process and its
/// agent update it while the API reads it.
/// </summary>
internal sealed class LiveRun
{
    /// <summary>How many resource samples are kept per run (the oldest are dropped).</summary>
    public const int MaxSamples = 120;

    private readonly object _gate = new();
    private readonly List<StageDto> _stages;
    private readonly List<ResourceSampleDto> _samples;
    private readonly IReadOnlyList<string> _expectedStages;
    private RunSummaryDto _run;

    private LiveRun(RunDetailDto detail)
    {
        _run = detail.Run;
        _stages = [.. detail.Stages];
        _samples = [.. detail.ResourceSamples];
        _expectedStages = detail.ExpectedStages;
    }

    public Guid Id => _run.Id;

    public Guid PipelineId => _run.PipelineId;

    public Guid? AgentId => Read(run => run.AgentId);

    public string Status => Read(run => run.Status);

    public bool IsActive => RunQueries.IsActive(Status);

    public DateTime RequestedAtUtc => _run.RequestedAtUtc;

    public DateTime? StartedAtUtc => Read(run => run.StartedAtUtc);

    public DateTime? CompletedAtUtc => Read(run => run.CompletedAtUtc);

    /// <summary>A live run holding what <paramref name="detail"/> (read from the database) says.</summary>
    public static LiveRun From(RunDetailDto detail) => new(detail);

    /// <summary>A run that has just been created and dispatched.</summary>
    public static LiveRun Create(RunCreatedEvent created, string? agentName) =>
        new(new RunDetailDto(
            new RunSummaryDto(
                created.RunId,
                created.PipelineId,
                created.PipelineName,
                created.PackageId,
                created.PackageVersion,
                created.AgentId,
                agentName,
                "Dispatched",
                created.AtUtc,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                created.ExpectedStages.Count > 0 ? created.ExpectedStages.Count : null),
            [],
            created.ExpectedStages,
            []));

    /// <summary>The run's process started: it is running, and has a start time if it had none.</summary>
    public void Start(DateTime at)
    {
        lock (_gate)
        {
            _run = _run with
            {
                StartedAtUtc = _run.StartedAtUtc ?? at,
                Status = _run.Status is "Queued" or "Dispatched" ? "Running" : _run.Status,
            };
        }
    }

    /// <summary>The run ended. A null exit code or row count leaves the one the run has.</summary>
    public void Finish(string status, int? exitCode, long? rowsRead, long? rowsWritten, long? rowsFailed, DateTime at)
    {
        lock (_gate)
        {
            _run = _run with
            {
                Status = status,
                CompletedAtUtc = at,
                ExitCode = exitCode ?? _run.ExitCode,
                RowsRead = rowsRead ?? _run.RowsRead,
                RowsWritten = rowsWritten ?? _run.RowsWritten,
                RowsFailed = rowsFailed ?? _run.RowsFailed,
            };
        }
    }

    /// <summary>Adds a completed stage, replacing one already held for the same sequence.</summary>
    public void AddStage(StageDto stage)
    {
        lock (_gate)
        {
            var index = _stages.FindIndex(existing => existing.Sequence == stage.Sequence);
            if (index >= 0)
            {
                _stages[index] = stage;
            }
            else
            {
                _stages.Add(stage);
                _stages.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            }

            _run = _run with { StagesCompleted = _stages.Count };
        }
    }

    /// <summary>Adds a resource sample, unless one with that timestamp is already held; keeps the newest <see cref="MaxSamples"/>.</summary>
    public void AddSample(ResourceSampleDto sample)
    {
        lock (_gate)
        {
            if (_samples.Any(existing => existing.SampledAtUtc == sample.SampledAtUtc))
            {
                return;
            }

            _samples.Add(sample);
            _samples.Sort((a, b) => a.SampledAtUtc.CompareTo(b.SampledAtUtc));
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }
        }
    }

    /// <summary>A snapshot of the run, safe to hand out.</summary>
    public RunSummaryDto ToSummary() => Read(run => run);

    /// <summary>A snapshot of the run with its stages and samples, safe to hand out.</summary>
    public RunDetailDto ToDetail()
    {
        lock (_gate)
        {
            return new RunDetailDto(_run, [.. _stages], _expectedStages, [.. _samples]);
        }
    }

    private T Read<T>(Func<RunSummaryDto, T> read)
    {
        lock (_gate)
        {
            return read(_run);
        }
    }
}
