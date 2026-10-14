using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal sealed record DictionaryTermChange(string PreviousText, string CorrectSpelling);

internal static class DictionaryCorrectionCandidates
{
    private const int MaximumTokens = 300;
    private const int MaximumChanges = 20;
    private static readonly Regex WordToken = new(
        @"[A-Za-z0-9]+(?:[._'’\-][A-Za-z0-9]+)*|[\u30A0-\u30FF\u31F0-\u31FFー]+|[\u3400-\u4DBF\u4E00-\u9FFF々〆〇]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<DictionaryTermChange> ExtractChanges(string before, string after)
    {
        before = Normalize(before);
        after = Normalize(after);
        if (before.Length == 0 || after.Length == 0 ||
            before.Length > 24_000 || after.Length > 24_000 ||
            string.Equals(before, after, StringComparison.Ordinal))
            return Array.Empty<DictionaryTermChange>();

        var oldTokens = WordToken.Matches(before).Cast<Match>().Take(MaximumTokens + 1).ToArray();
        var newTokens = WordToken.Matches(after).Cast<Match>().Take(MaximumTokens + 1).ToArray();
        if (oldTokens.Length == 0 || newTokens.Length == 0 ||
            oldTokens.Length > MaximumTokens || newTokens.Length > MaximumTokens)
            return Array.Empty<DictionaryTermChange>();

        var lcs = new int[oldTokens.Length + 1, newTokens.Length + 1];
        for (var oldIndex = oldTokens.Length - 1; oldIndex >= 0; oldIndex--)
        for (var newIndex = newTokens.Length - 1; newIndex >= 0; newIndex--)
        {
            lcs[oldIndex, newIndex] = string.Equals(oldTokens[oldIndex].Value, newTokens[newIndex].Value,
                StringComparison.Ordinal)
                ? lcs[oldIndex + 1, newIndex + 1] + 1
                : Math.Max(lcs[oldIndex + 1, newIndex], lcs[oldIndex, newIndex + 1]);
        }

        var result = new List<DictionaryTermChange>();
        var oldRun = new List<Match>();
        var newRun = new List<Match>();
        var i = 0;
        var j = 0;
        while (i < oldTokens.Length || j < newTokens.Length)
        {
            if (i < oldTokens.Length && j < newTokens.Length &&
                string.Equals(oldTokens[i].Value, newTokens[j].Value, StringComparison.Ordinal))
            {
                AddChange();
                i++;
                j++;
            }
            else if (i < oldTokens.Length &&
                     (j >= newTokens.Length || lcs[i + 1, j] >= lcs[i, j + 1]))
            {
                oldRun.Add(oldTokens[i++]);
            }
            else if (j < newTokens.Length)
            {
                newRun.Add(newTokens[j++]);
            }
        }
        AddChange();
        return result;

        void AddChange()
        {
            if (oldRun.Count > 0 && newRun.Count > 0 && result.Count < MaximumChanges)
            {
                var oldStart = oldRun[0].Index;
                var oldEnd = oldRun[^1].Index + oldRun[^1].Length;
                var newStart = newRun[0].Index;
                var newEnd = newRun[^1].Index + newRun[^1].Length;
                var oldText = before[oldStart..oldEnd].Trim();
                var correctedText = after[newStart..newEnd].Trim();
                if (IsCandidate(oldText, correctedText))
                    result.Add(new DictionaryTermChange(oldText, correctedText));
            }
            oldRun.Clear();
            newRun.Clear();
        }
    }

    public static IReadOnlyList<string> SelectFastCandidates(IReadOnlyList<DictionaryTermChange> changes) =>
        changes.Where(change => IsLikelyTypoCorrection(change.PreviousText, change.CorrectSpelling))
            .Select(change => change.CorrectSpelling)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumChanges)
            .ToArray();

    private static bool IsCandidate(string previous, string corrected) =>
        previous.Length is >= 2 and <= 120 && corrected.Length is >= 2 and <= 100 &&
        !string.Equals(previous, corrected, StringComparison.Ordinal) &&
        corrected.Any(char.IsLetterOrDigit);

    private static bool IsLikelyTypoCorrection(string previous, string corrected)
    {
        if (!IsCandidate(previous, corrected) || corrected.Any(char.IsWhiteSpace) ||
            corrected.Any(character => !char.IsLetterOrDigit(character) && character != 'ー' && character != '-'))
            return false;

        var distance = LevenshteinDistance(previous, corrected);
        return distance <= Math.Max(1, Math.Max(previous.Length, corrected.Length) / 4);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length > 120 || right.Length > 120) return int.MaxValue;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
