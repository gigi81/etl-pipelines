using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>A scope factory that fails a set number of times before working - a database that is briefly down.</summary>
internal sealed class FlakyScopeFactory(IServiceScopeFactory inner, int failures) : IServiceScopeFactory
{
    private int _failuresLeft = failures;

    /// <summary>How many times a scope was asked for, failed or not.</summary>
    public int Attempts { get; private set; }

    public IServiceScope CreateScope()
    {
        Attempts++;
        if (_failuresLeft > 0)
        {
            _failuresLeft--;
            throw new InvalidOperationException("The database is down.");
        }

        return inner.CreateScope();
    }
}
