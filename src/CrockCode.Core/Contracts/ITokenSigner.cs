using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Signs and verifies workspace access tokens for secure MCP authentication.
/// </summary>
public interface ITokenSigner
{
    /// <summary>Sign a workspace token with claims for the given context.</summary>
    Result<WorkspaceToken> Sign(WorkspaceContext context, TimeSpan validity);

    /// <summary>Verify a workspace token and extract the context.</summary>
    Result<WorkspaceContext> Verify(WorkspaceToken token);
}
