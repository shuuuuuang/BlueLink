using System.Globalization;
using System.Text;

namespace BlueLink.Domain;

internal sealed record SearchExcerpt(string Text, IReadOnlyList<(int Start, int Length)> Highlights)
{
    internal static SearchExcerpt Create(string source, string query, int budget = 96, bool fileName = false)
    {
        var bounds = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToArray();
        var count = bounds.Length - 1;
        var limit = Math.Max(1, budget);
        var needle = query.Trim();
        var match = needle.Length == 0 ? -1 : source.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        var unit = match < 0 ? 0 : Math.Min(count, Array.FindLastIndex(bounds, b => b <= match));
        var dot = fileName ? source.LastIndexOf('.') : -1;
        var suffix = dot > 0 && dot < source.Length - 1 ? Array.FindLastIndex(bounds, b => b <= dot) : count;
        var suffixUnits = count - suffix;
        var reserve = count > limit && suffixUnits > 0 && suffixUnits < limit / 2 ? suffixUnits : 0;
        var window = Math.Max(1, limit - reserve);
        var start = count <= limit ? 0 : Math.Max(0, unit - Math.Min(12, window / 4));
        var end = count <= limit ? count : Math.Min(count, start + window);
        var parts = new List<(int From, int To)> { (bounds[start], bounds[end]) };
        if (reserve > 0 && suffix >= end && end < count) parts.Add((bounds[suffix], source.Length));
        var matches = new List<(int Start, int End)>();
        if (needle.Length > 0)
            for (var index = source.IndexOf(needle, StringComparison.OrdinalIgnoreCase); index >= 0;
                 index = source.IndexOf(needle, index + needle.Length, StringComparison.OrdinalIgnoreCase))
                matches.Add((index, index + needle.Length));
        var text = new StringBuilder();
        var spans = new List<(int Start, int Length)>();
        var previous = 0;
        foreach (var (from, to) in parts)
        {
            if (from > previous) text.Append('…');
            var offset = text.Length;
            text.Append(source[from..to].Replace('\r', ' ').Replace('\n', ' '));
            foreach (var hit in matches)
            {
                var left = Math.Max(from, hit.Start);
                var right = Math.Min(to, hit.End);
                if (left < right) spans.Add((offset + left - from, right - left));
            }
            previous = to;
        }
        if (previous < source.Length) text.Append('…');
        return new(text.ToString(), spans);
    }
}
