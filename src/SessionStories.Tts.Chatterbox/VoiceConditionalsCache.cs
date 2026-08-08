using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SessionStories.Tts.Chatterbox;

/// <summary>Speech-encoder outputs for one reference voice: flat tensor data with ONNX dims.</summary>
public sealed record VoiceConditionals(
    float[] CondEmb, long[] CondEmbDims,
    long[] PromptToken, long[] PromptTokenDims,
    float[] RefXVector, long[] RefXVectorDims,
    float[] PromptFeat, long[] PromptFeatDims);

/// <summary>
/// Disk cache of voice conditioning: cacheDir/{voiceId}/ holds manifest.json plus one raw
/// little-endian .bin per tensor, so every future render of a prepared voice uses byte-identical
/// conditioning without re-running the speech encoder.
/// </summary>
public sealed class VoiceConditionalsCache
{
    private const string ManifestFileName = "manifest.json";
    private const string Float32 = "float32";
    private const string Int64 = "int64";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _cacheDir;

    public VoiceConditionalsCache(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        _cacheDir = Path.GetFullPath(cacheDir);
        Directory.CreateDirectory(_cacheDir);
    }

    public bool TryLoad(string voiceId, out VoiceConditionals conditionals)
    {
        conditionals = null!;
        string voiceDir = GetVoiceDir(voiceId);
        string manifestPath = Path.Combine(voiceDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            return false;

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (manifest is null ||
            string.IsNullOrEmpty(manifest.SourceWavPath) ||
            string.IsNullOrEmpty(manifest.SourceWavSha256) ||
            manifest.CreatedUtc == default ||
            manifest.Tensors is null)
        {
            return false;
        }

        if (!TryValidateTensor(voiceDir, manifest.Tensors, "cond_emb", Float32, out string condEmbPath, out long[] condEmbDims) ||
            !TryValidateTensor(voiceDir, manifest.Tensors, "prompt_token", Int64, out string promptTokenPath, out long[] promptTokenDims) ||
            !TryValidateTensor(voiceDir, manifest.Tensors, "ref_x_vector", Float32, out string refXVectorPath, out long[] refXVectorDims) ||
            !TryValidateTensor(voiceDir, manifest.Tensors, "prompt_feat", Float32, out string promptFeatPath, out long[] promptFeatDims))
        {
            return false;
        }

        conditionals = new VoiceConditionals(
            ReadTensor<float>(condEmbPath), condEmbDims,
            ReadTensor<long>(promptTokenPath), promptTokenDims,
            ReadTensor<float>(refXVectorPath), refXVectorDims,
            ReadTensor<float>(promptFeatPath), promptFeatDims);
        return true;
    }

    public VoiceConditionals Create(string voiceId, string referenceWavPath, Func<float[], VoiceConditionals> computeFromMono24k)
    {
        ArgumentNullException.ThrowIfNull(computeFromMono24k);
        string voiceDir = GetVoiceDir(voiceId);
        if (!File.Exists(referenceWavPath))
        {
            throw new FileNotFoundException(
                $"Reference wav for voice '{voiceId}' not found at '{referenceWavPath}'.",
                referenceWavPath);
        }

        string sourceWavPath = Path.GetFullPath(referenceWavPath);
        string sourceWavSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourceWavPath)));
        float[] mono24k = AudioPostProcessor.LoadMono24k(sourceWavPath);
        var conditionals = computeFromMono24k(mono24k);

        ValidateDims("cond_emb", conditionals.CondEmb.LongLength, conditionals.CondEmbDims);
        ValidateDims("prompt_token", conditionals.PromptToken.LongLength, conditionals.PromptTokenDims);
        ValidateDims("ref_x_vector", conditionals.RefXVector.LongLength, conditionals.RefXVectorDims);
        ValidateDims("prompt_feat", conditionals.PromptFeat.LongLength, conditionals.PromptFeatDims);

        string tempDir = voiceDir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(tempDir);
            WriteTensor(tempDir, "cond_emb.bin", conditionals.CondEmb);
            WriteTensor(tempDir, "prompt_token.bin", conditionals.PromptToken);
            WriteTensor(tempDir, "ref_x_vector.bin", conditionals.RefXVector);
            WriteTensor(tempDir, "prompt_feat.bin", conditionals.PromptFeat);

            var manifest = new Manifest
            {
                SourceWavPath = sourceWavPath,
                SourceWavSha256 = sourceWavSha256,
                CreatedUtc = DateTime.UtcNow,
                Tensors = new Dictionary<string, TensorInfo>
                {
                    ["cond_emb"] = new() { File = "cond_emb.bin", Dtype = Float32, Dims = conditionals.CondEmbDims },
                    ["prompt_token"] = new() { File = "prompt_token.bin", Dtype = Int64, Dims = conditionals.PromptTokenDims },
                    ["ref_x_vector"] = new() { File = "ref_x_vector.bin", Dtype = Float32, Dims = conditionals.RefXVectorDims },
                    ["prompt_feat"] = new() { File = "prompt_feat.bin", Dtype = Float32, Dims = conditionals.PromptFeatDims },
                },
            };
            File.WriteAllText(Path.Combine(tempDir, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOptions));

            if (Directory.Exists(voiceDir))
                Directory.Delete(voiceDir, recursive: true);
            Directory.Move(tempDir, voiceDir);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never mask the original failure.
            }
        }

        return conditionals;
    }

    private string GetVoiceDir(string voiceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        if (voiceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || voiceId is "." or "..")
            throw new ArgumentException($"Voice id '{voiceId}' cannot be used as a directory name.", nameof(voiceId));
        return Path.Combine(_cacheDir, voiceId);
    }

    private static bool TryValidateTensor(
        string voiceDir,
        Dictionary<string, TensorInfo> tensors,
        string name,
        string expectedDtype,
        out string path,
        out long[] dims)
    {
        path = "";
        dims = [];
        if (!tensors.TryGetValue(name, out var info) ||
            info is null ||
            info.Dtype != expectedDtype ||
            string.IsNullOrEmpty(info.File) ||
            info.Dims is not { Length: > 0 })
        {
            return false;
        }

        long elementCount = 1;
        foreach (long dim in info.Dims)
        {
            if (dim <= 0)
                return false;
            elementCount *= dim;
        }

        string binPath = Path.Combine(voiceDir, info.File);
        int elementSize = expectedDtype == Int64 ? sizeof(long) : sizeof(float);
        var binFile = new FileInfo(binPath);
        if (!binFile.Exists || binFile.Length != elementCount * elementSize)
            return false;

        path = binPath;
        dims = info.Dims;
        return true;
    }

    private static void ValidateDims(string name, long elementCount, long[] dims)
    {
        long product = dims.Length == 0 ? 0 : 1;
        foreach (long dim in dims)
            product *= dim;
        if (product != elementCount)
        {
            throw new InvalidOperationException(
                $"Speech encoder tensor '{name}' has dims [{string.Join(", ", dims)}] " +
                $"which do not match its {elementCount} elements.");
        }
    }

    // .bin files hold the tensor's raw bytes; all supported .NET targets are little-endian.
    private static void WriteTensor<T>(string dir, string fileName, T[] data) where T : unmanaged
    {
        using var stream = File.Create(Path.Combine(dir, fileName));
        stream.Write(MemoryMarshal.AsBytes(data.AsSpan()));
    }

    private static T[] ReadTensor<T>(string path) where T : unmanaged
    {
        byte[] bytes = File.ReadAllBytes(path);
        var data = new T[bytes.Length / Unsafe.SizeOf<T>()];
        bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(data.AsSpan()));
        return data;
    }

    private sealed class Manifest
    {
        public string? SourceWavPath { get; set; }
        public string? SourceWavSha256 { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Dictionary<string, TensorInfo>? Tensors { get; set; }
    }

    private sealed class TensorInfo
    {
        public string? File { get; set; }
        public string? Dtype { get; set; }
        public long[]? Dims { get; set; }
    }
}
