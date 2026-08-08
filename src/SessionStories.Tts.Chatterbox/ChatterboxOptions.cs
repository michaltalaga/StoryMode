namespace SessionStories.Tts.Chatterbox;

public sealed record ChatterboxOptions
{
    public required string ModelDir { get; init; }
    public required string VoiceCacheDir { get; init; }
    public bool ForceCpu { get; init; }
    public double Exaggeration { get; init; } = 0.65;
    public double CfgWeight { get; init; } = 0.3;
    public double Temperature { get; init; } = 0.8;
    public double MinP { get; init; } = 0.05;
    public double TopP { get; init; } = 1.0;
    public double RepetitionPenalty { get; init; } = 1.2;
    public int MaxTokensPerChunk { get; init; } = 1000;
    public string LanguageModelFile { get; init; } = "language_model_fp16.onnx";
}
