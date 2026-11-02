namespace RDCM;

public enum PaletteItemKind
{
    Session,
    Connection,
    Command
}

/// <summary>One command palette entry. <see cref="IsSuggested"/> entries are listed before anything is typed.</summary>
public sealed record PaletteItem(
    PaletteItemKind Kind,
    string Title,
    string? Subtitle,
    string Icon,
    string Label,
    string? Shortcut,
    Action Execute,
    IReadOnlyList<string> SearchFields,
    bool IsSuggested)
{
    public string AutomationName => string.IsNullOrWhiteSpace(Subtitle)
        ? $"{Title}, {Label}"
        : $"{Title}, {Subtitle}, {Label}";

    /// <summary>The shortcut split into keys for keycap display, for example ["Ctrl", "Shift", "P"].</summary>
    public IReadOnlyList<string> ShortcutKeys => Shortcut is null ? [] : Shortcut.Split('+');
}

public static class CommandPaletteSearch
{
    public const string CommandPrefix = ">";
    public const int MaxResults = 200;

    /// <summary>
    /// Ranks candidates for the palette text. "&gt;" limits results to commands. With no text, only suggested
    /// entries are listed in their given order.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Filter(string? text, IReadOnlyList<PaletteItem> candidates)
    {
        text ??= "";
        var commandsOnly = text.TrimStart().StartsWith(CommandPrefix, StringComparison.Ordinal);
        var query = SearchQuery.Parse(commandsOnly ? text.TrimStart()[CommandPrefix.Length..] : text);
        var pool = commandsOnly ? candidates.Where(item => item.Kind == PaletteItemKind.Command) : candidates;

        if (query.IsEmpty)
        {
            return pool.Where(item => commandsOnly || item.IsSuggested).Take(MaxResults).ToList();
        }

        return pool
            .Select(item => (Item: item, Score: Score(query, item)))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Item.Kind)
            .ThenBy(result => result.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(result => result.Item)
            .ToList();
    }

    private static int Score(SearchQuery query, PaletteItem item) =>
        query.Score(item.Title, [.. item.SearchFields]);
}
