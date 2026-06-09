using System.Collections.Immutable;
using System.Text.Json;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Engine;

public sealed class WorkflowRunner
{
    private readonly IEventStore _eventStore;
    private readonly IProjectionStore _projectionStore;
    private readonly IClock _clock;
    private readonly IRandom _random;

    public WorkflowRunner(
        IEventStore eventStore,
        IProjectionStore projectionStore,
        IClock clock,
        IRandom random)
    {
        _eventStore = eventStore;
        _projectionStore = projectionStore;
        _clock = clock;
        _random = random;
    }

    public async Task<Result<Unit>> ProcessEventAsync(TaskId taskId, WorkflowEvent evt, CancellationToken ct = default)
    {
        var versionResult = await _eventStore.GetVersionAsync(taskId, ct);
        if (versionResult is Result<long>.Err versionErr)
        {
            return new Result<Unit>.Err(versionErr.Error);
        }

        long version = versionResult.Unwrap();

        WorkflowState currentState;
        if (version == 0)
        {
            if (evt is WorkflowEvent.Enqueued enqueued)
            {
                var initResult = WorkflowEngine.Initialize(enqueued);
                if (initResult is Result<WorkflowState>.Err initErr)
                {
                    return new Result<Unit>.Err(initErr.Error);
                }
                currentState = initResult.Unwrap();

                // Generate EmitStreamEvent command for Enqueued
                var bootstrapEmitCmd = new Command.EmitStreamEvent(
                    new StreamEnvelope(evt.GetType().Name, taskId, 1, JsonSerializer.SerializeToElement(evt)));

                // For the Enqueued event, it is the first event, so we write it to bootstrap
                var batch = new TransitionBatch(
                    taskId,
                    ExpectedVersion: 0,
                    NextState: currentState,
                    Events: ImmutableArray.Create(evt),
                    Commands: ImmutableArray.Create<Command>(bootstrapEmitCmd));

                var appendResult = await _eventStore.AppendAsync(batch, ct);
                return appendResult.Map(_ => Unit.Value);
            }
            else
            {
                return new Result<Unit>.Err(new Error.Permanent("TASK_NOT_FOUND", $"Task {taskId} does not exist"));
            }
        }

        // Rehydrate current state from event stream
        var eventsResult = await _eventStore.LoadEventsAsync(taskId, 0, ct);
        if (eventsResult is Result<ImmutableArray<WorkflowEvent>>.Err eventsErr)
        {
            return new Result<Unit>.Err(eventsErr.Error);
        }

        var events = eventsResult.Unwrap();
        // The first event should be Enqueued, which initializes the state
        if (events.Length == 0 || events[0] is not WorkflowEvent.Enqueued firstEnqueued)
        {
            return new Result<Unit>.Err(new Error.Permanent("CORRUPTED_STREAM", $"Task {taskId} event stream is corrupted"));
        }

        var initialStateResult = WorkflowEngine.Initialize(firstEnqueued);
        if (initialStateResult is Result<WorkflowState>.Err initialErr)
        {
            return new Result<Unit>.Err(initialErr.Error);
        }

        currentState = WorkflowEngine.Replay(initialStateResult.Unwrap(), events.Skip(1));

        // Prepare context
        var ctx = new EngineContext(_clock.Now, _random);

        // Decide
        var decideResult = WorkflowEngine.Decide(currentState, evt, ctx);
        if (decideResult is Result<TransitionResult>.Err decideErr)
        {
            return new Result<Unit>.Err(decideErr.Error);
        }

        var transition = decideResult.Unwrap();

        // Generate EmitStreamEvent command for this event
        var emitCmd = new Command.EmitStreamEvent(
            new StreamEnvelope(evt.GetType().Name, taskId, version + 1, JsonSerializer.SerializeToElement(evt)));

        var commandsList = transition.Commands.Add(emitCmd);

        // Commit transition batch
        var transitionBatch = new TransitionBatch(
            taskId,
            ExpectedVersion: version,
            NextState: transition.Next,
            Events: ImmutableArray.Create(evt),
            Commands: commandsList);

        var appendRes = await _eventStore.AppendAsync(transitionBatch, ct);
        if (appendRes is Result<long>.Err err)
        {
            return new Result<Unit>.Err(err.Error);
        }

        // ── Subagent / Await Post-Commit Triggers ──
        
        // 1. If this task transitioned to a terminal state, notify parent (if any)
        if (transition.Next.IsTerminal)
        {
            var parentRes = await _projectionStore.GetParentTaskIdAsync(taskId, ct);
            if (parentRes is Result<TaskId?>.Ok ok && ok.Value is TaskId parentId)
            {
                ResultSummary summary = new ResultSummary("");
                if (transition.Next is WorkflowState.Completed comp)
                {
                    summary = comp.ResultSummary;
                }
                else if (transition.Next is WorkflowState.Failed fail)
                {
                    summary = new ResultSummary("Failed: " + fail.Reason.Match(t => t.Detail, p => p.Detail));
                }
                else if (transition.Next is WorkflowState.Cancelled)
                {
                    summary = new ResultSummary("Cancelled");
                }

                var childSettled = new WorkflowEvent.ChildSettled(parentId, taskId, summary, _clock.Now);
                await ProcessEventAsync(parentId, childSettled, ct);
            }
        }

        // 2. If parent received ChildSettled, check if all awaited children are terminal
        if (evt is WorkflowEvent.ChildSettled childSettledEvt)
        {
            var parentStateRes = await _projectionStore.LoadAsync(childSettledEvt.ParentId, ct);
            if (parentStateRes is Result<WorkflowState?>.Ok pStateOk && pStateOk.Value is WorkflowState.Suspended suspended)
            {
                if (suspended.AwaitSpec is AwaitSpec.AwaitTasksSpec tasksSpec)
                {
                    bool allTerminal = true;
                    var childResults = new List<ChildResult>();

                    foreach (var childId in tasksSpec.TaskIds)
                    {
                        var childStateRes = await _projectionStore.LoadAsync(childId, ct);
                        if (childStateRes is Result<WorkflowState?>.Ok cStateOk && cStateOk.Value != null)
                        {
                            var childState = cStateOk.Value;
                            if (!childState.IsTerminal)
                            {
                                allTerminal = false;
                                break;
                            }
                            else
                            {
                                ResultSummary cSummary = new ResultSummary("");
                                if (childState is WorkflowState.Completed comp) cSummary = comp.ResultSummary;
                                else if (childState is WorkflowState.Failed fail) cSummary = new ResultSummary("Failed: " + fail.Reason.Match(t => t.Detail, p => p.Detail));
                                else if (childState is WorkflowState.Cancelled) cSummary = new ResultSummary("Cancelled");
                                childResults.Add(new ChildResult(childId, cSummary));
                            }
                        }
                        else
                        {
                            allTerminal = false;
                            break;
                        }
                    }

                    if (allTerminal)
                    {
                        var awaitResolved = new WorkflowEvent.AwaitResolved(
                            childSettledEvt.ParentId,
                            childResults.ToImmutableArray(),
                            _clock.Now
                        );
                        await ProcessEventAsync(childSettledEvt.ParentId, awaitResolved, ct);
                    }
                }
            }
        }

        return Result.Ok(Unit.Value);
    }
}
