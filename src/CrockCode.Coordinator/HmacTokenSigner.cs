using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Coordinator;

public sealed class HmacTokenSigner : ITokenSigner
{
    private readonly byte[] _key;

    public HmacTokenSigner()
    {
        // Generates a random signing key for the session.
        // Because workers check in within the lifetime of the daemon execution,
        // a transient key is completely sufficient and highly secure.
        _key = RandomNumberGenerator.GetBytes(32);
    }

    public Result<WorkspaceToken> Sign(WorkspaceContext context, TimeSpan validity)
    {
        try
        {
            var payload = new TokenPayload(
                context.TaskId.Value,
                context.WorkingDir.Value,
                context.WorkerId.Value,
                DateTimeOffset.UtcNow.Add(validity)
            );
            string json = JsonSerializer.Serialize(payload);
            string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            using var hmac = new HMACSHA256(_key);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Json));
            string base64Sig = Convert.ToBase64String(hash);

            return Result.Ok(new WorkspaceToken($"{base64Json}.{base64Sig}"));
        }
        catch (Exception ex)
        {
            return new Result<WorkspaceToken>.Err(new Error.Permanent("SIGN_FAILED", ex.Message));
        }
    }

    public Result<WorkspaceContext> Verify(WorkspaceToken token)
    {
        try
        {
            string[] parts = token.Value.Split('.');
            if (parts.Length != 2)
            {
                return new Result<WorkspaceContext>.Err(new Error.Permanent("INVALID_TOKEN", "Token format is invalid"));
            }

            string base64Json = parts[0];
            string base64Sig = parts[1];

            using var hmac = new HMACSHA256(_key);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Json));
            string computedSig = Convert.ToBase64String(hash);

            if (base64Sig != computedSig)
            {
                return new Result<WorkspaceContext>.Err(new Error.Permanent("INVALID_SIGNATURE", "Token signature is invalid"));
            }

            string json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Json));
            var payload = JsonSerializer.Deserialize<TokenPayload>(json);

            if (payload == null)
            {
                return new Result<WorkspaceContext>.Err(new Error.Permanent("INVALID_PAYLOAD", "Token payload is corrupted"));
            }

            if (payload.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return new Result<WorkspaceContext>.Err(new Error.Permanent("TOKEN_EXPIRED", "Token has expired"));
            }

            return Result.Ok(new WorkspaceContext(
                new TaskId(payload.TaskId),
                new WorkingDir(payload.WorkingDir),
                new WorkerId(payload.WorkerId)
            ));
        }
        catch (Exception ex)
        {
            return new Result<WorkspaceContext>.Err(new Error.Permanent("VERIFY_FAILED", ex.Message));
        }
    }

    private record TokenPayload(string TaskId, string WorkingDir, string WorkerId, DateTimeOffset ExpiresAt);
}
