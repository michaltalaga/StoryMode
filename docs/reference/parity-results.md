# Token-parity results — C# port vs Python ONNX reference

Date: 2026-08-08. Input: "The elder of Marrowfield did not offer them chairs.", lang en,
default_voice.wav, greedy, cfg 0, exaggeration 0.5, `language_model_fp16.onnx`,
onnxruntime 1.28.0 both sides.

| Comparison | input_ids | generated tokens |
|---|---|---|
| Python (WSL/Linux) vs C# (Windows) | 38/38 identical | diverges at step 36 of 82 (near-tie flip) |
| Python (WSL/Linux) vs C# (linux-x64 build, WSL) | 38/38 identical | **83/83 identical** |

Conclusion: the C# pipeline (tokenizer incl. TemplateProcessing, prefill, KV-cache
present→past mapping, position ids, repetition penalty, greedy sampling, fp16 boundary
conversion) is **bit-correct** — same-platform output is byte-identical. The Windows
divergence is a Windows-vs-Linux difference in ORT CPU kernel rounding amplifying one
near-tie; it is a platform property, not a code defect, and is inaudible in practice.

Findings baked into the code during this exercise:

- The fp16 export has a **mixed interface**: `inputs_embeds` and `logits` are fp32;
  only `past_key_values.*` / `present.*` are fp16. Detect per tensor class via
  `NodeMetadata.ElementDataType` (the managed `Type` mapping for fp16 varies across
  OnnxRuntime releases).
- LM present outputs are named `present.{layer}.{key|value}`, not `present_key_values.*`;
  map presents to pasts by name.
- `tokenizer.json`'s template appends `[6563, 255(BOS), …text…, 0(EOS), 6561, 6561]` —
  two trailing START_SPEECH tokens. The reference position-id formula depends on this.
- Repetition penalty must be computed in float32 (numpy NEP-50 semantics); doing the
  arithmetic in double flips near-ties.

Reproduce: `tools/parity/README.md`.
