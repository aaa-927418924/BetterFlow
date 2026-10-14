using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal sealed record DictionaryTermChange(
    string PreviousText,
    string CorrectSpelling,
    string BeforeContext,
    string AfterContext);

internal enum DictionaryCorrectionDecision
{
    Confirmed,
    Pending,
    Rejected
}

internal sealed record DictionaryCorrectionReviewItem(
    int Index,
    DictionaryCorrectionDecision Decision,
    string Reason,
    double Confidence);

internal sealed record DictionaryCorrectionReviewResult(
    IReadOnlyList<DictionaryCorrectionReviewItem> Items,
    string OutputReason);

internal static class DictionaryCorrectionCandidates
{
    private const int MaximumTokens = 300;
    private const int MaximumChanges = 20;
    private const int MaximumContextLength = 320;
    private static readonly Regex WordToken = new(
        @"[A-Za-z0-9]+(?:[._'’\-][A-Za-z0-9]+)*|[\u30A0-\u30FF\u31F0-\u31FFー]+|[\u3400-\u4DBF\u4E00-\u9FFF々〆〇]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionToken = new(
        @"^\d+(?:[.,]\d+)*(?:[A-Za-z]+)?$",
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
                if (oldRun.Count > 0 && newRun.Count > 0 &&
                    CanAttachSharedVersion(oldRun[^1], newRun[^1], oldTokens[i], newTokens[j], before, after))
                {
                    // A version can be unchanged while the name around it is corrected. Keep it
                    // inside the candidate so "Claude Fable 5" is learned as one exact spelling.
                    oldRun.Add(oldTokens[i++]);
                    newRun.Add(newTokens[j++]);
                    continue;
                }

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
                {
                    result.Add(new DictionaryTermChange(
                        oldText,
                        correctedText,
                        GetSentenceContext(before, oldStart, oldEnd),
                        GetSentenceContext(after, newStart, newEnd)));
                }
            }
            oldRun.Clear();
            newRun.Clear();
        }
    }

    private static bool CanAttachSharedVersion(
        Match previousBefore,
        Match previousAfter,
        Match versionBefore,
        Match versionAfter,
        string before,
        string after)
    {
        if (!VersionToken.IsMatch(versionBefore.Value) ||
            !string.Equals(versionBefore.Value, versionAfter.Value, StringComparison.Ordinal))
            return false;

        return IsNameVersionGap(before, previousBefore, versionBefore) &&
               IsNameVersionGap(after, previousAfter, versionAfter);
    }

    private static bool IsNameVersionGap(string text, Match name, Match version)
    {
        var gapStart = name.Index + name.Length;
        if (version.Index < gapStart) return false;
        var gap = text[gapStart..version.Index];
        return gap.Length <= 1 && gap.All(character => char.IsWhiteSpace(character) || character is '-' or '_');
    }

    private static string GetSentenceContext(string text, int spanStart, int spanEnd)
    {
        var start = spanStart;
        while (start > 0 && spanStart - start < MaximumContextLength / 2 &&
               !IsSentenceBoundary(text[start - 1]))
            start--;

        var end = spanEnd;
        while (end < text.Length && end - spanEnd < MaximumContextLength / 2 &&
               !IsSentenceBoundary(text[end]))
            end++;

        return text[start..end].Trim();
    }

    private static bool IsSentenceBoundary(char value) => value is '。' or '！' or '？' or '\n' or '.' or '!' or '?';

    private static bool IsCandidate(string previous, string corrected) =>
        previous.Length is >= 2 and <= 120 && corrected.Length is >= 2 and <= 100 &&
        !string.Equals(previous, corrected, StringComparison.Ordinal) &&
        corrected.Any(char.IsLetterOrDigit);

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
