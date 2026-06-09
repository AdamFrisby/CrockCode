namespace CrockCode.Core.Contracts;

/// <summary>Random number source. Injected to avoid Random.Shared ambient state.</summary>
public interface IRandom
{
    /// <summary>Returns a random integer in [minInclusive, maxExclusive).</summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>Returns a random double in [0.0, 1.0).</summary>
    double NextDouble();
}
