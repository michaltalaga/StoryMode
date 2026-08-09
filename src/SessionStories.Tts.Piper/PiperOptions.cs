namespace SessionStories.Tts.Piper;

public sealed record PiperOptions
{
    /// <summary>
    /// Directory containing the sherpa-onnx piper bundles (e.g. &lt;repo&gt;\models\piper). Which bundle
    /// a voice uses is recorded on the voice itself (<c>engineData.bundle</c>), written by the
    /// installer — it is deliberately not configured here, because a voice you cannot add without
    /// editing appsettings is a voice the app cannot really offer.
    /// </summary>
    public required string ModelsRoot { get; init; }
}
