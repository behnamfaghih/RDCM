namespace RDCM;

public sealed record ProfileValidationIssue(string Field, string Message);

public static class ProfileValidation
{
    public static ProfileValidationIssue? Validate(
        ConnectionProfile profile,
        IEnumerable<ConnectionProfile> existingProfiles)
    {
        if (profile.Id == Guid.Empty)
        {
            return new("name", "This profile has an invalid identifier.");
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return new("name", "Enter a connection name.");
        }
        if (!ConnectionProfile.IsValidHost(profile.Host))
        {
            return new("host", "Enter a valid host name or IP address.");
        }
        if (profile.Port is < 1 or > 65535)
        {
            return new("port", "Enter a port between 1 and 65535.");
        }
        if (profile.Username is null || ConnectionProfile.ContainsLineBreak(profile.Username))
        {
            return new("username", "Username cannot contain line breaks.");
        }
        if (profile.Group is null || ConnectionProfile.ContainsLineBreak(profile.Group))
        {
            return new("group", "Group cannot contain line breaks.");
        }
        if (profile.Notes is null || ConnectionProfile.ContainsLineBreak(profile.Notes))
        {
            return new("notes", "Notes cannot contain line breaks.");
        }
        if (existingProfiles.Any(existing => existing.Id != profile.Id &&
                string.Equals(existing.Name?.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return new("name", "A profile with this name already exists. Choose a distinct name.");
        }

        return null;
    }

    /// <summary>A distinct name for a copy: "Name (copy)", then "Name (copy 2)", and so on.</summary>
    public static string CopyName(string name, IEnumerable<ConnectionProfile> existingProfiles)
    {
        var taken = existingProfiles
            .Select(existing => existing.Name?.Trim())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = name.Trim();
        var candidate = $"{baseName} (copy)";
        for (var number = 2; taken.Contains(candidate); number++)
        {
            candidate = $"{baseName} (copy {number})";
        }
        return candidate;
    }
}
