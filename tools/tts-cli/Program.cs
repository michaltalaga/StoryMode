using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
using SessionStories.Core.Tts;
using SessionStories.Tts.Chatterbox;

try
{
    if (args.Length == 0)
        return Usage("missing verb");

    return args[0] switch
    {
        "prepare-voice" => PrepareVoice(args[1..]),
        "synth" => Synth(args[1..]),
        "synth-piper" => SynthPiper(args[1..]),
        "synth-container" => SynthContainer(args[1..]),
        "parity" => Parity(args[1..]),
        _ => Usage($"unknown verb '{args[0]}'"),
    };
}
catch (UsageException ex)
{
    return Usage(ex.Message);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int PrepareVoice(string[] rest)
{
    var flags = ParseFlags(rest, []);
    var wav = Require(flags, "wav");
    var id = Require(flags, "id");
    var (modelDir, cacheDir) = ResolveDirs(flags);

    if (!File.Exists(wav))
        throw new UsageException($"reference wav not found: {wav}");

    var options = new ChatterboxOptions { ModelDir = modelDir, VoiceCacheDir = cacheDir };
    using var provider = new ChatterboxOnnxProvider(options);
    provider.PrepareVoiceAsync(wav, id).GetAwaiter().GetResult();
    Console.WriteLine($"voice '{id}' prepared into {cacheDir}");
    return 0;
}

static int Synth(string[] rest)
{
    var flags = ParseFlags(rest, ["cpu"]);
    var textFile = Require(flags, "text-file");
    var voice = Require(flags, "voice");
    var lang = Require(flags, "lang");
    var outPath = Require(flags, "out");
    var (modelDir, cacheDir) = ResolveDirs(flags);

    if (!File.Exists(textFile))
        throw new UsageException($"text file not found: {textFile}");

    var knobs = new Dictionary<string, double>();
    AddKnob(flags, knobs, "exaggeration");
    AddKnob(flags, knobs, "cfg");
    AddKnob(flags, knobs, "temperature");
    int? seed = flags.TryGetValue("seed", out var seedText) ? ParseInt(seedText, "seed") : null;

    var options = new ChatterboxOptions
    {
        ModelDir = modelDir,
        VoiceCacheDir = cacheDir,
        ForceCpu = flags.ContainsKey("cpu"),
        RepetitionPenalty = flags.TryGetValue("rep-penalty", out var rpText)
            ? ParseDouble(rpText, "rep-penalty")
            : 1.2,
    };

    using var provider = new ChatterboxOnnxProvider(options)
    {
        AlsoWriteWavPath = flags.GetValueOrDefault("wav-out"),
    };

    EnsureParentDir(outPath);
    var request = new TtsRequest(
        File.ReadAllText(textFile), lang, voice, outPath,
        knobs.Count > 0 ? knobs : null, seed);

    provider.SynthesizeAsync(request, new ConsoleProgress()).GetAwaiter().GetResult();
    Console.WriteLine($"wrote {outPath}");
    return 0;
}

/// <summary>
/// Renders through a piper bundle. Same panel-bypass promise as `synth`, for the engine whose
/// voices are fixed trained models — and how the shipped gallery samples get made.
/// </summary>
static int SynthPiper(string[] rest)
{
    var flags = ParseFlags(rest, []);
    var textFile = Require(flags, "text-file");
    var bundle = Require(flags, "bundle");
    var lang = Require(flags, "lang");
    var outPath = Require(flags, "out");

    if (!File.Exists(textFile))
        throw new UsageException($"text file not found: {textFile}");

    var modelsRoot = flags.GetValueOrDefault("models-root")
        ?? Path.Combine(FindRepoRoot(), "models", "piper");

    var knobs = new Dictionary<string, double>();
    AddKnob(flags, knobs, "speed");

    var provider = new SessionStories.Tts.Piper.PiperOnnxProvider(
        new SessionStories.Tts.Piper.PiperOptions { ModelsRoot = modelsRoot });

    EnsureParentDir(outPath);
    var request = new TtsRequest(
        File.ReadAllText(textFile), lang, bundle, outPath,
        knobs.Count > 0 ? knobs : null,
        EngineData: new Dictionary<string, string> { ["bundle"] = bundle });

    provider.SynthesizeAsync(request, new ConsoleProgress()).GetAwaiter().GetResult();
    Console.WriteLine($"wrote {outPath}");
    return 0;
}

/// <summary>
/// Renders through one of the container engines. Same panel-bypass promise as `synth`, and how
/// their gallery samples get made — the container is already running, so this only needs to know
/// which port to talk to. Deliberately takes the engine's shape as flags rather than importing the
/// API's registrations: those belong to the app, and duplicating them here would let the two drift.
/// </summary>
static int SynthContainer(string[] rest)
{
    var flags = ParseFlags(rest, ["no-clone"]);
    var textFile = Require(flags, "text-file");
    var name = Require(flags, "engine");
    var lang = Require(flags, "lang");
    var voice = Require(flags, "voice");
    var outPath = Require(flags, "out");
    var port = ParseInt(Require(flags, "port"), "port");

    if (!File.Exists(textFile))
        throw new UsageException($"text file not found: {textFile}");

    var repoRoot = FindRepoRoot();
    var knobs = new Dictionary<string, double>();
    AddKnob(flags, knobs, "temperature");
    AddKnob(flags, knobs, "speed");

    var engine = new SessionStories.Tts.Container.ContainerTtsEngine(
        Id: name,
        Image: $"storymode-{name}:latest",
        ContainerName: flags.GetValueOrDefault("container") ?? $"storymode-{name}",
        Port: port,
        ModelsRoot: flags.GetValueOrDefault("models-root") ?? Path.Combine(repoRoot, "models", name),
        Languages: [lang],
        StylePresets: SessionStories.Tts.Container.ContainerStylePresets.Default,
        LicenceNote: "",
        Accepted: true,
        Clones: !flags.ContainsKey("no-clone"));
    var mounts = new SessionStories.Tts.Container.ContainerTtsMounts(
        VoicesRoot: flags.GetValueOrDefault("voices-root") ?? Path.Combine(repoRoot, "library", "voices"));

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    var provider = new SessionStories.Tts.Container.ContainerTtsProvider(engine, mounts, http);

    // A cloning engine has to be taught the voice before it can read in it. --reference-wav names
    // a file under the voices root; without it the voice is assumed to be prepared already.
    if (flags.TryGetValue("reference-wav", out var referenceWav))
        provider.PrepareVoiceAsync(referenceWav, voice).GetAwaiter().GetResult();

    EnsureParentDir(outPath);
    var request = new TtsRequest(
        File.ReadAllText(textFile), lang, voice, outPath,
        knobs.Count > 0 ? knobs : null,
        EngineData: flags.TryGetValue("speaker", out var speaker)
            ? new Dictionary<string, string> { ["speaker"] = speaker }
            : null);

    var started = DateTimeOffset.UtcNow;
    provider.SynthesizeAsync(request, new ConsoleProgress()).GetAwaiter().GetResult();
    Console.WriteLine($"wrote {outPath} in {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s");
    return 0;
}

static int Parity(string[] rest)
{
    var flags = ParseFlags(rest, ["cpu"]);
    var lang = Require(flags, "lang");
    var dumpPath = Require(flags, "dump-tokens");
    var (modelDir, cacheDir) = ResolveDirs(flags);

    string text;
    if (flags.TryGetValue("text", out var inlineText))
    {
        if (flags.ContainsKey("text-file"))
            throw new UsageException("pass either --text or --text-file, not both");
        text = inlineText;
    }
    else if (flags.TryGetValue("text-file", out var textFile))
    {
        if (!File.Exists(textFile))
            throw new UsageException($"text file not found: {textFile}");
        text = File.ReadAllText(textFile);
    }
    else
    {
        throw new UsageException("parity requires --text or --text-file");
    }

    var voiceWav = flags.GetValueOrDefault("voice") ?? Path.Combine(modelDir, "default_voice.wav");
    if (!File.Exists(voiceWav))
        throw new UsageException($"reference wav not found: {voiceWav}");

    // The Python reference defaults to exaggeration=0.5; parity must match it, not the provider default.
    float exaggeration = flags.TryGetValue("exaggeration", out var exText)
        ? (float)ParseDouble(exText, "exaggeration")
        : 0.5f;
    int maxTokens = flags.TryGetValue("max-tokens", out var mtText) ? ParseInt(mtText, "max-tokens") : 1000;

    var options = new ChatterboxOptions
    {
        ModelDir = modelDir,
        VoiceCacheDir = cacheDir,
        ForceCpu = flags.ContainsKey("cpu"),
    };

    using var sessions = new OnnxSessionSet(options);
    var tokenizer = ChatterboxTokenizer.Load(Path.Combine(modelDir, "tokenizer.json"));
    var conditionals = ChatterboxOnnxProvider.ComputeConditionals(sessions, AudioPostProcessor.LoadMono24k(voiceWav));

    // No chunking: the whole text is a single sequence so tokens compare 1:1 with the reference.
    var inputIds = tokenizer.Encode($"[{lang.ToLowerInvariant()}]{text}");

    var sampler = new SpeechTokenSampler(temperature: 0, minP: 0.05, topP: 1.0, repetitionPenalty: 1.2, seed: 0);
    var result = SpeechTokenGenerator.GenerateDetailed(
        sessions, conditionals, inputIds, exaggeration, cfgWeight: 0, sampler, maxTokens,
        onProgress: n =>
        {
            if (n % 25 == 0)
                Console.Error.Write($"\r{n} tokens");
        });
    Console.Error.WriteLine();

    var generated = new List<long>(result.Tokens.Length + 2) { SpeechTokenGenerator.StartSpeechToken };
    generated.AddRange(result.Tokens);
    if (result.SawStop)
        generated.Add(SpeechTokenGenerator.StopSpeechToken);

    EnsureParentDir(dumpPath);
    File.WriteAllText(dumpPath, JsonSerializer.Serialize(new { input_ids = inputIds, generated }));
    Console.WriteLine($"wrote {dumpPath} ({inputIds.Length} input ids, {generated.Count} generated tokens, sawStop={result.SawStop})");

    if (flags.TryGetValue("wav-out", out var wavOut))
    {
        var samples = ChatterboxOnnxProvider.DecodeToWaveform(sessions, conditionals, result.Tokens);
        EnsureParentDir(wavOut);
        // Raw 24 kHz float wav, no post-processing, for direct comparison with the reference output.
        using var writer = new WaveFileWriter(wavOut, WaveFormat.CreateIeeeFloatWaveFormat(24000, 1));
        writer.WriteSamples(samples, 0, samples.Length);
        Console.WriteLine($"wrote {wavOut} ({samples.Length} samples @ 24000 Hz)");
    }

    return 0;
}

static Dictionary<string, string> ParseFlags(string[] rest, HashSet<string> boolFlags)
{
    var flags = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < rest.Length; i++)
    {
        var arg = rest[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"unexpected argument '{arg}'");
        var name = arg[2..];
        if (boolFlags.Contains(name))
        {
            flags[name] = "true";
            continue;
        }
        if (i + 1 >= rest.Length)
            throw new UsageException($"missing value for --{name}");
        flags[name] = rest[++i];
    }
    return flags;
}

static string Require(Dictionary<string, string> flags, string name)
    => flags.TryGetValue(name, out var value) ? value : throw new UsageException($"missing required flag --{name}");

static (string ModelDir, string CacheDir) ResolveDirs(Dictionary<string, string> flags)
{
    var root = FindRepoRoot();
    var modelDir = flags.GetValueOrDefault("model-dir") ?? Path.Combine(root, "models", "chatterbox");
    var cacheDir = flags.GetValueOrDefault("cache-dir") ?? Path.Combine(root, "library", "voice-cache");
    return (modelDir, cacheDir);
}

static string FindRepoRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StoryMode.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
    }
    return Directory.GetCurrentDirectory();
}

static void AddKnob(Dictionary<string, string> flags, Dictionary<string, double> knobs, string name)
{
    if (flags.TryGetValue(name, out var text))
        knobs[name] = ParseDouble(text, name);
}

static double ParseDouble(string text, string flag)
    => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new UsageException($"--{flag} expects a number, got '{text}'");

static int ParseInt(string text, string flag)
    => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new UsageException($"--{flag} expects an integer, got '{text}'");

static void EnsureParentDir(string path)
{
    var parent = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(parent))
        Directory.CreateDirectory(parent);
}

static int Usage(string? error)
{
    if (error is not null)
        Console.Error.WriteLine($"usage error: {error}");
    Console.Error.WriteLine("""
        tts-cli - Session Stories Chatterbox ONNX TTS

        usage:
          tts-cli prepare-voice --wav <path> --id <voiceId> [--model-dir <dir>] [--cache-dir <dir>]

          tts-cli synth --text-file <path> --voice <voiceId-or-wav> --lang <code> --out <mp3>
                        [--exaggeration <0..1>] [--cfg <0..1>] [--temperature <0..2>] [--seed <int>]
                        [--cpu] [--wav-out <path>] [--model-dir <dir>] [--cache-dir <dir>]

          tts-cli synth-piper --text-file <path> --bundle <bundleFolder> --lang <code> --out <mp3>
                        [--speed <0.5..2>] [--models-root <dir>]

          tts-cli synth-container --text-file <path> --engine <name> --port <n> --voice <voiceId>
                        --lang <code> --out <mp3> [--reference-wav <path>] [--speaker <name>]
                        [--no-clone] [--temperature <0.1..1.5>] [--speed <0.5..2>]
                        [--container <name>] [--models-root <dir>] [--voices-root <dir>]

          tts-cli parity (--text <string> | --text-file <path>) --lang <code> --dump-tokens <path.json>
                        [--voice <wav>] [--cpu] [--wav-out <path>] [--exaggeration <v>]
                        [--max-tokens <n>] [--model-dir <dir>]

        exit codes: 0 ok, 1 usage error, 2 runtime failure
        """);
    return 1;
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class ConsoleProgress : IProgress<TtsProgress>
{
    public void Report(TtsProgress value)
        => Console.WriteLine($"[{value.ChunkIndex}/{value.ChunkCount}] {value.Message}");
}
