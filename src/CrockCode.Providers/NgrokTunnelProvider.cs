using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;

namespace CrockCode.Providers;

public sealed class NgrokTunnelProvider : ITunnelProvider
{
    private readonly HttpClient _httpClient;
    private Process? _process;
    private PublicEndpoint? _endpoint;

    public NgrokTunnelProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Result<PublicEndpoint>> StartAsync(int localPort, CancellationToken ct = default)
    {
        if (_endpoint != null)
        {
            return Result.Ok(_endpoint);
        }

        try
        {
            _process = new Process();
            _process.StartInfo.FileName = "ngrok";
            _process.StartInfo.Arguments = $"http {localPort}";
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.CreateNoWindow = true;

            _process.Start();

            // Wait for ngrok API to become available and tunnels to be created
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(TimeSpan.FromSeconds(15));

            string? publicUrl = null;
            while (!delayCts.Token.IsCancellationRequested)
            {
                try
                {
                    var response = await _httpClient.GetAsync("http://127.0.0.1:4040/api/tunnels", delayCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync(delayCts.Token);
                        using var doc = JsonDocument.Parse(jsonString);
                        if (doc.RootElement.TryGetProperty("tunnels", out var tunnelsElement) && 
                            tunnelsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tunnel in tunnelsElement.EnumerateArray())
                            {
                                if (tunnel.TryGetProperty("public_url", out var urlProp) && 
                                    urlProp.ValueKind == JsonValueKind.String)
                                {
                                    var url = urlProp.GetString();
                                    if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                    {
                                        publicUrl = url;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient exceptions before ngrok is up
                }

                if (publicUrl != null)
                {
                    break;
                }

                await Task.Delay(500, delayCts.Token);
            }

            if (publicUrl != null)
            {
                _endpoint = new PublicEndpoint(publicUrl);
                return Result.Ok(_endpoint);
            }
            else
            {
                StopTunnel();
                return new Result<PublicEndpoint>.Err(new Error.Permanent("TUNNEL_TIMEOUT", "Timed out waiting for ngrok tunnel URL. Is ngrok installed and configured?"));
            }
        }
        catch (Exception ex)
        {
            StopTunnel();
            return new Result<PublicEndpoint>.Err(new Error.Permanent("TUNNEL_START_FAILED", ex.Message));
        }
    }

    public async Task<Result<bool>> ProbeAsync(PublicEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{endpoint.Url}/health", ct);
            if (response.IsSuccessStatusCode)
            {
                return Result.Ok(true);
            }
            return new Result<bool>.Err(new Error.Permanent("PROBE_FAILED", $"Reachability probe failed: {response.StatusCode}"));
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("PROBE_FAILED", $"Reachability probe failed: {ex.Message}"));
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        StopTunnel();
        return Task.CompletedTask;
    }

    private void StopTunnel()
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }
            _process.Dispose();
            _process = null;
        }
        _endpoint = null;
    }
}
