using Chelmsford.Core.Courses;

namespace Chelmsford.Web.Services;

/// <summary>
/// People search for jobs ("chef", "plumber"); the college names courses after the subject
/// ("catering", "plumbing"). This small map turns a job word into the words courses actually use, in
/// the web layer, before the course search service is called. The service itself matches every word
/// it is given, so each expansion is searched on its own and the results are merged.
/// </summary>
public static class CourseSearchSynonyms
{
    private const int MaxVariants = 8;

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chef"] = ["cookery", "catering"],
        ["cook"] = ["cookery", "catering"],
        ["plumber"] = ["plumbing"],
        ["electrician"] = ["electrical"],
        ["hairdresser"] = ["hairdressing"],
        ["barber"] = ["hairdressing"],
        ["beautician"] = ["beauty"],
        ["builder"] = ["carpentry", "construction"],
        ["joiner"] = ["carpentry", "construction"],
        ["mechanic"] = ["engineering"],
        ["nurse"] = ["care", "health"],
        ["carer"] = ["care", "health"],
        ["games"] = ["media", "digital"],
        ["game"] = ["media", "digital"],
        ["actor"] = ["performing"],
        ["acting"] = ["performing"]
    };

    /// <summary>The text as typed, followed by one variant for each synonym of each job word in it.</summary>
    public static IReadOnlyList<string> Variants(string? text)
    {
        var typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0)
        {
            return [typed];
        }

        var words = typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var variants = new List<string> { typed };
        for (var i = 0; i < words.Length && variants.Count < MaxVariants; i++)
        {
            if (!TryLookup(words[i], out var synonyms))
            {
                continue;
            }

            foreach (var synonym in synonyms)
            {
                var copy = (string[])words.Clone();
                copy[i] = synonym;
                var variant = string.Join(' ', copy);
                if (variants.Count < MaxVariants && !variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                {
                    variants.Add(variant);
                }
            }
        }

        return variants;
    }

    private static bool TryLookup(string word, out string[] synonyms)
    {
        word = word.Trim('.', ',', ';', ':', '!', '?', '"', '\'');
        if (Map.TryGetValue(word, out synonyms!))
        {
            return true;
        }

        // "chefs", "plumbers", "nurses"
        if (word.Length > 3 && word.EndsWith('s') && Map.TryGetValue(word[..^1], out synonyms!))
        {
            return true;
        }

        synonyms = [];
        return false;
    }

    /// <summary>
    /// Searches for the text as typed and for each of its synonym variants, then merges the courses
    /// (each course once, in the service's usual order). Filters apply to every variant. The facet
    /// counts come from the variant that found the most courses.
    /// </summary>
    public static async Task<CourseSearchResult> SearchWithSynonymsAsync(
        this ICourseSearchService service,
        CourseSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var variants = Variants(query.Text);
        if (variants.Count == 1)
        {
            return await service.SearchAsync(query, cancellationToken);
        }

        var results = new List<CourseSearchResult>();
        foreach (var variant in variants)
        {
            results.Add(await service.SearchAsync(query with { Text = variant }, cancellationToken));
        }

        var best = results.OrderByDescending(r => r.Courses.Count).First();
        var merged = results
            .SelectMany(r => r.Courses)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.LevelOrder)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();

        return best with { Courses = merged };
    }
}
