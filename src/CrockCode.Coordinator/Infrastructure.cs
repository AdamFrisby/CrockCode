using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;

namespace CrockCode.Coordinator;

public sealed class SystemClock : IClock
{
    public Instant Now => new(DateTimeOffset.UtcNow);
}

public sealed class SystemRandom : IRandom
{
    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class GuidIdFactory : IIdFactory
{
    public TaskId NewTaskId() => new("tsk_" + Guid.NewGuid().ToString("N"));
    public WorkerId NewWorkerId() => new("wkr_" + Guid.NewGuid().ToString("N"));
    public CommandId NewCommandId() => new("cmd_" + Guid.NewGuid().ToString("N"));
    public LeaseRef NewLeaseRef() => new("lease_" + Guid.NewGuid().ToString("N"));
}
