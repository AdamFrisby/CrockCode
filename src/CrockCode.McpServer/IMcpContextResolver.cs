using Microsoft.AspNetCore.Http;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.McpServer;

/// <summary>
/// Resolves the active task and workspace context from the request's authorization token and database.
/// </summary>
public interface IMcpContextResolver
{
    /// <summary>Resolves the context for the current HTTP request.</summary>
    Task<Result<WorkspaceContext>> ResolveContextAsync(HttpContext httpContext, CancellationToken ct = default);
}
