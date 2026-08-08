"""Python parity reference for the Chatterbox multilingual ONNX pipeline.

Adapted from the verbatim example in docs/reference/chatterbox-onnx-readme.md with
these deliberate changes (everything else, in particular the KV-cache generation
loop, is kept identical to the reference):

- models and tokenizer load from a local directory instead of hf_hub_download
- the zh/ja/he/ko text normalizers are removed; those languages are rejected
  (text preprocessing is ONLY the "[lang]" tag prepend)
- greedy argmax with repetition penalty 1.2, CPU execution provider only
- optional JSON dump of token ids for comparison against the C# implementation
- no watermarking
"""

import argparse
import json
import sys
from pathlib import Path

import librosa
import numpy as np
import onnxruntime
import soundfile as sf
from transformers import AutoTokenizer

S3GEN_SR = 24000
START_SPEECH_TOKEN = 6561
STOP_SPEECH_TOKEN = 6562
EXAGGERATION = 0.5  # fixed to the reference default; must match the C# side

SUPPORTED_LANGUAGES = {
    "ar": "Arabic",
    "da": "Danish",
    "de": "German",
    "el": "Greek",
    "en": "English",
    "es": "Spanish",
    "fi": "Finnish",
    "fr": "French",
    "hi": "Hindi",
    "it": "Italian",
    "ms": "Malay",
    "nl": "Dutch",
    "no": "Norwegian",
    "pl": "Polish",
    "pt": "Portuguese",
    "ru": "Russian",
    "sv": "Swedish",
    "sw": "Swahili",
    "tr": "Turkish",
}

# Languages whose reference pipeline requires an extra normalizer we do not port.
EXCLUDED_LANGUAGES = {"zh", "ja", "he", "ko"}


class RepetitionPenaltyLogitsProcessor:
    def __init__(self, penalty: float):
        if not isinstance(penalty, float) or not (penalty > 0):
            raise ValueError(f"`penalty` must be a strictly positive float, but is {penalty}")
        self.penalty = penalty

    def __call__(self, input_ids: np.ndarray, scores: np.ndarray) -> np.ndarray:
        score = np.take_along_axis(scores, input_ids, axis=1)
        score = np.where(score < 0, score * self.penalty, score / self.penalty)
        scores_processed = scores.copy()
        np.put_along_axis(scores_processed, input_ids, score, axis=1)
        return scores_processed


def prepare_language(txt: str, language_id: str) -> str:
    assert language_id.lower() not in EXCLUDED_LANGUAGES, (
        f"Language '{language_id}' requires a normalizer that is out of scope for this parity reference."
    )
    # Prepend language token (this is the ONLY preprocessing for supported languages).
    if language_id:
        txt = f"[{language_id.lower()}]{txt}"
    return txt


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Chatterbox ONNX parity reference (greedy, CPU, fp16 LM)."
    )
    text_group = parser.add_mutually_exclusive_group(required=True)
    text_group.add_argument("--text", help="Text to synthesize.")
    text_group.add_argument("--text-file", help="UTF-8 file containing the text to synthesize.")
    parser.add_argument("--lang", default="en", help="Language id, e.g. en or pl (default: en).")
    parser.add_argument(
        "--voice",
        default=None,
        help="Reference voice wav path (default: <model-dir>/default_voice.wav).",
    )
    parser.add_argument(
        "--model-dir",
        default=None,
        help="Directory with the ONNX models and tokenizer.json "
        "(default: ../../models/chatterbox relative to this script).",
    )
    parser.add_argument(
        "--lm",
        default="language_model_fp16.onnx",
        help="Language model file name inside --model-dir (default: language_model_fp16.onnx).",
    )
    parser.add_argument("--max-new-tokens", type=int, default=1000)
    parser.add_argument(
        "--dump-tokens",
        default=None,
        metavar="PATH.json",
        help='Write {"input_ids": [...], "generated": [...]} as JSON. '
        '"generated" includes the START seed and, if reached, the STOP token.',
    )
    parser.add_argument("--wav", default=None, help="Optional output wav path (24 kHz, no watermark).")
    return parser.parse_args()


def resolve_model_dir(arg: str | None) -> Path:
    if arg:
        return Path(arg).expanduser().resolve()
    return (Path(__file__).resolve().parent / ".." / ".." / "models" / "chatterbox").resolve()


def require_files(model_dir: Path, lm_name: str) -> None:
    required = ["speech_encoder.onnx", "embed_tokens.onnx", "conditional_decoder.onnx", lm_name, "tokenizer.json"]
    missing = [name for name in required if not (model_dir / name).is_file()]
    if missing:
        sys.exit(
            f"Missing model files in {model_dir}: {', '.join(missing)}. "
            "The model download may still be in progress."
        )


def main() -> None:
    args = parse_args()
    language_id = args.lang.lower()
    if language_id in EXCLUDED_LANGUAGES or language_id not in SUPPORTED_LANGUAGES:
        sys.exit(
            f"Unsupported language_id '{args.lang}'. Supported: {', '.join(sorted(SUPPORTED_LANGUAGES))}"
        )

    model_dir = resolve_model_dir(args.model_dir)
    require_files(model_dir, args.lm)

    text = args.text if args.text is not None else Path(args.text_file).read_text(encoding="utf-8")
    target_voice_path = args.voice if args.voice else str(model_dir / "default_voice.wav")
    if not Path(target_voice_path).is_file():
        sys.exit(f"Voice wav not found: {target_voice_path}")

    providers = ["CPUExecutionProvider"]
    speech_encoder_session = onnxruntime.InferenceSession(str(model_dir / "speech_encoder.onnx"), providers=providers)
    embed_tokens_session = onnxruntime.InferenceSession(str(model_dir / "embed_tokens.onnx"), providers=providers)
    llama_with_past_session = onnxruntime.InferenceSession(str(model_dir / args.lm), providers=providers)
    cond_decoder_session = (
        onnxruntime.InferenceSession(str(model_dir / "conditional_decoder.onnx"), providers=providers)
        if args.wav
        else None
    )

    audio_values, _ = librosa.load(target_voice_path, sr=S3GEN_SR)
    audio_values = audio_values[np.newaxis, :].astype(np.float32)

    ## Prepare input
    tokenizer = AutoTokenizer.from_pretrained(str(model_dir))
    text = prepare_language(text, language_id)
    input_ids = tokenizer(text, return_tensors="np")["input_ids"].astype(np.int64)

    position_ids = np.where(
        input_ids >= START_SPEECH_TOKEN,
        0,
        np.arange(input_ids.shape[1])[np.newaxis, :] - 1
    )

    ort_embed_tokens_inputs = {
        "input_ids": input_ids,
        "position_ids": position_ids.astype(np.int64),
        "exaggeration": np.array([EXAGGERATION], dtype=np.float32)
    }

    ## Instantiate the logits processors.
    repetition_penalty = 1.2
    repetition_penalty_processor = RepetitionPenaltyLogitsProcessor(penalty=repetition_penalty)

    # The fp16 export has a mixed interface: inputs_embeds/logits are fp32 while the
    # KV-cache tensors are fp16. Probe each input class separately.
    lm_input_types = {i.name: i.type for i in llama_with_past_session.get_inputs()}
    embeds_dtype = np.float16 if lm_input_types.get("inputs_embeds") == "tensor(float16)" else np.float32
    kv_dtype = np.float16 if lm_input_types.get("past_key_values.0.key") == "tensor(float16)" else np.float32

    num_hidden_layers = 30
    num_key_value_heads = 16
    head_dim = 64

    generate_tokens = np.array([[START_SPEECH_TOKEN]])

    prompt_token = ref_x_vector = prompt_feat = None

    # ---- Generation Loop using kv_cache (identical mechanics to the reference) ----
    for i in range(args.max_new_tokens):

        inputs_embeds = embed_tokens_session.run(None, ort_embed_tokens_inputs)[0]
        if i == 0:
            ort_speech_encoder_input = {
                "audio_values": audio_values,
            }
            cond_emb, prompt_token, ref_x_vector, prompt_feat = speech_encoder_session.run(None, ort_speech_encoder_input)
            inputs_embeds = np.concatenate((cond_emb, inputs_embeds), axis=1)

            ## Prepare llm inputs
            batch_size, seq_len, _ = inputs_embeds.shape
            past_key_values = {
                f"past_key_values.{layer}.{kv}": np.zeros([batch_size, num_key_value_heads, 0, head_dim], dtype=kv_dtype)
                for layer in range(num_hidden_layers)
                for kv in ("key", "value")
            }
            attention_mask = np.ones((batch_size, seq_len), dtype=np.int64)
        logits, *present_key_values = llama_with_past_session.run(None, dict(
            inputs_embeds=inputs_embeds.astype(embeds_dtype),
            attention_mask=attention_mask,
            **past_key_values,
        ))

        logits = logits[:, -1, :].astype(np.float32)
        next_token_logits = repetition_penalty_processor(generate_tokens, logits)

        next_token = np.argmax(next_token_logits, axis=-1, keepdims=True).astype(np.int64)
        generate_tokens = np.concatenate((generate_tokens, next_token), axis=-1)
        if (next_token.flatten() == STOP_SPEECH_TOKEN).all():
            break

        # Get embedding for the new token.
        position_ids = np.full(
            (input_ids.shape[0], 1),
            i + 1,
            dtype=np.int64,
        )
        ort_embed_tokens_inputs["input_ids"] = next_token
        ort_embed_tokens_inputs["position_ids"] = position_ids

        ## Update values for next generation loop
        attention_mask = np.concatenate([attention_mask, np.ones((batch_size, 1), dtype=np.int64)], axis=1)
        for j, key in enumerate(past_key_values):
            past_key_values[key] = present_key_values[j]

        if (i + 1) % 100 == 0:
            print(f"  generated {i + 1} tokens...", file=sys.stderr)

    generated = generate_tokens[0].tolist()
    stopped = generated[-1] == STOP_SPEECH_TOKEN
    print(
        f"Generated {len(generated)} tokens (incl. START seed){' incl. STOP' if stopped else ' (no STOP; hit max-new-tokens)'}.",
        file=sys.stderr,
    )

    if args.dump_tokens:
        dump_path = Path(args.dump_tokens)
        dump_path.parent.mkdir(parents=True, exist_ok=True)
        dump_path.write_text(
            json.dumps({"input_ids": input_ids[0].tolist(), "generated": generated}, indent=2),
            encoding="utf-8",
        )
        print(f"Token dump written to {dump_path}", file=sys.stderr)

    if args.wav:
        # Reference slices [1:-1]: strips the START seed and the final token
        # (STOP when generation stopped naturally).
        speech_tokens = generate_tokens[:, 1:-1]
        speech_tokens = np.concatenate([prompt_token, speech_tokens], axis=1)
        cond_incoder_input = {
            "speech_tokens": speech_tokens,
            "speaker_embeddings": ref_x_vector,
            "speaker_features": prompt_feat,
        }
        wav = cond_decoder_session.run(None, cond_incoder_input)[0]
        wav = np.squeeze(wav, axis=0)
        wav_path = Path(args.wav)
        wav_path.parent.mkdir(parents=True, exist_ok=True)
        sf.write(str(wav_path), wav, S3GEN_SR)
        print(f"{wav_path} was successfully saved", file=sys.stderr)


if __name__ == "__main__":
    main()
