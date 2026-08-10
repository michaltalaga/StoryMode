using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SessionStories.Core.Tts;
using SessionStories.Tts.Chatterbox;

namespace SessionStories.Tts.Container;

/// <summary>
/// Any speech engine that runs in a container, spoken to over HTTP. One class serves all of them:
/// the differences between models live in their images, not here.
/// <para>
/// This exists because the best multilingual models have no ONNX export worth trusting, and the
/// alternative was hand-porting each one — the chatterbox port took weeks and a token-parity
/// harness. Python lives inside the image; the host never sees it. Chunking, stitching and mp3
/// encoding are shared with the in-process engines, so the audio is finished identically whichever
/// one produced it.
/// </para>
/// </summary>
public sealed class ContainerTtsProvider(
    ContainerTtsEngine engine,
    ContainerTtsMounts mounts,
    HttpClient http) : ITtsProvider
{
    public string Id => engine.Id;

    public TtsCapabilities Capabilities { get; } = Describe(engine);

    /// <summary>Capabilities without an instance — reading metadata must never start a container.</summary>
    public static TtsCapabilities Describe(ContainerTtsEngine engine) => new(
        Languages: engine.Languages,
        Knobs:
        [
            new TtsKnob("temperature", 0.1, 1.5, 0.65),
            new TtsKnob("speed", 0.5, 2.0, 1.0),
        ],
        OutputSampleRate: 44100,
        AppliesWatermark: false,
        SupportsVoiceCloning: engine.Clones,
        StylePresets: engine.StylePresets);

    private string BaseUrl => $"http://127.0.0.1:{engine.Port}";

    public async Task PrepareVoiceAsync(string referenceWavPath, string voiceId, CancellationToken ct = default)
    {
        // A fixed-voice engine has nothing to learn from a recording, and pretending otherwise
        // would make an install look like it did something it did not.
        if (!engine.Clones)
            return;

        await EnsureRunningAsync(ct);

        // The container sees the voices folder at /voices; hand it a path it can open.
        using var response = await http.PostAsJsonAsync($"{BaseUrl}/prepare", new
        {
            voiceId,
            referenceWav = "/voices/" + Path.GetFileName(referenceWavPath),
            // Some engines clone markedly better when told what the reference says. Written at
            // install time by whoever produced the voice; absent is fine.
            referenceText = ReadReferenceText(referenceWavPath),
        }, ct);
        await ThrowIfFailedAsync(response, $"preparing voice '{voiceId}'", ct);
    }

    public async Task SynthesizeAsync(
        TtsRequest request, IProgress<TtsProgress>? progress = null, CancellationToken ct = default)
    {
        var language = NormalizeLanguage(request.Language);
        if (!engine.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"'{engine.Id}' does not speak '{request.Language}'.");

        await EnsureRunningAsync(ct);

        var chunks = SentenceChunker.Chunk(
            request.Text, engine.MinChunkCharacters, engine.MaxChunkCharacters);
        if (chunks.Count == 0)
            throw new InvalidOperationException("No text to synthesize.");

        var audio = new List<ChunkAudio>(chunks.Count);
        var sampleRate = 24_000;

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TtsProgress(i, chunks.Count, $"Generating chunk {i + 1}/{chunks.Count}"));

            using var response = await http.PostAsJsonAsync($"{BaseUrl}/synthesize", new
            {
                voiceId = request.VoiceId,
                text = chunks[i].Text,
                language,
                temperature = Knob(request, "temperature", 0.65),
                speed = Knob(request, "speed", 1.0),
                // Fixed-voice engines pick a built-in speaker instead of cloning one; which
                // speaker is the voice's own engine-private business.
                speaker = request.EngineData?.GetValueOrDefault("speaker"),
            }, ct);
            await ThrowIfFailedAsync(response, $"rendering chunk {i + 1}", ct);

            var body = await response.Content.ReadFromJsonAsync<SynthesizeResponse>(ct)
                ?? throw new InvalidOperationException($"'{engine.Id}' returned an empty response.");
            sampleRate = body.SampleRate;
            audio.Add(new ChunkAudio(ReadFloatWav(Convert.FromBase64String(body.WavBase64)), chunks[i].StartsParagraph));
        }

        AudioPostProcessor.EncodeMp3(audio, request.OutputMp3Path,
            new AudioPostOptions { InputSampleRate = sampleRate });
        progress?.Report(new TtsProgress(chunks.Count, chunks.Count, "Encoding complete"));
    }

    /// <summary>A sibling .txt next to the reference wav, written at install time. Null when absent.</summary>
    private static string? ReadReferenceText(string referenceWavPath)
    {
        var path = Path.ChangeExtension(referenceWavPath, ".txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>"pl-PL" → "pl"; Chinese keeps its region, which is how these models name it.</summary>
    private static string NormalizeLanguage(string language)
    {
        var lower = language.Trim().ToLowerInvariant().Replace('_', '-');
        if (lower.StartsWith("zh"))
            return "zh-cn";
        var dash = lower.IndexOf('-');
        return dash < 0 ? lower : lower[..dash];
    }

    // ---- container lifecycle -------------------------------------------------------------

    /// <summary>
    /// Answers /health, or starts the container and waits for it to. Left running afterwards:
    /// loading a multi-gigabyte model costs tens of seconds and paying that per chunk would
    /// dwarf the render.
    /// </summary>
    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (await IsHealthyAsync(ct))
            return;

        if (!engine.Accepted)
            throw new InvalidOperationException(engine.LicenceNote);

        StartContainer();

        var deadline = DateTimeOffset.UtcNow + engine.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (await IsHealthyAsync(ct))
                return;
        }

        throw new TimeoutException(
            $"The '{engine.Id}' container did not become ready within " +
            $"{engine.StartupTimeout.TotalMinutes:0} minutes. Check it with: docker logs {engine.ContainerName}");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync($"{BaseUrl}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Not up, or not up yet — the caller decides whether that is fatal.
            return false;
        }
    }

    /// <summary>
    /// Stops every other engine container before starting this one. Same rule the in-process
    /// engines follow — whisper and chatterbox may never share VRAM — extended to containers,
    /// because several multi-billion-parameter models resident at once will not fit on one card.
    /// Jobs run serially, so exactly one engine is ever needed.
    /// </summary>
    private void StopOtherEngines()
    {
        var running = DockerCaptured("ps", "--filter", "name=storymode-", "--format", "{{.Names}}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in running)
        {
            if (!string.Equals(name, engine.ContainerName, StringComparison.Ordinal))
                Docker("stop", name);
        }
    }

    private void StartContainer()
    {
        StopOtherEngines();

        // An existing-but-stopped container starts far faster than creating one, and keeps what it
        // cached. Failure is fine: `run` below creates it.
        Docker("start", engine.ContainerName);
        if (DockerCaptured("inspect", "-f", "{{.State.Running}}", engine.ContainerName).Trim() == "true")
            return;

        Directory.CreateDirectory(engine.ModelsRoot);
        var args = new List<string>
        {
            "run", "-d",
            "--name", engine.ContainerName,
            "--gpus", "all",
            // Bound to loopback: this is a model server, not something to expose on the LAN.
            "-p", $"127.0.0.1:{engine.Port}:8020",
            "-v", $"{engine.ModelsRoot}:/models",
            "-v", $"{mounts.VoicesRoot}:/voices:ro",
            "--restart", "unless-stopped",
        };
        foreach (var (key, value) in engine.Environment)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }
        args.Add(engine.Image);

        if (Docker([.. args]) != 0)
        {
            throw new InvalidOperationException(
                $"Could not start the '{engine.Id}' container. Is Docker Desktop running, and has " +
                $"'{engine.Image}' been built? See scripts/build-tts-images.ps1.");
        }
    }

    private static int Docker(params string[] args)
    {
        try
        {
            using var process = Process.Start(DockerInfo(args));
            if (process is null)
                return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string DockerCaptured(params string[] args)
    {
        try
        {
            using var process = Process.Start(DockerInfo(args));
            if (process is null)
                return "";
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static ProcessStartInfo DockerInfo(string[] args)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Engine failed while {what}: {(int)response.StatusCode} {body}");
    }

    private static double Knob(TtsRequest request, string name, double fallback)
        => request.Knobs is not null && request.Knobs.TryGetValue(name, out var value) ? value : fallback;

    /// <summary>Reads the float32 mono wav the container returns, walking chunks rather than
    /// assuming a 44-byte header — encoders add LIST chunks.</summary>
    private static float[] ReadFloatWav(byte[] wav)
    {
        int offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (id == "data")
            {
                var count = Math.Min(size, wav.Length - (offset + 8)) / sizeof(float);
                var samples = new float[count];
                Buffer.BlockCopy(wav, offset + 8, samples, 0, count * sizeof(float));
                return samples;
            }
            offset += 8 + size + (size % 2);
        }
        throw new InvalidOperationException("The engine returned a wav with no data chunk.");
    }

    private sealed record SynthesizeResponse(
        [property: JsonPropertyName("sampleRate")] int SampleRate,
        [property: JsonPropertyName("wavBase64")] string WavBase64);
}
