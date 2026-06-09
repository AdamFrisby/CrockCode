using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;

namespace CrockCode.Providers;

public sealed class CloudflaredTunnelProvider : ITunnelProvider
{
    private readonly HttpClient _httpClient;
    private Process? _process;
    private PublicEndpoint? _endpoint;

    public CloudflaredTunnelProvider(HttpClient httpClient)
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
            var tcs = new TaskCompletionSource<string>();

            _process = new Process();
            _process.StartInfo.FileName = "cloudflared";
            _process.StartInfo.Arguments = $"tunnel --url http://127.0.0.1:{localPort}";
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.CreateNoWindow = true;

            var urlRegex = new Regex(@"https://[a-zA-Z0-9\-]+\.trycloudflare\.com", RegexOptions.Compiled);

            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    var match = urlRegex.Match(e.Data);
                    if (match.Success)
                    {
                        tcs.TrySetResult(match.Value);
                    }
                }
            };

            _process.Start();
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(TimeSpan.FromSeconds(15));

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, delayCts.Token));
            if (completedTask == tcs.Task)
            {
                string publicUrl = await tcs.Task;
                _endpoint = new PublicEndpoint(publicUrl);
                return Result.Ok(_endpoint);
            }
            else
            {
                // Timeout or cancelled
                StopTunnel();
                return new Result<PublicEndpoint>.Err(new Error.Permanent("TUNNEL_TIMEOUT", "Timed out waiting for Cloudflare tunnel URL. Is cloudflared installed?"));
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
