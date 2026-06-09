using System.Collections.Immutable;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Contracts;

/// <summary>Descriptor for a scheduled or recurring job.</summary>
public sealed record JobDescriptor(
    string JobId,
    string JobType,
    TaskId? TaskId,
    Instant ScheduledAt,
    int MaxRetries,
    int Attempt);

/// <summary>
/// Persistent job store for scheduling deferred work (polling, retries, timeouts).
/// </summary>
public interface IJobStore
{
    /// <summary>Schedule a new job.</summary>
    Task<Result<bool>> ScheduleAsync(JobDescriptor job, CancellationToken ct = default);

    /// <summary>Fetch jobs that are due for execution.</summary>
    Task<Result<ImmutableArray<JobDescriptor>>> FetchDueAsync(Instant now, int maxBatchSize, CancellationToken ct = default);

    /// <summary>Mark a job as completed.</summary>
    Task<Result<bool>> CompleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>Mark a job as failed, incrementing its attempt count.</summary>
    Task<Result<bool>> FailAsync(string jobId, Error error, CancellationToken ct = default);

    /// <summary>Cancel a scheduled job.</summary>
    Task<Result<bool>> CancelAsync(string jobId, CancellationToken ct = default);
}
