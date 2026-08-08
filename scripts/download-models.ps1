# Downloads the ML models Session Stories needs into <repo>/models/.
# Dumb by design: curl.exe with resume (-C -), skip when the file already exists,
# print SHA256 at the end so a human can eyeball them against the HF pages.
# Re-run safely at any time.

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$chatterboxDir = Join-Path $repo 'models\chatterbox'
$whisperDir = Join-Path $repo 'models\whisper'
New-Item -ItemType Directory -Force $chatterboxDir | Out-Null
New-Item -ItemType Directory -Force $whisperDir | Out-Null

$cb = 'https://huggingface.co/onnx-community/chatterbox-multilingual-ONNX/resolve/main'
$files = @(
    # ONNX graphs + external weight files (.onnx_data must sit next to its .onnx)
    @{ url = "$cb/onnx/speech_encoder.onnx";             out = "$chatterboxDir\speech_encoder.onnx" }
    @{ url = "$cb/onnx/speech_encoder.onnx_data";        out = "$chatterboxDir\speech_encoder.onnx_data" }
    @{ url = "$cb/onnx/embed_tokens.onnx";               out = "$chatterboxDir\embed_tokens.onnx" }
    @{ url = "$cb/onnx/embed_tokens.onnx_data";          out = "$chatterboxDir\embed_tokens.onnx_data" }
    @{ url = "$cb/onnx/language_model_fp16.onnx";        out = "$chatterboxDir\language_model_fp16.onnx" }
    @{ url = "$cb/onnx/language_model_fp16.onnx_data";   out = "$chatterboxDir\language_model_fp16.onnx_data" }
    @{ url = "$cb/onnx/conditional_decoder.onnx";        out = "$chatterboxDir\conditional_decoder.onnx" }
    @{ url = "$cb/onnx/conditional_decoder.onnx_data";   out = "$chatterboxDir\conditional_decoder.onnx_data" }
    @{ url = "$cb/tokenizer.json";                       out = "$chatterboxDir\tokenizer.json" }
    @{ url = "$cb/tokenizer_config.json";                out = "$chatterboxDir\tokenizer_config.json" }
    @{ url = "$cb/generation_config.json";               out = "$chatterboxDir\generation_config.json" }
    @{ url = "$cb/default_voice.wav";                    out = "$chatterboxDir\default_voice.wav" }
    # Whisper large-v3 for Whisper.net (ggml format)
    @{ url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin'; out = "$whisperDir\ggml-large-v3.bin" }
)

foreach ($f in $files) {
    if (Test-Path $f.out) {
        Write-Host "skip (exists): $(Split-Path $f.out -Leaf)"
        continue
    }
    Write-Host "downloading: $($f.url)"
    & curl.exe -L --fail -C - -o $f.out $f.url
    if ($LASTEXITCODE -ne 0) { throw "download failed: $($f.url)" }
}

Write-Host "`nSHA256 (compare against the Hugging Face file pages):"
foreach ($f in $files) {
    if (Test-Path $f.out) {
        $h = (Get-FileHash $f.out -Algorithm SHA256).Hash.ToLower()
        $size = [math]::Round((Get-Item $f.out).Length / 1MB, 1)
        Write-Host ("{0}  {1,8} MB  {2}" -f $h, $size, (Split-Path $f.out -Leaf))
    }
}
