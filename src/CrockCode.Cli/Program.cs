using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new System.CommandLine.RootCommand("CrockCode - Slow, cheap coding agent queue client");

        var promptOption = new System.CommandLine.Option<string>("--prompt", new[] { "-p" })
        {
            Description = "The task prompt/instruction"
        };
        var cwdOption = new System.CommandLine.Option<string>("--cwd", new[] { "-C" })
        {
            Description = "The working directory for the task"
        };
        var priorityOption = new System.CommandLine.Option<int>("--priority", Array.Empty<string>())
        {
            Description = "Task execution priority (higher is prioritized)",
            DefaultValueFactory = _ => 0
        };
        var maxAttemptsOption = new System.CommandLine.Option<int>("--max-attempts", Array.Empty<string>())
        {
            Description = "Maximum retry attempts on failure",
            DefaultValueFactory = _ => 3
        };
        var detachOption = new System.CommandLine.Option<bool>("--detach", new[] { "-d" })
        {
            Description = "Submit the task and detach immediately without following progress"
        };
        var noAutostartOption = new System.CommandLine.Option<bool>("--no-daemon-autostart", Array.Empty<string>())
        {
            Description = "Do not automatically start the daemon if it is not running"
        };
        var allowedToolsOption = new System.CommandLine.Option<string>("--allowed-tools", Array.Empty<string>())
        {
            Description = "Comma-separated list of allowed tools"
        };
        var disallowedToolsOption = new System.CommandLine.Option<string>("--disallowed-tools", Array.Empty<string>())
        {
            Description = "Comma-separated list of disallowed tools"
        };

        rootCommand.Add(promptOption);
        rootCommand.Add(cwdOption);
        rootCommand.Add(priorityOption);
        rootCommand.Add(maxAttemptsOption);
        rootCommand.Add(detachOption);
        rootCommand.Add(noAutostartOption);
        rootCommand.Add(allowedToolsOption);
        rootCommand.Add(disallowedToolsOption);

        // 1. Submit Command
        var submitCommand = new System.CommandLine.Command("submit", "Enqueue a new task for processing");
        submitCommand.Add(promptOption);
        submitCommand.Add(cwdOption);
        submitCommand.Add(priorityOption);
        submitCommand.Add(maxAttemptsOption);
        submitCommand.Add(detachOption);
        submitCommand.Add(noAutostartOption);
        submitCommand.Add(allowedToolsOption);
        submitCommand.Add(disallowedToolsOption);

        submitCommand.SetAction(async (parseResult) =>
        {
            var prompt = parseResult.GetValue(promptOption);
            var cwd = parseResult.GetValue(cwdOption);
            var priority = parseResult.GetValue(priorityOption);
            var maxAttempts = parseResult.GetValue(maxAttemptsOption);
            var detach = parseResult.GetValue(detachOption);
            var noAutostart = parseResult.GetValue(noAutostartOption);
            var allowedToolsRaw = parseResult.GetValue(allowedToolsOption);
            var disallowedToolsRaw = parseResult.GetValue(disallowedToolsOption);

            var allowedTools = string.IsNullOrEmpty(allowedToolsRaw) 
                ? ImmutableArray<string>.Empty 
                : allowedToolsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
            var disallowedTools = string.IsNullOrEmpty(disallowedToolsRaw) 
                ? ImmutableArray<string>.Empty 
                : disallowedToolsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();

            await HandleSubmitAsync(prompt, cwd, priority, maxAttempts, detach, noAutostart, allowedTools, disallowedTools);
        });

        rootCommand.Add(submitCommand);

        // 2. Follow Command
        var taskIdArg = new System.CommandLine.Argument<string>("task-id")
        {
            Description = "The ID of the task"
        };
        var followCommand = new System.CommandLine.Command("follow", "Follow a task's real-time progress stream");
        followCommand.Add(taskIdArg);
        followCommand.Add(noAutostartOption);

        followCommand.SetAction(async (parseResult) =>
        {
            var taskId = parseResult.GetValue(taskIdArg);
            var noAutostart = parseResult.GetValue(noAutostartOption);
            if (taskId != null)
            {
                await HandleFollowAsync(taskId, noAutostart);
            }
        });

        rootCommand.Add(followCommand);

        // 3. Status Command
        var statusCommand = new System.CommandLine.Command("status", "Query the current state of a task");
        statusCommand.Add(taskIdArg);
        statusCommand.Add(noAutostartOption);

        statusCommand.SetAction(async (parseResult) =>
        {
            var taskId = parseResult.GetValue(taskIdArg);
            var noAutostart = parseResult.GetValue(noAutostartOption);
            if (taskId != null)
            {
                await HandleStatusAsync(taskId, noAutostart);
            }
        });

        rootCommand.Add(statusCommand);

        // 4. Cancel Command
        var cancelCommand = new System.CommandLine.Command("cancel", "Cancel a pending or running task");
        cancelCommand.Add(taskIdArg);
        cancelCommand.Add(noAutostartOption);

        cancelCommand.SetAction(async (parseResult) =>
        {
            var taskId = parseResult.GetValue(taskIdArg);
            var noAutostart = parseResult.GetValue(noAutostartOption);
            if (taskId != null)
            {
                await HandleCancelAsync(taskId, noAutostart);
            }
        });

        rootCommand.Add(cancelCommand);

        // 5. List Command
        var listCommand = new System.CommandLine.Command("list", "List all tasks in the system");
        listCommand.Add(noAutostartOption);

        listCommand.SetAction(async (parseResult) =>
        {
            var noAutostart = parseResult.GetValue(noAutostartOption);
            await HandleListAsync(noAutostart);
        });

        rootCommand.Add(listCommand);

        // 6. Daemon Command
        var daemonCommand = new System.CommandLine.Command("daemon", "Manage the coordinator daemon process");
        var daemonStartCommand = new System.CommandLine.Command("start", "Start the coordinator daemon in the background");
        
        daemonStartCommand.SetAction(async (parseResult) =>
        {
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            Directory.CreateDirectory(crockHome);
            
            AnsiConsole.MarkupLine("[yellow]Starting daemon...[/]");
            try
            {
                var config = CrockConfig.Load();
                await StartDaemonProcessAsync(crockHome, config.LocalPort);
                AnsiConsole.MarkupLine("[green]Daemon started successfully.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error starting daemon: {ex.Message}[/]");
            }
        });

        var daemonStopCommand = new System.CommandLine.Command("stop", "Stop the running coordinator daemon");
        daemonStopCommand.SetAction(parseResult =>
        {
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            StopDaemonIfRunning(crockHome);
        });

        var daemonRestartCommand = new System.CommandLine.Command("restart", "Restart the coordinator daemon");
        daemonRestartCommand.SetAction(async parseResult =>
        {
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            StopDaemonIfRunning(crockHome);
            AnsiConsole.MarkupLine("[yellow]Starting daemon...[/]");
            try
            {
                var config = CrockConfig.Load();
                await StartDaemonProcessAsync(crockHome, config.LocalPort);
                AnsiConsole.MarkupLine("[green]Daemon started successfully.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error starting daemon: {ex.Message}[/]");
            }
        });

        var daemonStatusCommand = new System.CommandLine.Command("status", "Show status of the coordinator daemon");
        daemonStatusCommand.SetAction(parseResult =>
        {
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
            bool running = false;
            int pid = 0;
            int port = 5000;
            if (File.Exists(daemonJsonPath))
            {
                try
                {
                    var json = File.ReadAllText(daemonJsonPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("pid", out var pidProp)) pid = pidProp.GetInt32();
                    if (doc.RootElement.TryGetProperty("controlPort", out var portProp)) port = portProp.GetInt32();
                    var process = System.Diagnostics.Process.GetProcessById(pid);
                    if (!process.HasExited) running = true;
                }
                catch {}
            }
            if (running)
            {
                AnsiConsole.MarkupLine($"[green]Daemon is running (PID: {pid}, Control Port: {port})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Daemon is NOT running.[/]");
            }
        });

        var daemonLogsCommand = new System.CommandLine.Command("logs", "Show coordinator daemon logs");
        daemonLogsCommand.SetAction(async parseResult =>
        {
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            var logPath = Path.Combine(crockHome, "daemon.log");
            if (File.Exists(logPath))
            {
                AnsiConsole.MarkupLine($"[blue]Log file path: {logPath}[/]");
                var lines = await File.ReadAllLinesAsync(logPath);
                int count = Math.Min(lines.Length, 50);
                for (int i = lines.Length - count; i < lines.Length; i++)
                {
                    Console.WriteLine(lines[i]);
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No log file found at ~/.crockcode/daemon.log[/]");
            }
        });

        daemonCommand.Add(daemonStartCommand);
        daemonCommand.Add(daemonStopCommand);
        daemonCommand.Add(daemonRestartCommand);
        daemonCommand.Add(daemonStatusCommand);
        daemonCommand.Add(daemonLogsCommand);
        rootCommand.Add(daemonCommand);

        // 7. Setup Command
        var setupCommand = new System.CommandLine.Command("setup", "Configure CrockCode settings and start daemon");
        setupCommand.SetAction(async parseResult =>
        {
            AnsiConsole.MarkupLine("[bold blue]=== CrockCode Setup Wizard ===[/]");
            
            var apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter your [green]Anthropic API Key[/]:")
                    .PromptStyle("yellow")
                    .Secret());

            var provider = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose your [green]Tunnel / Ingress Provider[/]:")
                    .PageSize(10)
                    .AddChoices(new[] { "cloudflared", "ngrok", "manual" }));

            var publicUrl = "";
            if (provider == "manual")
            {
                publicUrl = AnsiConsole.Prompt(
                    new TextPrompt<string>("Enter your [green]Stable Public HTTPS URL[/]:")
                        .DefaultValue("https://your-domain.com")
                        .PromptStyle("yellow"));
            }

            var maxConcurrency = AnsiConsole.Prompt(
                new TextPrompt<int>("Enter [green]Max Concurrency[/] (Pool capacity):")
                    .DefaultValue(4)
                    .PromptStyle("yellow"));

            var model = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]Default Claude Model[/]:")
                    .DefaultValue("claude-3-5-sonnet-20241022")
                    .PromptStyle("yellow"));

            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            Directory.CreateDirectory(crockHome);
            var configPath = Path.Combine(crockHome, "config.json");

            var configObj = new
            {
                anthropic_api_key = apiKey,
                tunnel_provider = provider,
                mcp_public_url = publicUrl,
                max_concurrency = maxConcurrency,
                model = model
            };

            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true }));
            AnsiConsole.MarkupLine($"[green]Configuration saved to: {configPath}[/]");

            AnsiConsole.MarkupLine("[blue]Starting/restarting daemon to apply new configuration...[/]");
            try
            {
                StopDaemonIfRunning(crockHome);
                await StartDaemonProcessAsync(crockHome, 5000);
                AnsiConsole.MarkupLine("[green]Daemon started successfully.[/]");
                
                // Perform reachability probe
                AnsiConsole.MarkupLine("[blue]Testing public tunnel reachability...[/]");
                var channel = await GetControlChannelAsync(false);
                var urlRes = await channel.GetTunnelUrlAsync();
                if (urlRes.IsOk)
                {
                    AnsiConsole.MarkupLine($"[blue]Public MCP URL: {urlRes.Unwrap()}[/]");
                    AnsiConsole.MarkupLine("[yellow]Probing tunnel (external round-trip check)...[/]");
                    var probeRes = await channel.ProbeTunnelAsync();
                    if (probeRes.IsOk && probeRes.Unwrap())
                    {
                        AnsiConsole.MarkupLine("[bold green]SUCCESS: Public endpoint is fully reachable![/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[bold red]FAILED: Public endpoint is NOT reachable from the internet. Please check your tunnel provider logs.[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to get tunnel URL: {urlRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error starting daemon: {ex.Message}[/]");
            }
        });
        rootCommand.Add(setupCommand);

        // 8. Doctor Command
        var doctorCommand = new System.CommandLine.Command("doctor", "Perform diagnostic checks on settings and connection");
        doctorCommand.SetAction(async parseResult =>
        {
            bool hasErrors = false;
            AnsiConsole.MarkupLine("[bold blue]=== CrockCode Doctor Diagnostic ===[/]");

            // Check 1: Home directory
            var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
            if (Directory.Exists(crockHome))
            {
                AnsiConsole.MarkupLine("[green]✔[/] Home directory: ~/.crockcode exists");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✘[/] Home directory: ~/.crockcode does NOT exist");
                hasErrors = true;
            }

            // Check 2: Configuration
            var config = CrockConfig.Load();
            var configPath = Path.Combine(crockHome, "config.json");
            if (File.Exists(configPath))
            {
                AnsiConsole.MarkupLine("[green]✔[/] Configuration file: found");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] Configuration file: NOT found (using defaults)");
            }

            // Check 3: Anthropic API Key
            if (!string.IsNullOrEmpty(config.AnthropicApiKey))
            {
                AnsiConsole.MarkupLine("[green]✔[/] Anthropic API Key: configured");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✘[/] Anthropic API Key: NOT configured (set ANTHROPIC_API_KEY or run 'crock setup')");
                hasErrors = true;
            }

            // Check 4: Daemon status and local health
            var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
            bool daemonRunning = false;
            if (File.Exists(daemonJsonPath))
            {
                try
                {
                    var json = File.ReadAllText(daemonJsonPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("pid", out var pidProp))
                    {
                        int pid = pidProp.GetInt32();
                        var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (!process.HasExited)
                        {
                            daemonRunning = true;
                        }
                    }
                }
                catch { }
            }

            if (daemonRunning)
            {
                AnsiConsole.MarkupLine("[green]✔[/] Daemon process: running");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] Daemon process: NOT running (attempting autostart...)");
                try
                {
                    await StartDaemonProcessAsync(crockHome, config.LocalPort);
                    AnsiConsole.MarkupLine("[green]✔[/] Daemon process: started successfully");
                    daemonRunning = true;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘[/] Daemon process: failed to start: {ex.Message}");
                    hasErrors = true;
                }
            }

            if (daemonRunning)
            {
                // Probe local health
                using var client = new HttpClient();
                try
                {
                    var res = await client.GetAsync($"http://localhost:{config.LocalPort}/health");
                    if (res.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine("[green]✔[/] Local daemon health endpoint: responsive");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✘[/] Local daemon health endpoint: returned {res.StatusCode}");
                        hasErrors = true;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘[/] Local daemon health endpoint: unreachable: {ex.Message}");
                    hasErrors = true;
                }

                // Check 5: Tunnel & public URL reachability
                try
                {
                    var channel = new HttpControlChannel(new HttpClient { BaseAddress = new Uri($"http://localhost:{config.LocalPort}") });
                    AnsiConsole.MarkupLine("[blue]Retrieving public tunnel URL...[/]");
                    var urlRes = await channel.GetTunnelUrlAsync();
                    if (urlRes.IsOk)
                    {
                        var publicUrl = urlRes.Unwrap();
                        AnsiConsole.MarkupLine($"[green]✔[/] Public MCP URL: {publicUrl}");
                        
                        AnsiConsole.MarkupLine("[blue]Probing public tunnel reachability (external round-trip)...[/]");
                        var probeRes = await channel.ProbeTunnelAsync();
                        if (probeRes.IsOk && probeRes.Unwrap())
                        {
                            AnsiConsole.MarkupLine("[green]✔[/] Public tunnel reachability: fully reachable");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]✘[/] Public tunnel reachability: NOT reachable from the internet");
                            hasErrors = true;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✘[/] Public MCP URL: failed to retrieve: {urlRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}");
                        hasErrors = true;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘[/] Tunnel diagnostic failed: {ex.Message}");
                    hasErrors = true;
                }
            }

            if (hasErrors)
            {
                AnsiConsole.MarkupLine("\n[bold red]Doctor found critical errors. Please verify your settings and infrastructure.[/]");
                Environment.Exit(1);
            }
            else
            {
                AnsiConsole.MarkupLine("\n[bold green]CrockCode is healthy and ready![/]");
            }
        });
        rootCommand.Add(doctorCommand);

        // 9. Tunnel Command
        var tunnelCommand = new System.CommandLine.Command("tunnel", "Manage/inspect the public MCP tunnel");
        var tunnelStatusCommand = new System.CommandLine.Command("status", "Query current tunnel status and check reachability");
        tunnelStatusCommand.SetAction(async parseResult =>
        {
            var config = CrockConfig.Load();
            try
            {
                var channel = await GetControlChannelAsync(false);
                var urlRes = await channel.GetTunnelUrlAsync();
                if (urlRes.IsOk)
                {
                    var url = urlRes.Unwrap();
                    AnsiConsole.MarkupLine($"[blue]Tunnel Provider:[/] {config.TunnelProvider}");
                    AnsiConsole.MarkupLine($"[blue]Public MCP URL:[/] {url}");
                    AnsiConsole.MarkupLine("[yellow]Checking reachability...[/]");
                    var probeRes = await channel.ProbeTunnelAsync();
                    if (probeRes.IsOk && probeRes.Unwrap())
                    {
                        AnsiConsole.MarkupLine("[green]Status: Reachable[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]Status: Unreachable[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to get tunnel URL: {urlRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }
        });
        
        var tunnelUrlCommand = new System.CommandLine.Command("url", "Print only the public tunnel URL");
        tunnelUrlCommand.SetAction(async parseResult =>
        {
            try
            {
                var channel = await GetControlChannelAsync(false);
                var urlRes = await channel.GetTunnelUrlAsync();
                if (urlRes.IsOk)
                {
                    Console.WriteLine(urlRes.Unwrap());
                }
                else
                {
                    Console.Error.WriteLine(urlRes.UnwrapErr().Match(t => t.Detail, p => p.Detail));
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(1);
            }
        });

        tunnelCommand.Add(tunnelStatusCommand);
        tunnelCommand.Add(tunnelUrlCommand);
        
        tunnelCommand.SetAction(async parseResult =>
        {
            var config = CrockConfig.Load();
            try
            {
                var channel = await GetControlChannelAsync(false);
                var urlRes = await channel.GetTunnelUrlAsync();
                if (urlRes.IsOk)
                {
                    var url = urlRes.Unwrap();
                    AnsiConsole.MarkupLine($"[blue]Tunnel Provider:[/] {config.TunnelProvider}");
                    AnsiConsole.MarkupLine($"[blue]Public MCP URL:[/] {url}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to get tunnel: {urlRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }
        });
        rootCommand.Add(tunnelCommand);

        // Root command fallback handler
        rootCommand.SetAction(async (parseResult) =>
        {
            var prompt = parseResult.GetValue(promptOption);
            if (!string.IsNullOrEmpty(prompt))
            {
                var cwd = parseResult.GetValue(cwdOption);
                var priority = parseResult.GetValue(priorityOption);
                var maxAttempts = parseResult.GetValue(maxAttemptsOption);
                var detach = parseResult.GetValue(detachOption);
                var noAutostart = parseResult.GetValue(noAutostartOption);
                var allowedToolsRaw = parseResult.GetValue(allowedToolsOption);
                var disallowedToolsRaw = parseResult.GetValue(disallowedToolsOption);

                var allowedTools = string.IsNullOrEmpty(allowedToolsRaw) 
                    ? ImmutableArray<string>.Empty 
                    : allowedToolsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
                var disallowedTools = string.IsNullOrEmpty(disallowedToolsRaw) 
                    ? ImmutableArray<string>.Empty 
                    : disallowedToolsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();

                await HandleSubmitAsync(prompt, cwd, priority, maxAttempts, detach, noAutostart, allowedTools, disallowedTools);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No prompt specified. Use -p/--prompt to submit a task, or run 'crock --help' to see commands.[/]");
            }
        });

        var pResult = System.CommandLine.Parsing.CommandLineParser.Parse(rootCommand, args);
        return await pResult.InvokeAsync();
    }

    private static async Task HandleSubmitAsync(
        string? prompt, string? cwd, int priority, int maxAttempts, bool detach, bool noAutostart,
        ImmutableArray<string> allowedTools, ImmutableArray<string> disallowedTools)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnsiConsole.MarkupLine("[red]Error: Prompt cannot be empty.[/]");
            return;
        }

        var resolvedCwd = string.IsNullOrEmpty(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        
        try
        {
            var channel = await GetControlChannelAsync(noAutostart);
            AnsiConsole.MarkupLine($"[blue]Submitting task to daemon...[/]");
            
            var result = await channel.EnqueueTaskAsync(
                new WorkingDir(resolvedCwd),
                prompt,
                new Priority(priority),
                maxAttempts,
                allowedTools,
                disallowedTools,
                parentId: null,
                ct: CancellationToken.None);
                
            if (result is Result<TaskId>.Ok ok)
            {
                var taskId = ok.Value.Value;
                AnsiConsole.MarkupLine($"[green]Task submitted successfully. Task ID: [bold]{taskId}[/][/]");
                
                if (detach)
                {
                    AnsiConsole.MarkupLine($"[yellow]Detached. Run 'crock follow {taskId}' to monitor progress.[/]");
                    return;
                }
                
                await HandleFollowAsync(taskId, noAutostart);
            }
            else
            {
                var err = result.UnwrapErr();
                AnsiConsole.MarkupLine($"[red]Failed to submit task: {err.Match(t => t.Detail, p => p.Detail)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }
    }

    private static async Task HandleFollowAsync(string taskId, bool noAutostart)
    {
        try
        {
            var channel = await GetControlChannelAsync(noAutostart);
            
            using var cts = new CancellationTokenSource();
            bool ctrlCPressedOnce = false;
            
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                if (!ctrlCPressedOnce)
                {
                    ctrlCPressedOnce = true;
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl-C detected. Detaching view... (Task is still running in the background)[/]");
                    AnsiConsole.MarkupLine($"[yellow]To re-attach, run: crock follow {taskId}[/]");
                    cts.Cancel();
                }
                else
                {
                    AnsiConsole.MarkupLine("\n[red]Double Ctrl-C detected. Cancelling task...[/]");
                    Task.Run(async () =>
                    {
                        var cancelRes = await channel.CancelTaskAsync(new TaskId(taskId));
                        if (cancelRes.IsOk)
                        {
                            AnsiConsole.MarkupLine("[red]Task cancelled successfully.[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]Failed to cancel task: {cancelRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                        }
                        Environment.Exit(0);
                    });
                }
            };

            AnsiConsole.MarkupLine($"[blue]Tailing events for Task ID: {taskId} (Press Ctrl-C to detach, double Ctrl-C to cancel)[/]");

            // Check if task is already terminal
            var stateRes = await channel.GetTaskStateAsync(new TaskId(taskId), cts.Token);
            if (stateRes.IsOk && stateRes.Unwrap().IsTerminal)
            {
                RenderTerminalState(stateRes.Unwrap());
                return;
            }

            await foreach (var envelope in channel.FollowStreamAsync(cts.Token))
            {
                if (envelope.TaskId.Value != taskId)
                {
                    continue;
                }

                RenderEnvelope(envelope);

                if (envelope.Type == "WorkerSettled" || envelope.Type == "PermanentFailed" || envelope.Type == "TransientFailed" || envelope.Type == "WorkerExpired")
                {
                    var checkRes = await channel.GetTaskStateAsync(new TaskId(taskId), cts.Token);
                    if (checkRes.IsOk && checkRes.Unwrap().IsTerminal)
                    {
                        RenderTerminalState(checkRes.Unwrap());
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful exit from Ctrl-C Cancellation
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error following stream: {ex.Message}[/]");
        }
    }

    private static void RenderTerminalState(WorkflowState state)
    {
        state.Match(
            queued => Unit.Value,
            dispatched => Unit.Value,
            running => Unit.Value,
            awaitingSettlement => Unit.Value,
            completed =>
            {
                AnsiConsole.MarkupLine($"[green]Task completed successfully![/]");
                AnsiConsole.MarkupLine($"[green]Summary:[/] {completed.ResultSummary.Summary}");
                AnsiConsole.MarkupLine($"[green]Cost:[/] ${completed.Usage.InputTokens * 0.000003 + completed.Usage.OutputTokens * 0.000015:F4}");
                return Unit.Value;
            },
            failed =>
            {
                AnsiConsole.MarkupLine($"[red]Task failed permanently.[/]");
                AnsiConsole.MarkupLine($"[red]Reason:[/] {failed.Reason.Match(t => t.Detail, p => p.Detail)}");
                return Unit.Value;
            },
            suspended => Unit.Value,
            retrying => Unit.Value,
            cancelled =>
            {
                AnsiConsole.MarkupLine($"[yellow]Task was cancelled by user.[/]");
                return Unit.Value;
            }
        );
    }

    private static void RenderEnvelope(StreamEnvelope envelope)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        switch (envelope.Type)
        {
            case "Enqueued":
                AnsiConsole.MarkupLine($"[[{time}]] [blue][[Queued]][/] Task enqueued and waiting for worker...");
                break;
            case "WorkerSubmitted":
                AnsiConsole.MarkupLine($"[[{time}]] [yellow][[Submitted]][/] Worker submitted to provider...");
                break;
            case "TaskClaimed":
                AnsiConsole.MarkupLine($"[[{time}]] [green][[Claimed]][/] Worker has claimed the task and is actively working.");
                break;
            case "CompletionRequested":
                AnsiConsole.MarkupLine($"[[{time}]] [green][[Completion Requested]][/] Worker requested task completion.");
                break;
            case "WorkerSettled":
                AnsiConsole.MarkupLine($"[[{time}]] [green][[Settled]][/] Worker settled and cost recorded.");
                break;
            case "WorkerExpired":
                AnsiConsole.MarkupLine($"[[{time}]] [red][[Expired]][/] Worker expired before completing the task.");
                break;
            case "TransientFailed":
                AnsiConsole.MarkupLine($"[[{time}]] [red][[Transient Failed]][/] Task failed with a transient error.");
                break;
            case "PermanentFailed":
                AnsiConsole.MarkupLine($"[[{time}]] [red][[Permanent Failed]][/] Task failed permanently.");
                break;
            default:
                AnsiConsole.MarkupLine($"[[{time}]] [grey][[{envelope.Type}]][/] {envelope.Data}");
                break;
        }
    }

    private static async Task HandleStatusAsync(string taskId, bool noAutostart)
    {
        try
        {
            var channel = await GetControlChannelAsync(noAutostart);
            var stateRes = await channel.GetTaskStateAsync(new TaskId(taskId));
            if (stateRes.IsErr)
            {
                AnsiConsole.MarkupLine($"[red]Error: {stateRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                return;
            }
            
            var state = stateRes.Unwrap();
            state.Match(
                queued =>
                {
                    AnsiConsole.MarkupLine($"[blue]Status:[/] Queued");
                    AnsiConsole.MarkupLine($"[blue]Prompt:[/] {queued.Prompt}");
                    AnsiConsole.MarkupLine($"[blue]Working Dir:[/] {queued.WorkingDir.Value}");
                    AnsiConsole.MarkupLine($"[blue]Attempt:[/] {queued.Attempt}/{queued.MaxAttempts}");
                    return Unit.Value;
                },
                dispatched =>
                {
                    AnsiConsole.MarkupLine($"[yellow]Status:[/] Dispatched");
                    AnsiConsole.MarkupLine($"[yellow]Worker ID:[/] {dispatched.WorkerId.Value}");
                    AnsiConsole.MarkupLine($"[yellow]Dispatched At:[/] {dispatched.DispatchedAt.Value}");
                    return Unit.Value;
                },
                running =>
                {
                    AnsiConsole.MarkupLine($"[green]Status:[/] Running");
                    AnsiConsole.MarkupLine($"[green]Worker ID:[/] {running.WorkerId.Value}");
                    AnsiConsole.MarkupLine($"[green]Started At:[/] {running.StartedAt.Value}");
                    return Unit.Value;
                },
                awaitingSettlement =>
                {
                    AnsiConsole.MarkupLine($"[yellow]Status:[/] Awaiting Cost Settlement");
                    AnsiConsole.MarkupLine($"[yellow]Worker ID:[/] {awaitingSettlement.WorkerId.Value}");
                    AnsiConsole.MarkupLine($"[yellow]Result Summary:[/] {awaitingSettlement.ResultSummary.Summary}");
                    return Unit.Value;
                },
                completed =>
                {
                    AnsiConsole.MarkupLine($"[green]Status:[/] Completed");
                    AnsiConsole.MarkupLine($"[green]Result Summary:[/] {completed.ResultSummary.Summary}");
                    AnsiConsole.MarkupLine($"[green]Cost:[/] ${completed.Usage.InputTokens * 0.000003 + completed.Usage.OutputTokens * 0.000015:F4}");
                    return Unit.Value;
                },
                failed =>
                {
                    AnsiConsole.MarkupLine($"[red]Status:[/] Failed");
                    AnsiConsole.MarkupLine($"[red]Reason:[/] {failed.Reason.Match(t => t.Detail, p => p.Detail)}");
                    return Unit.Value;
                },
                suspended =>
                {
                    AnsiConsole.MarkupLine($"[yellow]Status:[/] Suspended");
                    AnsiConsole.MarkupLine($"[yellow]Worker ID:[/] {suspended.WorkerId.Value}");
                    AnsiConsole.MarkupLine($"[yellow]Awaiting:[/] {suspended.AwaitSpec}");
                    return Unit.Value;
                },
                retrying =>
                {
                    AnsiConsole.MarkupLine($"[yellow]Status:[/] Retrying");
                    AnsiConsole.MarkupLine($"[yellow]Attempt:[/] {retrying.Attempt}/{retrying.MaxAttempts}");
                    AnsiConsole.MarkupLine($"[yellow]Next Attempt At:[/] {retrying.NextAttemptAt.Value}");
                    AnsiConsole.MarkupLine($"[yellow]Last Error:[/] {retrying.LastError.Match(t => t.Detail, p => p.Detail)}");
                    return Unit.Value;
                },
                cancelled =>
                {
                    AnsiConsole.MarkupLine($"[grey]Status:[/] Cancelled");
                    AnsiConsole.MarkupLine($"[grey]Cancelled At:[/] {cancelled.CancelledAt.Value}");
                    return Unit.Value;
                }
            );
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }
    }

    private static async Task HandleCancelAsync(string taskId, bool noAutostart)
    {
        try
        {
            var channel = await GetControlChannelAsync(noAutostart);
            var result = await channel.CancelTaskAsync(new TaskId(taskId));
            if (result.IsOk)
            {
                AnsiConsole.MarkupLine("[green]Task cancelled successfully.[/]");
            }
            else
            {
                var err = result.UnwrapErr();
                AnsiConsole.MarkupLine($"[red]Failed to cancel task: {err.Match(t => t.Detail, p => p.Detail)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }
    }

    private static async Task HandleListAsync(bool noAutostart)
    {
        try
        {
            var channel = await GetControlChannelAsync(noAutostart);
            var tasksRes = await channel.ListTasksAsync();
            if (tasksRes.IsErr)
            {
                AnsiConsole.MarkupLine($"[red]Error: {tasksRes.UnwrapErr().Match(t => t.Detail, p => p.Detail)}[/]");
                return;
            }
            
            var tasks = tasksRes.Unwrap();
            if (tasks.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No tasks found.[/]");
                return;
            }
            
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Task ID[/]");
            table.AddColumn("[bold]Status[/]");
            table.AddColumn("[bold]Working Dir[/]");
            table.AddColumn("[bold]Prompt/Summary[/]");

            foreach (var task in tasks)
            {
                string id = task.TaskId.Value;
                string status = task.GetType().Name;
                string dir = task.WorkingDir.Value;
                string prompt = "";
                
                task.Match(
                    queued => { prompt = queued.Prompt; return Unit.Value; },
                    dispatched => { prompt = dispatched.Prompt; return Unit.Value; },
                    running => { prompt = running.Prompt; return Unit.Value; },
                    awaitingSettlement => { prompt = awaitingSettlement.ResultSummary.Summary; return Unit.Value; },
                    completed => { prompt = completed.ResultSummary.Summary; return Unit.Value; },
                    failed => { prompt = failed.Reason.Match(t => t.Detail, p => p.Detail); return Unit.Value; },
                    suspended => { prompt = suspended.Prompt; return Unit.Value; },
                    retrying => { prompt = retrying.Prompt; return Unit.Value; },
                    cancelled => { prompt = "Cancelled"; return Unit.Value; }
                );
                
                if (prompt.Length > 50) prompt = prompt.Substring(0, 47) + "...";
                
                string statusColor = status switch
                {
                    "Queued" => "blue",
                    "Dispatched" => "yellow",
                    "Running" => "green",
                    "AwaitingSettlement" => "yellow",
                    "Completed" => "green",
                    "Failed" => "red",
                    "Suspended" => "yellow",
                    "Retrying" => "yellow",
                    "Cancelled" => "grey",
                    _ => "white"
                };
                
                table.AddRow(id, $"[{statusColor}]{status}[/]", dir, prompt);
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error listing tasks: {ex.Message}[/]");
        }
    }

    private static async Task<HttpControlChannel> GetControlChannelAsync(bool noAutostart)
    {
        var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
        Directory.CreateDirectory(crockHome);
        var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
        
        int port = 5000;
        bool daemonRunning = false;
        
        if (File.Exists(daemonJsonPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(daemonJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("controlPort", out var portProp))
                {
                    port = portProp.GetInt32();
                }
                if (doc.RootElement.TryGetProperty("pid", out var pidProp))
                {
                    int pid = pidProp.GetInt32();
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (!process.HasExited)
                        {
                            daemonRunning = true;
                        }
                    }
                    catch
                    {
                        // process is dead or access denied
                    }
                }
            }
            catch
            {
                // ignore errors reading json
            }
        }
        
        if (!daemonRunning)
        {
            if (noAutostart)
            {
                throw new Exception("Daemon is not running and autostart is disabled.");
            }
            
            var lockPath = Path.Combine(crockHome, "daemon.lock");
            FileStream? fs = null;
            try
            {
                fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // We got the lock! Start the daemon.
                await StartDaemonProcessAsync(crockHome, port);
            }
            catch (IOException)
            {
                // We failed to acquire the lock. Another CLI instance is starting the daemon.
                // Wait for the daemon to start responding to health checks.
                using var client = new HttpClient();
                bool started = false;
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (File.Exists(daemonJsonPath))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(daemonJsonPath);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("controlPort", out var portProp))
                            {
                                port = portProp.GetInt32();
                            }
                        }
                        catch { }
                    }
                    try
                    {
                        var res = await client.GetAsync($"http://localhost:{port}/health");
                        if (res.IsSuccessStatusCode)
                        {
                            started = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!started)
                {
                    throw new TimeoutException("Timed out waiting for another instance to start the daemon.");
                }
            }
            finally
            {
                if (fs != null)
                {
                    fs.Dispose();
                    try { File.Delete(lockPath); } catch { }
                }
            }
        }
        
        var httpClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        return new HttpControlChannel(httpClient);
    }

    private static string GetDaemonPath()
    {
        var envPath = Environment.GetEnvironmentVariable("CROCK_COORDINATOR_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var cliDir = AppContext.BaseDirectory;
        var daemonPath = Path.Combine(cliDir, "CrockCode.Coordinator");
        if (File.Exists(daemonPath)) return daemonPath;
        
        daemonPath = Path.Combine(cliDir, "CrockCode.Coordinator.dll");
        if (File.Exists(daemonPath)) return daemonPath;

        var parentDir = new DirectoryInfo(cliDir);
        for (int i = 0; i < 10; i++)
        {
            if (parentDir == null) break;
            
            var devDllPath = Path.Combine(parentDir.FullName, "src", "CrockCode.Coordinator", "bin", "Debug", "net9.0", "CrockCode.Coordinator.dll");
            if (File.Exists(devDllPath)) return devDllPath;

            var devProjPath = Path.Combine(parentDir.FullName, "src", "CrockCode.Coordinator", "CrockCode.Coordinator.csproj");
            if (File.Exists(devProjPath)) return devProjPath;

            parentDir = parentDir.Parent;
        }

        throw new FileNotFoundException("Could not locate CrockCode.Coordinator executable or project. Please set CROCK_COORDINATOR_PATH environment variable.");
    }

    private static async Task StartDaemonProcessAsync(string crockHome, int port)
    {
        var daemonPath = GetDaemonPath();
        var isProj = daemonPath.EndsWith(".csproj");
        var isDll = daemonPath.EndsWith(".dll");
        var logFile = Path.Combine(crockHome, "daemon.log");
        
        var runCmd = isProj 
            ? $"dotnet run --project '{daemonPath}' --urls 'http://localhost:{port}'"
            : (isDll ? $"dotnet '{daemonPath}' --urls 'http://localhost:{port}'" : $"'{daemonPath}' --urls 'http://localhost:{port}'");
            
        var psi = new System.Diagnostics.ProcessStartInfo();
        psi.FileName = "bash";
        psi.Arguments = $"-c \"exec {runCmd} > '{logFile}' 2>&1\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        
        var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new Exception("Failed to start daemon process.");
        }
        
        var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
        var metadata = new
        {
            pid = process.Id,
            controlPort = port,
            mcpPublicUrl = $"http://localhost:{port}"
        };
        await File.WriteAllTextAsync(daemonJsonPath, JsonSerializer.Serialize(metadata));
        
        // Wait for the daemon to start responding to health checks
        using var client = new HttpClient();
        for (int i = 0; i < 20; i++)
        {
            try
            {
                var res = await client.GetAsync($"http://localhost:{port}/health");
                if (res.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // ignore connection errors
            }
            await Task.Delay(500);
        }
        
        throw new TimeoutException("Timed out waiting for daemon to start.");
    }

    private static void StopDaemonIfRunning(string crockHome)
    {
        var daemonJsonPath = Path.Combine(crockHome, "daemon.json");
        if (File.Exists(daemonJsonPath))
        {
            try
            {
                var json = File.ReadAllText(daemonJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("pid", out var pidProp))
                {
                    int pid = pidProp.GetInt32();
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (!process.HasExited)
                        {
                            AnsiConsole.MarkupLine($"[yellow]Stopping existing daemon (PID {pid})...[/]");
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(3000);
                        }
                    }
                    catch
                    {
                        // ignore if process not found/dead
                    }
                }
            }
            catch
            {
                // ignore errors reading json
            }
            try { File.Delete(daemonJsonPath); } catch { }
        }
    }
}
