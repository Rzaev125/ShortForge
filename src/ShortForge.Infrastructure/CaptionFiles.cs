using System.Globalization;
using System.Text;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public static class CaptionFiles
{
    public static string ToAss(EditorClip clip, long offsetUs, long durationUs)
    {
        var style = clip.Edit.Style;
        if (!style.IsValid) throw new UserError("caption_style", "Проверьте настройки стиля субтитров.");
        string bgr = style.Color[5..7] + style.Color[3..5] + style.Color[1..3];
        var text = new StringBuilder(FormattableString.Invariant($"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1080
            PlayResY: 1920
            ScaledBorderAndShadow: yes
            WrapStyle: 0
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Segoe UI,{style.Size},&H00{bgr},&H00{bgr},&H00000000,&H00000000,0,0,0,0,100,100,0,0,{(style.Background ? 3 : 1)},{style.Outline},0,2,65,65,{(1920 * (100 - style.Position) + 50) / 100},1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """));
        text.AppendLine();
        foreach (var cue in Window(clip, offsetUs, durationUs, 10_000))
        {
            var lines = Wrap(cue.Text, style.Size);
            // ASS alignment 2 anchors the bottom edge. Leave room for font ascenders and outline.
            if (lines.Count * style.Size * 1.25 + 2 * style.Outline > style.Position * 19.2)
                throw new UserError("caption_overflow", $"Текст фразы {cue.Index + 1} не помещается в кадр. Сократите текст, уменьшите размер или опустите субтитры.");
            text.Append("Dialogue: 0,").Append(AssTime(cue.StartUs)).Append(',').Append(AssTime(cue.EndUs))
                .Append(",Default,,0,0,0,,").AppendJoin(@"\N", lines.Select(EscapeAss)).AppendLine();
        }
        return text.ToString();
    }

    public static string ToSrt(EditorClip clip)
    {
        var text = new StringBuilder();
        int number = 0;
        foreach (var cue in Window(clip, 0, clip.Edit.EndUs - clip.Edit.StartUs, 1000))
        {
            // Empty lines terminate an SRT entry. Keep user-provided content inside its own event.
            var lines = Clean(cue.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            string body = string.Join("\r\n", lines).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("{", "&#123;").Replace("}", "&#125;");
            text.Append((++number).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(SrtTime(cue.StartUs)).Append(" --> ").Append(SrtTime(cue.EndUs)).Append("\r\n")
                .Append(body).Append("\r\n\r\n");
        }
        return text.ToString();
    }

    private static IEnumerable<EditorCue> Window(EditorClip clip, long offset, long duration, long unit)
    {
        long length = clip.Edit.EndUs - clip.Edit.StartUs;
        if (offset < 0 || offset >= length || duration <= 0 || duration > length - offset)
            throw new UserError("caption_interval", "Выберите интервал в пределах клипа.");
        foreach (var cue in clip.Cues)
        {
            long start = Math.Max(offset, cue.StartUs), end = Math.Min(offset + duration, cue.EndUs);
            if (end <= start || string.IsNullOrWhiteSpace(cue.Text)) continue;
            // Quantize both boundaries on the same grid so adjacent cues stay adjacent.
            start = (start - offset) / unit * unit;
            end = (end - offset) / unit * unit;
            if (end > start) yield return cue with { StartUs = start, EndUs = end };
        }
    }

    private static string AssTime(long us) => FormattableString.Invariant(
        $"{us / 3_600_000_000}:{us / 60_000_000 % 60:00}:{us / 1_000_000 % 60:00}.{us / 10_000 % 100:00}");
    private static string SrtTime(long us) => FormattableString.Invariant(
        $"{us / 3_600_000_000:00}:{us / 60_000_000 % 60:00}:{us / 1_000_000 % 60:00},{us / 1000 % 1000:000}");
    private static string Clean(string text) => new(text.ReplaceLineEndings("\n").Replace('\t', ' ')
        .Where(c => c == '\n' || !char.IsControl(c)).ToArray());
    // A zero-width word joiner prevents a literal backslash from becoming libass \N, \n or \h.
    private static string EscapeAss(string text) => text.Replace("\\", "\\\u2060").Replace("{", "\\{").Replace("}", "\\}");

    private static List<string> Wrap(string text, int size)
    {
        var lines = new List<string>();
        double limit = 28.0 * 64 / size;
        foreach (var paragraph in Clean(text).Split('\n'))
        {
            var line = new StringBuilder();
            double used = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int[] elements = StringInfo.ParseCombiningCharacters(word);
                double wordWidth = elements.Sum(i => Width(word[i]));
                if (used > 0 && used + 1 + wordWidth > limit)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    used = 0;
                }
                if (used > 0) { line.Append(' '); used++; }
                for (int i = 0; i < elements.Length; ++i)
                {
                    int begin = elements[i], end = i + 1 < elements.Length ? elements[i + 1] : word.Length;
                    double width = Width(word[begin]);
                    if (used > 0 && used + width > limit)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                        used = 0;
                    }
                    line.Append(word, begin, end - begin);
                    used += width;
                }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static double Width(char c) => c >= '\u2e80' || "MWЖШЩЮЫФМ@%".Contains(c) ? 1.8
        : char.IsUpper(c) ? 1.25 : 1;
}
