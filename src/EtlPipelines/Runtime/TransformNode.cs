using System.Threading.Channels;

namespace EtlPipelines.Runtime;

/// <summary>Pumps batches through an <see cref="IDataTransform{TIn,TOut}"/> between two channels.</summary>
internal sealed class TransformNode<TIn, TOut>(
    string name,
    Func<IServiceProvider, IDataTransform<TIn, TOut>> factory) : DataflowNode, IParallelizable
{
    public override string Name { get; } = name;

    /// <inheritdoc />
    public int DegreeOfParallelism { get; set; } = 1;

    public override DataflowNode CreateInstance() =>
        new TransformNode<TIn, TOut>(Name, factory) { DegreeOfParallelism = DegreeOfParallelism };

    public override object? Start(object? input, DataflowRunContext context)
    {
        var reader = (ChannelReader<PooledBatch<TIn>>)input!;
        var degreeOfParallelism = DegreeOfParallelism;
        var channel = CreateChannel<TOut>(context, singleWriter: degreeOfParallelism == 1);
        var transform = Create(context);

        if (degreeOfParallelism == 1)
        {
            Completion = Task.Run(() => RunAsync(transform, reader, channel.Writer, context), CancellationToken.None);
        }
        else
        {
            // The transform is a scoped service, so all workers share the one instance this run
            // resolved — above a parallelism of one it must therefore be thread-safe. Stateful
            // transforms never reach here: the builder rejects that combination outright, because
            // each worker would otherwise emit its own partial result.
            var workers = new Task[degreeOfParallelism];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(
                    () => RunAsync(transform, reader, channel.Writer, context, completeWriter: false),
                    CancellationToken.None);
            }

            Completion = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.WhenAll(workers).ConfigureAwait(false);
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                },
                CancellationToken.None);
        }

        return channel.Reader;
    }

    private IDataTransform<TIn, TOut> Create(DataflowRunContext context)
    {
        var transform = factory(context.Pipeline.Services);
        var degreeOfParallelism = DegreeOfParallelism;

        if (degreeOfParallelism > 1 && transform is IDrainable<TOut>)
        {
            throw new InvalidOperationException(
                $"Transform '{Name}' implements IDrainable<{typeof(TOut).Name}> and accumulates state " +
                $"across batches, so it cannot run at a parallelism of {degreeOfParallelism}: each " +
                "worker would accumulate and drain a separate partial aggregate, silently producing " +
                "wrong results. Remove WithParallelism, or pre-partition the data by key.");
        }

        return transform;
    }

    private async Task RunAsync(
        IDataTransform<TIn, TOut> transform,
        ChannelReader<PooledBatch<TIn>> input,
        ChannelWriter<PooledBatch<TOut>> output,
        DataflowRunContext context,
        bool completeWriter = true)
    {
        var batchSize = context.Options.BatchSize;
        var deadLetters = context.Errors.DeadLetters
            ? context.Pipeline.Services.GetService(typeof(IDeadLetterSink<TIn>)) as IDeadLetterSink<TIn>
            : null;

        try
        {
            await InitializeAsync(transform, context.Token).ConfigureAwait(false);

            await foreach (var batch in input.ReadAllAsync(context.Token).ConfigureAwait(false))
            {
                try
                {
                    RowsIn += batch.Count;

                    // A transform may consume less than it is offered — an expansion whose output
                    // buffer filled mid-row, for instance — so keep re-offering the tail until the
                    // batch is exhausted.
                    var remaining = batch.Memory;
                    while (!remaining.IsEmpty)
                    {
                        var consumed = await StepAsync(transform, remaining, output, context, deadLetters, batchSize)
                            .ConfigureAwait(false);

                        if (consumed < 0)
                        {
                            return;
                        }

                        remaining = remaining[consumed..];
                    }
                }
                finally
                {
                    batch.Release();
                }
            }

            if (!await DrainAsync(transform, output, context, batchSize).ConfigureAwait(false))
            {
                return;
            }

            var completed = await CompleteAsync(transform, context.Token).ConfigureAwait(false);
            if (completed.IsError)
            {
                context.Fail(completed.FirstError);
            }
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled, or a sibling node failed. The stage reports whichever it was.
        }
        catch (Exception ex)
        {
            context.Fail(ToError(Name, ex));
        }
        finally
        {
            if (completeWriter)
            {
                output.TryComplete();
            }

            // The transform is a scoped service; the run's scope disposes it.
        }
    }

    /// <summary>
    /// Runs one <c>TransformAsync</c> call and publishes whatever it produced.
    /// </summary>
    /// <returns>Input rows consumed, or <c>-1</c> when the run must stop.</returns>
    private async ValueTask<int> StepAsync(
        IDataTransform<TIn, TOut> transform,
        ReadOnlyMemory<TIn> remaining,
        ChannelWriter<PooledBatch<TOut>> output,
        DataflowRunContext context,
        IDeadLetterSink<TIn>? deadLetters,
        int batchSize)
    {
        var buffer = PooledBatch<TOut>.Rent(batchSize);
        TransformResult step;

        try
        {
            var result = await transform
                .TransformAsync(remaining, buffer.AsMemory(0, batchSize), context.Token)
                .ConfigureAwait(false);

            if (result.IsError)
            {
                PooledBatch<TOut>.Return(buffer);
                context.Fail(result.FirstError);
                return -1;
            }

            step = result.Value;
        }
        catch
        {
            PooledBatch<TOut>.Return(buffer);
            throw;
        }

        if (step.Consumed == 0 && step.Produced == 0)
        {
            PooledBatch<TOut>.Return(buffer);
            context.Fail(Error.Failure(
                $"stage.{Name}.no_progress",
                $"Transform '{Name}' consumed no input and produced no output while {remaining.Length} " +
                "rows were pending. It was given room for at least one row, so this would loop forever."));
            return -1;
        }

        if (step.Produced > 0)
        {
            RowsOut += step.Produced;
            await output.WriteAsync(new PooledBatch<TOut>(buffer, step.Produced), context.Token).ConfigureAwait(false);
        }
        else
        {
            PooledBatch<TOut>.Return(buffer);
        }

        if (step.RejectedRow is { } rejected)
        {
            // Consumed includes the rejected row, so it sits at the end of the consumed span.
            var row = remaining.Span[step.Consumed - 1];

            if (deadLetters is not null)
            {
                await deadLetters.WriteAsync(row, rejected, context.Token).ConfigureAwait(false);
            }

            if (context.Errors.Record(rejected) is { } fatal)
            {
                context.Fail(fatal);
                return -1;
            }
        }

        return step.Consumed;
    }

    /// <summary>
    /// Empties a stateful transform after its input ends, which is where an aggregation's results
    /// actually appear. Called repeatedly until it reports zero.
    /// </summary>
    /// <returns><see langword="false"/> when the run must stop.</returns>
    private async ValueTask<bool> DrainAsync(
        IDataTransform<TIn, TOut> transform,
        ChannelWriter<PooledBatch<TOut>> output,
        DataflowRunContext context,
        int batchSize)
    {
        if (transform is not IDrainable<TOut> drainable)
        {
            return true;
        }

        while (true)
        {
            var buffer = PooledBatch<TOut>.Rent(batchSize);
            ErrorOr<int> drained;

            try
            {
                drained = await drainable.DrainAsync(buffer.AsMemory(0, batchSize), context.Token).ConfigureAwait(false);
            }
            catch
            {
                PooledBatch<TOut>.Return(buffer);
                throw;
            }

            if (drained.IsError)
            {
                PooledBatch<TOut>.Return(buffer);
                context.Fail(drained.FirstError);
                return false;
            }

            if (drained.Value == 0)
            {
                PooledBatch<TOut>.Return(buffer);
                return true;
            }

            RowsOut += drained.Value;
            await output.WriteAsync(new PooledBatch<TOut>(buffer, drained.Value), context.Token).ConfigureAwait(false);
        }
    }
}
