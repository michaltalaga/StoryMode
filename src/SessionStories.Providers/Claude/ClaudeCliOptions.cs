namespace SessionStories.Providers.Claude;

/// <summary>
/// Configuration for spawning the Claude CLI headlessly (verified against 2.1.170).
/// Plain settable properties so it binds from appsettings.json.
/// </summary>
public sealed class ClaudeCliOptions
{
    /// <summary>Executable name or full path; npm shims ending in .cmd are spawned via cmd.exe.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>
    /// cwd for spawns. Must be the repo root so skills/story/, library/universes/ and
    /// library/stories/ are all reachable without --add-dir. Empty = inherit current directory.
    /// </summary>
    public string RepoRoot { get; set; } = "";

    /// <summary>
    /// Runaway protection: --max-turns does not exist in CLI 2.1.170, so the budget cap
    /// plus the process timeout are the only guards.
    /// </summary>
    // Real-run data (2026-08-09): every stage re-reads the skill + session + universe corpus,
    // which alone approaches $0.50; resumed scene sessions accumulate the outline session's
    // cost on top. $2 is a brake against runaways, not a target.
    public decimal MaxBudgetUsd { get; set; } = 2.00m;

    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Per-stage --max-budget-usd overrides keyed by lowercase stage name
    /// (extract|outline|scene|regen|verify|bible). Absent stage falls back to <see cref="MaxBudgetUsd"/>.
    /// </summary>
    public Dictionary<string, decimal> StageBudgetOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal ResolveBudget(string stage)
    {
        if (StageBudgetOverrides.TryGetValue(stage, out var cap))
            return cap;
        // Config binding may have replaced the case-insensitive dictionary instance.
        foreach (var (key, value) in StageBudgetOverrides)
            if (string.Equals(key, stage, StringComparison.OrdinalIgnoreCase))
                return value;
        return MaxBudgetUsd;
    }
}
