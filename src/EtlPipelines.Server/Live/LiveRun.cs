using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database.Entities;

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

    public DateTime? CompletedAtUtc => Read(run => run.CompletedAtUtc);

    /// <summary>A live run holding what <paramref name="detail"/> (read from the database) says.</summary>
    public static LiveRun From(RunDetailDto detail) => new(detail);

    /// <summary>Copies the status, times and row counts of <paramref name="run"/> (a database row) onto this run.</summary>
    public void Apply(Run run, string? agentName)
    {
        lock (_gate)
        {
            _run = _run with
            {
                AgentId = run.AgentId,
                AgentName = run.AgentId == _run.AgentId ? _run.AgentName : agentName,
                Status = run.Status,
                StartedAtUtc = Utc(run.StartedAt),
                CompletedAtUtc = Utc(run.CompletedAt),
                ExitCode = run.ExitCode,
                RowsRead = run.RowsRead,
                RowsWritten = run.RowsWritten,
                RowsFailed = run.RowsFailed,
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

    private static DateTime? Utc(DateTime? value) =>
        value is { } dateTime ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : null;
}
