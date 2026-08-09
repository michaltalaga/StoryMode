namespace SessionStories.Tts.Piper;

public sealed record PiperOptions
{
    /// <summary>Directory containing the sherpa-onnx piper bundles (e.g. &lt;repo&gt;\models\piper).</summary>
    public required string ModelsRoot { get; init; }

    /// <summary>
    /// Catalog voice id → bundle folder name under <see cref="ModelsRoot"/>
    /// (e.g. "narrator-pl-gosia" → "vits-piper-pl_PL-gosia-medium").
    /// </summary>
    public required IReadOnlyDictionary<string, string> VoiceModels { get; init; }
}
