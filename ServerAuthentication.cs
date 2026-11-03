using System.IO;
using Microsoft.Win32;

namespace RDCM;

/// <summary>
/// Remote Desktop server authentication (IMsRdpClientAdvancedSettings4.AuthenticationLevel; the same values are
/// used by the "Configure server authentication for client" policy).
/// </summary>
public enum ServerAuthenticationMode
{
    /// <summary>0: connect without verifying the server's identity ("Always connect, even if authentication fails").</summary>
    Off = 0,

    /// <summary>1: connect only if the server's identity is verified ("Do not connect if authentication fails").</summary>
    Required = 1,

    /// <summary>2: verify the server; if that fails, ask the user ("Warn me if authentication fails").</summary>
    WarnIfUnverified = 2
}

public enum ServerAuthenticationSource
{
    /// <summary>The level RDCM requested is in effect.</summary>
    Application,

    /// <summary>The machine policy "Configure server authentication for client" decides the level.</summary>
    MachinePolicy,

    /// <summary>Remote Desktop didn't apply the requested level and no policy explains why.</summary>
    Unexplained
}

/// <summary>What RDCM asked for, and what Remote Desktop will actually do, for one connection attempt.</summary>
public sealed record ServerAuthenticationStatus(
    ServerAuthenticationMode Requested,
    ServerAuthenticationMode? Effective,
    ServerAuthenticationSource Source)
{
    /// <summary>Server identity checks are weaker than requested (or couldn't be confirmed).</summary>
    public bool IsWeakened => Effective is not { } effective || Strength(effective) < Strength(Requested);

    public string Describe() => Effective switch
    {
        null => "Unknown: Remote Desktop didn't report the server authentication level",
        ServerAuthenticationMode.Off when Source == ServerAuthenticationSource.MachinePolicy =>
            "Off: a policy on this PC connects even if the server's identity can't be verified",
        ServerAuthenticationMode.Off => "Off: Remote Desktop won't verify the server's identity",
        ServerAuthenticationMode.Required => "Required: connects only to a verified server",
        _ => "Warn: asks before connecting to a server whose identity can't be verified"
    };

    private static int Strength(ServerAuthenticationMode mode) => mode switch
    {
        ServerAuthenticationMode.Required => 2,
        ServerAuthenticationMode.WarnIfUnverified => 1,
        _ => 0
    };
}

public static class ServerAuthentication
{
    /// <summary>RDCM asks Remote Desktop to verify the server and to warn the user when it can't.</summary>
    public const ServerAuthenticationMode RequestedMode = ServerAuthenticationMode.WarnIfUnverified;

    /// <summary>Registry location of the "Configure server authentication for client" policy (TerminalServer.admx).</summary>
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    public const string PolicyValue = "AuthenticationLevel";

    /// <summary>
    /// Combines the requested level, the policy, and the control's read-back. A configured policy overrides the
    /// application (the control then rejects the requested value with S_FALSE and reports the policy's level).
    /// </summary>
    public static ServerAuthenticationStatus Evaluate(ServerAuthenticationMode requested, int? controlReadBack, int? machinePolicy)
    {
        if (ToMode(machinePolicy) is { } policy)
        {
            return new(requested, policy, ServerAuthenticationSource.MachinePolicy);
        }
        if (ToMode(controlReadBack) is { } effective)
        {
            return new(requested, effective,
                effective == requested ? ServerAuthenticationSource.Application : ServerAuthenticationSource.Unexplained);
        }
        return new(requested, null, ServerAuthenticationSource.Unexplained);
    }

    public static int? ReadMachinePolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(PolicyValue) is int value ? value : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning("The server authentication policy couldn't be read.", exception);
            return null;
        }
    }

    private static ServerAuthenticationMode? ToMode(int? value) => value is 0 or 1 or 2 ? (ServerAuthenticationMode)value : null;
}
