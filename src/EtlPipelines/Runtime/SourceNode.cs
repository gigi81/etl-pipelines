using System.Threading.Channels;

namespace EtlPipelines.Runtime;

/// <summary>Pumps an <see cref="IDataSource{TRow}"/> into the first channel of a dataflow.</summary>
internal sealed class SourceNode<TRow>(string name, Func<IServiceProvider, IDataSource<TRow>> factory) : DataflowNode
{
    public override string Name { get; } = name;

    public override DataflowNode CreateInstance() => new SourceNode<TRow>(Name, factory);

    public override object? Start(object? input, DataflowRunContext context)
    {
        var channel = CreateChannel<TRow>(context, singleWriter: true);
        Completion = Task.Run(() => PumpAsync(channel.Writer, context), CancellationToken.None);
        return channel.Reader;
    }

    private async Task PumpAsync(ChannelWriter<PooledBatch<TRow>> output, DataflowRunContext context)
    {
        IDataSource<TRow>? source = null;
        var batchSize = context.Options.BatchSize;

        try
        {
            source = factory(context.Pipeline.Services);
            await InitializeAsync(source, context.Token).ConfigureAwait(false);

            while (!context.Token.IsCancellationRequested)
            {
                var buffer = PooledBatch<TRow>.Rent(batchSize);
                var read = await source.ReadAsync(buffer.AsMemory(0, batchSize), context.Token).ConfigureAwait(false);

                if (read.IsError)
                {
                    PooledBatch<TRow>.Return(buffer);
                    context.Fail(read.FirstError);
                    return;
                }

                if (read.Value == 0)
                {
                    // Zero is the single end-of-data signal; a short read is not.
                    PooledBatch<TRow>.Return(buffer);
                    break;
                }

                RowsIn += read.Value;
                RowsOut += read.Value;

                // Ownership of the buffer passes downstream here; it is returned by whoever reads it.
                await output.WriteAsync(new PooledBatch<TRow>(buffer, read.Value), context.Token).ConfigureAwait(false);
            }

            var completed = await CompleteAsync(source, context.Token).ConfigureAwait(false);
            if (completed.IsError)
            {
                context.Fail(completed.FirstError);
            }
        }
        catch (OperationCanceledException)
        {
            // Either the caller cancelled or a sibling node failed. Both are handled by the stage.
        }
        catch (Exception ex)
        {
            context.Fail(ToError(Name, ex));
        }
        finally
        {
            output.TryComplete();
            await DisposeAsync(source).ConfigureAwait(false);
        }
    }
}
