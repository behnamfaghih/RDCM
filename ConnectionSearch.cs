namespace RDCM;

/// <summary>
/// Case-insensitive, multi-word search shared by the connection list and the command palette.
/// Every word must appear in at least one field, so "web prod" narrows to web servers noted as production.
/// </summary>
public sealed class SearchQuery
{
    public static readonly SearchQuery Empty = new([]);

    private readonly string[] _terms;

    private SearchQuery(string[] terms) => _terms = terms;

    public IReadOnlyList<string> Terms => _terms;
    public bool IsEmpty => _terms.Length == 0;

    public static SearchQuery Parse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Empty
            : new SearchQuery(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public bool Matches(params ReadOnlySpan<string?> fields)
    {
        foreach (var term in _terms)
        {
            var found = false;
            foreach (var field in fields)
            {
                if (field?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Relevance for ranking (0 = no match). Matches at the start of the title, or of a word in it, rank above
    /// matches inside the title, which rank above matches in the secondary fields.
    /// </summary>
    public int Score(string title, params ReadOnlySpan<string?> secondary)
    {
        var total = 0;
        foreach (var term in _terms)
        {
            var best = ScoreField(title, term, 100, 70, 40);
            foreach (var field in secondary)
            {
                best = Math.Max(best, ScoreField(field, term, 25, 20, 10));
            }
            if (best == 0)
            {
                return 0;
            }
            total += best;
        }
        return Math.Max(total, 1);
    }

    private static int ScoreField(string? field, string term, int prefix, int wordStart, int contains)
    {
        if (string.IsNullOrEmpty(field))
        {
            return 0;
        }
        var index = field.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }
        if (index == 0)
        {
            return prefix;
        }
        // Prefer a later match that starts a word ("prod" in "web-prod") over the first inner match.
        for (var i = index; i >= 0 && i < field.Length; i = field.IndexOf(term, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsLetterOrDigit(field[i - 1]))
            {
                return wordStart;
            }
        }
        return contains;
    }
}

public static class ConnectionSearch
{
    public static bool Matches(SearchQuery query, ConnectionProfile profile) =>
        query.IsEmpty ||
        query.Matches(profile.Name, profile.Address, profile.Username, profile.Group, profile.Notes);

    public static int Score(SearchQuery query, ConnectionProfile profile) =>
        query.Score(profile.Name, profile.Address, profile.Username, profile.Group, profile.Notes);
}
