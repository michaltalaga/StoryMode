using SessionStories.Core.Jobs;

namespace SessionStories.Api;

/// <summary>Bound from the "SessionStories" configuration section; call <see cref="ResolveDefaults"/> once at startup.</summary>
public sealed class SessionStoriesOptions
{
    public string LibraryRoot { get; set; } = "";
    public string RepoRoot { get; private set; } = "";
    public WhisperOptions Whisper { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();
    public ClaudeOptions Claude { get; set; } = new();
    public JobsOptions Jobs { get; set; } = new();

    public sealed class WhisperOptions
    {
        public string ModelPath { get; set; } = "";
    }

    public sealed class TtsOptions
    {
        public string ModelDir { get; set; } = "";
        public string VoiceCacheDir { get; set; } = "";
    }

    public sealed class ClaudeOptions
    {
        public string ExecutablePath { get; set; } = "claude";
        public decimal MaxBudgetUsd { get; set; } = 2.00m;
    }

    public sealed class JobsOptions
    {
        public Dictionary<JobType, int> TimeoutMinutes { get; set; } = [];
    }

    private static readonly Dictionary<JobType, int> DefaultTimeouts = new()
    {
        [JobType.Transcribe] = 30,
        [JobType.Generate] = 45,
        [JobType.RegenScene] = 15,
        [JobType.Verify] = 10,
        [JobType.RenderTts] = 60,
    };

    public TimeSpan TimeoutFor(JobType type)
        => TimeSpan.FromMinutes(Jobs.TimeoutMinutes.TryGetValue(type, out var minutes)
            ? minutes
            : DefaultTimeouts.GetValueOrDefault(type, 30));

    /// <summary>Fills unset values and resolves relative paths against the repo root.</summary>
    public void ResolveDefaults(string contentRootPath)
    {
        RepoRoot = FindRepoRoot(contentRootPath);

        LibraryRoot = ResolvePath(LibraryRoot, Path.Combine(RepoRoot, "library"));
        Whisper.ModelPath = ResolvePath(Whisper.ModelPath, Path.Combine(RepoRoot, "models", "whisper", "ggml-large-v3.bin"));
        Tts.ModelDir = ResolvePath(Tts.ModelDir, Path.Combine(RepoRoot, "models", "chatterbox"));
        Tts.VoiceCacheDir = ResolvePath(Tts.VoiceCacheDir, Path.Combine(LibraryRoot, "voice-cache"));

        foreach (var (type, minutes) in DefaultTimeouts)
            Jobs.TimeoutMinutes.TryAdd(type, minutes);
    }

    private string ResolvePath(string configured, string fallback)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured, RepoRoot);

    /// <summary>Walks up from the content root to the folder containing SessionStories.slnx.</summary>
    private static string FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SessionStories.slnx")))
                return dir.FullName;
        }
        return start;
    }
}
