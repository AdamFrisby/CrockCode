using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Resolves and prepares workspaces for task execution.
/// Handles git branching, worktree creation, etc.
/// </summary>
public interface IWorkspaceResolver
{
    /// <summary>Prepare a workspace for a task, returning the resolved working directory.</summary>
    Task<Result<WorkingDir>> PrepareAsync(
        TaskId taskId, WorkingDir requestedDir, CancellationToken ct = default);

    /// <summary>Clean up a workspace after task completion.</summary>
    Task<Result<bool>> CleanupAsync(
        TaskId taskId, WorkingDir workingDir, CancellationToken ct = default);

    /// <summary>Compute diff statistics for a workspace.</summary>
    Task<Result<DiffStat>> GetDiffStatAsync(
        WorkingDir workingDir, CancellationToken ct = default);
}
