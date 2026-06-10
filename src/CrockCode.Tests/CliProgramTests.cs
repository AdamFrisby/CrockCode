using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Cli;
using Spectre.Console;
using Xunit;

namespace CrockCode.Tests;

[Collection("CliTests")]
public class CliProgramTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _originalHome;
    private readonly MockDaemonServer _server;

    public CliProgramTests()
    {
        // 1. Redirect Home directory so tests don't touch user ~/.crockcode
        _tempHome = Path.Combine(Path.GetTempPath(), "crock_cli_home_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);

        _originalHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        Environment.SetEnvironmentVariable("HOME", _tempHome);

        // 2. Start mock HTTP Daemon Server
        _server = new MockDaemonServer();

        // 3. Write daemon.json with current PID to pretend daemon is running
        var crockHome = Path.Combine(_tempHome, ".crockcode");
        Directory.CreateDirectory(crockHome);
        var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
        var daemonInfo = new
        {
            controlPort = _server.Port,
            pid = Environment.ProcessId
        };
        File.WriteAllText(daemonJsonPath, JsonSerializer.Serialize(daemonInfo));
    }

    public void Dispose()
    {
        _server.Dispose();
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        try
        {
            if (Directory.Exists(_tempHome))
            {
                Directory.Delete(_tempHome, recursive: true);
            }
        }
        catch { }
    }

    private class MockDaemonServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _listenTask;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }
        public Func<HttpListenerRequest, HttpListenerResponse, Task<bool>>? Handler { get; set; }

        public MockDaemonServer()
        {
            _listener = new HttpListener();
            var random = new Random();
            for (int i = 0; i < 50; i++)
            {
                int p = random.Next(20000, 30000);
                try
                {
                    _listener.Prefixes.Add($"http://localhost:{p}/");
                    _listener.Start();
                    Port = p;
                    break;
                }
                catch
                {
                    _listener.Prefixes.Clear();
                }
            }

            _listenTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                bool handled = false;
                                if (Handler != null)
                                {
                                    handled = await Handler(ctx.Request, ctx.Response);
                                }
                                if (!handled)
                                {
                                    ctx.Response.StatusCode = 404;
                                    ctx.Response.Close();
                                }
                            }
                            catch
                            {
                                ctx.Response.StatusCode = 500;
                                ctx.Response.Close();
                            }
                        });
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }

    [Fact]
    public async Task Cli_Submit_Success_ReturnsZero()
    {
        _server.Handler = async (req, resp) =>
        {
            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/api/tasks")
            {
                resp.StatusCode = 200;
                var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { TaskId = "tsk_cli_test_123" }));
                resp.ContentType = "application/json";
                resp.ContentLength64 = resBytes.Length;
                await resp.OutputStream.WriteAsync(resBytes);
                resp.OutputStream.Close();
                return true;
            }
            return false;
        };

        using var sw = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw)
        });

        try
        {
            int exitCode = await Cli.Program.Main(new[] { "submit", "-p", "My Cli Test Prompt", "-d", "--no-daemon-autostart" });
            string output = sw.ToString();
            if (exitCode != 0)
            {
                throw new Exception($"CLI returned exit code {exitCode}. Output:\n{output}");
            }
            Assert.Contains("Task submitted successfully", output);
            Assert.Contains("tsk_cli_test_123", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Cli_Status_Success_ReturnsZero()
    {
        var taskId = "tsk_cli_test_456";
        _server.Handler = async (req, resp) =>
        {
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == $"/api/tasks/{taskId}")
            {
                var state = new WorkflowState.Queued(
                    new TaskId(taskId), new WorkingDir("/tmp"), "My Prompt", new Priority(3), 1, 3,
                    ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
                );
                
                resp.StatusCode = 200;
                var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, typeof(WorkflowState)));
                resp.ContentType = "application/json";
                resp.ContentLength64 = resBytes.Length;
                await resp.OutputStream.WriteAsync(resBytes);
                resp.OutputStream.Close();
                return true;
            }
            return false;
        };

        using var sw = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw)
        });

        try
        {
            int exitCode = await Cli.Program.Main(new[] { "status", taskId, "--no-daemon-autostart" });
            string output = sw.ToString();
            if (exitCode != 0)
            {
                throw new Exception($"CLI returned exit code {exitCode}. Output:\n{output}");
            }
            Assert.Contains("Status: Queued", output);
            Assert.Contains("Prompt: My Prompt", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Cli_Cancel_Success_ReturnsZero()
    {
        var taskId = "tsk_cli_test_789";
        _server.Handler = async (req, resp) =>
        {
            if (req.HttpMethod == "DELETE" && req.Url?.AbsolutePath == $"/api/tasks/{taskId}")
            {
                resp.StatusCode = 200;
                var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true }));
                resp.ContentType = "application/json";
                resp.ContentLength64 = resBytes.Length;
                await resp.OutputStream.WriteAsync(resBytes);
                resp.OutputStream.Close();
                return true;
            }
            return false;
        };

        using var sw = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw)
        });

        try
        {
            int exitCode = await Cli.Program.Main(new[] { "cancel", taskId, "--no-daemon-autostart" });
            string output = sw.ToString();
            if (exitCode != 0)
            {
                throw new Exception($"CLI returned exit code {exitCode}. Output:\n{output}");
            }
            Assert.Contains("cancelled successfully", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Cli_List_Success_ReturnsZero()
    {
        _server.Handler = async (req, resp) =>
        {
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/api/tasks")
            {
                var state1 = new WorkflowState.Queued(
                    new TaskId("tsk_list_1"), new WorkingDir("/tmp"), "Prompt 1", new Priority(3), 1, 3,
                    ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
                );
                var state2 = new WorkflowState.Completed(
                    new TaskId("tsk_list_2"), new WorkingDir("/tmp"), new ResultSummary("Complete 2"), new DiffStat(1, 0, 0), Usage.Zero
                );
                var list = ImmutableArray.Create<WorkflowState>(state1, state2);

                resp.StatusCode = 200;
                var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list, typeof(ImmutableArray<WorkflowState>)));
                resp.ContentType = "application/json";
                resp.ContentLength64 = resBytes.Length;
                await resp.OutputStream.WriteAsync(resBytes);
                resp.OutputStream.Close();
                return true;
            }
            return false;
        };

        using var sw = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw)
        });

        try
        {
            int exitCode = await Cli.Program.Main(new[] { "list", "--no-daemon-autostart" });
            string output = sw.ToString();
            if (exitCode != 0)
            {
                throw new Exception($"CLI returned exit code {exitCode}. Output:\n{output}");
            }
            Assert.Contains("tsk_list_1", output);
            Assert.Contains("tsk_list_2", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
