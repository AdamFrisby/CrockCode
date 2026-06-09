using System;
using System.Text.Json.Serialization;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Workflow;

/// <summary>
/// Closed union representing side-effecting commands emitted by the workflow engine.
/// These are processed by the infrastructure layer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SubmitWorker), "submit_worker")]
[JsonDerivedType(typeof(EmitStreamEvent), "emit_stream_event")]
[JsonDerivedType(typeof(ScheduleRetry), "schedule_retry")]
[JsonDerivedType(typeof(Requeue), "requeue")]
[JsonDerivedType(typeof(ReleaseLease), "release_lease")]
[JsonDerivedType(typeof(CancelWorker), "cancel_worker")]
public abstract record Command
{
    private Command() { }

    public abstract T Match<T>(
        Func<SubmitWorker, T> submitWorker,
        Func<EmitStreamEvent, T> emitStreamEvent,
        Func<ScheduleRetry, T> scheduleRetry,
        Func<Requeue, T> requeue,
        Func<ReleaseLease, T> releaseLease,
        Func<CancelWorker, T> cancelWorker);

    /// <summary>Command to submit a new worker to the batch provider.</summary>
    public sealed record SubmitWorker(WorkerId IdemKey, WorkerSpec Spec) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => submitWorker(this);
    }

    /// <summary>Command to emit a stream event to the event bus.</summary>
    public sealed record EmitStreamEvent(StreamEnvelope Envelope) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => emitStreamEvent(this);
    }

    /// <summary>Command to schedule a retry attempt after a delay.</summary>
    public sealed record ScheduleRetry(TaskId TaskId, Instant DueAt, int Attempt) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => scheduleRetry(this);
    }

    /// <summary>Command to requeue a task.</summary>
    public sealed record Requeue(TaskId TaskId) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => requeue(this);
    }

    /// <summary>Command to release a directory lease.</summary>
    public sealed record ReleaseLease(WorkingDir WorkingDir, TaskId TaskId) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => releaseLease(this);
    }

    /// <summary>Command to cancel a worker batch.</summary>
    public sealed record CancelWorker(WorkerId WorkerId) : Command
    {
        public override T Match<T>(Func<SubmitWorker, T> submitWorker, Func<EmitStreamEvent, T> emitStreamEvent,
            Func<ScheduleRetry, T> scheduleRetry, Func<Requeue, T> requeue,
            Func<ReleaseLease, T> releaseLease, Func<CancelWorker, T> cancelWorker)
            => cancelWorker(this);
    }
}
