# Chatterbox ONNX parity reference

`reference_inference.py` is the Python ground truth for the C# Chatterbox pipeline in
`src/SessionStories.Tts.Chatterbox`. It is the verbatim example from
`docs/reference/chatterbox-onnx-readme.md`, adapted to load models from the local
`models/chatterbox` directory, with the zh/ja/he/ko normalizers removed (only the
`[lang]` tag prepend remains) and no watermarking. The KV-cache generation loop is
unchanged from the reference.

## Setup — Docker only, no Python on the machine

This is the one Python artifact in the project, kept solely as validation ground
truth (re-run it after ONNX Runtime upgrades or loop changes). **No Python is ever
installed on the machine**: it runs in a disposable container.

The models must already be present in `models/chatterbox` at the repo root
(`speech_encoder.onnx`, `embed_tokens.onnx`, `language_model_fp16.onnx`,
`conditional_decoder.onnx` plus their `.onnx_data` files, `tokenizer.json`,
`default_voice.wav`). The script exits with a clear error if any are missing.

## Running

From the repo root, on Windows PowerShell (first run installs packages inside the
throwaway container, ~1 min; nothing persists after `--rm`):

```powershell
docker run --rm -v "${PWD}:/work" -w /work/tools/parity python:3.12-slim `
    sh -c "pip install -q -r requirements.txt && python reference_inference.py --text 'Hello there, adventurers.' --lang en --dump-tokens /work/models/py_tokens.json --wav /work/models/py_output.wav"
```

Options:

| Flag | Default | Meaning |
|---|---|---|
| `--text` / `--text-file` | (one required) | Inline text, or a UTF-8 file to read it from. |
| `--lang` | `en` | Language id (`pl`, `en`, ...). zh/ja/he/ko are rejected. |
| `--voice` | `<model-dir>/default_voice.wav` | Reference voice wav. |
| `--model-dir` | `../../models/chatterbox` (relative to the script) | Model directory. |
| `--lm` | `language_model_fp16.onnx` | LM file name inside the model dir. |
| `--max-new-tokens` | `1000` | Generation cap. |
| `--dump-tokens` | off | Write token ids as JSON (see below). |
| `--wav` | off | Also run the conditional decoder and write a 24 kHz wav. |

The token dump has the shape:

```json
{ "input_ids": [255, 4, ...], "generated": [6561, ..., 6562] }
```

`input_ids` is the tokenizer output for the language-tagged text; `generated` is the
full generated sequence including the leading START seed (6561) and, when generation
stopped naturally, the trailing STOP token (6562).

## Parity workflow

Both sides are configured to be deterministic and comparable:

- **same LM**: `language_model_fp16.onnx`
- **same execution provider**: CPU
- **same decoding**: greedy argmax with repetition penalty 1.2, exaggeration 0.5

To verify the C# port:

1. Pick a short text and run this script with `--dump-tokens py_tokens.json`.
2. Run the C# pipeline on the identical text/language/voice with its token dump
   enabled to produce `cs_tokens.json`.
3. Compare the two JSON files. `input_ids` must match exactly (tokenizer parity);
   `generated` must match exactly (embed/LM/sampling-loop parity). Any divergence
   pinpoints the stage where the port differs — a mismatch in `input_ids` is a
   tokenizer bug, a divergence at generated token *k* means the LM inputs (KV cache,
   attention mask, position ids) went wrong at step *k*.
4. Once tokens match, generate wavs on both sides (`--wav` here) and listen to
   confirm the conditional decoder and audio path sound identical.

Note that fp16 CPU kernels are deterministic run-to-run, so exact token equality is
the expected outcome, not an approximation.
