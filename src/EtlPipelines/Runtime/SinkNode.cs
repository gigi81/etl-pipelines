using System.Threading.Channels;

namespace EtlPipelines.Runtime;

/// <summary>Drains the last channel of a dataflow into an <see cref="IDataSink{TRow}"/>.</summary>
internal sealed class SinkNode<TRow>(string name, Func<IServiceProvider, IDataSink<TRow>> factory) : DataflowNode
{
    public override string Name { get; } = name;

    public override DataflowNode CreateInstance() => new SinkNode<TRow>(Name, factory);

    public override object? Start(object? input, DataflowRunContext context)
    {
        var reader = (ChannelReader<PooledBatch<TRow>>)input!;
        Completion = Task.Run(() => PumpAsync(reader, context), CancellationToken.None);
        return null;
    }

    private async Task PumpAsync(ChannelReader<PooledBatch<TRow>> input, DataflowRunContext context)
    {
        IDataSink<TRow>? sink = null;
        var faulted = false;

        try
        {
            sink = factory(context.Pipeline.Services);
            await InitializeAsync(sink, context.Token).ConfigureAwait(false);

            await foreach (var batch in input.ReadAllAsync(context.Token).ConfigureAwait(false))
            {
                try
                {
                    RowsIn += batch.Count;

                    var written = await sink.WriteAsync(batch.Memory, context.Token).ConfigureAwait(false);

                    if (written.IsError)
                    {
                        // A sink reports failure for the batch, not for a particular row, so the row
                        // error policy is applied at batch granularity here. A sink that can isolate
                        // the offending row should reject only that row by writing the rest.
                        if (context.Errors.Record(written.FirstError, batch.Count) is { } fatal)
                        {
                            faulted = true;
                            context.Fail(fatal);
                            return;
                        }

                        continue;
                    }

                    RowsOut += written.Value;
                }
                finally
                {
                    batch.Release();
                }
            }

            // Only reached when every batch landed, which is the precondition for committing.
            var completed = await CompleteAsync(sink, context.Token).ConfigureAwait(false);
            if (completed.IsError)
            {
                faulted = true;
                context.Fail(completed.FirstError);
            }
        }
        catch (OperationCanceledException)
        {
            faulted = true;
        }
        catch (Exception ex)
        {
            faulted = true;
            context.Fail(ToError(Name, ex));
        }
        finally
        {
            // Drain whatever is still queued so upstream never blocks writing into a dead channel,
            // and so pooled buffers are not lost when the run ends early.
            if (faulted)
            {
                while (input.TryRead(out var orphan))
                {
                    orphan.Release();
                }
            }

            // The sink is a scoped service; the run's scope disposes it.
        }
    }
}
