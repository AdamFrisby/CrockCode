using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.McpServer;

[McpServerToolType]
public sealed class CodingTools
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMcpContextResolver _mcpContextResolver;
    private readonly BackgroundProcessManager _backgroundProcessManager;

    public CodingTools(
        IHttpContextAccessor httpContextAccessor, 
        IMcpContextResolver mcpContextResolver,
        BackgroundProcessManager backgroundProcessManager)
    {
        _httpContextAccessor = httpContextAccessor;
        _mcpContextResolver = mcpContextResolver;
        _backgroundProcessManager = backgroundProcessManager;
    }

    private async Task<Result<WorkspaceContext>> GetContextAsync(CancellationToken ct)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return new Result<WorkspaceContext>.Err(new Error.Permanent("NO_HTTP_CONTEXT", "No active HTTP request"));
        }
        return await _mcpContextResolver.ResolveContextAsync(httpContext, ct);
    }

    private Result<string> ResolveAndValidatePath(string relativePath, string workingDir)
    {
        try
        {
            string canonicalWorkingDir = Path.GetFullPath(workingDir);
            string combinedPath = Path.Combine(canonicalWorkingDir, relativePath);
            string canonicalPath = Path.GetFullPath(combinedPath);

            if (!canonicalPath.StartsWith(canonicalWorkingDir, StringComparison.Ordinal))
            {
                return new Result<string>.Err(new Error.Permanent("PATH_ESCAPE", $"Path {relativePath} escapes the working directory {workingDir}"));
            }

            return Result.Ok(canonicalPath);
        }
        catch (Exception ex)
        {
            return new Result<string>.Err(new Error.Permanent("INVALID_PATH", ex.Message));
        }
    }

    [McpServerTool]
    [Description("Read the contents of a file from the workspace.")]
    public async Task<string> Read(
        [Description("The path to the file relative to the working directory.")] string file_path,
        [Description("Optional. Line offset (1-indexed) to start reading from.")] int? offset,
        [Description("Optional. Maximum number of lines to read.")] int? limit,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        var pathResult = ResolveAndValidatePath(file_path, ctx.WorkingDir.Value);
        if (pathResult is Result<string>.Err pathErr) return pathErr.Error.Match(t => t.Detail, p => p.Detail);
        string fullPath = pathResult.Unwrap();

        if (!File.Exists(fullPath))
        {
            return $"File not found: {file_path}";
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, ct);
            int startLine = offset ?? 1;
            int maxLines = limit ?? lines.Length;

            int startIndex = Math.Max(0, startLine - 1);
            int count = Math.Min(maxLines, lines.Length - startIndex);

            if (startIndex >= lines.Length || count <= 0)
            {
                return "";
            }

            var sb = new StringBuilder();
            for (int i = startIndex; i < startIndex + count; i++)
            {
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Write content to a file, replacing it entirely if it already exists.")]
    public async Task<string> Write(
        [Description("The path to the file relative to the working directory.")] string file_path,
        [Description("The content to write to the file.")] string content,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        var pathResult = ResolveAndValidatePath(file_path, ctx.WorkingDir.Value);
        if (pathResult is Result<string>.Err pathErr) return pathErr.Error.Match(t => t.Detail, p => p.Detail);
        string fullPath = pathResult.Unwrap();

        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, content, ct);
            return $"Successfully wrote {content.Length} characters to {file_path}";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Edit an existing file by replacing a single exact match of old_string with new_string.")]
    public async Task<string> Edit(
        [Description("The path to the file relative to the working directory.")] string file_path,
        [Description("The exact string block in the file to replace.")] string old_string,
        [Description("The replacement string block.")] string new_string,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        var pathResult = ResolveAndValidatePath(file_path, ctx.WorkingDir.Value);
        if (pathResult is Result<string>.Err pathErr) return pathErr.Error.Match(t => t.Detail, p => p.Detail);
        string fullPath = pathResult.Unwrap();

        if (!File.Exists(fullPath))
        {
            return $"File not found: {file_path}";
        }

        try
        {
            string content = await File.ReadAllTextAsync(fullPath, ct);
            int firstIdx = content.IndexOf(old_string, StringComparison.Ordinal);
            if (firstIdx == -1)
            {
                return "Error: The old_string was not found in the file. Ensure you provide the exact characters, including whitespace.";
            }

            int secondIdx = content.IndexOf(old_string, firstIdx + old_string.Length, StringComparison.Ordinal);
            if (secondIdx != -1)
            {
                return "Error: Multiple occurrences of old_string found. Please specify a unique block of context.";
            }

            string updatedContent = content[..firstIdx] + new_string + content[(firstIdx + old_string.Length)..];
            await File.WriteAllTextAsync(fullPath, updatedContent, ct);
            return $"Successfully edited {file_path}";
        }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Find files matching a glob pattern (e.g. **/*.cs).")]
    public async Task<string> Glob(
        [Description("The pattern to search for.")] string pattern,
        [Description("Optional. Directory to search in, relative to working directory.")] string? path,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        var pathResult = ResolveAndValidatePath(path ?? ".", ctx.WorkingDir.Value);
        if (pathResult is Result<string>.Err pathErr) return pathErr.Error.Match(t => t.Detail, p => p.Detail);
        string searchPath = pathResult.Unwrap();

        try
        {
            var regex = GlobToRegex(pattern);
            var matchingFiles = new List<string>();

            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string relative = Path.GetRelativePath(ctx.WorkingDir.Value, file).Replace('\\', '/');
                if (relative.StartsWith(".git")) continue;

                string relativeToSearch = Path.GetRelativePath(searchPath, file).Replace('\\', '/');
                if (regex.IsMatch(relativeToSearch))
                {
                    matchingFiles.Add(relative);
                }
            }

            if (matchingFiles.Count == 0)
            {
                return "No files matched the pattern.";
            }

            return string.Join("\n", matchingFiles);
        }
        catch (Exception ex)
        {
            return $"Error performing glob: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Search for a text pattern recursively in files, excluding build and git folders.")]
    public async Task<string> Grep(
        [Description("The search query pattern.")] string pattern,
        [Description("Optional. Path relative to working directory to search.")] string? path,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        var pathResult = ResolveAndValidatePath(path ?? ".", ctx.WorkingDir.Value);
        if (pathResult is Result<string>.Err pathErr) return pathErr.Error.Match(t => t.Detail, p => p.Detail);
        string searchPath = pathResult.Unwrap();

        var rgResult = await RunRipGrepAsync(searchPath, ctx.WorkingDir.Value, pattern, ct);
        if (rgResult != null)
        {
            return rgResult;
        }

        try
        {
            var results = new List<string>();
            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                string relative = Path.GetRelativePath(ctx.WorkingDir.Value, file);
                if (relative.StartsWith(".git") || relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("\\bin\\") || relative.Contains("\\obj\\"))
                {
                    continue;
                }

                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add($"{relative}:{i + 1}: {lines[i]}");
                            if (results.Count >= 100) break;
                        }
                    }
                }
                catch
                {
                    // Skip read errors for binaries
                }
                if (results.Count >= 100) break;
            }

            if (results.Count == 0)
            {
                return "No matches found.";
            }

            return string.Join("\n", results);
        }
        catch (Exception ex)
        {
            return $"Error performing grep: {ex.Message}";
        }
    }

    private static async Task<string?> RunRipGrepAsync(string searchPath, string workingDir, string pattern, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "rg";
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add("-n");
            process.StartInfo.ArgumentList.Add("--no-heading");
            process.StartInfo.ArgumentList.Add("--color=never");
            process.StartInfo.ArgumentList.Add(pattern);
            process.StartInfo.WorkingDirectory = searchPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            var sb = new StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await process.WaitForExitAsync(cts.Token);

            string output = sb.ToString();
            if (string.IsNullOrWhiteSpace(output))
            {
                return "No matches found.";
            }

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var results = new List<string>();
            foreach (var line in lines)
            {
                int colon1 = line.IndexOf(':');
                if (colon1 > 0)
                {
                    string filePart = line[..colon1];
                    string rest = line[colon1..];
                    string fullFilePath = Path.GetFullPath(Path.Combine(searchPath, filePart));
                    string relativeToWorking = Path.GetRelativePath(workingDir, fullFilePath);
                    results.Add($"{relativeToWorking}{rest}");
                }
                else
                {
                    results.Add(line);
                }
            }

            return string.Join("\n", results);
        }
        catch
        {
            return null; // Fallback
        }
    }

    [McpServerTool]
    [Description("Execute a command in bash inside the working directory. Timeout is capped at 50 seconds.")]
    public async Task<string> Bash(
        [Description("The bash command to execute.")] string command,
        [Description("Optional. Timeout in seconds (default 50).")] int? timeout,
        [Description("Optional. If true, run in the background detached.")] bool? run_in_background,
        [Description("Optional description of command.")] string? description,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err) return err.Error.Match(t => t.Detail, p => p.Detail);
        var ctx = ctxResult.Unwrap();

        if (run_in_background == true)
        {
            try
            {
                string id = _backgroundProcessManager.Start(command, ctx.WorkingDir.Value);
                return $"Background task started. ID: {id}. Use BashOutput(id) to poll output.";
            }
            catch (Exception ex)
            {
                return $"Error starting background command: {ex.Message}";
            }
        }

        int seconds = Math.Min(timeout ?? 50, 50);

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "bash";
            process.StartInfo.WorkingDirectory = ctx.WorkingDir.Value;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.StandardInput.WriteLineAsync(command);
            process.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return $"Command timed out after {seconds} seconds.\nOutput:\n{outputBuilder}\nError:\n{errorBuilder}";
            }

            return $"Exit Code: {process.ExitCode}\nOutput:\n{outputBuilder}\nError:\n{errorBuilder}";
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Retrieve output from a background bash task.")]
    public async Task<string> BashOutput(
        [Description("The background task ID.")] string bash_id,
        [Description("Optional filter.")] string? filter,
        CancellationToken ct)
    {
        var bgProcess = _backgroundProcessManager.Get(bash_id);
        if (bgProcess == null)
        {
            return $"Error: Background task {bash_id} not found.";
        }

        string output;
        string error;
        lock (bgProcess.Output)
        {
            output = bgProcess.Output.ToString();
        }
        lock (bgProcess.Error)
        {
            error = bgProcess.Error.ToString();
        }

        bool completed = bgProcess.IsCompleted;
        int? exitCode = bgProcess.ExitCode;

        var sb = new StringBuilder();
        sb.AppendLine($"Task ID: {bash_id}");
        sb.AppendLine($"Status: {(completed ? $"Completed (Exit Code: {exitCode})" : "Running")}");
        sb.AppendLine("Output:");
        sb.AppendLine(output);
        if (!string.IsNullOrEmpty(error))
        {
            sb.AppendLine("Error:");
            sb.AppendLine(error);
        }

        return sb.ToString();
    }

    private static Regex GlobToRegex(string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
                                          .Replace(@"\*\*", ".*")
                                          .Replace(@"\*", "[^/]*")
                                          .Replace(@"\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
