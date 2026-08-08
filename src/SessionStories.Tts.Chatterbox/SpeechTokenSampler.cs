namespace SessionStories.Tts.Chatterbox;

/// <summary>
/// Samples the next speech token from LM logits. Applies, in order: repetition penalty over
/// all previously generated ids (including the START seed), temperature, softmax, min-p filter,
/// top-p nucleus filter, renormalization, then draws from a seeded <see cref="Random"/>.
/// Temperature &lt;= 0 means greedy argmax (after the repetition penalty), matching the reference.
/// </summary>
public sealed class SpeechTokenSampler
{
    private readonly double _temperature;
    private readonly double _minP;
    private readonly double _topP;
    private readonly double _repetitionPenalty;
    private readonly Random _random;

    public SpeechTokenSampler(double temperature, double minP, double topP, double repetitionPenalty, int seed)
    {
        if (repetitionPenalty <= 0)
            throw new ArgumentOutOfRangeException(nameof(repetitionPenalty), "Repetition penalty must be strictly positive.");
        _temperature = temperature;
        _minP = minP;
        _topP = topP;
        _repetitionPenalty = repetitionPenalty;
        _random = new Random(seed);
    }

    /// <summary>Mutates <paramref name="logits"/> in place when applying the repetition penalty.</summary>
    public long Sample(float[] logits, IReadOnlyList<long> generatedIds)
    {
        ApplyRepetitionPenalty(logits, generatedIds);

        if (_temperature <= 0)
            return ArgMax(logits);

        int n = logits.Length;
        var probs = new double[n];
        double maxScore = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            double s = logits[i] / _temperature;
            probs[i] = s;
            if (s > maxScore)
                maxScore = s;
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double e = Math.Exp(probs[i] - maxScore);
            probs[i] = e;
            sum += e;
        }

        double maxProb = 0;
        for (int i = 0; i < n; i++)
        {
            probs[i] /= sum;
            if (probs[i] > maxProb)
                maxProb = probs[i];
        }

        if (_minP > 0)
        {
            double threshold = _minP * maxProb;
            for (int i = 0; i < n; i++)
            {
                if (probs[i] < threshold)
                    probs[i] = 0;
            }
        }

        if (_topP < 1.0)
        {
            // Keep the smallest prefix of descending-probability tokens whose mass reaches top-p.
            var order = Enumerable.Range(0, n).Where(i => probs[i] > 0).OrderByDescending(i => probs[i]).ToArray();
            double cumulative = 0;
            int keep = order.Length;
            for (int k = 0; k < order.Length; k++)
            {
                cumulative += probs[order[k]];
                if (cumulative >= _topP)
                {
                    keep = k + 1;
                    break;
                }
            }
            for (int k = keep; k < order.Length; k++)
                probs[order[k]] = 0;
        }

        double total = 0;
        for (int i = 0; i < n; i++)
            total += probs[i];

        double r = _random.NextDouble() * total;
        double accumulated = 0;
        int lastCandidate = -1;
        for (int i = 0; i < n; i++)
        {
            if (probs[i] <= 0)
                continue;
            lastCandidate = i;
            accumulated += probs[i];
            if (accumulated >= r)
                return i;
        }

        return lastCandidate >= 0 ? lastCandidate : ArgMax(logits);
    }

    private void ApplyRepetitionPenalty(float[] logits, IReadOnlyList<long> generatedIds)
    {
        if (_repetitionPenalty == 1.0)
            return;

        // Single-precision on purpose: numpy (NEP 50) computes this as float32 op float32,
        // and double-then-round diverges from it by 1 ulp on near-ties.
        float penalty = (float)_repetitionPenalty;
        var seen = new HashSet<long>();
        foreach (var id in generatedIds)
        {
            if (!seen.Add(id))
                continue;
            int index = (int)id;
            if (index < 0 || index >= logits.Length)
                continue;
            float score = logits[index];
            logits[index] = score < 0 ? score * penalty : score / penalty;
        }
    }

    private static long ArgMax(float[] logits)
    {
        int best = 0;
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > logits[best])
                best = i;
        }
        return best;
    }
}
