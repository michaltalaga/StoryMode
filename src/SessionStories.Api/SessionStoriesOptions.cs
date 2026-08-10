using SessionStories.Core.Jobs;

namespace SessionStories.Api;

/// <summary>Bound from the "SessionStories" configuration section; call <see cref="ResolveDefaults"/> once at startup.</summary>
public sealed class SessionStoriesOptions
{
    public string LibraryRoot { get; set; } = "";
    public string RepoRoot { get; private set; } = "";

    /// <summary>
    /// The curated shelf of installable voices. App content, so it lives with the code rather than in
    /// the reader's library — it is versioned alongside the engines that know how to install it.
    /// </summary>
    public string GalleryRoot { get; set; } = "";
    public WhisperOptions Whisper { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();
    public PiperOptions Piper { get; set; } = new();
    public XttsOptions Xtts { get; set; } = new();
    public ContainerEngineOptions Moss { get; set; } = new();
    public ContainerEngineOptions Qwen { get; set; } = new();
    public ContainerEngineOptions Higgs { get; set; } = new();
    public ContainerEngineOptions VibeVoice { get; set; } = new();
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

    public sealed class PiperOptions
    {
        public string ModelsRoot { get; set; } = "";
    }

    public sealed class XttsOptions
    {
        public string ModelsRoot { get; set; } = "";
        public string Image { get; set; } = "storymode-xtts:latest";
        public string ContainerName { get; set; } = "storymode-xtts";
        public int Port { get; set; } = 8020;

        /// <summary>
        /// XTTS-v2 is under the Coqui Public Model License — non-commercial only. Accepting it is
        /// the operator's call, so the engine stays unavailable until this is switched on by hand.
        /// </summary>
        public bool AcceptCoquiLicense { get; set; }
    }

    /// <summary>
    /// A container speech engine. <see cref="Enabled"/> says its image has been built — an engine
    /// whose image is missing would appear as an option and then fail at render time, which is
    /// worse than being absent. Everything else has a default derived from the engine's name.
    /// </summary>
    public sealed class ContainerEngineOptions
    {
        public bool Enabled { get; set; }
        public string Image { get; set; } = "";
        public string ContainerName { get; set; } = "";
        public int Port { get; set; }

        /// <summary>Where this engine's weights live on the host. One directory per engine.</summary>
        public string ModelsRoot { get; set; } = "";

        /// <summary>
        /// Engines published under research or non-commercial terms stay unavailable until this is
        /// switched on by hand, because accepting them is the operator's decision and not this
        /// code's. Permissively licensed engines ignore it — there is nothing to accept.
        /// </summary>
        public bool AcceptLicense { get; set; }
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
        [JobType.PreviewVoice] = 10,
        // A shelf install is a download plus a copy; an uploaded voice also pays one conditioning pass.
        [JobType.InstallVoice] = 20,
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
        Piper.ModelsRoot = ResolvePath(Piper.ModelsRoot, Path.Combine(RepoRoot, "models", "piper"));
        Xtts.ModelsRoot = ResolvePath(Xtts.ModelsRoot, Path.Combine(RepoRoot, "models", "xtts"));
        GalleryRoot = ResolvePath(GalleryRoot, Path.Combine(RepoRoot, "voice-gallery"));

        // Each container engine gets its own image name, container name, port and weights
        // directory, all derived from one word. Spelling them out per engine in configuration was
        // four near-identical lines each, and the ports had to be kept distinct by hand.
        ApplyContainerDefaults(Moss, "moss", 8021);
        ApplyContainerDefaults(Qwen, "qwen", 8022);
        ApplyContainerDefaults(Higgs, "higgs", 8023);
        ApplyContainerDefaults(VibeVoice, "vibevoice", 8024);

        foreach (var (type, minutes) in DefaultTimeouts)
            Jobs.TimeoutMinutes.TryAdd(type, minutes);
    }

    private void ApplyContainerDefaults(ContainerEngineOptions engine, string name, int port)
    {
        if (string.IsNullOrWhiteSpace(engine.Image))
            engine.Image = $"storymode-{name}:latest";
        if (string.IsNullOrWhiteSpace(engine.ContainerName))
            engine.ContainerName = $"storymode-{name}";
        if (engine.Port == 0)
            engine.Port = port;
        engine.ModelsRoot = ResolvePath(engine.ModelsRoot, Path.Combine(RepoRoot, "models", name));
    }

    private string ResolvePath(string configured, string fallback)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured, RepoRoot);

    /// <summary>Walks up from the content root to the folder containing StoryMode.slnx.</summary>
    private static string FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StoryMode.slnx")))
                return dir.FullName;
        }
        return start;
    }
}
