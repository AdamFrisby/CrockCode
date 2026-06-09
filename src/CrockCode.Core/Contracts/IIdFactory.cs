using CrockCode.Core.Domain;

namespace CrockCode.Core.Contracts;

/// <summary>Factory for generating unique identifiers. Injected to avoid Guid.NewGuid ambient state.</summary>
public interface IIdFactory
{
    TaskId NewTaskId();
    WorkerId NewWorkerId();
    CommandId NewCommandId();
    LeaseRef NewLeaseRef();
}
