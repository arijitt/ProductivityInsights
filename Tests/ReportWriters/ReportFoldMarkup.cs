using System.Text.RegularExpressions;

namespace ProductivityInsights.ReportTests.ReportWriters;

internal static class ReportFoldMarkup
{
    internal sealed record Fold(string Kind, int Start, int Length, string Content, int? ParentStart);

    internal static Fold[] Parse(string html)
    {
        var stack = new Stack<(string Kind, int Start, int ContentStart, int? ParentStart)>();
        var folds = new List<Fold>();
        foreach (Match tag in Regex.Matches(html, @"<details\b[^>]*>|</details>"))
        {
            if (tag.Value == "</details>")
            {
                if (!stack.TryPop(out var section))
                    throw new InvalidOperationException("A report fold has an unmatched closing tag.");
                folds.Add(new(section.Kind, section.Start, tag.Index + tag.Length - section.Start,
                    html[section.ContentStart..tag.Index], section.ParentStart));
            }
            else
            {
                var kind = Regex.Match(tag.Value, @"^<details class=""report-(person|item|context|queue|owner)-details"">$");
                if (!kind.Success)
                    throw new InvalidOperationException("A report fold must have a known kind and start collapsed.");
                stack.Push((kind.Groups[1].Value, tag.Index, tag.Index + tag.Length,
                    stack.TryPeek(out var parent) ? parent.Start : null));
            }
        }
        if (stack.Count != 0) throw new InvalidOperationException("A report fold is missing its closing tag.");
        return folds.OrderBy(fold => fold.Start).ToArray();
    }

    internal static string OutsidePersons(string html)
    {
        foreach (var fold in Parse(html).Where(fold => fold.Kind == "person").OrderByDescending(fold => fold.Start))
            html = html.Remove(fold.Start, fold.Length);
        return html;
    }
}
