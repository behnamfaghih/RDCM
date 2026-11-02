using System.Windows.Input;

namespace RDCM;

/// <summary>A keyboard shortcut. Number-pad digits are treated like the main digit keys.</summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public bool Matches(Key key, ModifierKeys modifiers) =>
        Normalize(key) == Normalize(Key) && modifiers == Modifiers;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Key Normalize(Key key) =>
        key is >= Key.NumPad0 and <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Delete => "Del",
        Key.Escape => "Esc",
        Key.OemQuestion => "/",
        Key.OemComma => ",",
        _ => key.ToString()
    };
}

/// <summary>
/// An application command. The registry is the single source for keyboard routing, the command palette,
/// and the keyboard shortcut reference.
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string Icon { get; init; } = "\uE756";
    public Shortcut? Shortcut { get; init; }

    /// <summary>Shortcut text for the reference when it is not a single routable shortcut (for example "Ctrl+1…9").</summary>
    public string? ShortcutText { get; init; }

    /// <summary>Extra words the palette matches, such as synonyms.</summary>
    public string Keywords { get; init; } = "";

    public bool ShowInPalette { get; init; } = true;
    public bool ShowInReference { get; init; } = true;

    /// <summary>False for shortcuts that would steal editing keys (such as Delete) while a text field has focus.</summary>
    public bool AllowInTextInput { get; init; } = true;

    public Func<bool> CanExecute { get; init; } = static () => true;
    public Action Execute { get; init; } = static () => { };

    public string? DisplayShortcut => ShortcutText ?? Shortcut?.ToString();
}

public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];

    public IReadOnlyList<AppCommand> Commands => _commands;

    public void Add(AppCommand command)
    {
        if (_commands.Any(existing => existing.Id == command.Id))
        {
            throw new ArgumentException($"A command with id '{command.Id}' is already registered.", nameof(command));
        }
        if (command.Shortcut is { } shortcut &&
            _commands.FirstOrDefault(existing => existing.Shortcut is { } other &&
                                                 other.Matches(shortcut.Key, shortcut.Modifiers)) is { } conflict)
        {
            throw new ArgumentException($"{shortcut} is already used by '{conflict.Id}'.", nameof(command));
        }
        _commands.Add(command);
    }

    public AppCommand Get(string id) => _commands.First(command => command.Id == id);

    /// <summary>Returns the command bound to the key, or null when none applies in the current context.</summary>
    public AppCommand? FindByShortcut(Key key, ModifierKeys modifiers, bool inTextInput) =>
        _commands.FirstOrDefault(command =>
            command.Shortcut is { } shortcut &&
            shortcut.Matches(key, modifiers) &&
            (command.AllowInTextInput || !inTextInput) &&
            command.CanExecute());
}
