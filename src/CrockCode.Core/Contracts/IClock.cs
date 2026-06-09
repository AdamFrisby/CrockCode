using CrockCode.Core.Domain;

namespace CrockCode.Core.Contracts;

/// <summary>Provides the current time. Injected to avoid DateTime.Now ambient state.</summary>
public interface IClock
{
    Instant Now { get; }
}
