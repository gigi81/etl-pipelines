using System.Threading.Channels;

namespace EtlPipelines.Runtime;

/// <summary>
/// Fans one stream out to several independent branches, each with its own transforms and sink.
/// </summary>
/// <remarks>
/// <para>
/// A branch point is a terminal node that happens to own sub-chains. Because
/// <see cref="DataflowNode.Start"/> already returns "the reader the next node consumes, or
/// <see langword="null"/> when nothing follows", the stage's linear wiring loop can drive this node
/// without knowing a fan-out exists — no graph model is needed anywhere else.
/// </para>
/// <para>
/// <b>Each branch gets its own copy of the batch buffer.</b> The pipeline's pooling invariant is that
/// exactly one consumer releases each rented array; letting N branches share one batch would return
/// the same array to the pool N times, which hands it out again while it is still in use. Copying the
/// rows into a per-branch buffer costs one <c>memcpy</c> per branch and keeps that invariant intact
/// on every channel.
/// </para>
/// <para>
/// The <i>rows</i> inside are still shared references — copying the buffer fixes buffer lifetime, not
/// aliasing. A branch that mutates a row changes what its siblings see, so rows must be treated as
/// read-only once they enter a branch.
/// </para>
/// </remarks>
internal sealed class BranchNode<T>(string name, IReadOnlyList<IReadOnlyList<DataflowNode>> branches)
    : DataflowNode
{
    public override string Name { get; } = name;

    public override DataflowNode CreateInstance() => new BranchNode<T>(Name, branches);

    public override object? Start(object? input, DataflowRunContext context)
    {
        var reader = (ChannelReader<PooledBatch<T>>)input!;

        // Each run gets its own nodes, exactly as the stage does for the top-level chain.
        var live = new DataflowNode[branches.Count][];
        var writers = new ChannelWriter<PooledBatch<T>>[branches.Count];

        for (var i = 0; i < branches.Count; i++)
        {
            var channel = CreateChannel<T>(context, singleWriter: true);
            writers[i] = channel.Writer;

            var template = branches[i];
            var nodes = new DataflowNode[template.Count];
            object? downstream = channel.Reader;

            for (var n = 0; n < template.Count; n++)
            {
                nodes[n] = template[n].CreateInstance();
                downstream = nodes[n].Start(downstream, context);
            }

            live[i] = nodes;
        }

        Completion = Task.Run(() => RunAsync(reader, writers, live, context), CancellationToken.None);

        // Terminal: every path ends inside one of the branches.
        return null;
    }

    private async Task RunAsync(
        ChannelReader<PooledBatch<T>> input,
        ChannelWriter<PooledBatch<T>>[] writers,
        DataflowNode[][] branchNodes,
        DataflowRunContext context)
    {
        try
        {
            await PumpAsync(input, writers, context).ConfigureAwait(false);
        }
        finally
        {
            // Complete every branch before awaiting them, or a branch would block forever on a
            // channel that will never receive another batch.
            foreach (var writer in writers)
            {
                writer.TryComplete();
            }
        }

        foreach (var nodes in branchNodes)
        {
            await Task.WhenAll(nodes.Select(n => n.Completion)).ConfigureAwait(false);
        }

        // Rolled up once every branch has finished: rows in is what arrived, rows out is what all the
        // destinations together actually wrote.
        foreach (var nodes in branchNodes)
        {
            RowsOut += nodes[^1].RowsOut;
        }
    }

    private async Task PumpAsync(
        ChannelReader<PooledBatch<T>> input,
        ChannelWriter<PooledBatch<T>>[] writers,
        DataflowRunContext context)
    {
        try
        {
            await foreach (var batch in input.ReadAllAsync(context.Token).ConfigureAwait(false))
            {
                try
                {
                    RowsIn += batch.Count;

                    for (var i = 0; i < writers.Length; i++)
                    {
                        var buffer = PooledBatch<T>.Rent(batch.Count);
                        batch.Memory.Span.CopyTo(buffer);

                        try
                        {
                            await writers[i]
                                .WriteAsync(new PooledBatch<T>(buffer, batch.Count), context.Token)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ownership had not passed to the branch yet, so this side still owns it.
                            PooledBatch<T>.Return(buffer);
                            throw;
                        }
                    }
                }
                finally
                {
                    // Released regardless of how the copies went, so an abort cannot leak the batch
                    // that was in flight when it happened.
                    batch.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled, or a branch failed and tripped the shared token.
        }
        catch (Exception ex)
        {
            context.Fail(ToError(Name, ex));
        }
        finally
        {
            // Anything still queued upstream would otherwise keep its pooled buffer forever.
            while (input.TryRead(out var orphan))
            {
                orphan.Release();
            }
        }
    }
}
