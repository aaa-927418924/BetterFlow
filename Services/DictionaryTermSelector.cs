using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal static class DictionaryTermSelector
{
    private const int MaximumPromptTerms = 15;
    private const double MinimumRelevance = 0.52;
    private static readonly Regex TokenPattern = new(
        @"[A-Za-z0-9]+(?:[._'’\-][A-Za-z0-9]+)*|[\u3040-\u30FF\u31F0-\u31FFー]+|[\u3400-\u4DBF\u4E00-\u9FFF々〆〇]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberPattern = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<char, string> Kana = CreateKanaMap();
    private static readonly IReadOnlyDictionary<string, string> Digraphs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["キャ"] = "kya", ["キュ"] = "kyu", ["キョ"] = "kyo",
        ["シャ"] = "sha", ["シュ"] = "shu", ["ショ"] = "sho",
        ["チャ"] = "cha", ["チュ"] = "chu", ["チョ"] = "cho",
        ["ニャ"] = "nya", ["ニュ"] = "nyu", ["ニョ"] = "nyo",
        ["ヒャ"] = "hya", ["ヒュ"] = "hyu", ["ヒョ"] = "hyo",
        ["ミャ"] = "mya", ["ミュ"] = "myu", ["ミョ"] = "myo",
        ["リャ"] = "rya", ["リュ"] = "ryu", ["リョ"] = "ryo",
        ["ギャ"] = "gya", ["ギュ"] = "gyu", ["ギョ"] = "gyo",
        ["ジャ"] = "ja", ["ジュ"] = "ju", ["ジョ"] = "jo",
        ["ビャ"] = "bya", ["ビュ"] = "byu", ["ビョ"] = "byo",
        ["ピャ"] = "pya", ["ピュ"] = "pyu", ["ピョ"] = "pyo",
        ["ファ"] = "fa", ["フィ"] = "fi", ["フェ"] = "fe", ["フォ"] = "fo",
        ["ウィ"] = "wi", ["ウェ"] = "we", ["ウォ"] = "wo",
        ["ティ"] = "ti", ["ディ"] = "di", ["トゥ"] = "tu", ["ドゥ"] = "du"
    };

    public static IReadOnlyList<CorrectionDictionaryEntry> SelectRelevant(
        string transcript,
        IReadOnlyList<CorrectionDictionaryEntry>? dictionary)
    {
        if (string.IsNullOrWhiteSpace(transcript) || dictionary is not { Count: > 0 })
            return Array.Empty<CorrectionDictionaryEntry>();

        var transcriptTokens = TokenPattern.Matches(transcript).Cast<Match>().Select(match => match.Value).ToArray();
        var now = DateTimeOffset.UtcNow;
        var ranked = dictionary
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.PreferredSpelling))
            .Select(entry => new
            {
                Entry = entry,
                Relevance = entry.AlternativeSpellings
                    .Prepend(entry.PreferredSpelling)
                    .Select(term => CalculateRelevance(transcriptTokens, term))
                    .DefaultIfEmpty(0)
                    .Max(),
                Usage = Math.Min(Math.Max(entry.UseCount, 0), 10) * 0.002 +
                        (entry.LastUsedUtc is { } lastUsed && now - lastUsed <= TimeSpan.FromDays(30)
                            ? 0.01
                            : 0)
            })
            .Where(item => item.Relevance >= MinimumRelevance)
            .OrderByDescending(item => item.Relevance + item.Usage)
            .ThenBy(item => item.Entry.PreferredSpelling, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPromptTerms)
            .Select(item => item.Entry)
            .ToArray();

        return ranked;
    }

    private static double CalculateRelevance(IReadOnlyList<string> transcriptTokens, string term)
    {
        var normalizedTerm = NormalizeForMatch(term);
        if (normalizedTerm.Length == 0) return 0;

        var termKey = PronunciationKey(term);
        var termNumbers = NumberPattern.Matches(term).Cast<Match>().Select(match => match.Value).ToArray();
        var maxTokens = Math.Clamp(TokenPattern.Matches(term).Count + 1, 1, 8);
        var best = 0d;
        for (var start = 0; start < transcriptTokens.Count; start++)
        {
            var endLimit = Math.Min(transcriptTokens.Count, start + maxTokens);
            for (var end = start + 1; end <= endLimit; end++)
            {
                var window = string.Concat(transcriptTokens.Skip(start).Take(end - start));
                var normalizedWindow = NormalizeForMatch(window);
                if (normalizedWindow.Length == 0) continue;

                var direct = Similarity(normalizedTerm, normalizedWindow);
                var key = PronunciationKey(window);
                var phonetic = termKey.Length > 0 && key.Length > 0 ? Similarity(termKey, key) : 0;
                var score = Math.Max(direct * 0.92, phonetic);

                var windowNumbers = NumberPattern.Matches(window).Cast<Match>().Select(match => match.Value).ToArray();
                if (termNumbers.Length > 0 && !termNumbers.SequenceEqual(windowNumbers, StringComparer.Ordinal))
                    score *= 0.55;
                else if (termNumbers.Length == 0 && windowNumbers.Length > 0)
                    score *= 0.82;

                best = Math.Max(best, score);
            }
        }

        if (normalizedTerm.Length <= 4 && best < 0.85) return 0;
        return best;
    }

    private static string NormalizeForMatch(string value) =>
        string.Concat(RomanizeKana(value).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string PronunciationKey(string value)
    {
        var romanized = RomanizeKana(value).ToLowerInvariant();
        return string.Concat(romanized.Where(character => !"aeiou".Contains(character) || char.IsDigit(character)));
    }

    private static string RomanizeKana(string value)
    {
        if (value.Length == 0) return string.Empty;
        var output = new System.Text.StringBuilder(value.Length * 2);
        var geminateNext = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = ToKatakana(value[index]);
            if (current == 'ッ')
            {
                geminateNext = true;
                continue;
            }
            if (current == 'ー') continue;

            string syllable;
            if (index + 1 < value.Length)
            {
                var pair = string.Concat(current, ToKatakana(value[index + 1]));
                if (Digraphs.TryGetValue(pair, out var combined))
                {
                    syllable = combined;
                    index++;
                }
                else syllable = Kana.TryGetValue(current, out var mapped) ? mapped : current.ToString();
            }
            else syllable = Kana.TryGetValue(current, out var mapped) ? mapped : current.ToString();

            if (geminateNext)
            {
                if (syllable.Length > 0 && !"aeioun".Contains(syllable[0])) output.Append(syllable[0]);
                geminateNext = false;
            }
            output.Append(syllable);
        }
        return output.ToString();
    }

    private static char ToKatakana(char value) => value is >= '\u3041' and <= '\u3096'
        ? (char)(value + 0x60)
        : value;

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
            return (double)Math.Min(left.Length, right.Length) / Math.Max(left.Length, right.Length);
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++) previous[index] = index;
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] +
                                   (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    private static IReadOnlyDictionary<char, string> CreateKanaMap()
    {
        var result = new Dictionary<char, string>();
        AddRow(result, "アイウエオ", "a i u e o");
        AddRow(result, "カキクケコ", "ka ki ku ke ko");
        AddRow(result, "サシスセソ", "sa shi su se so");
        AddRow(result, "タチツテト", "ta chi tsu te to");
        AddRow(result, "ナニヌネノ", "na ni nu ne no");
        AddRow(result, "ハヒフヘホ", "ha hi fu he ho");
        AddRow(result, "マミムメモ", "ma mi mu me mo");
        AddRow(result, "ヤユヨ", "ya yu yo");
        AddRow(result, "ラリルレロ", "ra ri ru re ro");
        AddRow(result, "ワヰヱヲ", "wa wi we wo");
        AddRow(result, "ガギグゲゴ", "ga gi gu ge go");
        AddRow(result, "ザジズゼゾ", "za ji zu ze zo");
        AddRow(result, "ダヂヅデド", "da ji zu de do");
        AddRow(result, "バビブベボ", "ba bi bu be bo");
        AddRow(result, "パピプペポ", "pa pi pu pe po");
        AddRow(result, "ァィゥェォ", "a i u e o");
        AddRow(result, "ヵヶ", "ka ke");
        result['ン'] = "n";
        return result;
    }

    private static void AddRow(IDictionary<char, string> target, string characters, string values)
    {
        var syllables = values.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Min(characters.Length, syllables.Length); index++)
            target[characters[index]] = syllables[index];
    }
}
