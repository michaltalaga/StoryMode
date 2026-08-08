namespace SessionStories.Providers.Whisper;

public sealed class WhisperOptions
{
    public string ModelPath { get; set; } = Path.Combine("models", "whisper", "ggml-large-v3.bin");
}
