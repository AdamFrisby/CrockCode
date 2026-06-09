using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Coordinator;

public sealed class StreamEventPublisher : IStreamEventPublisher
{
    private readonly ConcurrentDictionary<string, HttpResponse> _subscribers = new();

    public void Subscribe(string id, HttpResponse response)
    {
        _subscribers[id] = response;
    }

    public void Unsubscribe(string id)
    {
        _subscribers.TryRemove(id, out _);
    }

    public async Task<Result<Unit>> PublishAsync(StreamEnvelope envelope, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(envelope);
        string sseMessage = $"data: {json}\n\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sseMessage);

        foreach (var sub in _subscribers)
        {
            try
            {
                await sub.Value.Body.WriteAsync(bytes, ct);
                await sub.Value.Body.FlushAsync(ct);
            }
            catch
            {
                Unsubscribe(sub.Key);
            }
        }

        return Result.Ok(Unit.Value);
    }
}
