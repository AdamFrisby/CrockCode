using Microsoft.AspNetCore.Http;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.McpServer;

public sealed class McpContextResolver : IMcpContextResolver
{
    private readonly ITokenSigner _tokenSigner;
    private readonly IProjectionStore _projectionStore;

    public McpContextResolver(ITokenSigner tokenSigner, IProjectionStore projectionStore)
    {
        _tokenSigner = tokenSigner;
        _projectionStore = projectionStore;
    }

    public async Task<Result<WorkspaceContext>> ResolveContextAsync(HttpContext httpContext, CancellationToken ct = default)
    {
        string authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new Result<WorkspaceContext>.Err(new Error.Permanent("UNAUTHORIZED", "Missing or invalid Authorization header"));
        }

        string tokenStr = authHeader["Bearer ".Length..].Trim();
        var token = new WorkspaceToken(tokenStr);

        var verifyResult = _tokenSigner.Verify(token);
        if (verifyResult is Result<WorkspaceContext>.Err err)
        {
            return err;
        }

        var verifiedCtx = verifyResult.Unwrap();

        // Check if there is an active running task assignment for this worker in the database
        var taskResult = await _projectionStore.LoadByWorkerAsync(verifiedCtx.WorkerId, ct);
        if (taskResult is Result<WorkflowState?>.Ok ok && ok.Value != null)
        {
            var state = ok.Value;
            return Result.Ok(new WorkspaceContext(state.TaskId, state.WorkingDir, verifiedCtx.WorkerId));
        }

        // Otherwise return the context verified from the token directly (used during get_task)
        return Result.Ok(verifiedCtx);
    }
}
