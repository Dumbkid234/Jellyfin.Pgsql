using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// An execution strategy that never retries but makes EF Core buffer query results so commands can run while iterating.
/// </summary>
public sealed class BufferingExecutionStrategy : IExecutionStrategy
{
    private readonly ExecutionStrategyDependencies _dependencies;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferingExecutionStrategy"/> class.
    /// </summary>
    /// <param name="dependencies">The execution strategy dependencies.</param>
    public BufferingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    /// <inheritdoc/>
    public bool RetriesOnFailure => true;

    /// <inheritdoc/>
    public TResult Execute<TState, TResult>(
        TState state,
        Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded)
        => operation(_dependencies.CurrentContext.Context, state);

    /// <inheritdoc/>
    public Task<TResult> ExecuteAsync<TState, TResult>(
        TState state,
        Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
        Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
        CancellationToken cancellationToken = default)
        => operation(_dependencies.CurrentContext.Context, state, cancellationToken);
}
