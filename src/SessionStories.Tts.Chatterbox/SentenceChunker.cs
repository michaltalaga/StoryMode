using System.Text;
using System.Text.RegularExpressions;

namespace SessionStories.Tts.Chatterbox;

/// <summary>A whitespace-normalized piece of text sized for one TTS pass.</summary>
public sealed record TextChunk(string Text, bool StartsParagraph);

/// <summary>
/// Splits text into TTS-sized chunks on sentence boundaries.
/// <para>
/// Heuristics (pragmatic by design, tuned for Polish and English prose):
/// a run of <c>. ! ? …</c> plus any trailing closing quotes/parens ends a sentence when it is
/// followed by end-of-text or by a space and a non-lowercase character. A lone period does not
/// end a sentence when the preceding word is a single letter (an initial such as "J.") or a
/// known abbreviation ("np.", "dr.", "vs.", "m.in.", ...). Sentences merge greedily until a
/// chunk reaches <c>minChars</c>, never exceeding <c>maxChars</c>; a single sentence longer than
/// <c>maxChars</c> is split at clause punctuation (<c>, ; : — –</c>), then at a word boundary,
/// then hard-cut as a last resort. Blank lines delimit paragraphs; chunks never span them.
/// </para>
/// </summary>
public static class SentenceChunker
{
    private static readonly Regex ParagraphSeparator = new(@"\n\s*\n", RegexOptions.Compiled);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        // Polish
        "np", "dr", "prof", "mgr", "inż", "hab", "im", "ul", "al", "tzn", "tzw",
        "itd", "itp", "m.in", "św", "nr", "ok", "godz", "tel", "woj", "art",
        "ust", "pkt", "str", "tj", "cd", "ww", "ds",
        // English
        "vs", "etc", "mr", "mrs", "ms", "st", "jr", "sr", "fig", "vol",
        "dept", "inc", "ltd", "co", "approx",
    };

    public static IReadOnlyList<TextChunk> Chunk(string text, int minChars = 150, int maxChars = 350)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, minChars);

        if (string.IsNullOrWhiteSpace(text))
            return [];

        var chunks = new List<TextChunk>();
        var unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var paragraph in ParagraphSeparator.Split(unified))
        {
            var normalized = NormalizeWhitespace(paragraph);
            if (normalized.Length > 0)
                ChunkParagraph(normalized, minChars, maxChars, chunks);
        }
        return chunks;
    }

    private static void ChunkParagraph(string paragraph, int minChars, int maxChars, List<TextChunk> chunks)
    {
        var units = new List<string>();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (sentence.Length <= maxChars)
                units.Add(sentence);
            else
                units.AddRange(SplitOversized(sentence, maxChars));
        }

        var startsParagraph = true;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            chunks.Add(new TextChunk(buffer.ToString(), startsParagraph));
            startsParagraph = false;
            buffer.Clear();
        }

        foreach (var unit in units)
        {
            if (buffer.Length > 0 && buffer.Length + 1 + unit.Length > maxChars)
                Flush();
            if (buffer.Length > 0)
                buffer.Append(' ');
            buffer.Append(unit);
            if (buffer.Length >= minChars)
                Flush();
        }
        Flush();
    }

    /// <summary>Splits normalized (single-spaced) text into sentences.</summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsTerminator(text[i]))
            {
                i++;
                continue;
            }

            var terminatorStart = i;
            while (i < text.Length && IsTerminator(text[i]))
                i++;
            var singleDot = i - terminatorStart == 1 && text[terminatorStart] == '.';
            while (i < text.Length && IsClosingMark(text[i]))
                i++;

            var boundary = i >= text.Length
                || (text[i] == ' ' && i + 1 < text.Length && !char.IsLower(text[i + 1]));
            if (boundary && singleDot && IsAbbreviationDot(text, terminatorStart, start))
                boundary = false;
            if (!boundary)
                continue;

            sentences.Add(text[start..i]);
            if (i < text.Length)
                i++; // skip the single separating space
            start = i;
        }

        if (start < text.Length)
            sentences.Add(text[start..]);
        return sentences;
    }

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '…';

    private static bool IsClosingMark(char c) =>
        c is '"' or '\'' or '”' or '’' or '»' or '«' or ')' or ']';

    /// <summary>
    /// True when the word directly before the period at <paramref name="dotIndex"/> is a
    /// single-letter initial or a known abbreviation, so the period is not a sentence end.
    /// </summary>
    private static bool IsAbbreviationDot(string text, int dotIndex, int sentenceStart)
    {
        var wordStart = dotIndex;
        while (wordStart > sentenceStart && (char.IsLetter(text[wordStart - 1]) || text[wordStart - 1] == '.'))
            wordStart--;
        if (wordStart == dotIndex)
            return false;

        var word = text[wordStart..dotIndex].Trim('.');
        if (word.Length == 0)
            return false;

        var lastSegmentLength = word.Length - (word.LastIndexOf('.') + 1);
        if (lastSegmentLength == 1)
            return true; // initials: "J.", and dotted forms like "e.g.", "i.e."
        return Abbreviations.Contains(word.ToLowerInvariant());
    }

    private static IEnumerable<string> SplitOversized(string sentence, int maxChars)
    {
        var pos = 0;
        while (sentence.Length - pos > maxChars)
        {
            var limit = pos + maxChars;
            var cut = LastClauseCut(sentence, pos, limit);
            if (cut < 0)
                cut = LastSpaceCut(sentence, pos, limit);
            if (cut < 0)
                cut = limit; // no clause or word boundary in the window: hard cut

            var piece = sentence[pos..cut].TrimEnd();
            if (piece.Length > 0)
                yield return piece;

            pos = cut;
            while (pos < sentence.Length && sentence[pos] == ' ')
                pos++;
        }

        if (pos < sentence.Length)
            yield return sentence[pos..];
    }

    private static int LastClauseCut(string s, int start, int limit)
    {
        for (var i = limit - 1; i > start; i--)
        {
            if (s[i] is ',' or ';' or ':' or '—' or '–')
                return i + 1;
        }
        return -1;
    }

    private static int LastSpaceCut(string s, int start, int limit)
    {
        for (var i = limit; i > start; i--)
        {
            if (s[i] == ' ')
                return i;
        }
        return -1;
    }

    private static string NormalizeWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
