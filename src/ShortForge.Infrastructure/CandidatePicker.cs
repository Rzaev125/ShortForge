using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record ClipScores(int Independence, int Clarity, int Opening, int Completion)
{
    public int Total => Independence + Clarity + Opening + Completion;
}
public sealed record ClipCandidate(int StartId, int EndId, long StartUs, long EndUs, string Title, string Reason, ClipScores? Scores, string State = "offered");
public sealed record ClipSelection(ClipSearch Search, ClipCandidate[] Clips, Guid Id = default);

internal static class CandidatePicker
{
    internal static bool EndsSentence(string text) =>
        text.TrimEnd(' ', '"', '”', '»', ')', '’').EndsWithAny(".!?…。！？");

    private static bool EndsWithAny(this string text, string chars) => text.Length > 0 && chars.Contains(text[^1]);

    private static int SentenceEnd(string text, bool last = false)
    {
        int boundary = -1;
        for (int i=0;i<text.Length;i++)
        {
            if (!".!?…。！？".Contains(text[i])) continue;
            if (text[i]=='.' && i+1<text.Length && char.IsDigit(text[i+1])) continue;
            boundary=i;
            if (!last) break;
        }
        return boundary;
    }

    internal static string Title(SpeechSegment[] speech, int first, int last, int anchor)
    {
        int begin = anchor;
        string prefix = "";
        while (begin > first)
        {
            string previous = speech[begin - 1].Text.Trim();
            int stop = SentenceEnd(previous, last:true);
            if (stop >= 0)
            {
                prefix = previous[(stop+1)..].TrimStart(' ', '"', '”', '»', ')', '’');
                break;
            }
            begin--;
        }
        var sentence = new System.Text.StringBuilder(prefix);
        for (int i=begin; i<=last && sentence.Length<=95; i++)
        {
            var part = speech[i].Text.Trim();
            int stop = SentenceEnd(part);
            if (sentence.Length > 0) sentence.Append(' ');
            sentence.Append(stop < 0 ? part : part[..(stop+1)]);
            if (stop >= 0) break;
        }
        string text = sentence.ToString().Trim();
        if (text.Length <= 95) return text;
        int cut = text.LastIndexOf(' ', 94);
        if (cut < 1) cut = char.IsHighSurrogate(text[94]) ? 94 : 95;
        return text[..cut] + "…";
    }

    internal static ClipCandidate[] ByStructure(SpeechSegment[] speech, ClipSearch search, CancellationToken token)
    {
        var ranked = new List<(int Rank, ClipCandidate Clip)>();
        long target = (search.MinSeconds + search.MaxSeconds) * 500_000L;
        for (int start = 0; start < speech.Length; start++)
        {
            token.ThrowIfCancellationRequested();
            bool strongStart = start == 0 || EndsSentence(speech[start - 1].Text);
            if (!strongStart && speech[start].StartUs - speech[start - 1].EndUs < 600_000) continue;
            int best = -1, bestRank = -1;
            for (int end = start; end < speech.Length; end++)
            {
                long duration = speech[end].EndUs - speech[start].StartUs;
                if (duration > search.MaxSeconds * 1_000_000L || (end > start && speech[end].StartUs - speech[end - 1].EndUs > 4_000_000)) break;
                if (!search.Fits(duration)) continue;
                bool complete = EndsSentence(speech[end].Text);
                if (!complete && (end + 1 == speech.Length || speech[end + 1].StartUs - speech[end].EndUs < 600_000)) continue;
                int rank = (strongStart ? 40 : 0) + (complete ? 40 : 0) + (int)(20 - 20 * Math.Abs(target - duration) / target);
                if (rank > bestRank) { best = end; bestRank = rank; }
            }
            if (best >= 0)
                ranked.Add((bestRank, new(start, best, speech[start].StartUs, speech[best].EndUs, Title(speech,start,best,start),
                    EndsSentence(speech[best].Text) ? "Окончание предложения в выбранном диапазоне длительности." : "Граница по паузе между репликами.", null)));
        }
        return Distinct(ranked.OrderByDescending(item => item.Rank).Select(item => item.Clip), speech, search.Count, token);
    }

    internal static ClipCandidate[] Distinct(IEnumerable<ClipCandidate> ranked, SpeechSegment[] speech, int count, CancellationToken token)
    {
        var chosen = new List<ClipCandidate>();
        var quotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in ranked)
        {
            token.ThrowIfCancellationRequested();
            if (chosen.Any(c => c.StartUs < clip.EndUs && c.EndUs > clip.StartUs)) continue;
            var text = string.Join(" ", speech[clip.StartId..(clip.EndId + 1)].Select(s => s.Text.Trim()));
            var words = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!quotes.Add(words)) continue;
            chosen.Add(clip);
            if (chosen.Count == count) break;
        }
        return chosen.ToArray();
    }
}
