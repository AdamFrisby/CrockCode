using System.Text.Json.Serialization;

namespace CrockCode.Core.Domain;

/// <summary>
/// Closed union representing domain errors.
/// Transient errors may be retried; Permanent errors are terminal.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Transient), "transient")]
[JsonDerivedType(typeof(Permanent), "permanent")]
public abstract record Error
{
    private Error() { }

    public abstract T Match<T>(Func<Transient, T> transient, Func<Permanent, T> permanent);

    public sealed record Transient(string Code, string Detail, TimeSpan? RetryAfter = null) : Error
    {
        public override T Match<T>(Func<Transient, T> t, Func<Permanent, T> p) => t(this);
    }

    public sealed record Permanent(string Code, string Detail) : Error
    {
        public override T Match<T>(Func<Transient, T> t, Func<Permanent, T> p) => p(this);
    }
}
