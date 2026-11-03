using System.Text;
using System.Text.RegularExpressions;

namespace RDCM;

/// <summary>What kind of problem ended a connection, for user guidance. Derived from the control's reason codes.</summary>
public enum RdpFailureCategory
{
    Unknown,
    AddressNotFound,
    Unreachable,
    TimedOut,
    NetworkInterrupted,
    SignInFailed,
    SecurityCheckFailed,
    Licensing,
    AccessDenied,
    ClientError
}

/// <summary>The native codes behind a failure. <see cref="ErrorCode"/> is an HRESULT or control error code, when known.</summary>
public sealed record RdpFailureInfo(
    RdpFailureCategory Category,
    int? Reason = null,
    int? ExtendedReason = null,
    int? ErrorCode = null);

public sealed record RdpFailureGuidance(string Title, string? Hint);

/// <summary>
/// Maps Remote Desktop ActiveX disconnect codes to categories. Sources (Microsoft Learn):
/// IMsTscAxEvents::OnDisconnected (discReason values) and the ExtendedDisconnectReasonCode enumeration.
/// </summary>
public static class RdpFailureClassifier
{
    private static readonly Dictionary<int, RdpFailureCategory> ReasonCategories = new()
    {
        // Name lookup and invalid addresses
        [260] = RdpFailureCategory.AddressNotFound,   // disconnectReasonDNSLookupFailed
        [520] = RdpFailureCategory.AddressNotFound,   // disconnectReasonHostNotFound
        [776] = RdpFailureCategory.AddressNotFound,   // disconnectReasonInvalidIPAddr
        [1288] = RdpFailureCategory.AddressNotFound,  // disconnectReasonDNSLookupFailed2
        [1540] = RdpFailureCategory.AddressNotFound,  // disconnectReasonGetHostByNameFailed
        [2052] = RdpFailureCategory.AddressNotFound,  // disconnectReasonInvalidIP
        // Connection refused or no route
        [516] = RdpFailureCategory.Unreachable,       // disconnectReasonSocketConnectFailed
        // Timeouts
        [264] = RdpFailureCategory.TimedOut,          // disconnectReasonConnectionTimedOut
        [1796] = RdpFailureCategory.TimedOut,         // disconnectReasonTimeoutOccurred
        // Network failures after the socket opened
        [772] = RdpFailureCategory.NetworkInterrupted,  // disconnectReasonWinsockSendFailed
        [1028] = RdpFailureCategory.NetworkInterrupted, // disconnectReasonSocketRecvFailed
        [2308] = RdpFailureCategory.NetworkInterrupted, // disconnectReasonAtClientWinsockFDCLOSE
        // Sign-in and credential delegation
        [2055] = RdpFailureCategory.SignInFailed,     // SSL_ERR_LOGON_FAILURE
        [2567] = RdpFailureCategory.SignInFailed,     // SSL_ERR_NO_SUCH_USER
        [2823] = RdpFailureCategory.SignInFailed,     // SSL_ERR_ACCOUNT_DISABLED
        [3079] = RdpFailureCategory.SignInFailed,     // SSL_ERR_ACCOUNT_RESTRICTION
        [3335] = RdpFailureCategory.SignInFailed,     // SSL_ERR_ACCOUNT_LOCKED_OUT
        [3591] = RdpFailureCategory.SignInFailed,     // SSL_ERR_ACCOUNT_EXPIRED
        [3847] = RdpFailureCategory.SignInFailed,     // SSL_ERR_PASSWORD_EXPIRED
        [4615] = RdpFailureCategory.SignInFailed,     // SSL_ERR_PASSWORD_MUST_CHANGE
        [5639] = RdpFailureCategory.SignInFailed,     // SSL_ERR_DELEGATION_POLICY
        [5895] = RdpFailureCategory.SignInFailed,     // SSL_ERR_POLICY_NTLM_ONLY
        [6151] = RdpFailureCategory.SignInFailed,     // SSL_ERR_NO_AUTHENTICATING_AUTHORITY
        [7175] = RdpFailureCategory.SignInFailed,     // SSL_ERR_SMARTCARD_WRONG_PIN
        [8455] = RdpFailureCategory.SignInFailed,     // SSL_ERR_FRESH_CRED_REQUIRED_BY_SERVER
        [8711] = RdpFailureCategory.SignInFailed,     // SSL_ERR_SMARTCARD_CARD_BLOCKED
        // Certificates, encryption, and security negotiation
        [1030] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonInvalidSecurityData
        [1286] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonInvalidEncryption
        [1542] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonInvalidServerSecurityInfo
        [1798] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonServerCertificateUnpackErr
        [2310] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonInternalSecurityError
        [2566] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonInternalSecurityError2
        [2822] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonEncryptionError
        [3078] = RdpFailureCategory.SecurityCheckFailed, // disconnectReasonDecryptionError
        [6919] = RdpFailureCategory.SecurityCheckFailed, // SSL_ERR_CERT_EXPIRED
        // Licensing
        [2056] = RdpFailureCategory.Licensing,        // disconnectReasonLicensingFailed
        [2312] = RdpFailureCategory.Licensing,        // disconnectReasonLicensingTimeout
        // Client-side failures
        [262] = RdpFailureCategory.ClientError,       // disconnectReasonOutOfMemory
        [518] = RdpFailureCategory.ClientError,       // disconnectReasonOutOfMemory2
        [774] = RdpFailureCategory.ClientError,       // disconnectReasonOutOfMemory3
        [1032] = RdpFailureCategory.ClientError,      // disconnectReasonInternalError
        [1544] = RdpFailureCategory.ClientError,      // disconnectReasonTimerError
        [3080] = RdpFailureCategory.ClientError       // disconnectReasonClientDecompressionError
    };

    /// <summary>The extended reason is more specific, so it takes precedence when it identifies a category.</summary>
    public static RdpFailureCategory Classify(int reason, int extendedReason) => extendedReason switch
    {
        7 or 8 or 9 => RdpFailureCategory.AccessDenied,     // ServerDeniedConnection(Fips), ServerInsufficientPrivileges
        10 or 768 => RdpFailureCategory.SignInFailed,       // ServerFreshCredsRequired, RdpEncInvalidCredentials
        >= 256 and <= 267 => RdpFailureCategory.Licensing,  // exDiscReasonLicense*
        _ => ReasonCategories.GetValueOrDefault(reason, RdpFailureCategory.Unknown)
    };

    public static RdpFailureGuidance Describe(RdpFailureCategory category, RdpEndReason endReason, int port)
    {
        var lost = endReason == RdpEndReason.ConnectionLost;
        return category switch
        {
            RdpFailureCategory.AddressNotFound => new("Couldn't find the remote computer",
                "Check the host name for typing errors, or use the computer's IP address. If the computer is on a private network, connect to its VPN first."),
            RdpFailureCategory.Unreachable => new("Couldn't reach the remote computer",
                $"Check that the computer is turned on, Remote Desktop is enabled on it, and a firewall isn't blocking port {port}."),
            RdpFailureCategory.TimedOut => new(lost ? "Connection lost" : "The connection timed out",
                "The remote computer didn't respond in time. Check your network or VPN connection, then try again."),
            RdpFailureCategory.NetworkInterrupted => new(lost ? "Connection lost" : "The connection was interrupted",
                "The network connection to the remote computer was interrupted. Check your network or VPN connection, then try again."),
            RdpFailureCategory.SignInFailed => new("Sign-in failed",
                "Check the user name and password. The account might be locked or disabled, or its password might have expired."),
            RdpFailureCategory.SecurityCheckFailed => new("Security check failed",
                "The remote computer's identity or security settings couldn't be verified. If this continues, contact the computer's administrator."),
            RdpFailureCategory.Licensing => new("Remote Desktop licensing problem",
                "The remote computer couldn't provide a Remote Desktop license. Contact the computer's administrator."),
            RdpFailureCategory.AccessDenied => new("Access denied",
                "Your account isn't allowed to connect to this computer, or the server refused the connection. Contact the computer's administrator."),
            RdpFailureCategory.ClientError => new("Remote Desktop problem",
                "If this keeps happening, restart RDCM."),
            _ => new(lost ? "Connection lost" : "Couldn't connect", null)
        };
    }
}

/// <summary>
/// Plain-text diagnostics for support requests. Connection name, host, and user names are replaced with
/// placeholders so the text can be shared; reason codes and versions are kept.
/// </summary>
public static class RdpDiagnosticReport
{
    public static string Build(RdpSessionSnapshot snapshot, DateTimeOffset time, string appVersion, string windowsVersion)
    {
        var identity = snapshot.Identity;
        var builder = new StringBuilder();
        builder.AppendLine("RDCM connection diagnostics");
        builder.AppendLine($"Time: {time.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine($"RDCM version: {appVersion}");
        builder.AppendLine($"Windows: {windowsVersion}");
        builder.AppendLine($"Session state: {snapshot.State}");
        builder.AppendLine($"Outcome: {snapshot.EndReason}");
        if (snapshot.Failure is { } failure)
        {
            builder.AppendLine($"Category: {failure.Category}");
            if (failure.Reason is { } reason)
            {
                builder.AppendLine($"Disconnect reason: {reason} (0x{reason:X})");
            }
            if (failure.ExtendedReason is { } extended)
            {
                builder.AppendLine($"Extended reason: {extended}");
            }
            if (failure.ErrorCode is { } code)
            {
                builder.AppendLine($"Error code: 0x{code:X8}");
            }
        }
        builder.AppendLine($"Port: {(identity.Port == 3389 ? "default" : "custom (number omitted)")}");
        if (snapshot.ServerAuthentication is { } serverAuthentication)
        {
            builder.AppendLine($"Server authentication: {serverAuthentication.Describe()}");
        }
        builder.AppendLine($"User name provided: {(string.IsNullOrWhiteSpace(identity.Username) ? "No" : "Yes")}");
        builder.AppendLine($"Message: {Redact(snapshot.Message, identity)}");
        if (!string.IsNullOrWhiteSpace(snapshot.Detail) && snapshot.Detail != snapshot.Message)
        {
            builder.AppendLine($"Details: {Redact(snapshot.Detail, identity)}");
        }
        builder.AppendLine();
        builder.Append("Connection, computer, and user names are replaced with <connection>, <host>, and <user>.");
        return builder.ToString();
    }

    public static string Redact(string text, SessionIdentity identity)
    {
        var tokens = new List<(string Value, string Placeholder)>
        {
            (identity.Address, "<host>"),
            (identity.Host.Trim().Trim('[', ']'), "<host>"),
            (identity.Name, "<connection>"),
            (identity.Username, "<user>")
        };
        // Parts of DOMAIN\user and user@domain; ".\admin" yields "." which must never become a pattern.
        foreach (var part in identity.Username.Split('\\', '@'))
        {
            if (part.Trim().Length >= 2)
            {
                tokens.Add((part, "<user>"));
            }
        }

        // One pass with the longest names first, so "server.contoso.com" wins over "server" and
        // placeholders are never rescanned.
        var ordered = tokens
            .Where(token => !string.IsNullOrWhiteSpace(token.Value) && token.Value.Any(char.IsLetterOrDigit))
            .Select(token => (Value: token.Value.Trim(), token.Placeholder))
            .DistinctBy(token => token.Value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(token => token.Value.Length)
            .ToList();
        if (ordered.Count == 0)
        {
            return text;
        }
        var pattern = $"(?<![A-Za-z0-9])(?:{string.Join("|", ordered.Select(token => Regex.Escape(token.Value)))})(?![A-Za-z0-9])";
        return Regex.Replace(text, pattern,
            match => ordered.FirstOrDefault(token => string.Equals(token.Value, match.Value, StringComparison.OrdinalIgnoreCase)).Placeholder ?? "<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
