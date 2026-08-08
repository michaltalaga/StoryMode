using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SessionStories.Tts.Chatterbox;

/// <summary>
/// Faithful reader of the Chatterbox HF tokenizer.json. Hand-rolled rather than built on
/// Microsoft.ML.Tokenizers because the pipeline requires added tokens matched both before
/// normalization (language/event tags) and after it ([SPACE]), plus a TemplateProcessing
/// post-processor — none of which BpeTokenizer can express. Every stage replicates the
/// HuggingFace tokenizers crate; anything the file could specify but this reader does not
/// implement fails loudly at load time.
/// </summary>
public sealed class ChatterboxTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(int Left, int Right), (int Rank, int NewId)> _merges;
    private readonly (string Content, int Id)[] _rawAddedTokens;        // matched on raw text, longest-first
    private readonly (string Content, int Id)[] _normalizedAddedTokens; // matched on normalized text, longest-first
    private readonly (string Pattern, string Replacement)[] _replacements;
    private readonly bool _whitespacePreTokenizer;
    private readonly int[] _templatePrefix;
    private readonly int[] _templateSuffix;
    private readonly int _unkId;
    private readonly bool _fuseUnk;

    private ChatterboxTokenizer(
        Dictionary<string, int> vocab,
        Dictionary<(int, int), (int, int)> merges,
        (string, int)[] rawAddedTokens,
        (string, int)[] normalizedAddedTokens,
        (string, string)[] replacements,
        bool whitespacePreTokenizer,
        int[] templatePrefix,
        int[] templateSuffix,
        int unkId,
        bool fuseUnk)
    {
        _vocab = vocab;
        _merges = merges;
        _rawAddedTokens = rawAddedTokens;
        _normalizedAddedTokens = normalizedAddedTokens;
        _replacements = replacements;
        _whitespacePreTokenizer = whitespacePreTokenizer;
        _templatePrefix = templatePrefix;
        _templateSuffix = templateSuffix;
        _unkId = unkId;
        _fuseUnk = fuseUnk;
    }

    public static ChatterboxTokenizer Load(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException(
                $"Chatterbox tokenizer not found at '{tokenizerJsonPath}'. The model download may not have completed.",
                tokenizerJsonPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var root = doc.RootElement;

        var model = root.GetProperty("model");
        string? modelType = model.GetProperty("type").GetString();
        if (modelType != "BPE")
            throw Unsupported($"model.type '{modelType}'");
        if (model.TryGetProperty("dropout", out var dropout) && dropout.ValueKind is not JsonValueKind.Null)
            throw Unsupported("model.dropout");
        if (HasNonEmptyString(model, "continuing_subword_prefix"))
            throw Unsupported("model.continuing_subword_prefix");
        if (HasNonEmptyString(model, "end_of_word_suffix"))
            throw Unsupported("model.end_of_word_suffix");
        if (IsTrue(model, "byte_fallback"))
            throw Unsupported("model.byte_fallback");
        if (IsTrue(model, "ignore_merges"))
            throw Unsupported("model.ignore_merges");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
            vocab[entry.Name] = entry.Value.GetInt32();

        var merges = new Dictionary<(int, int), (int, int)>();
        int rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            string left, right;
            if (merge.ValueKind == JsonValueKind.String)
            {
                string s = merge.GetString()!;
                int space = s.IndexOf(' ');
                if (space <= 0 || s.IndexOf(' ', space + 1) >= 0)
                    throw new FormatException($"Malformed merge entry '{s}' in '{tokenizerJsonPath}'.");
                left = s[..space];
                right = s[(space + 1)..];
            }
            else
            {
                left = merge[0].GetString()!;
                right = merge[1].GetString()!;
            }

            if (!vocab.TryGetValue(left, out int leftId) ||
                !vocab.TryGetValue(right, out int rightId) ||
                !vocab.TryGetValue(left + right, out int mergedId))
            {
                throw new FormatException($"Merge '{left} {right}' references tokens missing from the vocab.");
            }

            merges[(leftId, rightId)] = (rank++, mergedId);
        }

        int unkId = -1;
        if (model.TryGetProperty("unk_token", out var unk) && unk.ValueKind == JsonValueKind.String)
        {
            string unkToken = unk.GetString()!;
            if (!vocab.TryGetValue(unkToken, out unkId))
                throw new FormatException($"unk_token '{unkToken}' is missing from the vocab.");
        }

        bool fuseUnk = IsTrue(model, "fuse_unk");

        var replacements = new List<(string, string)>();
        if (root.TryGetProperty("normalizer", out var normalizer) && normalizer.ValueKind == JsonValueKind.Object)
            ParseNormalizer(normalizer, replacements);

        bool whitespacePreTokenizer = false;
        if (root.TryGetProperty("pre_tokenizer", out var preTokenizer) && preTokenizer.ValueKind == JsonValueKind.Object)
        {
            string? preType = preTokenizer.GetProperty("type").GetString();
            whitespacePreTokenizer = preType == "Whitespace" ? true : throw Unsupported($"pre_tokenizer '{preType}'");
        }

        var rawAdded = new List<(string, int)>();
        var normalizedAdded = new List<(string, int)>();
        if (root.TryGetProperty("added_tokens", out var addedTokens) && addedTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                string content = token.GetProperty("content").GetString()!;
                int id = token.GetProperty("id").GetInt32();
                if (IsTrue(token, "single_word") || IsTrue(token, "lstrip") || IsTrue(token, "rstrip"))
                    throw Unsupported("added-token single_word/lstrip/rstrip matching");

                if (IsTrue(token, "normalized"))
                    normalizedAdded.Add((ApplyReplacements(content, replacements), id));
                else
                    rawAdded.Add((content, id));
            }
        }

        // Longest-first gives leftmost-longest matching, as the HF Aho-Corasick automaton does.
        rawAdded.Sort(static (a, b) => b.Item1.Length - a.Item1.Length);
        normalizedAdded.Sort(static (a, b) => b.Item1.Length - a.Item1.Length);

        var templatePrefix = new List<int>();
        var templateSuffix = new List<int>();
        if (root.TryGetProperty("post_processor", out var postProcessor) && postProcessor.ValueKind == JsonValueKind.Object)
        {
            string? postType = postProcessor.GetProperty("type").GetString();
            if (postType != "TemplateProcessing")
                throw Unsupported($"post_processor '{postType}'");

            var specialTokens = postProcessor.GetProperty("special_tokens");
            bool seenSequence = false;
            foreach (var item in postProcessor.GetProperty("single").EnumerateArray())
            {
                if (item.TryGetProperty("Sequence", out var sequence))
                {
                    if (seenSequence || sequence.GetProperty("id").GetString() != "A")
                        throw Unsupported("a post_processor template with more than one input sequence");
                    seenSequence = true;
                }
                else if (item.TryGetProperty("SpecialToken", out var special))
                {
                    string name = special.GetProperty("id").GetString()!;
                    foreach (var id in specialTokens.GetProperty(name).GetProperty("ids").EnumerateArray())
                        (seenSequence ? templateSuffix : templatePrefix).Add(id.GetInt32());
                }
                else
                {
                    throw Unsupported("a post_processor template item other than Sequence/SpecialToken");
                }
            }
        }

        return new ChatterboxTokenizer(
            vocab,
            merges,
            [.. rawAdded],
            [.. normalizedAdded],
            [.. replacements],
            whitespacePreTokenizer,
            [.. templatePrefix],
            [.. templateSuffix],
            unkId,
            fuseUnk);
    }

    /// <summary>
    /// Encodes exactly as HF AutoTokenizer with add_special_tokens=true, including the ids the
    /// template prepends/appends around the text (EXAGGERATION, BOS ... EOS, START_SPEECH x2).
    /// </summary>
    public long[] Encode(string textWithLanguageTag)
    {
        ArgumentNullException.ThrowIfNull(textWithLanguageTag);

        var ids = new List<int>(_templatePrefix.Length + _templateSuffix.Length + textWithLanguageTag.Length / 2 + 4);
        ids.AddRange(_templatePrefix);

        foreach (var (rawSegment, rawTokenId) in SplitOnAddedTokens(textWithLanguageTag, _rawAddedTokens))
        {
            if (rawSegment is null)
            {
                ids.Add(rawTokenId);
                continue;
            }

            string normalized = ApplyReplacements(rawSegment, _replacements);
            foreach (var (segment, tokenId) in SplitOnAddedTokens(normalized, _normalizedAddedTokens))
            {
                if (segment is null)
                {
                    ids.Add(tokenId);
                    continue;
                }

                foreach (var word in PreTokenize(segment))
                    EncodeWord(word, ids);
            }
        }

        ids.AddRange(_templateSuffix);

        var result = new long[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            result[i] = ids[i];
        return result;
    }

    private static IEnumerable<(string? Segment, int TokenId)> SplitOnAddedTokens(
        string text, (string Content, int Id)[] tokens)
    {
        if (text.Length == 0)
            yield break;
        if (tokens.Length == 0)
        {
            yield return (text, 0);
            yield break;
        }

        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            int matched = -1;
            for (int t = 0; t < tokens.Length; t++)
            {
                if (text.AsSpan(i).StartsWith(tokens[t].Content, StringComparison.Ordinal))
                {
                    matched = t;
                    break;
                }
            }

            if (matched >= 0)
            {
                if (i > start)
                    yield return (text[start..i], 0);
                yield return (null, tokens[matched].Id);
                i += tokens[matched].Content.Length;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length)
            yield return (text[start..], 0);
    }

    private static string ApplyReplacements(string text, IReadOnlyList<(string Pattern, string Replacement)> replacements)
    {
        foreach (var (pattern, replacement) in replacements)
            text = text.Replace(pattern, replacement, StringComparison.Ordinal);
        return text;
    }

    // The HF Whitespace pre-tokenizer keeps matches of \w+|[^\w\s]+ and drops whitespace.
    private IEnumerable<string> PreTokenize(string segment)
    {
        if (!_whitespacePreTokenizer)
        {
            yield return segment;
            yield break;
        }

        int i = 0;
        while (i < segment.Length)
        {
            Rune.DecodeFromUtf16(segment.AsSpan(i), out var rune, out int consumed);
            if (Rune.IsWhiteSpace(rune))
            {
                i += consumed;
                continue;
            }

            bool isWord = IsWordRune(rune);
            int start = i;
            i += consumed;
            while (i < segment.Length)
            {
                Rune.DecodeFromUtf16(segment.AsSpan(i), out var next, out int nextConsumed);
                if (Rune.IsWhiteSpace(next) || IsWordRune(next) != isWord)
                    break;
                i += nextConsumed;
            }

            yield return segment[start..i];
        }
    }

    // Mirrors the Rust regex \w class used by the Whitespace pre-tokenizer:
    // [\p{Alphabetic}\p{M}\p{Nd}\p{Pc}\p{Join_Control}], approximating Alphabetic with L* + Nl.
    private static bool IsWordRune(Rune rune)
    {
        if (rune.Value is 0x200C or 0x200D)
            return true;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.ConnectorPunctuation => true,
            _ => false,
        };
    }

    private void EncodeWord(string word, List<int> output)
    {
        var symbols = new List<int>(word.Length);
        Span<char> buffer = stackalloc char[2];
        bool lastWasUnknown = false;
        foreach (var rune in word.EnumerateRunes())
        {
            int length = rune.EncodeToUtf16(buffer);
            string ch = new(buffer[..length]);
            if (_vocab.TryGetValue(ch, out int id))
            {
                symbols.Add(id);
                lastWasUnknown = false;
            }
            else
            {
                if (_unkId >= 0 && !(_fuseUnk && lastWasUnknown))
                    symbols.Add(_unkId);
                lastWasUnknown = true;
            }
        }

        if (symbols.Count > 1)
            MergeAll(symbols);
        output.AddRange(symbols);
    }

    // Same algorithm as Word::merge_all in the HF tokenizers crate: a min-queue of candidate
    // merges ordered by (rank, position), entries re-validated on pop via their expected new id.
    private void MergeAll(List<int> symbols)
    {
        int n = symbols.Count;
        var next = new int[n];
        var prev = new int[n];
        var alive = new bool[n];
        for (int i = 0; i < n; i++)
        {
            prev[i] = i - 1;
            next[i] = i + 1 < n ? i + 1 : -1;
            alive[i] = true;
        }

        var queue = new PriorityQueue<(int Pos, int NewId), (int Rank, int Pos)>();
        for (int i = 0; i + 1 < n; i++)
        {
            if (_merges.TryGetValue((symbols[i], symbols[i + 1]), out var merge))
                queue.Enqueue((i, merge.NewId), (merge.Rank, i));
        }

        while (queue.TryDequeue(out var candidate, out _))
        {
            var (pos, newId) = candidate;
            if (!alive[pos])
                continue;
            int right = next[pos];
            if (right < 0)
                continue;
            if (!_merges.TryGetValue((symbols[pos], symbols[right]), out var merge) || merge.NewId != newId)
                continue; // stale entry: a neighbor merged since this was enqueued

            symbols[pos] = newId;
            alive[right] = false;
            next[pos] = next[right];
            if (next[right] >= 0)
                prev[next[right]] = pos;

            if (prev[pos] >= 0 && _merges.TryGetValue((symbols[prev[pos]], symbols[pos]), out var leftMerge))
                queue.Enqueue((prev[pos], leftMerge.NewId), (leftMerge.Rank, prev[pos]));
            if (next[pos] >= 0 && _merges.TryGetValue((symbols[pos], symbols[next[pos]]), out var rightMerge))
                queue.Enqueue((pos, rightMerge.NewId), (rightMerge.Rank, pos));
        }

        var merged = new List<int>(n);
        for (int i = 0; i >= 0; i = next[i])
            merged.Add(symbols[i]);
        symbols.Clear();
        symbols.AddRange(merged);
    }

    private static void ParseNormalizer(JsonElement normalizer, List<(string, string)> replacements)
    {
        string? type = normalizer.GetProperty("type").GetString();
        switch (type)
        {
            case "Sequence":
                foreach (var inner in normalizer.GetProperty("normalizers").EnumerateArray())
                    ParseNormalizer(inner, replacements);
                break;
            case "Replace":
                var pattern = normalizer.GetProperty("pattern");
                if (!pattern.TryGetProperty("String", out var literal))
                    throw Unsupported("a non-literal Replace normalizer pattern");
                replacements.Add((literal.GetString()!, normalizer.GetProperty("content").GetString()!));
                break;
            default:
                throw Unsupported($"normalizer '{type}'");
        }
    }

    private static bool HasNonEmptyString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Length > 0;

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static NotSupportedException Unsupported(string what) =>
        new($"tokenizer.json uses {what}, which ChatterboxTokenizer does not implement.");
}
