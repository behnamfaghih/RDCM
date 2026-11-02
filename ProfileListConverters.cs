using System.Globalization;
using System.Windows.Data;

namespace RDCM;

/// <summary>Values: [0] profile id, [1] set of ids. Returns whether the id is in the set.</summary>
public sealed class ProfileIdInSetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [Guid id, IReadOnlySet<Guid> ids, ..] && ids.Contains(id);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Accessible item status for a connection row.
/// Values: [0] profile id, [1] session indicator map, [2] favourite ids.
/// </summary>
public sealed class ProfileAutomationStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [Guid id, ..])
        {
            return "";
        }
        var parts = new List<string>(2);
        if (values.Length > 2 && values[2] is IReadOnlySet<Guid> favorites && favorites.Contains(id))
        {
            parts.Add("Favourite");
        }
        if (values.Length > 1 && values[1] is IReadOnlyDictionary<Guid, string> sessions &&
            sessions.TryGetValue(id, out var indicator))
        {
            parts.Add(SessionIndicator.Describe(indicator));
        }
        return string.Join(", ", parts.Where(part => part.Length > 0));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Initial expanded state for a list group, looked up by group name in <see cref="CollapsedGroups"/>.</summary>
public sealed class GroupExpansionConverter : IValueConverter
{
    public ISet<string>? CollapsedGroups { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not string name || CollapsedGroups?.Contains(name) != true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
