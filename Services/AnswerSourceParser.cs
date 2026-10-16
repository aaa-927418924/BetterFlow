using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal sealed record AnswerSourceCard(string Title, string Url, string Host);

internal sealed record ParsedAnswer(string Text, IReadOnlyList<AnswerSourceCard> Sources);

internal static class AnswerSourceParser
{
    private const int MaximumSourceCards = 8;

    private static readonly Regex SourceHeading = new(
        @"(?im)^\s{0,3}(?:#{1,6}\s*)?(?:出典|参考資料|Sources|References)(?:(?:\s*[:：]\s*.*)|\s*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownLink = new(
        @"(?:[-*•]\s*)?\[(?<title>[^\]]{1,200})\]\((?<url>https?://[^)\s]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ParsedAnswer Parse(string? response)
    {
        var text = response ?? string.Empty;
        var heading = SourceHeading.Match(text);
        if (!heading.Success)
            return new ParsedAnswer(text, Array.Empty<AnswerSourceCard>());

        var sourceText = text[heading.Index..];
        var cards = new List<AnswerSourceCard>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MarkdownLink.Matches(sourceText))
        {
            if (cards.Count >= MaximumSourceCards) break;
            var title = match.Groups["title"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            if (title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !seenUrls.Add(uri.AbsoluteUri))
                continue;

            cards.Add(new AnswerSourceCard(title, uri.AbsoluteUri, uri.Host));
        }

        return new ParsedAnswer(text[..heading.Index].TrimEnd(), cards);
    }
}
