using System.Collections.Immutable;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>Wraps a command with its unique outbox identifier and associated task ID.</summary>
public sealed record OutboxEntry(CommandId Id, TaskId TaskId, Command Command);

/// <summary>
/// Transactional outbox for reliably dispatching commands.
/// Commands are enqueued atomically with event persistence and processed asynchronously.
/// </summary>
public interface IOutbox
{
    /// <summary>Enqueue commands for later processing.</summary>
    Task<Result<bool>> EnqueueAsync(ImmutableArray<Command> commands, CancellationToken ct = default);

    /// <summary>Dequeue the next batch of pending commands.</summary>
    Task<Result<ImmutableArray<OutboxEntry>>> DequeueAsync(int maxBatchSize, CancellationToken ct = default);

    /// <summary>Acknowledge that commands have been processed.</summary>
    Task<Result<bool>> AcknowledgeAsync(ImmutableArray<CommandId> commandIds, CancellationToken ct = default);
}
