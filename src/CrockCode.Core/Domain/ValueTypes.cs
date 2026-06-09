namespace CrockCode.Core.Domain;

/// <summary>Represents void in a Result context (Result&lt;Unit&gt;).</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>Strongly-typed task identifier (format: "tsk_{ulid}").</summary>
public readonly record struct TaskId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Strongly-typed worker identifier (format: "wkr_{ulid}").</summary>
public readonly record struct WorkerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Strongly-typed command identifier for idempotency.</summary>
public readonly record struct CommandId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Absolute path to a working directory.</summary>
public readonly record struct WorkingDir(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Opaque lease reference for distributed locking.</summary>
public readonly record struct LeaseRef(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Provider-specific batch reference for tracking worker batches.</summary>
public readonly record struct ProviderBatchRef(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Task priority. Higher values = more urgent.</summary>
public readonly record struct Priority(int Value) : IComparable<Priority>
{
    public int CompareTo(Priority other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();

    public static bool operator >(Priority left, Priority right) => left.Value > right.Value;
    public static bool operator <(Priority left, Priority right) => left.Value < right.Value;
    public static bool operator >=(Priority left, Priority right) => left.Value >= right.Value;
    public static bool operator <=(Priority left, Priority right) => left.Value <= right.Value;
}

/// <summary>A point in time, wrapping DateTimeOffset for domain clarity.</summary>
public readonly record struct Instant(DateTimeOffset Value) : IComparable<Instant>
{
    public int CompareTo(Instant other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("O");

    public static Instant operator +(Instant instant, TimeSpan duration) =>
        new(instant.Value + duration);

    public static TimeSpan operator -(Instant left, Instant right) =>
        left.Value - right.Value;

    public static bool operator >(Instant left, Instant right) => left.Value > right.Value;
    public static bool operator <(Instant left, Instant right) => left.Value < right.Value;
    public static bool operator >=(Instant left, Instant right) => left.Value >= right.Value;
    public static bool operator <=(Instant left, Instant right) => left.Value <= right.Value;
}

/// <summary>Opaque workspace access token.</summary>
public readonly record struct WorkspaceToken(string Value)
{
    public override string ToString() => Value;
}
