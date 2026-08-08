using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SessionStories.Core.Generation;

namespace SessionStories.Providers.Claude;

/// <summary>
/// Runs one skill stage via headless `claude -p` (CLI 2.1.170): spawns with cwd = repo root,
/// streams stream-json stdout into compact progress lines, and returns the final result
/// envelope. Does not write gen.&lt;variant&gt;.json and does not splice scenes — the job
/// runner owns both.
/// </summary>
public sealed class ClaudeCliGenerator(ClaudeCliOptions options) : IStoryGenerator
{
    private const int StderrTailCapacity = 50;
    private const int ProgressLineMaxLength = 160;

    public async Task<GenerationResult> RunStageAsync(
        GenerationStage stage,
        StageContext context,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var stageName = ClaudeArgumentBuilder.StageName(stage);
        var budget = options.ResolveBudget(stageName);
        var args = ClaudeArgumentBuilder.Build(stage, context, budget);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(options.TimeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var process = StartProcess(args);
        progress?.Report(
            $"claude {stageName}: pid {process.Id}, cap ${budget.ToString(CultureInfo.InvariantCulture)}");

        var stderrTail = new Queue<string>(StderrTailCapacity);
        string? sessionId = null;
        decimal? costUsd = null;
        var isError = false;
        string? resultText = null;

        var stdoutTask = Task.Run(ReadStdoutAsync, CancellationToken.None);
        var stderrTask = Task.Run(ReadStderrAsync, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* reap best-effort */ }
            await Task.WhenAll(stdoutTask, stderrTask);
            // Caller-initiated cancel propagates (job runner records Cancelled); only the
            // internal timeout converts to a failure result.
            ct.ThrowIfCancellationRequested();
            return new GenerationResult(false, sessionId, costUsd,
                ComposeError($"claude {stageName} timed out after {options.TimeoutMinutes} min; process tree killed.",
                    resultText: null, stderrTail));
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0 || isError)
        {
            var headline = $"claude {stageName} failed (exit {process.ExitCode}"
                + (isError ? ", is_error" : "") + ").";
            return new GenerationResult(false, sessionId, costUsd, ComposeError(headline, resultText, stderrTail));
        }

        progress?.Report($"claude {stageName}: done"
            + (costUsd is { } c ? $" (${c.ToString(CultureInfo.InvariantCulture)})" : ""));
        return new GenerationResult(true, sessionId, costUsd, null);

        async Task ReadStdoutAsync()
        {
            var reader = process.StandardOutput;
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // stream-json is line-delimited but the CLI may interleave plain text.
                    progress?.Report(Compact(line));
                    continue;
                }

                using (doc)
                    HandleEvent(doc.RootElement);
            }
        }

        void HandleEvent(JsonElement root)
        {
            // Any event carrying session_id updates it early, so even a timed-out run
            // reports which session it was (useful for resume diagnostics).
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString();

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "assistant":
                    ForwardAssistantEvent(root);
                    break;

                case "result":
                    if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                        costUsd = cost.GetDecimal();
                    if (root.TryGetProperty("is_error", out var err) &&
                        err.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        isError = err.GetBoolean();
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                        resultText = res.GetString();
                    break;
            }
        }

        void ForwardAssistantEvent(JsonElement root)
        {
            if (progress is null
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return;

            foreach (var block in content.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (blockType == "text"
                    && block.TryGetProperty("text", out var text)
                    && Compact(text.GetString()) is { Length: > 0 } line)
                {
                    progress.Report(line);
                }
                else if (blockType == "tool_use")
                {
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                    var target = DescribeToolTarget(block);
                    progress.Report(target is null ? $"[tool] {name}" : $"[tool] {name} {target}");
                }
            }
        }

        async Task ReadStderrAsync()
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                stderrTail.Enqueue(line);
                while (stderrTail.Count > StderrTailCapacity)
                    stderrTail.Dequeue();
            }
        }
    }

    private Process StartProcess(IReadOnlyList<string> args)
    {
        var exe = options.ExecutablePath;
        if (!exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Start(exe, args, viaCmd: false);
            }
            catch (Win32Exception)
            {
                // npm shims (claude -> claude.cmd) are not directly CreateProcess-able;
                // retry through the shell.
            }
        }
        return Start(exe, args, viaCmd: true);
    }

    private Process Start(string exe, IReadOnlyList<string> args, bool viaCmd)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrEmpty(options.RepoRoot)
                ? Environment.CurrentDirectory
                : options.RepoRoot,
        };

        if (viaCmd)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi.FileName = exe;
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited or never fully started — nothing to kill.
        }
    }

    private static string ComposeError(string headline, string? resultText, Queue<string> stderrTail)
    {
        var parts = new List<string> { headline };
        if (!string.IsNullOrWhiteSpace(resultText))
            parts.Add(Compact(resultText));
        if (stderrTail.Count > 0)
            parts.Add(string.Join(Environment.NewLine, stderrTail));
        return string.Join(Environment.NewLine, parts);
    }

    private static string? DescribeToolTarget(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in (ReadOnlySpan<string>)["file_path", "path", "pattern", "command", "url"])
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return Compact(value.GetString());
        return null;
    }

    private static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var span = text.AsSpan().Trim();
        var newline = span.IndexOfAny('\r', '\n');
        if (newline >= 0)
            span = span[..newline].TrimEnd();
        return span.Length > ProgressLineMaxLength
            ? string.Concat(span[..(ProgressLineMaxLength - 1)], "…")
            : span.ToString();
    }
}
