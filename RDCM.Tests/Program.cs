using System.IO;
using RDCM;

var testCount = 0;
var failedTests = new List<string>();
string? currentTest = null;
// A crash on another thread (or an exception nobody observed) is reported with the check that was running.
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    Console.WriteLine($"CRASH while '{currentTest ?? "(between checks)"}' ran, on another thread: {Describe(args.ExceptionObject as Exception)}");
TaskScheduler.UnobservedTaskException += (_, args) =>
{
    failedTests.Add($"unobserved task exception (last check: {currentTest ?? "none"})");
    Console.WriteLine($"FAIL unobserved task exception: {Describe(args.Exception)}");
};
Run("profile validation accepts a valid profile", () =>
{
    Assert(ProfileValidation.Validate(Profile("Admin"), []) is null);
});
Run("profile validation rejects duplicate names case-insensitively", () =>
{
    var original = Profile("Production");
    var duplicate = Profile("production");
    Assert(ProfileValidation.Validate(duplicate, [original])?.Field == "name");
});
Run("profile validation checks host and port", () =>
{
    var invalidHost = Profile("Invalid");
    invalidHost.Host = "host name";
    Assert(ProfileValidation.Validate(invalidHost, [])?.Field == "host");
    invalidHost.Host = "rdcm-test.local";
    invalidHost.Port = 65536;
    Assert(ProfileValidation.Validate(invalidHost, [])?.Field == "port");
});
Run("profile validation rejects null fields from deserialized data", () =>
{
    var invalid = Profile("Malformed");
    invalid.Name = null!;
    Assert(ProfileValidation.Validate(invalid, [])?.Field == "name");

    invalid = Profile("Malformed");
    invalid.Host = null!;
    Assert(ProfileValidation.Validate(invalid, [])?.Field == "host");

    invalid = Profile("Malformed");
    invalid.Username = null!;
    Assert(ProfileValidation.Validate(invalid, [])?.Field == "username");

    invalid = Profile("Malformed");
    invalid.Group = null!;
    Assert(ProfileValidation.Validate(invalid, [])?.Field == "group");

    invalid = Profile("Malformed");
    invalid.Notes = null!;
    Assert(ProfileValidation.Validate(invalid, [])?.Field == "notes");
});
Run("session identity is independent of edited profiles", () =>
{
    var profile = Profile("Before edit");
    var identity = SessionIdentity.Capture(profile);
    profile.Name = "After edit";
    profile.Host = "changed.example";
    profile.Port = 3390;
    Assert(identity.Name == "Before edit");
    Assert(identity.Address == "rdcm-test.local");
});
Run("session state rejects stale transitions and identity changes while active", () =>
{
    var model = new RdpSessionModel(SessionIdentity.Capture(Profile("Session")));
    model.TransitionTo(RdpSessionState.Connecting);
    AssertThrows<InvalidOperationException>(() =>
        model.ReplaceIdentityWhenInactive(SessionIdentity.Capture(Profile("Changed"))));
    model.TransitionTo(RdpSessionState.Connected);
    model.TransitionTo(RdpSessionState.Disconnecting);
    model.TransitionTo(RdpSessionState.Disconnected);
    model.ReplaceIdentityWhenInactive(SessionIdentity.Capture(Profile("Changed")));
    model.TransitionTo(RdpSessionState.Disposed);
    AssertThrows<InvalidOperationException>(() => model.TransitionTo(RdpSessionState.Connected));
});
Run("legacy profile JSON remains readable without notes", () =>
{
    var id = Guid.NewGuid();
    var json = $$"""[{"Id":"{{id}}","Name":"Legacy","Host":"rdcm-test.local","Port":3389,"Username":"","Group":"General"}]""";
    var profiles = ProfileStore.Deserialize(json);
    Assert(profiles.Count == 1 && profiles[0].Notes == "");
});
Run("profile store saves, recovers a backup, and preserves corrupt source", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "RDCM.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var filePath = Path.Combine(directory, "connections.json");
    try
    {
        var store = new ProfileStore(filePath);
        var first = Profile("First");
        store.Save([first]);
        var second = Profile("Second");
        store.Save([second]);
        File.WriteAllText(filePath, "{ invalid json");

        var recoveringStore = new ProfileStore(filePath);
        var recovered = recoveringStore.Load();
        Assert(recoveringStore.RecoveredFromBackup);
        Assert(recovered.Count == 1 && recovered[0].Name == "First");
        Assert(File.ReadAllText(filePath) == "{ invalid json");

        recoveringStore.Save(recovered);
        var preservedPath = recoveringStore.PreservedCorruptPath
            ?? throw new InvalidOperationException("The corrupt source was not preserved.");
        Assert(File.ReadAllText(preservedPath) == "{ invalid json");
        Assert(new ProfileStore(filePath).Load().Single().Name == "First");
    }
    finally
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }
        Directory.Delete(directory);
    }
});

RunLifecycleTests();
RunPreferenceTests();
RunDpiTests();
RunFeatureTests();
// Runner self-test (opt-in): proves failures and crashes are reported with context rather than lost.
switch (Environment.GetEnvironmentVariable("RDCM_TESTS_SELF_TEST"))
{
    case "failure":
        Run("self-test: injected failure is reported", () => throw new IOException(Path.Combine(Path.GetTempPath(), "injected.json")));
        break;
    case "crash":
        currentTest = "self-test: injected background crash";
        var crash = new Thread(() => throw new InvalidOperationException("injected background crash"));
        crash.Start();
        crash.Join();
        break;
}
GC.Collect();
GC.WaitForPendingFinalizers();
if (failedTests.Count == 0)
{
    Console.WriteLine($"Passed {testCount} RDCM checks.");
    return 0;
}
Console.WriteLine($"FAILED {failedTests.Count} of {testCount + failedTests.Count} RDCM checks (test host process {Environment.ProcessId}, exit code 1):");
failedTests.ForEach(name => Console.WriteLine($"  - {name}"));
return 1;

// ---------------------------------------------------------------------------------------------
// Search, command palette, shortcuts, diagnostics, duplication, and single instance
// ---------------------------------------------------------------------------------------------

void RunFeatureTests()
{
    Run("search: every word must match some field, case-insensitively", () =>
    {
        var profile = new ConnectionProfile
        {
            Name = "web-01", Host = "web01.contoso.local", Port = 3390, Username = @"CONTOSO\ops",
            Group = "Production", Notes = "Rack 4"
        };
        Assert(ConnectionSearch.Matches(SearchQuery.Parse(""), profile));
        Assert(ConnectionSearch.Matches(SearchQuery.Parse("  WEB   prod "), profile));
        Assert(ConnectionSearch.Matches(SearchQuery.Parse(":3390"), profile));
        Assert(ConnectionSearch.Matches(SearchQuery.Parse("ops rack"), profile));
        Assert(!ConnectionSearch.Matches(SearchQuery.Parse("web staging"), profile));
    });

    Run("search: title prefix outranks word start, inner match, and other fields", () =>
    {
        var query = SearchQuery.Parse("sql");
        var prefix = query.Score("sql-01");
        var wordStart = query.Score("prod-sql");
        var inner = query.Score("mysqlhost");
        var secondary = query.Score("database", "sql.contoso.local");
        Assert(prefix > wordStart && wordStart > inner && inner > secondary && secondary > 0);
        Assert(query.Score("web", "app") == 0);
        Assert(SearchQuery.Parse("db sql").Score("db-sql") > SearchQuery.Parse("db sql").Score("db", "sql"));
    });

    Run("palette: empty text lists suggestions; '>' limits to commands; results are ranked", () =>
    {
        var executed = new List<string>();
        PaletteItem Item(PaletteItemKind kind, string title, bool suggested, params string[] fields) =>
            new(kind, title, null, "", "", null, () => executed.Add(title), fields, suggested);
        var items = new List<PaletteItem>
        {
            Item(PaletteItemKind.Session, "Jump host", true),
            Item(PaletteItemKind.Connection, "sql-prod", false, "10.0.0.5"),
            Item(PaletteItemKind.Connection, "web-sql", true),
            Item(PaletteItemKind.Command, "New connection…", true, "Connections"),
            Item(PaletteItemKind.Command, "Show favourite connections", true, "View")
        };
        var suggested = CommandPaletteSearch.Filter("", items);
        Assert(suggested.Count == 4 && suggested.All(item => item.IsSuggested));
        var commands = CommandPaletteSearch.Filter(">", items);
        Assert(commands.Count == 2 && commands.All(item => item.Kind == PaletteItemKind.Command));
        Assert(CommandPaletteSearch.Filter("> fav", items).Single().Title == "Show favourite connections");
        var ranked = CommandPaletteSearch.Filter("sql", items);
        Assert(ranked.Count == 2 && ranked[0].Title == "sql-prod" && ranked[1].Title == "web-sql");
        Assert(CommandPaletteSearch.Filter("10.0.0", items).Single().Title == "sql-prod");
        Assert(CommandPaletteSearch.Filter("connection", items)[0].Kind == PaletteItemKind.Command);
        Assert(CommandPaletteSearch.Filter("nothing-matches", items).Count == 0);
        var many = Enumerable.Range(0, 500).Select(index => Item(PaletteItemKind.Connection, $"host-{index}", false)).ToList();
        Assert(CommandPaletteSearch.Filter("host", many).Count == CommandPaletteSearch.MaxResults);
        Assert(Item(PaletteItemKind.Command, "x", true) with { Shortcut = "Ctrl+Shift+P" } is { ShortcutKeys.Count: 3 });
    });

    Run("shortcuts: display text, number pad digits, and modifier matching", () =>
    {
        Assert(new Shortcut(System.Windows.Input.Key.P, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift).ToString() == "Ctrl+Shift+P");
        Assert(new Shortcut(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control).ToString() == "Ctrl+1");
        Assert(new Shortcut(System.Windows.Input.Key.Delete).ToString() == "Del");
        Assert(new Shortcut(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control).ToString() == "Ctrl+Enter");
        var ctrl1 = new Shortcut(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control);
        Assert(ctrl1.Matches(System.Windows.Input.Key.NumPad1, System.Windows.Input.ModifierKeys.Control));
        Assert(!ctrl1.Matches(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
    });

    Run("shortcuts: registry rejects conflicts and honours text input and availability", () =>
    {
        var registry = new CommandRegistry();
        var removed = 0;
        var canRemove = true;
        registry.Add(new AppCommand
        {
            Id = "remove", Title = "Remove", Category = "Test", Shortcut = new(System.Windows.Input.Key.Delete),
            AllowInTextInput = false, CanExecute = () => canRemove, Execute = () => removed++
        });
        registry.Add(new AppCommand { Id = "help", Title = "Help", Category = "Test", Shortcut = new(System.Windows.Input.Key.F1) });
        AssertThrows<ArgumentException>(() => registry.Add(new AppCommand { Id = "help", Title = "Again", Category = "Test" }));
        AssertThrows<ArgumentException>(() => registry.Add(new AppCommand
        {
            Id = "other", Title = "Other", Category = "Test", Shortcut = new(System.Windows.Input.Key.F1)
        }));
        Assert(registry.FindByShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None, inTextInput: true) is null);
        registry.FindByShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None, inTextInput: false)!.Execute();
        Assert(removed == 1);
        canRemove = false;
        Assert(registry.FindByShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None, inTextInput: false) is null);
        Assert(registry.FindByShortcut(System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.Control, inTextInput: false) is null);
    });

    Run("failures: documented disconnect codes map to actionable categories", () =>
    {
        Assert(RdpFailureClassifier.Classify(260, 0) == RdpFailureCategory.AddressNotFound);
        Assert(RdpFailureClassifier.Classify(516, 0) == RdpFailureCategory.Unreachable);
        Assert(RdpFailureClassifier.Classify(264, 0) == RdpFailureCategory.TimedOut);
        Assert(RdpFailureClassifier.Classify(2308, 0) == RdpFailureCategory.NetworkInterrupted);
        Assert(RdpFailureClassifier.Classify(2055, 0) == RdpFailureCategory.SignInFailed);
        Assert(RdpFailureClassifier.Classify(6919, 0) == RdpFailureCategory.SecurityCheckFailed);
        Assert(RdpFailureClassifier.Classify(2056, 0) == RdpFailureCategory.Licensing);
        Assert(RdpFailureClassifier.Classify(3080, 0) == RdpFailureCategory.ClientError);
        Assert(RdpFailureClassifier.Classify(99999, 0) == RdpFailureCategory.Unknown);
        // The extended reason is more specific than the base reason.
        Assert(RdpFailureClassifier.Classify(3, 9) == RdpFailureCategory.AccessDenied);
        Assert(RdpFailureClassifier.Classify(2055, 260) == RdpFailureCategory.Licensing);
        Assert(RdpFailureClassifier.Classify(0, 768) == RdpFailureCategory.SignInFailed);
        var unreachable = RdpFailureClassifier.Describe(RdpFailureCategory.Unreachable, RdpEndReason.ConnectFailed, 3390);
        Assert(unreachable.Hint!.Contains("3390"));
        Assert(RdpFailureClassifier.Describe(RdpFailureCategory.NetworkInterrupted, RdpEndReason.ConnectionLost, 3389).Title == "Connection lost");
        Assert(RdpFailureClassifier.Describe(RdpFailureCategory.Unknown, RdpEndReason.ConnectFailed, 3389) is { Title: "Couldn't connect", Hint: null });
    });

    Run("failures: the session snapshot carries the category and native codes", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(260);
        Assert(controller.Snapshot.Failure == new RdpFailureInfo(RdpFailureCategory.AddressNotFound, 260, 0));

        (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        client.ExtendedReason = 7;
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(2308);
        Assert(controller.Snapshot is { EndReason: RdpEndReason.ConnectionLost, Failure.Category: RdpFailureCategory.AccessDenied, Failure.ExtendedReason: 7 });

        controller.Reconnect();
        Assert(controller.Snapshot.Failure is null);
        controller.OnNativeConnecting();
        controller.OnNativeFatalError(42);
        Assert(controller.Snapshot.Failure == new RdpFailureInfo(RdpFailureCategory.ClientError, ErrorCode: 42));
    });

    Run("diagnostics: names are redacted as whole words; codes and versions are kept", () =>
    {
        var identity = new SessionIdentity(Guid.NewGuid(), Guid.NewGuid(), "Payroll DB", "db", 3390, @"CORP\jsmith", "Finance");
        var redacted = RdpDiagnosticReport.Redact(
            "Can't reach db:3390 (db) for CORP\\jsmith on Payroll DB; feedback from jsmith@corp.", identity);
        Assert(redacted == "Can't reach <host> (<host>) for <user> on <connection>; feedback from <user>@<user>.");

        var host = new SessionIdentity(Guid.NewGuid(), Guid.NewGuid(), "host", "host.contoso.com", 3389, "host", "G");
        Assert(RdpDiagnosticReport.Redact("host.contoso.com and host", host) == "<host> and <connection>");

        var local = new SessionIdentity(Guid.NewGuid(), Guid.NewGuid(), "Lab", "10.0.0.9", 3389, @".\admin", "G");
        Assert(RdpDiagnosticReport.Redact("Error (code 5). Close the tab... admin and .\\admin left", local) ==
               "Error (code 5). Close the tab... <user> and <user> left");

        var snapshot = new RdpSessionSnapshot(3, RdpSessionState.Failed, RdpEndReason.ConnectFailed,
            "Couldn't connect: Remote Desktop can't find the computer \"db\".",
            "Remote Desktop can't find the computer \"db\". Check the name.", identity,
            new RdpFailureInfo(RdpFailureCategory.AddressNotFound, 260, 0));
        var report = RdpDiagnosticReport.Build(snapshot, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "1.0.0", "Windows 11");
        Assert(report.Contains("Time: 2026-01-02 03:04:05 UTC") && report.Contains("RDCM version: 1.0.0"));
        Assert(report.Contains("Category: AddressNotFound") && report.Contains("Disconnect reason: 260 (0x104)"));
        Assert(report.Contains("Port: custom (number omitted)") && !report.Contains("3390") && report.Contains("User name provided: Yes"));
        Assert(report.Contains("computer \"<host>\"") && !report.Contains("\"db\"") && !report.Contains("jsmith") && !report.Contains("Payroll"));
        var defaultPort = RdpDiagnosticReport.Build(snapshot with { Identity = local }, DateTimeOffset.UnixEpoch, "1.0.0", "Windows 11");
        Assert(defaultPort.Contains("Port: default") && !defaultPort.Contains("3389"));
    });

    Run("server authentication: a policy overrides the requested level; weaker checks are flagged", () =>
    {
        var warn = ServerAuthenticationMode.WarnIfUnverified;
        // Observed on the test workstation: policy 0 ("Always connect"), control rejects the write and reads back 0.
        var policyOff = ServerAuthentication.Evaluate(warn, controlReadBack: 0, machinePolicy: 0);
        Assert(policyOff is { Effective: ServerAuthenticationMode.Off, Source: ServerAuthenticationSource.MachinePolicy, IsWeakened: true });
        Assert(policyOff.Describe().Contains("policy"));
        Assert(ServerAuthentication.Evaluate(warn, 0, machinePolicy: 1) is { Effective: ServerAuthenticationMode.Required, IsWeakened: false });
        Assert(ServerAuthentication.Evaluate(warn, 0, machinePolicy: 2) is { Effective: ServerAuthenticationMode.WarnIfUnverified, IsWeakened: false });
        Assert(ServerAuthentication.Evaluate(warn, 2, machinePolicy: null) is { Source: ServerAuthenticationSource.Application, IsWeakened: false });
        Assert(ServerAuthentication.Evaluate(warn, 0, machinePolicy: null) is { Source: ServerAuthenticationSource.Unexplained, IsWeakened: true });
        Assert(ServerAuthentication.Evaluate(warn, null, machinePolicy: null) is { Effective: null, IsWeakened: true });
        // An out-of-range policy value isn't a documented setting, so the control's read-back decides.
        Assert(ServerAuthentication.Evaluate(warn, 2, machinePolicy: 7) is { Source: ServerAuthenticationSource.Application, IsWeakened: false });
    });

    Run("server authentication: each attempt publishes what applies, and diagnostics include it", () =>
    {
        var (controller, client, _) = Lifecycle();
        client.ServerAuthentication = ServerAuthentication.Evaluate(ServerAuthenticationMode.WarnIfUnverified, 0, 0);
        controller.Start();
        Assert(controller.Snapshot.ServerAuthentication is { IsWeakened: true, Source: ServerAuthenticationSource.MachinePolicy });
        var report = RdpDiagnosticReport.Build(controller.Snapshot, DateTimeOffset.UnixEpoch, "1.0.0", "Windows");
        Assert(report.Contains("Server authentication: Off: a policy on this PC"));

        controller.OnNativeConnecting();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(260);
        client.ServerAuthentication = ServerAuthentication.Evaluate(ServerAuthenticationMode.WarnIfUnverified, 2, null);
        controller.Reconnect();
        Assert(controller.Snapshot.ServerAuthentication is { IsWeakened: false, Source: ServerAuthenticationSource.Application });
    });

    Run("key routing: keys from the session host are left to the remote session", () =>
    {
        var results = new List<bool>();
        var thread = new Thread(() =>
        {
            var host = new System.Windows.Forms.Integration.WindowsFormsHost();
            var panel = new System.Windows.Controls.Grid();
            panel.Children.Add(host);
            results.Add(SessionKeyRouting.IsFromSessionHost(host));
            results.Add(SessionKeyRouting.IsFromSessionHost(panel));
            results.Add(SessionKeyRouting.IsFromSessionHost(new System.Windows.Controls.TextBox()));
            results.Add(SessionKeyRouting.IsFromSessionHost(null));
            host.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert(results.SequenceEqual([true, false, false, false]));
    });

    Run("credentials: persisted profiles and settings have no password or credential fields", () =>
    {
        static IEnumerable<string> Names(System.Text.Json.JsonElement element) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().SelectMany(Names),
            _ => []
        };
        var settings = new AppSettings
        {
            FavoriteProfileIds = [Guid.NewGuid()],
            RecentConnections = [new RecentConnection { ProfileId = Guid.NewGuid(), LastConnected = DateTimeOffset.Now }],
            MainWindow = new WindowPlacementSettings { Left = 1, Top = 1, Width = 900, Height = 600 }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new object[] { Profile("Persisted"), settings });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var names = Names(document.RootElement).ToList();
        Assert(names.Count > 10 && !names.Any(name =>
            name.Contains("pass", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase)));
    });

    Run("duplicate: copy names are distinct and case-insensitive", () =>
    {
        var existing = new[] { Profile("Web"), Profile("web (COPY)"), Profile("Web (copy 2)") };
        Assert(ProfileValidation.CopyName("Web", existing) == "Web (copy 3)");
        Assert(ProfileValidation.CopyName("  SQL ", existing) == "SQL (copy)");
    });

    Run("single instance: one owner per data folder; the name ignores case and trailing separators", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "rdcm-instance-" + Guid.NewGuid().ToString("N"));
        var name = SingleInstanceGuard.NameFor(folder);
        Assert(name == SingleInstanceGuard.NameFor(folder.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert(name != SingleInstanceGuard.NameFor(folder + "-other") && name.StartsWith(@"Local\RDCM.", StringComparison.Ordinal));
        using (var first = new SingleInstanceGuard(name))
        {
            Assert(first.IsFirstInstance);
            var second = new Thread(() =>
            {
                using var other = new SingleInstanceGuard(name);
                Assert(!other.IsFirstInstance);
            });
            second.Start();
            second.Join();
        }
        using var afterExit = new SingleInstanceGuard(name);
        Assert(afterExit.IsFirstInstance);
    });

    Run("single instance: a mutex this user can't open (elevated owner) means another instance", () =>
    {
        var name = SingleInstanceGuard.NameFor(Path.Combine(Path.GetTempPath(), "rdcm-acl-" + Guid.NewGuid().ToString("N")));
        var security = new System.Security.AccessControl.MutexSecurity();
        security.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.MutexRights.FullControl,
            System.Security.AccessControl.AccessControlType.Deny));
        using var restricted = System.Threading.MutexAcl.Create(false, name, out var created, security);
        Assert(created);
        var guard = new SingleInstanceGuard(name);
        Assert(!guard.IsFirstInstance);
        guard.Dispose();
    });
}

// ---------------------------------------------------------------------------------------------
// Connection lifecycle (fake native client, manual clock)
// ---------------------------------------------------------------------------------------------

void RunLifecycleTests()
{
    Run("lifecycle: native events drive connect", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        Assert(controller.State == RdpSessionState.Connecting && client.ConnectCalls == 1);
        controller.OnNativeConnecting();
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        Assert(controller.State == RdpSessionState.Connected && controller.Snapshot.EndReason == RdpEndReason.None);
    });

    Run("lifecycle: disconnect while connecting is a cancellation, not a failure", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        Assert(controller.Disconnect());
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 1);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.State == RdpSessionState.Disconnected);
        Assert(controller.Snapshot.EndReason == RdpEndReason.ConnectionCancelled);
    });

    Run("lifecycle: disconnect after the native attempt already ended never calls Disconnect", () =>
    {
        // The original bug: the app still believed it was connecting, called Disconnect on an idle
        // control, received E_FAIL, and showed "Connection problem".
        var (controller, client, _) = Lifecycle(events: false);
        controller.Start();
        client.State = RdpConnectionController.NativeNotConnected;
        client.ThrowOnDisconnect = true;
        Assert(controller.Disconnect());
        Assert(client.DisconnectCalls == 0);
        Assert(controller.State == RdpSessionState.Disconnected);
        Assert(controller.Snapshot.EndReason == RdpEndReason.ConnectionCancelled);
    });

    Run("lifecycle: a rejected disconnect request is retried, not reported as a failure", () =>
    {
        var (controller, client, clock) = Lifecycle(events: false);
        controller.Start();
        client.ThrowOnDisconnect = true;
        controller.Disconnect();
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 1);
        clock.Advance(RdpConnectionController.DisconnectRetryMs);
        controller.Poll();
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 2);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.Poll();
        Assert(controller.State == RdpSessionState.Disconnected);
        Assert(controller.Snapshot.EndReason == RdpEndReason.ConnectionCancelled);
    });

    Run("lifecycle: a connection that completes after cancel is torn down again", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.Disconnect();
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 2);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.State == RdpSessionState.Disconnected);
    });

    Run("lifecycle: duplicate disconnect requests are ignored", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        Assert(controller.Disconnect());
        Assert(!controller.Disconnect());
        Assert(client.DisconnectCalls == 1);
    });

    Run("lifecycle: cancelling a new attempt right after a previous cancel is not delayed", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.Disconnect();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        controller.Reconnect();
        controller.OnNativeConnecting();
        Assert(controller.Disconnect() && client.DisconnectCalls == 2);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.State == RdpSessionState.Disconnected &&
               controller.Snapshot.EndReason == RdpEndReason.ConnectionCancelled);
    });

    Run("lifecycle: genuine connect failure reports the native description", () =>
    {
        var (controller, _, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeDisconnected(260);
        Assert(controller.State == RdpSessionState.Failed);
        Assert(controller.Snapshot.EndReason == RdpEndReason.ConnectFailed);
        Assert(controller.Snapshot.Message == "Couldn't connect: Remote Desktop can't find the computer \"x\".");
        Assert(controller.Snapshot.Detail!.Contains("More text."));
    });

    Run("lifecycle: reconnect waits for the old connection and uses the new identity", () =>
    {
        var (controller, client, _) = Connected();
        var updated = SessionIdentity.Capture(Profile("Updated")) with { ProfileId = controller.Identity.ProfileId };
        controller.Reconnect(updated);
        Assert(controller.State == RdpSessionState.Reconnecting && client.ConnectCalls == 1 && client.DisconnectCalls == 1);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.State == RdpSessionState.Reconnecting && controller.HasPendingWork);
        controller.Poll();
        Assert(controller.State == RdpSessionState.Connecting && client.ConnectCalls == 2);
        Assert(client.LastIdentity!.Name == "Updated");
        Assert(controller.Identity.SessionId != updated.SessionId);
    });

    Run("lifecycle: a stale disconnect event from the previous connection is ignored", () =>
    {
        var (controller, client, _) = Connected();
        controller.Reconnect();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        controller.Poll();
        Assert(controller.State == RdpSessionState.Connecting && client.ConnectCalls == 2);
        controller.OnNativeDisconnected(1);
        Assert(controller.State == RdpSessionState.Connecting);
        controller.OnNativeConnecting();
        controller.OnNativeDisconnected(516);
        Assert(controller.State == RdpSessionState.Failed);
    });

    Run("lifecycle: remote sign-out ends the session without an error", () =>
    {
        var (controller, client, _) = Connected();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(3);
        Assert(controller.State == RdpSessionState.Disconnected);
        Assert(controller.Snapshot.EndReason == RdpEndReason.RemoteEnded);
    });

    Run("lifecycle: a network drop while connected is a lost connection", () =>
    {
        var (controller, client, _) = Connected();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(772);
        Assert(controller.State == RdpSessionState.Failed);
        Assert(controller.Snapshot.EndReason == RdpEndReason.ConnectionLost);
    });

    Run("lifecycle: polling fallback detects connect and timeouts", () =>
    {
        var (controller, client, clock) = Lifecycle(events: false);
        controller.Start();
        client.State = RdpConnectionController.NativeConnected;
        controller.Poll();
        Assert(controller.State == RdpSessionState.Connected);

        (controller, client, clock) = Lifecycle(events: false);
        controller.Start();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.Poll();
        clock.Advance(RdpConnectionController.StartTimeoutMs);
        controller.Poll();
        Assert(controller.State == RdpSessionState.Failed && controller.Snapshot.EndReason == RdpEndReason.ConnectFailed);
    });

    Run("lifecycle: cancel before the control is ready never starts a connection", () =>
    {
        var (controller, client, _) = Lifecycle();
        Assert(controller.Disconnect());
        controller.Start();
        Assert(controller.State == RdpSessionState.Disconnected && client.ConnectCalls == 0);
    });

    Run("lifecycle: a fatal control error before start is reported, not thrown", () =>
    {
        var (controller, _, _) = Lifecycle();
        controller.OnNativeFatalError(7);
        Assert(controller.State == RdpSessionState.Failed && controller.Snapshot.EndReason == RdpEndReason.ControlError);
    });

    Run("lifecycle: shutdown disconnects and ignores later events", () =>
    {
        var (controller, client, _) = Connected();
        var published = 0;
        controller.Changed += _ => published++;
        controller.Shutdown();
        Assert(controller.State == RdpSessionState.Disposed && client.DisconnectCalls == 1);
        controller.OnNativeDisconnected(1);
        controller.OnNativeConnected();
        controller.Poll();
        Assert(controller.State == RdpSessionState.Disposed && published == 0);
    });

    Run("lifecycle: snapshots are published in increasing order", () =>
    {
        var (controller, client, _) = Lifecycle();
        var sequences = new List<long>();
        controller.Changed += snapshot => sequences.Add(snapshot.Sequence);
        controller.Start();
        controller.OnNativeConnecting();
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        controller.Disconnect();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(sequences.Count >= 4 && sequences.SequenceEqual(sequences.Order()) && sequences.Distinct().Count() == sequences.Count);
        Assert(controller.Snapshot.EndReason == RdpEndReason.UserDisconnected);
    });

    // Sequences observed live against a real server (control 10.0.26100.8875): Connect() returns before OnConnecting;
    // OnAuthenticationWarningDisplayed/Dismissed bracket the Windows Security sign-in prompt; OnConnected follows sign-in.
    Run("lifecycle (observed live): sign-in prompt, then connected; the prompt state is published", () =>
    {
        var (controller, client, clock) = Lifecycle();
        controller.Start();
        Assert(client.ConnectCalls == 1 && controller.State == RdpSessionState.Connecting);
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        Assert(controller.Snapshot.NativeDialogOpen && controller.NativeDialogOpen);
        // The user can take a long time to sign in; nothing times out while the prompt is open.
        client.State = RdpConnectionController.NativeNotConnected;
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(10_000);
            controller.Poll();
        }
        Assert(controller.State == RdpSessionState.Connecting);
        controller.OnNativeDialogClosed();
        Assert(!controller.Snapshot.NativeDialogOpen);
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        Assert(controller.State == RdpSessionState.Connected && controller.Snapshot.EndReason == RdpEndReason.None);
    });

    Run("lifecycle (observed live): cancel while the sign-in prompt is open waits for the prompt, then reports cancelled", () =>
    {
        var (controller, client, clock) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        Assert(controller.Disconnect());
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 1);
        // Live: Disconnect() returns at once but the prompt stays open (it closed by itself after about 11 s).
        for (var i = 0; i < 6; i++)
        {
            clock.Advance(RdpConnectionController.DisconnectTimeoutMs / 2);
            controller.Poll();
        }
        Assert(controller.State == RdpSessionState.Disconnecting && client.DisconnectCalls == 1);
        controller.OnNativeDialogClosed();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.ConnectionCancelled, NativeDialogOpen: false });
        // A late Connected from the cancelled attempt is torn down and doesn't change the result.
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        Assert(controller.State == RdpSessionState.Disconnected && client.DisconnectCalls == 2);
    });

    // Observed live (guided run 1): the operator's Cancel in the Windows Security window ended the attempt with reason
    // 0x1F07 (7943), which the control has no description for. Reason 1 comes only from RDCM's own disconnect.
    Run("lifecycle (observed live): Cancel in the Windows Security window (0x1F07) is a cancellation, not a failure", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        controller.OnNativeDialogClosed();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(7943);
        Assert(controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.ConnectionCancelled, Failure: null });
        Assert(controller.Snapshot.Message == "Connection cancelled" && !controller.HasActiveSession);
        // In an attempt without a sign-in window the same code is an error that keeps its number.
        Assert(controller.Reconnect() && client.ConnectCalls == 2);
        controller.OnNativeConnecting();
        controller.OnNativeDisconnected(7943);
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ConnectFailed, Failure.Reason: 7943 });
    });

    Run("lifecycle: after the sign-in prompt closes, the teardown timeout applies again", () =>
    {
        var (controller, client, clock) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        controller.Disconnect();
        clock.Advance(RdpConnectionController.DisconnectTimeoutMs * 3);
        controller.Poll();
        controller.OnNativeDialogClosed();
        controller.Poll();
        Assert(controller.State == RdpSessionState.Disconnecting);
        clock.Advance(RdpConnectionController.DisconnectTimeoutMs);
        controller.Poll();
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ControlError });
    });

    Run("lifecycle (observed live): user disconnect and reconnect from the same tab", () =>
    {
        var (controller, client, _) = Connected();
        controller.Disconnect();
        Assert(controller.State == RdpSessionState.Disconnecting);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected });
        Assert(controller.Reconnect());
        Assert(controller.State == RdpSessionState.Connecting && client.ConnectCalls == 2);
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        controller.OnNativeDialogClosed();
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeConnected();
        Assert(controller.State == RdpSessionState.Connected);
    });

    Run("lifecycle: prompt events after shutdown are ignored", () =>
    {
        var (controller, _, _) = Lifecycle();
        controller.Start();
        controller.Shutdown();
        controller.OnNativeDialogShown();
        Assert(!controller.NativeDialogOpen && controller.State == RdpSessionState.Disposed);
    });

    Run("lifecycle: a lost prompt-closed event can't leave the session waiting forever", () =>
    {
        // The attempt ending means its sign-in window is gone (live, the prompt-closed event always came first).
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeDialogShown();
        controller.Disconnect();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(1);
        Assert(controller.Snapshot is { State: RdpSessionState.Disconnected, NativeDialogOpen: false } && !controller.NativeDialogOpen);
    });

    // Auto-reconnect: event semantics from IMsTscAxEvents::OnAutoReconnecting2 / OnAutoReconnected (fake control).
    Run("auto-reconnect: progress is shown, the watchdog waits, and success restores the session", () =>
    {
        var (controller, client, clock) = Connected();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeAutoReconnecting(2308, networkAvailable: true, attempt: 1, maxAttempts: 5);
        Assert(controller.Snapshot is { State: RdpSessionState.AutoReconnecting, AutoReconnect: { Attempt: 1, MaxAttempts: 5 } });
        Assert(controller.Snapshot.Message.Contains("attempt 1 of 5") && controller.HasActiveSession);
        // A "not connected" reading is not evidence that the session ended while the control reconnects.
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(RdpConnectionController.EventGraceMs);
            controller.Poll();
        }
        Assert(controller.State == RdpSessionState.AutoReconnecting && client.DisconnectCalls == 0);
        controller.OnNativeAutoReconnecting(2308, true, 2, 5);
        Assert(controller.Snapshot.AutoReconnect is { Attempt: 2 });
        client.State = RdpConnectionController.NativeConnected;
        controller.OnNativeAutoReconnected();
        Assert(controller.Snapshot is { State: RdpSessionState.Connected, AutoReconnect: null, EndReason: RdpEndReason.None });
        Assert(client.ConnectCalls == 1);
    });

    Run("auto-reconnect: when the control gives up, the session is lost with the attempt count", () =>
    {
        var (controller, client, _) = Connected();
        controller.OnNativeAutoReconnecting(2308, true, 5, 5);
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(264);
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ConnectionLost, AutoReconnect: null });
        Assert(controller.Snapshot.Detail!.Contains("tried to reconnect 5 times") && controller.Snapshot.Failure!.Category == RdpFailureCategory.TimedOut);

        // Observed live: the last notification before giving up is numbered one past the maximum (6 of 5).
        (controller, client, _) = Connected();
        controller.OnNativeAutoReconnecting(516, true, 5, 5);
        controller.OnNativeAutoReconnecting(516, true, 6, 5);
        Assert(controller.Snapshot.AutoReconnect is { Attempt: 5, MaxAttempts: 5 });
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeDisconnected(516);
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ConnectionLost } &&
               controller.Snapshot.Detail!.Contains("tried to reconnect 5 times"));
    });

    Run("auto-reconnect: the event sink writes 'stop' to both possible positions of the control's answer", () =>
    {
        var keep = true;
        var asked = new List<int>();
        var sink = new RdpClientEventSink(_ => { }, attempt => { asked.Add(attempt); return keep; });
        // Live layout: the [out] status (0 = continue automatically) comes first, the disconnect reason last.
        int first = 0, last = 2308;
        sink.OnAutoReconnecting(ref first, 1, ref last);
        Assert(first == 0 && last == 2308);
        keep = false;
        sink.OnAutoReconnecting(ref first, 2, ref last);
        Assert(first == RdpClientEventSink.AutoReconnectContinueStop && last == RdpClientEventSink.AutoReconnectContinueStop);
        Assert(asked.SequenceEqual([1, 2]));
    });

    Run("auto-reconnect: events outside a connected session aren't shown as progress; odd progress values aren't shown", () =>
    {
        var (controller, client, _) = Lifecycle();
        controller.Start();
        controller.OnNativeConnecting();
        controller.OnNativeAutoReconnecting(2308, true, 1, 5);
        Assert(controller.State == RdpSessionState.Connecting);
        controller.Disconnect();
        controller.OnNativeAutoReconnecting(2308, true, 1, 5);
        Assert(controller.State == RdpSessionState.Disconnecting);

        (controller, _, _) = Connected();
        controller.OnNativeAutoReconnecting(2308, true, attempt: 7, maxAttempts: 5);
        Assert(controller.Snapshot.AutoReconnect is { Attempt: null, MaxAttempts: null } && controller.Snapshot.Message.Contains("attempting to reconnect"));
        Assert(SessionIndicator.For(RdpSessionState.AutoReconnecting) == SessionIndicator.Busy);
    });

    Run("auto-reconnect: a silent control is resolved by the safety net and replaced, not left reconnecting", () =>
    {
        var (controller, client, clock) = Connected();
        client.State = RdpConnectionController.NativeNotConnected;
        controller.OnNativeAutoReconnecting(2308, false, 1, 5);
        clock.Advance(RdpConnectionController.AutoReconnectSilenceTimeoutMs - 1);
        controller.Poll();
        Assert(controller.State == RdpSessionState.AutoReconnecting && client.ReplaceCalls == 0);
        clock.Advance(1);
        controller.Poll();
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ConnectionLost });
        Assert(client.ReplaceCalls == 1 && client.DisconnectCalls == 0);

        // Live, the Connected property reads 1 throughout auto-reconnect, so it isn't evidence of a restored session.
        (controller, client, clock) = Connected();
        controller.OnNativeAutoReconnecting(2308, true, 1, 5);
        clock.Advance(RdpConnectionController.AutoReconnectSilenceTimeoutMs);
        controller.Poll();
        Assert(controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ConnectionLost } && client.ReplaceCalls == 1);
    });

    // Hard disconnect. The installed control ignores Disconnect() and the "stop" answer while it auto-reconnects
    // (observed live), and releasing it (as closing a tab does) ends that at once. These checks run the controller
    // with the real RdpControlSlot and fake control generations, routed as EmbeddedRdpControl routes native events.
    Run("hard disconnect: Disconnect during auto-reconnect replaces the control promptly and ends Disconnected", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        var identity = host.Controller.Identity;
        BeginAutoReconnect(host);
        host.Journal.Clear();
        var published = host.Published.Count;
        Assert(host.Controller.Disconnect());
        // Accepted at once; the release runs as separate work, after the UI has shown "Disconnecting…".
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnecting, Message: "Disconnecting…", Detail: null });
        Assert(host.Created.Count == 1 && !first.Disposed && host.Controller.HasPendingWork && first.DisconnectRequests == 0);
        host.RunPendingWork();
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected, Failure: null });
        // Released in the live-verified order, exactly once, and replaced by exactly one fresh, unconnected control.
        Assert(host.Journal.SequenceEqual(["disconnect-request 1", "detach 1", "dispose 1", "create 2", "initialize 2"]));
        Assert(first.DisposeCalls == 1 && first.DetachCalls == 1 && !first.EventsAttached);
        Assert(host.Created.Count == 2 && host.Current.Generation == 2 && host.Current.EventsAttached && host.Current.ConnectCalls == 0);
        Assert(host.Controller.Identity == identity && !host.Controller.HasPendingWork && !host.Controller.HasActiveSession);
        // Never reported as connected on the way (so no Recent entry), and nothing reconnects without the user.
        Assert(host.Published.Skip(published).Select(snapshot => snapshot.State)
            .SequenceEqual([RdpSessionState.Disconnecting, RdpSessionState.Disconnected]));
        host.StartCurrent();
        host.RunPendingWork();
        host.Controller.Poll();
        Assert(host.Current.ConnectCalls == 0 && host.Created.Count == 2 && host.Controller.State == RdpSessionState.Disconnected);
    });

    Run("hard disconnect: nothing from the released control can change the session, before or after the replacement", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        host.Controller.Disconnect();
        // Accepted but not yet replaced: the old control is still current, yet nothing it raises is accepted. In
        // particular the session must not say Disconnected before the old control is actually released.
        host.Raise(first, controller => controller.OnNativeAutoReconnected());
        host.Raise(first, controller => controller.OnNativeConnected());
        host.Raise(first, controller => controller.OnNativeAutoReconnecting(516, true, 2, 5));
        host.Raise(first, controller => controller.OnNativeDisconnected(516));
        Assert(!host.AskContinue(first, 2) && host.Controller.State == RdpSessionState.Disconnecting);
        Assert(!first.Disposed && first.DisconnectRequests == 0 && host.Controller.HasPendingWork);
        host.RunPendingWork();
        var published = host.Published.Count;
        var stale = host.Slot.StaleCallbacks;
        Assert(!host.Raise(first, controller => controller.OnNativeAutoReconnected()));
        Assert(!host.Raise(first, controller => controller.OnNativeConnected()));
        Assert(!host.Raise(first, controller => controller.OnNativeConnecting()));
        Assert(!host.Raise(first, controller => controller.OnNativeDisconnected(516)));
        Assert(!host.Raise(first, controller => controller.OnNativeDialogShown()));
        Assert(!host.Raise(first, controller => controller.OnNativeDialogClosed()));
        Assert(!host.Raise(first, controller => controller.OnNativeFatalError(4)));
        Assert(!host.Raise(first, controller => controller.OnNativeAutoReconnecting(516, true, 3, 5)));
        Assert(!host.AskContinue(first, 3));
        Assert(host.Slot.StaleCallbacks == stale + 8 && host.Published.Count == published);
        // A watchdog poll after the replacement looks only at the fresh control.
        first.State = RdpConnectionController.NativeConnected;
        host.StartCurrent();
        host.Controller.Poll();
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected, NativeDialogOpen: false });
        Assert(host.Current.DisconnectRequests == 0 && host.Created.Count == 2);
    });

    Run("hard disconnect: a later Reconnect uses only the fresh control and re-evaluates its server authentication", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        var weakened = ServerAuthentication.Evaluate(ServerAuthenticationMode.WarnIfUnverified, 0, 0);
        var verified = ServerAuthentication.Evaluate(ServerAuthenticationMode.WarnIfUnverified, 2, null);
        host.AuthenticationFor = generation => generation == 1 ? weakened : verified;
        BeginAutoReconnect(host);
        host.Controller.Disconnect();
        host.RunPendingWork();
        host.StartCurrent();
        var second = host.Current;
        Assert(host.Controller.Reconnect());
        Assert(host.Controller.State == RdpSessionState.Connecting && second.ConnectCalls == 1 && first.ConnectCalls == 1);
        Assert(host.Controller.Snapshot.ServerAuthentication == verified && !verified.IsWeakened);
        host.Raise(second, controller => controller.OnNativeConnecting());
        second.State = RdpConnectionController.NativeConnected;
        host.Raise(second, controller => controller.OnNativeConnected());
        Assert(host.Controller.State == RdpSessionState.Connected);
        // A late failure report from the released control can't fail the new connection.
        Assert(!host.Raise(first, controller => controller.OnNativeDisconnected(516)) && host.Controller.State == RdpSessionState.Connected);
    });

    Run("hard disconnect: Reconnect during auto-reconnect replaces the control, then connects once the fresh one starts", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        Assert(host.Controller.Reconnect());
        Assert(host.Controller.State == RdpSessionState.Reconnecting && first.ConnectCalls == 1 && first.DisconnectRequests == 0);
        host.RunPendingWork();
        var second = host.Current;
        // The fresh control isn't connected from inside the replacement; it is once the host reports it started.
        Assert(first.Disposed && host.Controller.State == RdpSessionState.Connecting && second.ConnectCalls == 0);
        host.StartCurrent();
        Assert(second.ConnectCalls == 1 && first.ConnectCalls == 1 && host.Created.Count == 2);
    });

    Run("hard disconnect: repeated Disconnect, Close tab, and application exit during the replacement are safe", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        Assert(host.Controller.Disconnect());
        Assert(!host.Controller.Disconnect());
        host.RunPendingWork();
        host.RunPendingWork();
        Assert(host.Created.Count == 2 && first.DisposeCalls == 1 && host.Controller.State == RdpSessionState.Disconnected);

        // Closing the tab (or exiting, which closes every tab) before the pending replacement runs: the old control is
        // released once by the close itself and no fresh one is created afterwards.
        FakeSessionHost[] tabs = [ConnectedHost(), ConnectedHost()];
        BeginAutoReconnect(tabs[0]);
        var doomed = tabs[0].Current;
        tabs[0].Controller.Disconnect();
        foreach (var tab in tabs)
        {
            tab.CloseTab();
            tab.RunPendingWork();
            tab.Controller.Poll();
            Assert(tab.Controller.State == RdpSessionState.Disposed && tab.Created.Count == 1 && tab.Slot.Current is null);
            Assert(tab.Created[0].DisposeCalls == 1 && tab.Created[0].DetachCalls == 1 && !tab.Controller.HasPendingWork);
        }
        Assert(!tabs[0].Raise(doomed, controller => controller.OnNativeAutoReconnected()) && !tabs[0].AskContinue(doomed, 2));
    });

    Run("hard disconnect: if no fresh control can be created, the session fails truthfully and Try again is bounded", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        host.FailCreateFor = 2;
        host.Controller.Disconnect();
        host.RunPendingWork();
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ControlError } &&
               host.Controller.Snapshot.Message.Contains("couldn't be restarted"));
        Assert(first.DisposeCalls == 1 && host.Slot.Current is null && !host.Controller.HasActiveSession && !host.Controller.HasPendingWork);
        Assert(!host.Raise(first, controller => controller.OnNativeAutoReconnected()) && host.Controller.State == RdpSessionState.Failed);
        host.Controller.Poll();
        Assert(host.Journal.Count(entry => entry.StartsWith("create")) == 2);
        // Try again makes one creation attempt per request.
        host.FailCreateFor = 3;
        Assert(host.Controller.Reconnect());
        Assert(host.Controller.State == RdpSessionState.Failed && host.Journal.Count(entry => entry.StartsWith("create")) == 3);
        host.Controller.Poll();
        Assert(host.Journal.Count(entry => entry.StartsWith("create")) == 3);
        Assert(host.Controller.Reconnect());
        var fresh = host.Current;
        Assert(fresh.Generation == 4 && host.Controller.State == RdpSessionState.Connecting && fresh.ConnectCalls == 0);
        host.StartCurrent();
        Assert(fresh.ConnectCalls == 1);
    });

    Run("hard disconnect: a fresh control that fails to start is released and doesn't revive the old session", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        host.FailInitializeFor = 2;
        host.Controller.Disconnect();
        host.RunPendingWork();
        var failed = host.Created[1];
        Assert(failed.DetachCalls == 1 && failed.DisposeCalls == 1 && !failed.EventsAttached && host.Slot.Current is null);
        Assert(first.Disposed && host.Controller.Snapshot is { State: RdpSessionState.Failed, EndReason: RdpEndReason.ControlError });
        Assert(!host.Raise(first, controller => controller.OnNativeAutoReconnected()));
        Assert(!host.Raise(failed, controller => controller.OnNativeConnected()));
        Assert(host.Controller.State == RdpSessionState.Failed && !host.Controller.HasActiveSession);
    });

    Run("hard disconnect: releasing keeps going when a step fails, and the failed control is never used again", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        BeginAutoReconnect(host);
        first.ThrowOnRelease = true;
        host.Journal.Clear();
        host.Controller.Disconnect();
        host.RunPendingWork();
        Assert(host.Journal.SequenceEqual(["disconnect-request 1", "detach 1", "dispose 1", "create 2", "initialize 2"]));
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected });
        Assert(host.Current.Generation == 2 && !host.Raise(first, controller => controller.OnNativeConnected()));
    });

    Run("hard disconnect: a normal Disconnect of a connected session doesn't replace the control", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        host.Controller.Disconnect();
        Assert(host.Controller.State == RdpSessionState.Disconnecting && first.DisconnectRequests == 1 && !host.Controller.HasPendingWork);
        first.State = RdpConnectionController.NativeNotConnected;
        host.Raise(first, controller => controller.OnNativeDisconnected(1));
        host.Controller.Poll();
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected });
        Assert(host.Created.Count == 1 && !first.Disposed);
    });

    Run("hard disconnect: never while the Windows Security window is open; it waits for the window, then replaces", () =>
    {
        // Cancelling the initial sign-in keeps its behaviour: no replacement at all.
        var host = new FakeSessionHost(SessionIdentity.Capture(Profile("Sign-in")));
        host.StartCurrent();
        var first = host.Current;
        host.Raise(first, controller => controller.OnNativeConnecting());
        host.Raise(first, controller => controller.OnNativeDialogShown());
        host.Controller.Disconnect();
        host.RunPendingWork();
        host.Controller.Poll();
        Assert(host.Controller.State == RdpSessionState.Disconnecting && first.DisconnectRequests == 1 && host.Created.Count == 1);
        host.Raise(first, controller => controller.OnNativeDialogClosed());
        first.State = RdpConnectionController.NativeNotConnected;
        host.Raise(first, controller => controller.OnNativeDisconnected(7943));
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.ConnectionCancelled });
        Assert(host.Created.Count == 1 && !first.Disposed);

        // The control asks for sign-in while reconnecting: Disconnect waits for that window, then replaces the control.
        host = ConnectedHost();
        first = host.Current;
        BeginAutoReconnect(host);
        host.Raise(first, controller => controller.OnNativeDialogShown());
        host.Controller.Disconnect();
        host.RunPendingWork();
        host.Controller.Poll();
        Assert(host.Created.Count == 1 && !first.Disposed && host.Controller.Snapshot is { State: RdpSessionState.Disconnecting, NativeDialogOpen: true });
        host.Raise(first, controller => controller.OnNativeDialogClosed());
        host.Controller.Poll();
        Assert(first.DisposeCalls == 1 && host.Created.Count == 2);
        Assert(host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected, NativeDialogOpen: false });

        // A sign-in window that opens after the replacement was accepted also holds it until the window closes.
        host = ConnectedHost();
        first = host.Current;
        BeginAutoReconnect(host);
        host.Controller.Disconnect();
        host.Raise(first, controller => controller.OnNativeDialogShown());
        Assert(!host.Controller.HasPendingWork && host.Controller.NativeDialogOpen);
        host.Controller.Poll();
        Assert(!first.Disposed);
        host.Raise(first, controller => controller.OnNativeDialogClosed());
        Assert(host.Controller.HasPendingWork);
        host.RunPendingWork();
        Assert(first.Disposed && host.Controller.State == RdpSessionState.Disconnected);
    });

    Run("hard disconnect: a control reconnecting or restoring a session that had already ended is replaced", () =>
    {
        // Disconnect was requested while connected, and the network dropped before the control finished.
        var host = ConnectedHost();
        var first = host.Current;
        host.Controller.Disconnect();
        host.Raise(first, controller => controller.OnNativeAutoReconnecting(2308, true, 1, 5));
        Assert(host.Controller.HasPendingWork && host.Controller.State == RdpSessionState.Disconnecting);
        host.RunPendingWork();
        Assert(first.Disposed && host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected });

        // A restore without attempt events before it (other control versions), after the session had ended.
        host = ConnectedHost();
        first = host.Current;
        host.Controller.Disconnect();
        first.State = RdpConnectionController.NativeNotConnected;
        host.Raise(first, controller => controller.OnNativeDisconnected(1));
        first.State = RdpConnectionController.NativeConnected;
        host.Raise(first, controller => controller.OnNativeAutoReconnected());
        Assert(host.Controller.HasPendingWork && host.Controller.State == RdpSessionState.Disconnected);
        host.RunPendingWork();
        Assert(first.Disposed && host.Created.Count == 2 && host.Controller.Snapshot is { State: RdpSessionState.Disconnected, EndReason: RdpEndReason.UserDisconnected });
    });

    Run("hard disconnect: a reconnect loop found while connecting is replaced, and the attempt restarts on the fresh control", () =>
    {
        // Review finding: the connect had gone to the released control and the fresh one was never asked to connect.
        foreach (var progressFirst in new[] { false, true })
        {
            var host = new FakeSessionHost(SessionIdentity.Capture(Profile("Connecting")));
            host.StartCurrent();
            var first = host.Current;
            host.Raise(first, controller => controller.OnNativeConnecting());
            Assert(host.Controller.State == RdpSessionState.Connecting && first.ConnectCalls == 1);
            if (progressFirst)
            {
                host.Raise(first, controller => controller.OnNativeAutoReconnecting(2308, true, 2, 5));
            }
            Assert(!host.AskContinue(first, 2) && host.Controller.HasPendingWork);
            host.RunPendingWork();
            var second = host.Current;
            Assert(first.Disposed && second.Generation == 2 && host.Controller.State == RdpSessionState.Connecting && second.ConnectCalls == 0);
            host.StartCurrent();
            Assert(second.ConnectCalls == 1 && first.ConnectCalls == 1);
            host.Raise(second, controller => controller.OnNativeConnecting());
            second.State = RdpConnectionController.NativeConnected;
            host.Raise(second, controller => controller.OnNativeConnected());
            Assert(host.Controller.State == RdpSessionState.Connected);
        }

        // Before any attempt there is nothing to replace: the control is only told to stop.
        var idle = new FakeSessionHost(SessionIdentity.Capture(Profile("Created")));
        Assert(!idle.AskContinue(idle.Current, 1) && !idle.Controller.HasPendingWork && idle.Controller.State == RdpSessionState.Created);
        idle.StartCurrent();
        Assert(idle.Controller.State == RdpSessionState.Connecting && idle.Current.ConnectCalls == 1 && idle.Created.Count == 1);

        // Review finding: while the replacement waits for the old control's sign-in window, its Connected property (1
        // throughout its reconnect loop) must not mark the new attempt connected.
        var waiting = new FakeSessionHost(SessionIdentity.Capture(Profile("Waiting")));
        waiting.StartCurrent();
        var old = waiting.Current;
        waiting.Raise(old, controller => controller.OnNativeConnecting());
        waiting.Raise(old, controller => controller.OnNativeAutoReconnecting(2308, true, 2, 5));
        waiting.Raise(old, controller => controller.OnNativeDialogShown());
        old.State = RdpConnectionController.NativeConnected;
        var published = waiting.Published.Count;
        waiting.Controller.Poll();
        waiting.Clock.Advance(RdpConnectionController.StartTimeoutMs * 2);
        waiting.Controller.Poll();
        Assert(waiting.Controller.State == RdpSessionState.Connecting && !old.Disposed);
        Assert(waiting.Published.Skip(published).All(snapshot => snapshot.State == RdpSessionState.Connecting));
        waiting.Raise(old, controller => controller.OnNativeDialogClosed());
        waiting.RunPendingWork();
        waiting.StartCurrent();
        Assert(old.Disposed && waiting.Current.Generation == 2 && waiting.Current.ConnectCalls == 1 && waiting.Controller.State == RdpSessionState.Connecting);
    });

    Run("hard disconnect: 100 replace-and-reconnect cycles leave one live control and no stale subscriptions", () =>
    {
        var host = ConnectedHost();
        for (var cycle = 1; cycle <= 100; cycle++)
        {
            var previous = host.Current;
            BeginAutoReconnect(host);
            host.Controller.Disconnect();
            host.RunPendingWork();
            host.StartCurrent();
            Assert(host.Controller.State == RdpSessionState.Disconnected);
            host.Controller.Reconnect();
            var fresh = host.Current;
            host.Raise(fresh, controller => controller.OnNativeConnecting());
            fresh.State = RdpConnectionController.NativeConnected;
            host.Raise(fresh, controller => controller.OnNativeConnected());
            Assert(host.Controller.State == RdpSessionState.Connected && fresh.Generation == cycle + 1);
            Assert(!host.Raise(previous, controller => controller.OnNativeDisconnected(516)));
        }
        Assert(host.Created.Count == 101 && host.Slot.StaleCallbacks == 100);
        Assert(host.Created.Take(100).All(control => control.DisposeCalls == 1 && control.DetachCalls == 1 && !control.EventsAttached));
        Assert(host.Created.Count(control => control.EventsAttached) == 1 && !host.Created[100].Disposed);
        Assert(host.Created.All(control => control.ConnectCalls == 1));
    });

    Run("session view: Disconnecting hides the remote surface and shows no failure; the host releases its control", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/RDCM;component/Themes/Controls.xaml")
                    });
                }
                var identity = SessionIdentity.Capture(Profile("View"));
                var session = new EmbeddedRdpControl(identity);
                var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = session };
                var view = new SessionView();
                view.AttachSession(session, host);
                var element = (string name) => (System.Windows.FrameworkElement)view.FindName(name);
                // The first control generation exists but isn't created natively until the tab is shown.
                Assert(session.ControlGeneration == 1);
                var reconnecting = new RdpSessionSnapshot(1, RdpSessionState.AutoReconnecting, RdpEndReason.None,
                    "Connection interrupted · reconnecting (attempt 1 of 5)", null, identity);
                view.Apply(reconnecting);
                Assert(host.Visibility == System.Windows.Visibility.Visible);
                view.Apply(reconnecting with { Sequence = 2, State = RdpSessionState.Disconnecting, Message = "Disconnecting…" });
                Assert(host.Visibility == System.Windows.Visibility.Hidden && element("StatePanel").Visibility == System.Windows.Visibility.Visible);
                Assert(((System.Windows.Controls.TextBlock)element("StateTitle")).Text == "Disconnecting…" &&
                       element("PanelCopyButton").Visibility == System.Windows.Visibility.Collapsed &&
                       element("StateHint").Visibility == System.Windows.Visibility.Collapsed);
                session.Dispose();
                Assert(session.ControlGeneration == 0 && session.Controls.Count == 0);
                host.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    });

    Run("session view: hiding the session leaves keyboard focus on the session's tab, not on the released remote desktop", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/RDCM;component/Themes/Controls.xaml")
                    });
                }
                var identity = SessionIdentity.Capture(Profile("Focus"));
                (SessionView View, EmbeddedRdpControl Session, System.Windows.Forms.Integration.WindowsFormsHost Host) Create()
                {
                    var session = new EmbeddedRdpControl(identity);
                    var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = session };
                    var view = new SessionView();
                    view.AttachSession(session, host);
                    return (view, session, host);
                }
                var connected = new RdpSessionSnapshot(1, RdpSessionState.Connected, RdpEndReason.None, "Connected", null, identity);
                var disconnecting = connected with { Sequence = 2, State = RdpSessionState.Disconnecting, Message = "Disconnecting…" };

                // In a tab (as in MainWindow) of a window that isn't active (it is never shown here): the window's remembered
                // focus becomes the session's tab, so activating it doesn't send keys to a released remote desktop.
                var (view, session, host) = Create();
                var tab = new System.Windows.Controls.TabItem { Header = "Focus", Content = view };
                var tabs = new System.Windows.Controls.TabControl { Items = { tab } };
                var window = new System.Windows.Window { Content = tabs, ShowActivated = false };
                view.Apply(connected);
                System.Windows.Input.FocusManager.SetFocusedElement(window, host);
                view.Apply(disconnecting);
                Assert(ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(window), tab));

                // Focus the user had moved to another RDCM control is left alone.
                var search = new System.Windows.Controls.TextBox();
                var (view2, session2, host2) = Create();
                var window2 = new System.Windows.Window
                {
                    Content = new System.Windows.Controls.StackPanel { Children = { search, new System.Windows.Controls.TabControl { Items = { new System.Windows.Controls.TabItem { Content = view2 } } } } }
                };
                view2.Apply(connected);
                System.Windows.Input.FocusManager.SetFocusedElement(window2, search);
                view2.Apply(disconnecting);
                Assert(ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(window2), search));

                // Outside a tab the release-keyboard button is the target.
                var (view3, session3, host3) = Create();
                var window3 = new System.Windows.Window { Content = view3 };
                view3.Apply(connected);
                view3.Apply(disconnecting);
                Assert(ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(window3), view3.FindName("AppControlsButton")));

                foreach (var item in new System.IDisposable[] { session, host, session2, host2, session3, host3 })
                {
                    item.Dispose();
                }
                window.Close();
                window2.Close();
                window3.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    });

    Run("session view: the real Remote Desktop control's window is shown when the session appears (it is created hidden)", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/RDCM;component/Themes/Controls.xaml")
                    });
                }
                static void Pump()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                var identity = SessionIdentity.Capture(Profile("Surface"));
                var session = new EmbeddedRdpControl(identity);
                var host = new SessionHost { Child = session };
                var view = new SessionView();
                view.AttachSession(session, host);
                // Shown off-screen and never activated; showing it creates the native control while the surface is hidden.
                var window = new System.Windows.Window
                {
                    Content = view, Width = 900, Height = 600, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
                };
                window.Show();
                var activeX = IntPtr.Zero;
                for (var attempt = 0; attempt < 50 && activeX == IntPtr.Zero; attempt++)
                {
                    Pump();
                    activeX = session.IsHandleCreated ? TestWindows.FindChild(session.Handle, "ATL:") : IntPtr.Zero;
                }
                Assert(activeX != IntPtr.Zero);

                var connected = new RdpSessionSnapshot(1, RdpSessionState.Connected, RdpEndReason.None, "Connected", null, identity);
                view.Apply(connected);
                Pump();
                Assert(TestWindows.HasVisibleStyle(activeX) && TestWindows.IsWindowVisible(activeX));

                // The native surface fills the WPF layout region exactly, in physical pixels at the window's DPI.
                var container = (System.Windows.FrameworkElement)view.FindName("HostContainer");
                var scale = System.Windows.Media.VisualTreeHelper.GetDpi(container);
                var area = session.SessionArea;
                Assert(area.Dpi.Value == (int)Math.Round(scale.PixelsPerInchX));
                Assert(area.Width == area.Dpi.ToPhysical(container.ActualWidth) && area.Height == area.Dpi.ToPhysical(container.ActualHeight));
                var hostWindow = TestWindows.ClientSize(TestWindows.Parent(session.Handle));
                Assert(Math.Abs(hostWindow.Width - area.Width) <= 1 && Math.Abs(hostWindow.Height - area.Height) <= 1);
                Assert(TestWindows.ClientSize(session.Handle) == hostWindow && TestWindows.ClientSize(activeX) == hostWindow);

                // Hidden while disconnecting, shown again on the next connection, and nothing takes keyboard focus.
                view.Apply(connected with { Sequence = 2, State = RdpSessionState.Disconnecting, Message = "Disconnecting…" });
                Pump();
                Assert(!TestWindows.IsWindowVisible(activeX));
                view.Apply(connected with { Sequence = 3 });
                Pump();
                Assert(TestWindows.IsWindowVisible(activeX) && !session.ContainsFocus);

                session.Dispose();
                host.Dispose();
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    });
}

(RdpConnectionController Controller, FakeRdpClient Client, ManualClock Clock) Lifecycle(bool events = true)
{
    var client = new FakeRdpClient();
    var clock = new ManualClock();
    var controller = new RdpConnectionController(SessionIdentity.Capture(Profile("Lifecycle")), client, clock.Now)
    {
        NativeEventsAvailable = events
    };
    return (controller, client, clock);
}

(RdpConnectionController Controller, FakeRdpClient Client, ManualClock Clock) Connected()
{
    var (controller, client, clock) = Lifecycle();
    controller.Start();
    controller.OnNativeConnecting();
    client.State = RdpConnectionController.NativeConnected;
    controller.OnNativeConnected();
    Assert(controller.State == RdpSessionState.Connected);
    return (controller, client, clock);
}

FakeSessionHost ConnectedHost()
{
    var host = new FakeSessionHost(SessionIdentity.Capture(Profile("Hard disconnect")));
    host.StartCurrent();
    var first = host.Current;
    Assert(first.ConnectCalls == 1);
    host.Raise(first, controller => controller.OnNativeConnecting());
    first.State = RdpConnectionController.NativeConnected;
    host.Raise(first, controller => controller.OnNativeConnected());
    Assert(host.Controller.State == RdpSessionState.Connected);
    return host;
}

/// <summary>Live order: OnAutoReconnecting2 reports progress; the Connected property stays 1 meanwhile.</summary>
void BeginAutoReconnect(FakeSessionHost host)
{
    var current = host.Current;
    host.Raise(current, controller => controller.OnNativeAutoReconnecting(2308, true, 1, 5));
    Assert(host.AskContinue(current, 1) && host.Controller.State == RdpSessionState.AutoReconnecting);
}

// ---------------------------------------------------------------------------------------------
// Preferences and window placement
// ---------------------------------------------------------------------------------------------

void RunPreferenceTests()
{
    Run("storage: transient lock failures are retried with a bounded back-off; others are not", () =>
    {
        var waits = new List<int>();
        var calls = 0;
        TransientFileRetry.Run(() =>
        {
            if (++calls <= 3) throw new UnauthorizedAccessException("locked");
        }, waits.Add);
        Assert(calls == 4 && waits.SequenceEqual([25, 50, 75]));

        calls = 0;
        waits.Clear();
        AssertThrows<IOException>(() => TransientFileRetry.Run(() => { calls++; throw new IOException("sharing violation"); }, waits.Add));
        Assert(calls == TransientFileRetry.Attempts && waits.Sum() < 500);

        calls = 0;
        AssertThrows<FileNotFoundException>(() => TransientFileRetry.Run(() => { calls++; throw new FileNotFoundException(); }, waits.Add));
        Assert(calls == 1);
    });

    // Reproduces the cause of the earlier one-off crash: another process (an antivirus scanner or the indexer)
    // has the file open while it is replaced, which fails with "access denied" on NTFS.
    Run("storage: profiles and settings save once a brief external file lock is released", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RDCM.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProfileStore(Path.Combine(directory, "connections.json"));
            store.Save([Profile("Before")]);
            var settingsStore = new AppSettingsStore(Path.Combine(directory, "settings.json"));
            settingsStore.Save(new AppSettings());

            foreach (var file in new[] { store.FilePath, settingsStore.FilePath })
            {
                var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var probe = file + ".probe";
                File.WriteAllText(probe, "[]");
                AssertThrowsAny(() => File.Move(probe, file, overwrite: true), typeof(UnauthorizedAccessException), typeof(IOException));
                File.Delete(probe);
                var release = Task.Run(async () => { await Task.Delay(60); holder.Dispose(); });
                if (file == store.FilePath)
                {
                    store.Save([Profile("After")]);
                }
                else
                {
                    settingsStore.Save(new AppSettings { CompactConnectionList = true });
                }
                release.Wait();
            }
            Assert(new ProfileStore(store.FilePath).Load().Single().Name == "After");
            Assert(settingsStore.Load().CompactConnectionList);
            Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    Run("storage: a file that stays locked still fails visibly and keeps the previous content", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RDCM.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProfileStore(Path.Combine(directory, "connections.json"));
            store.Save([Profile("Kept")]);
            using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                AssertThrowsAny(() => store.Save([Profile("Lost")]), typeof(UnauthorizedAccessException), typeof(IOException));
            }
            Assert(new ProfileStore(store.FilePath).Load().Single().Name == "Kept");
            Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    Run("settings: normalize repairs nulls, duplicates, and invalid values", () =>
    {
        var id = Guid.NewGuid();
        var settings = new AppSettings
        {
            Appearance = "Purple",
            SidebarWidth = double.NaN,
            CollapsedGroups = null!,
            FavoriteProfileIds = [id, id, Guid.Empty],
            RecentConnections = null!,
            MainWindow = new WindowPlacementSettings { Width = -5, Height = 10 }
        };
        settings.Normalize();
        Assert(settings.Appearance == "System" && settings.SidebarWidth == AppSettings.DefaultSidebarWidth);
        Assert(settings.CollapsedGroups.Count == 0 && settings.RecentConnections.Count == 0);
        Assert(settings.FavoriteProfileIds.SequenceEqual([id]));
        Assert(settings.MainWindow is null);
    });

    Run("settings: legacy files load with defaults and corrupt files are preserved", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RDCM.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(filePath, """{"Appearance":"Dark","SidebarWidth":300,"SidebarCollapsed":false}""");
            var legacy = new AppSettingsStore(filePath).Load();
            Assert(legacy.Appearance == "Dark" && !legacy.CompactConnectionList && legacy.FavoriteProfileIds.Count == 0);

            File.WriteAllText(filePath, "{ not json");
            var store = new AppSettingsStore(filePath);
            AssertThrows<System.Text.Json.JsonException>(() => store.Load());
            Assert(File.ReadAllText(store.InvalidCopyPath) == "{ not json");

            var saved = new AppSettings { CompactConnectionList = true, CollapsedGroups = ["Production"] };
            store.Save(saved);
            var reloaded = store.Load();
            Assert(reloaded.CompactConnectionList && reloaded.CollapsedGroups.SequenceEqual(["Production"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    Run("recents: recording moves to the top without duplicates and stays bounded", () =>
    {
        var start = DateTimeOffset.Now;
        var ids = Enumerable.Range(0, AppSettings.MaxRecentConnections + 3).Select(_ => Guid.NewGuid()).ToList();
        var recents = new List<RecentConnection>();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            recents = RecentConnectionList.Record(recents, id, start.AddMinutes(index));
        }
        Assert(recents.Count == AppSettings.MaxRecentConnections && recents[0].ProfileId == ids[^1]);
        recents = RecentConnectionList.Record(recents, ids[^4], start.AddHours(1));
        Assert(recents[0].ProfileId == ids[^4] && recents.Count(recent => recent.ProfileId == ids[^4]) == 1);
        Assert(recents.Count == AppSettings.MaxRecentConnections);
    });

    Run("recents and favourites: references to removed profiles are pruned", () =>
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var settings = new AppSettings
        {
            FavoriteProfileIds = [kept, removed],
            RecentConnections = [new() { ProfileId = removed, LastConnected = DateTimeOffset.Now }, new() { ProfileId = kept }]
        };
        settings.PruneProfileReferences(new HashSet<Guid> { kept });
        Assert(settings.FavoriteProfileIds.SequenceEqual([kept]) && settings.RecentConnections.Single().ProfileId == kept);
    });

    Run("window placement: unreachable or implausible bounds are rejected", () =>
    {
        IReadOnlyList<MonitorDescription> monitors = [Monitor(0, 0, 1920, 1040)];
        Assert(WindowPlacementSettings.Validate(Placement(5000, 100, 1200, 800), monitors, new Dpi(96)) is null);
        Assert(WindowPlacementSettings.Validate(Placement(100, -2000, 1200, 800), monitors, new Dpi(96)) is null);
        Assert(WindowPlacementSettings.Validate(Placement(0, 0, 10, 10), monitors, new Dpi(96)) is null);
        Assert(WindowPlacementSettings.Validate(Placement(0, 0, 1200, 800), [], new Dpi(96)) is null);
        Assert(WindowPlacementSettings.Validate(null, monitors, new Dpi(96)) is null);
    });

    Run("window placement: partially visible windows are moved fully on screen", () =>
    {
        IReadOnlyList<MonitorDescription> monitors = [Monitor(0, 0, 1920, 1040), Monitor(1920, 0, 1280, 984)];
        var clamped = WindowPlacementSettings.Validate(Placement(1500, 500, 1000, 800, maximized: true), monitors, new Dpi(96))!;
        // Most of the window is on the second monitor, so it is moved fully onto that monitor.
        Assert(clamped.Left == 1920 && clamped.Top == 184 && clamped.Width == 1000 && clamped.Height == 800 && clamped.Maximized);
        var inside = WindowPlacementSettings.Validate(Placement(1200, 700, 1000, 600), [Monitor(0, 0, 1920, 1040)], new Dpi(96))!;
        Assert(inside.Left == 920 && inside.Top == 440);
        var shrunk = WindowPlacementSettings.Validate(Placement(2000, 10, 3000, 2000), monitors, new Dpi(96))!;
        Assert(shrunk.Left == 1920 && shrunk.Width == 1280 && shrunk.Height == 984);
        Assert(clamped.Format == WindowPlacementSettings.CurrentFormat && clamped.Dpi == 96);
    });
}

// ---------------------------------------------------------------------------------------------
// Per-monitor DPI
// ---------------------------------------------------------------------------------------------

// The validation environment: a 175% primary monitor and a 100% monitor above it at negative coordinates.
static MonitorDescription Monitor(int x, int y, int width, int height, int dpi = 96, bool primary = false, int taskbar = 0) =>
    new(new(x, y, width, height), new(x, y, width, height - taskbar), new Dpi(dpi), primary);

static IReadOnlyList<MonitorDescription> MixedDpiMonitors() =>
    [Monitor(0, 0, 2560, 1600, 168, primary: true, taskbar: 84), Monitor(1228, -1080, 1920, 1080, 96, taskbar: 48), Monitor(-692, -1080, 1920, 1080, 96, taskbar: 48)];

static WindowPlacementSettings Placement(int left, int top, int width, int height, bool maximized = false, int format = 0, int dpi = 0) =>
    new() { Left = left, Top = top, Width = width, Height = height, Maximized = maximized, Format = format, Dpi = dpi };

void RunDpiTests()
{
    Run("dpi: device-independent units and physical pixels convert with the window's own DPI", () =>
    {
        var standard = new Dpi(96);
        var large = new Dpi(168);
        Assert(standard.Percent == 100 && large.Percent == 175 && new Dpi(120).Percent == 125 && new Dpi(144).Percent == 150 && new Dpi(192).Percent == 200);
        Assert(large.ToPhysical(1000) == 1750 && standard.ToPhysical(1000) == 1000 && new Dpi(120).ToPhysical(1000) == 1250);
        Assert(Math.Abs(large.ToLogical(1750) - 1000) < 1e-9 && Math.Abs(standard.ToLogical(900) - 900) < 1e-9);
        // The same apparent size on another monitor: 1575 px at 175% is 900 px at 100%, and back again.
        Assert(large.Rescale(1575, standard) == 900 && standard.Rescale(900, large) == 1575);
        Assert(!Dpi.IsPlausible(0) && !Dpi.IsPlausible(5000) && Dpi.IsPlausible(96));
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new Dpi(0));
    });

    Run("dpi: the remote desktop matches the session area's physical pixels at 96, 120, 144, 168, and 192 DPI", () =>
    {
        var fit = RemoteDisplayOptions.Resolutions[0];
        var scale = RemoteDisplayOptions.Scales[0];
        // One tab area of 1000 × 600 device-independent units on monitors with different scaling.
        var millimeters = new List<(uint, uint)>();
        foreach (var (value, device) in new[] { (96, 100u), (120, 140u), (144, 140u), (168, 180u), (192, 180u) })
        {
            var dpi = new Dpi(value);
            var area = new SessionAreaMetrics(dpi.ToPhysical(1000), dpi.ToPhysical(600), dpi);
            var settings = RemoteDisplayPlan.For(fit, scale, area);
            // Pixel for pixel (rounded down to a multiple of 4): no second scaling of an already physical size.
            Assert(settings.DesktopWidth == area.Width - area.Width % 4 && settings.DesktopHeight == area.Height - area.Height % 4);
            Assert(settings.DeviceScaleFactor == device && settings.DesktopScaleFactor == 100);
            millimeters.Add((settings.PhysicalWidthMillimeters, settings.PhysicalHeightMillimeters));
        }
        // The physical size of the tab is the same whatever the monitor's scaling.
        Assert(millimeters.Distinct().Count() == 1 && millimeters[0] == (265u, 159u));
        // A fixed resolution is independent of the monitor.
        var fixedSize = RemoteDisplayPlan.For(RemoteDisplayOptions.Resolutions[5], scale, new SessionAreaMetrics(1750, 1050, new Dpi(168)));
        Assert(fixedSize is { DesktopWidth: 1920, DesktopHeight: 1080, DeviceScaleFactor: 180 });
        // The smallest RDCM window on a 100% monitor has a 565 × 380 pixel session area: the remote desktop must not be
        // larger than it (the control would clip it). Only areas under the documented 200-pixel minimum are enlarged.
        Assert(RemoteDisplayPlan.DesktopSize(fit, new SessionAreaMetrics(565, 380, new Dpi(96))) == (564, 380));
        Assert(RemoteDisplayPlan.DesktopSize(fit, new SessionAreaMetrics(150, 90, new Dpi(96))) == (200, 200));
        Assert(RemoteDisplayPlan.DesktopSize(fit, new SessionAreaMetrics(9000, 9000, new Dpi(96))) == (8192, 8192));
    });

    Run("dpi: moving a connected session between 100% and 175% monitors resizes the remote desktop once per move", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: true);
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        display.OnConnected();
        delay.Elapse(display);
        Assert(client.Applied.Count == 1 && client.Applied[0] is { DesktopWidth: 1600, DesktopHeight: 900, DeviceScaleFactor: 100 });

        // 100% to 175%: the window keeps its apparent size, so the area keeps its units and gains physical pixels.
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        Assert(client.Applied.Count == 1 && delay.Restarts > 0);
        delay.Elapse(display);
        Assert(client.Applied.Count == 2 && client.Applied[1] is { DesktopWidth: 2800, DesktopHeight: 1572, DeviceScaleFactor: 180 });
        Assert(client.Applied[1].PhysicalWidthMillimeters == client.Applied[0].PhysicalWidthMillimeters);

        // 175% back to 100%.
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        delay.Elapse(display);
        Assert(client.Applied.Count == 3 && client.Applied[2] is { DesktopWidth: 1600, DesktopHeight: 900, DeviceScaleFactor: 100 });
        Assert(display.ActiveResolution.IsFitToTab && display.UpdatesSent == 3);
    });

    Run("dpi: rapid moves and resizes are coalesced into one remote display update", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: true);
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        display.OnConnected();
        delay.Elapse(display);
        var restartsBefore = delay.Restarts;
        for (var i = 0; i < 20; i++)
        {
            var dpi = new Dpi(i % 2 == 0 ? 168 : 96);
            display.SetSessionArea(new SessionAreaMetrics(dpi.ToPhysical(1600 - i), dpi.ToPhysical(900), dpi));
            display.OnControlResized();
        }
        // Every change restarts the settle delay; nothing is sent until it elapses.
        Assert(client.Applied.Count == 1 && delay.Restarts - restartsBefore == 40);
        delay.Elapse(display);
        Assert(client.Applied.Count == 2 && client.Applied[1] is { DesktopWidth: 1580, DesktopHeight: 900, DeviceScaleFactor: 100 });
        // Nothing changed since: a later settle sends nothing, and an identical area doesn't even restart the delay.
        delay.Elapse(display);
        display.SetSessionArea(new SessionAreaMetrics(1581, 900, new Dpi(96)));
        display.SetSessionArea(new SessionAreaMetrics(1581, 900, new Dpi(96)));
        delay.Elapse(display);
        Assert(client.Applied.Count == 2);
    });

    Run("dpi: a fixed resolution is not renegotiated when the window moves; nothing is sent while disconnected", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: false);
        Assert(display.SetResolution(RemoteDisplayOptions.Resolutions[5]) && client.Applied.Count == 0);
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        Assert(delay.Restarts == 0 && client.Applied.Count == 0);
        // The pending preference is what the next connection uses, so connecting needs no display update.
        Assert(display.SettingsForConnect() is { DesktopWidth: 1920, DesktopHeight: 1080, DesktopScaleFactor: 100, DeviceScaleFactor: 180 });
        client.Connected = true;
        display.OnConnected();
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        delay.Elapse(display);
        Assert(client.Applied.Count == 0 && delay.Restarts == 0 && display.ActiveResolution == RemoteDisplayOptions.Resolutions[5]);
    });

    Run("dpi: a fresh control connects with the session area of the monitor the window is on now", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: false);
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        Assert(display.SettingsForConnect() is { DesktopWidth: 1600, DesktopHeight: 900, DeviceScaleFactor: 100 });
        // Moved to the 175% monitor while disconnected, then reconnected with a replacement control.
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        Assert(client.Applied.Count == 0 && delay.Restarts == 0);
        Assert(display.SettingsForConnect() is { DesktopWidth: 2800, DesktopHeight: 1572, DeviceScaleFactor: 180 });
        client.Connected = true;
        display.OnConnected();
        delay.Elapse(display);
        // Connected at the right size already, so nothing more is sent.
        Assert(client.Applied.Count == 0 && display.ActiveResolution.IsFitToTab);
    });

    Run("dpi: a reconnection keeps the remote scaling chosen for the session, set before connecting", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: false);
        var statuses = new List<string>();
        display.StatusChanged += statuses.Add;
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        Assert(display.SettingsForConnect() is { DesktopScaleFactor: 100 });
        client.Connected = true;
        display.OnConnected();
        // Chosen while connected and accepted by the session.
        Assert(display.SetScale(RemoteDisplayOptions.Scales[2]) && client.Applied.Count == 1);
        // Disconnected, then reconnected (for example with a replacement control): the connection itself asks for it.
        client.Connected = false;
        Assert(display.SettingsForConnect() is { DesktopWidth: 1600, DesktopHeight: 900 } connect && connect.DesktopScaleFactor == RemoteDisplayOptions.Scales[2].Percent);
        client.Connected = true;
        display.OnConnected();
        delay.Elapse(display);
        Assert(client.Applied.Count == 1 && display.ActiveScale == RemoteDisplayOptions.Scales[2]);
        Assert(statuses[^1] == $"Connected · 1600 × 900 · {RemoteDisplayOptions.Scales[2].Percent}%");

        // Chosen while disconnected: the next connection asks for it, and nothing more is sent once connected.
        client.Connected = false;
        Assert(display.SetScale(RemoteDisplayOptions.Scales[1]) && client.Applied.Count == 1);
        Assert(display.SettingsForConnect().DesktopScaleFactor == RemoteDisplayOptions.Scales[1].Percent);
        client.Connected = true;
        display.OnConnected();
        Assert(client.Applied.Count == 1 && display.ActiveScale == RemoteDisplayOptions.Scales[1]);
    });

    Run("dpi: a scaling changed while signing in, or not accepted before connecting, is sent once connected", () =>
    {
        // Changed after Connect, before the session is connected.
        var (display, client, _) = DisplayCoordinator(connected: false);
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        display.SettingsForConnect();
        Assert(display.SetScale(RemoteDisplayOptions.Scales[1]) && client.Applied.Count == 0);
        client.Connected = true;
        display.OnConnected();
        Assert(client.Applied.Count == 1 && client.Applied[0].DesktopScaleFactor == RemoteDisplayOptions.Scales[1].Percent &&
               client.Applied[0].DeviceScaleFactor == 180 && display.ActiveScale == RemoteDisplayOptions.Scales[1]);

        // The control couldn't take it before connecting: it connected at 100%, and a rejected update leaves it there.
        var (other, otherClient, _) = DisplayCoordinator(connected: false);
        var failures = new List<bool>();
        other.UpdateFailed += (_, _, notify) => failures.Add(notify);
        Assert(other.SetScale(RemoteDisplayOptions.Scales[3]));
        other.SettingsForConnect();
        other.OnConnectScaleUnavailable();
        otherClient.Connected = true;
        otherClient.Reject = true;
        other.OnConnected();
        Assert(failures.SequenceEqual([true]) && other.Scale == RemoteDisplayOptions.Scales[0] && other.ActiveScale == RemoteDisplayOptions.Scales[0]);
    });

    Run("dpi: a rejected display update keeps the session and the last confirmed settings, and is reported once", () =>
    {
        var (display, client, delay) = DisplayCoordinator(connected: true);
        var failures = new List<(string Message, bool Notify)>();
        var statuses = new List<string>();
        display.UpdateFailed += (message, _, notify) => failures.Add((message, notify));
        display.StatusChanged += statuses.Add;
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        display.OnConnected();
        client.Reject = true;
        Assert(!display.SetScale(RemoteDisplayOptions.Scales[3]));
        Assert(display.Scale == RemoteDisplayOptions.Scales[0] && display.ActiveScale == RemoteDisplayOptions.Scales[0]);
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        delay.Elapse(display);
        Assert(failures.Count == 2 && failures[0].Notify && !failures[1].Notify && client.Applied.Count == 0);
        // Accepted again later: the next failure is reported again.
        client.Reject = false;
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        delay.Elapse(display);
        Assert(client.Applied.Count == 1 && statuses[^1] == "Connected · 1600 × 900 · 100%");
        client.Reject = true;
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        delay.Elapse(display);
        Assert(failures.Count == 3 && failures[2].Notify);
    });

    Run("dpi: display updates go only to the current control; a released one never gets one", () =>
    {
        var host = ConnectedHost();
        var first = host.Current;
        var applied = new List<int>();
        var client = new FakeDisplayClient { Connected = true };
        client.OnApply = _ => applied.Add(host.Slot.Current!.Generation);
        var delay = new FakeSettleDelay();
        var display = new RemoteDisplayCoordinator(client, () => host.Controller.State == RdpSessionState.Connected, delay.Restart,
            () => new SessionAreaMetrics(1024, 768, new Dpi(96)));
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        display.OnConnected();
        delay.Elapse(display);
        Assert(applied.SequenceEqual([1]));

        // A resize just before the connection drops leaves the settle delay running.
        display.SetSessionArea(new SessionAreaMetrics(1700, 900, new Dpi(96)));
        Assert(delay.Pending);
        // Hard Disconnect during auto-reconnect replaces the control; the delay elapsing afterwards, and moves meanwhile,
        // send nothing.
        BeginAutoReconnect(host);
        host.Controller.Disconnect();
        host.RunPendingWork();
        Assert(first.Disposed && host.Current.Generation == 2 && host.Controller.State == RdpSessionState.Disconnected);
        delay.Elapse(display);
        display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
        delay.Elapse(display);
        Assert(applied.SequenceEqual([1]));

        // Reconnect on the fresh control: it connects at the current size, and later updates reach only generation 2.
        host.StartCurrent();
        var second = host.Current;
        Assert(host.Controller.Reconnect() && display.SettingsForConnect() is { DesktopWidth: 2800, DesktopHeight: 1572 });
        host.Raise(second, controller => controller.OnNativeConnecting());
        second.State = RdpConnectionController.NativeConnected;
        host.Raise(second, controller => controller.OnNativeConnected());
        display.OnConnected();
        display.SetSessionArea(new SessionAreaMetrics(1600, 900, new Dpi(96)));
        delay.Elapse(display);
        Assert(applied.SequenceEqual([1, 2]));
        Assert(!host.Raise(first, controller => controller.OnNativeDisconnected(516)) && host.Controller.State == RdpSessionState.Connected);
    });

    Run("dpi: display diagnostics contain only sizes, DPI, and scale factors", () =>
    {
        var lines = new List<string>();
        var listener = new CollectingTraceListener(lines);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            var (display, _, delay) = DisplayCoordinator(connected: true);
            display.SetSessionArea(new SessionAreaMetrics(2800, 1575, new Dpi(168)));
            display.OnConnected();
            delay.Elapse(display);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
        var update = lines.Single(line => line.Contains("Remote display update"));
        Assert(update.Contains("2800 × 1572 for a 2800 × 1575 area at 168 DPI (175%); desktop scale 100%, device scale 180%."));
        Assert(System.Text.RegularExpressions.Regex.IsMatch(update, @"^[^@\\/]*$"));
    });

    Run("window placement: a window saved on the 100% monitor reopens there with the same physical bounds", () =>
    {
        var monitors = MixedDpiMonitors();
        var saved = WindowPlacementSettings.FromNative(1, 0, new System.Drawing.Rectangle(1300, -1000, 900, 560), new Dpi(96));
        Assert(saved is { Format: 2, Dpi: 96, Maximized: false, Left: 1300, Top: -1000, Width: 900, Height: 560 });
        var restored = WindowPlacementSettings.Validate(saved, monitors, new Dpi(168))!;
        Assert(restored is { Left: 1300, Top: -1000, Width: 900, Height: 560, Dpi: 96, Maximized: false });
        // And on the 175% primary monitor.
        var primary = WindowPlacementSettings.Validate(Placement(200, 150, 1575, 980, format: 2, dpi: 168), monitors, new Dpi(168))!;
        Assert(primary is { Left: 200, Top: 150, Width: 1575, Height: 980, Dpi: 168 });
        // On the monitor at negative coordinates to the left.
        var left = WindowPlacementSettings.Validate(Placement(-600, -1000, 900, 560, format: 2, dpi: 96), monitors, new Dpi(168))!;
        Assert(left is { Left: -600, Top: -1000, Width: 900, Height: 560 });
    });

    Run("window placement: maximized windows restore maximized on either monitor; minimized is never the start state", () =>
    {
        var monitors = MixedDpiMonitors();
        var maximized100 = WindowPlacementSettings.FromNative(3, 0, new System.Drawing.Rectangle(1300, -1000, 900, 560), new Dpi(96));
        var maximized175 = WindowPlacementSettings.FromNative(3, 0, new System.Drawing.Rectangle(200, 150, 1575, 980), new Dpi(168));
        Assert(WindowPlacementSettings.Validate(maximized100, monitors, new Dpi(168)) is { Maximized: true, Left: 1300, Width: 900 });
        Assert(WindowPlacementSettings.Validate(maximized175, monitors, new Dpi(168)) is { Maximized: true, Left: 200, Width: 1575 });
        var minimized = WindowPlacementSettings.FromNative(WindowPlacementSettings.ShowMinimized, 0, new System.Drawing.Rectangle(200, 150, 1575, 980), new Dpi(168));
        var minimizedFromMaximized = WindowPlacementSettings.FromNative(WindowPlacementSettings.ShowMinimized, WindowPlacementSettings.RestoreToMaximized,
            new System.Drawing.Rectangle(200, 150, 1575, 980), new Dpi(168));
        Assert(!minimized.Maximized && minimizedFromMaximized.Maximized);
        Assert(WindowPlacementSettings.Validate(minimized, monitors, new Dpi(168)) is { Maximized: false, Width: 1575 });
    });

    Run("window placement: a window saved at another scale keeps its apparent size", () =>
    {
        // Saved at 175%, and the same monitor is now at 100% (or the window lands on a 100% monitor).
        IReadOnlyList<MonitorDescription> monitors = [Monitor(0, 0, 2560, 1600, 96, primary: true, taskbar: 48)];
        var rescaled = WindowPlacementSettings.Validate(Placement(200, 150, 1575, 980, format: 2, dpi: 168), monitors, new Dpi(96))!;
        Assert(rescaled is { Left: 200, Top: 150, Width: 900, Height: 560, Dpi: 96 });
        // Saved at 100% and now at 150%: larger, but never larger than the work area.
        IReadOnlyList<MonitorDescription> scaledUp = [Monitor(0, 0, 1920, 1080, 144, primary: true, taskbar: 72)];
        var enlarged = WindowPlacementSettings.Validate(Placement(100, 50, 1200, 800, format: 2, dpi: 96), scaledUp, new Dpi(144))!;
        Assert(enlarged is { Width: 1800, Height: 1008, Left: 100, Top: 0, Dpi: 144 });
    });

    Run("window placement: settings from system DPI aware builds (no Format) are converted to physical pixels", () =>
    {
        var monitors = MixedDpiMonitors();
        // A system DPI aware build at 168 system DPI saw the 100% monitor scaled by 1.75: physical (1300,-1000) 900 × 560 was saved as:
        var legacy = Placement(2275, -1750, 1575, 980);
        var converted = WindowPlacementSettings.Validate(legacy, monitors, new Dpi(168))!;
        Assert(converted is { Left: 1300, Top: -1000, Width: 900, Height: 560, Dpi: 96, Format: 2 });
        // On the primary monitor (system DPI) legacy coordinates are already physical.
        var primary = WindowPlacementSettings.Validate(Placement(200, 150, 1575, 980, maximized: true), monitors, new Dpi(168))!;
        Assert(primary is { Left: 200, Top: 150, Width: 1575, Height: 980, Dpi: 168, Maximized: true });
        // The left monitor at negative coordinates.
        var left = WindowPlacementSettings.Validate(Placement(-1050, -1750, 1575, 980), monitors, new Dpi(168))!;
        Assert(left is { Left: -600, Top: -1000, Width: 900, Height: 560 });
        // A file without the new fields is a legacy file.
        var parsed = System.Text.Json.JsonSerializer.Deserialize<WindowPlacementSettings>("""{"Left":200,"Top":150,"Width":1575,"Height":980,"Maximized":false}""")!;
        Assert(parsed.Format == 0 && parsed.Dpi == 0);
    });

    Run("window placement: a missing monitor, a changed primary monitor, or corrupt values never place the window off-screen", () =>
    {
        // Saved on the 100% monitor, which is now disconnected.
        IReadOnlyList<MonitorDescription> laptopOnly = [Monitor(0, 0, 2560, 1600, 168, primary: true, taskbar: 84)];
        Assert(WindowPlacementSettings.Validate(Placement(1300, -1000, 900, 560, format: 2, dpi: 96), laptopOnly, new Dpi(168)) is null);
        Assert(WindowPlacementSettings.Validate(Placement(2275, -1750, 1575, 980), laptopOnly, new Dpi(168)) is null);
        // The 100% monitor became primary at the origin and the laptop moved below it; the saved position is now on the
        // 100% monitor, so the window keeps its apparent size there.
        IReadOnlyList<MonitorDescription> swapped = [Monitor(0, 0, 1920, 1080, 96, primary: true, taskbar: 48), Monitor(0, 1080, 2560, 1600, 168, taskbar: 84)];
        Assert(WindowPlacementSettings.Validate(Placement(200, 150, 1575, 980, format: 2, dpi: 168), swapped, new Dpi(96)) is { Left: 200, Top: 150, Width: 900, Height: 560, Dpi: 96 });
        // An implausible DPI is treated as a legacy value and still validated against the monitors.
        Assert(WindowPlacementSettings.Validate(Placement(200, 150, 1575, 980, format: 2, dpi: 5000), MixedDpiMonitors(), new Dpi(168)) is { Left: 200, Width: 1575 });
        Assert(WindowPlacementSettings.Validate(Placement(int.MaxValue, 0, 900, 560, format: 2, dpi: 96), MixedDpiMonitors(), new Dpi(168)) is null);
        // The settings store drops a corrupt placement entirely.
        var settings = new AppSettings { MainWindow = Placement(0, 0, -5, 10, format: 2, dpi: 96) };
        settings.Normalize();
        Assert(settings.MainWindow is null);
    });

    Run("window placement: without a usable saved position the window opens centred on the monitor at that monitor's DPI", () =>
    {
        var monitors = MixedDpiMonitors();
        // 1280 × 800 units: 2240 × 1400 pixels on the 175% monitor, 1280 × 800 on a 100% one; centred in the work area.
        Assert(WindowPlacementSettings.CenteredOn(monitors[0], 1280, 800) is { Left: 160, Top: 58, Width: 2240, Height: 1400, Dpi: 168, Format: 2, Maximized: false });
        Assert(WindowPlacementSettings.CenteredOn(monitors[1], 1280, 800) is { Left: 1548, Top: -964, Width: 1280, Height: 800, Dpi: 96 });
        Assert(WindowPlacementSettings.CenteredOn(monitors[2], 1280, 800) is { Left: -372, Top: -964, Width: 1280, Height: 800 });
        // Never larger than a small monitor's work area.
        var small = Monitor(0, 0, 1366, 768, 120, primary: true, taskbar: 48);
        Assert(WindowPlacementSettings.CenteredOn(small, 1280, 800) is { Left: 0, Top: 0, Width: 1366, Height: 720, Dpi: 120 });
        // The result is itself a valid placement on that monitor.
        Assert(WindowPlacementSettings.Validate(WindowPlacementSettings.CenteredOn(monitors[1], 1280, 800), monitors, new Dpi(168)) is { Left: 1548, Width: 1280, Dpi: 96 });
    });

    Run("dpi: the application manifest declares PerMonitorV2 awareness (with system awareness as the fallback)", () =>
    {
        var manifest = ReadEmbeddedManifest(Path.Combine(AppContext.BaseDirectory, "RDCM.exe"));
        var document = System.Xml.Linq.XDocument.Parse(manifest);
        var awareness = document.Descendants().Single(element => element.Name.LocalName == "dpiAwareness");
        var aware = document.Descendants().Single(element => element.Name.LocalName == "dpiAware");
        Assert(awareness.Value.Trim() == "PerMonitorV2" && awareness.Name.NamespaceName == "http://schemas.microsoft.com/SMI/2016/WindowsSettings");
        Assert(aware.Value.Trim() == "true" && aware.Name.NamespaceName == "http://schemas.microsoft.com/SMI/2005/WindowsSettings");
        Assert(document.Descendants().Single(element => element.Name.LocalName == "requestedExecutionLevel").Attribute("level")?.Value == "asInvoker");
    });

    Run("dpi: the real Remote Desktop control takes the remote scaling and device scale factor before connecting", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            object? control = null;
            try
            {
                // The same class RDCM hosts (MsRdpClient9NotSafeForScripting); nothing connects.
                control = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("8B918B82-7985-4C24-89DF-C33AD2BBFBCD"), throwOnError: true)!)!;
                RdpExtendedSettings.SetScaleFactors(control, 150, 180);
                Assert(RdpExtendedSettings.GetUInt32(control, "DesktopScaleFactor") == 150);
                Assert(RdpExtendedSettings.GetUInt32(control, "DeviceScaleFactor") == 180);
                RdpExtendedSettings.SetScaleFactors(control, 100, 100);
                Assert(RdpExtendedSettings.GetUInt32(control, "DesktopScaleFactor") == 100 && RdpExtendedSettings.GetUInt32(control, "DeviceScaleFactor") == 100);
                var rejected = false;
                try
                {
                    RdpExtendedSettings.SetScaleFactors(control, 100, 123);
                }
                catch (Exception exception) when (exception is ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    rejected = true;
                }
                Assert(rejected && RdpExtendedSettings.GetUInt32(control, "DeviceScaleFactor") == 100);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (control is not null)
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(control);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    });

    if (MonitorLayout.GetMonitors().Select(monitor => monitor.Dpi).Distinct().Count() < 2)
    {
        Console.WriteLine("NOTE window placement and session tabs across monitor scales were not tested: they need monitors with different scales.");
        return;
    }
    Run("window placement: a window created on one monitor opens on a monitor with another scale at exactly the saved pixel bounds", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new System.Windows.Window
                {
                    Width = 1280,
                    Height = 800,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
                };
                WindowPlacementSettings? placement = null;
                Dpi? creationDpi = null;
                window.SourceInitialized += (_, _) =>
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    creationDpi = MonitorLayout.GetWindowDpi(handle);
                    var target = MonitorLayout.GetMonitors().First(monitor => monitor.Dpi != creationDpi);
                    // Larger than any minimum size, so a rescale to the other monitor's DPI would show.
                    placement = WindowPlacementSettings.CenteredOn(target, 1200, 760);
                    WindowPlacementService.Apply(window, placement);
                };
                window.Show();
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var bounds = TestWindows.Bounds(hwnd);
                var dpi = MonitorLayout.GetWindowDpi(hwnd);
                var actual = (window.ActualWidth, window.ActualHeight);
                window.Close();
                Assert(placement is not null && creationDpi is not null && dpi != creationDpi && dpi == new Dpi(placement.Dpi));
                Assert(bounds == new System.Drawing.Rectangle(placement!.Left, placement.Top, placement.Width, placement.Height));
                Assert(Math.Abs(actual.ActualWidth - 1200) < 0.5 && Math.Abs(actual.ActualHeight - 760) < 0.5);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    });

    Run("session view: a session tab shown again after the window moved to another scale measures its area at the new DPI", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/RDCM;component/Themes/Controls.xaml")
                    });
                }
                static void Pump()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                var session = new EmbeddedRdpControl(SessionIdentity.Capture(Profile("Hidden tab")));
                var host = new SessionHost { Child = session };
                // A fixed size in device-independent units, so the move itself causes no SizeChanged in the session area.
                var view = new SessionView { Width = 800, Height = 500 };
                view.AttachSession(session, host);
                var other = new System.Windows.Controls.TabItem { Header = "Other", Content = new System.Windows.Controls.Border() };
                var sessionTab = new System.Windows.Controls.TabItem { Header = "Session", Content = view };
                var tabs = new System.Windows.Controls.TabControl { Items = { sessionTab, other } };
                var window = new System.Windows.Window
                {
                    Content = tabs,
                    SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0
                };
                window.Show();
                Pump();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var before = session.SessionArea;
                Assert(before.Dpi == MonitorLayout.GetWindowDpi(hwnd));

                // Hide the session tab, move the window to a monitor with another scale, then show the tab again.
                tabs.SelectedItem = other;
                Pump();
                var target = MonitorLayout.GetMonitors().First(monitor => monitor.Dpi != before.Dpi);
                TestWindows.MoveWithoutResizing(hwnd, target.WorkArea.Left + 40, target.WorkArea.Top + 40);
                Pump();
                Assert(MonitorLayout.GetWindowDpi(hwnd) == target.Dpi);
                tabs.SelectedItem = sessionTab;
                Pump();
                var container = (System.Windows.FrameworkElement)view.FindName("HostContainer");
                var after = session.SessionArea;
                session.Dispose();
                host.Dispose();
                window.Close();
                Assert(after.Dpi == target.Dpi);
                Assert(after.Width == target.Dpi.ToPhysical(container.ActualWidth) && after.Height == target.Dpi.ToPhysical(container.ActualHeight));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    });
}

(RemoteDisplayCoordinator Display, FakeDisplayClient Client, FakeSettleDelay Delay) DisplayCoordinator(bool connected)
{
    var client = new FakeDisplayClient { Connected = connected };
    var delay = new FakeSettleDelay();
    var display = new RemoteDisplayCoordinator(client, () => client.Connected, delay.Restart,
        () => new SessionAreaMetrics(1024, 768, new Dpi(96)));
    return (display, client, delay);
}

/// <summary>The application manifest (RT_MANIFEST resource 1) embedded in an executable.</summary>
static string ReadEmbeddedManifest(string path)
{
    var module = NativeResources.LoadLibraryEx(path, IntPtr.Zero, 0x2 /* LOAD_LIBRARY_AS_DATAFILE */);
    Assert(module != IntPtr.Zero);
    try
    {
        var resource = NativeResources.FindResource(module, 1, 24);
        Assert(resource != IntPtr.Zero);
        var size = NativeResources.SizeofResource(module, resource);
        var pointer = NativeResources.LockResource(NativeResources.LoadResource(module, resource));
        var bytes = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(pointer, bytes, 0, (int)size);
        return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }
    finally
    {
        NativeResources.FreeLibrary(module);
    }
}

void Run(string name, Action test)
{
    currentTest = name;
    try
    {
        test();
        testCount++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        // Reported, not swallowed: the run continues so every failure is visible, and the exit code is 1.
        failedTests.Add(name);
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine($"  {Describe(exception)}");
    }
    finally
    {
        currentTest = null;
    }
}

// Exception type, message, and stack with local profile paths replaced. Tests use placeholder data only.
static string Describe(Exception? exception)
{
    if (exception is null)
    {
        return "(no exception object)";
    }
    var text = $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
    foreach (var (path, placeholder) in new[]
             {
                 (Path.GetTempPath().TrimEnd('\\'), "%TEMP%"),
                 (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
             })
    {
        if (!string.IsNullOrEmpty(path))
        {
            text = text.Replace(path, placeholder, StringComparison.OrdinalIgnoreCase);
        }
    }
    return text.Replace(Environment.NewLine, Environment.NewLine + "    ");
}

static ConnectionProfile Profile(string name) => new()
{
    Name = name,
    Host = "rdcm-test.local",
    Port = 3389,
    Group = "Tests"
};

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Test assertion failed.");
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertThrowsAny(Action action, params Type[] accepted)
{
    try
    {
        action();
    }
    catch (Exception exception) when (accepted.Any(type => type.IsInstanceOfType(exception)))
    {
        return;
    }
    throw new InvalidOperationException($"Expected one of: {string.Join(", ", accepted.Select(type => type.Name))}.");
}

/// <summary>Scriptable stand-in for the Remote Desktop ActiveX control.</summary>
sealed class FakeRdpClient : IRdpNativeClient
{
    public bool IsReady { get; set; } = true;
    public int State { get; set; } = RdpConnectionController.NativeNotConnected;
    public bool ThrowOnDisconnect { get; set; }
    public int ExtendedReason { get; set; }
    public int ConnectCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public SessionIdentity? LastIdentity { get; private set; }

    public int ReadConnectionState() => State;

    /// <summary>What the fake reports as in effect for the next connection (default: the requested level applies).</summary>
    public ServerAuthenticationStatus ServerAuthentication { get; set; } =
        RDCM.ServerAuthentication.Evaluate(RDCM.ServerAuthentication.RequestedMode, (int)RDCM.ServerAuthentication.RequestedMode, null);

    public ServerAuthenticationStatus BeginConnect(SessionIdentity identity)
    {
        ConnectCalls++;
        LastIdentity = identity;
        State = RdpConnectionController.NativeConnecting;
        return ServerAuthentication;
    }

    public void RequestDisconnect()
    {
        DisconnectCalls++;
        if (ThrowOnDisconnect)
        {
            throw new RdpClientException("Remote Desktop rejected the disconnect request (error 0x80004005).");
        }
    }

    public RdpDisconnectInfo DescribeDisconnect(int reason) =>
        new(ExtendedReason, reason == 260 ? "Remote Desktop can't find the computer \"x\". More text." : null);

    public int ReplaceCalls { get; private set; }
    public int Generation { get; private set; } = 1;
    public bool FailReplacement { get; set; }

    public bool ReplaceControl()
    {
        ReplaceCalls++;
        State = RdpConnectionController.NativeNotConnected;
        if (FailReplacement)
        {
            IsReady = false;
            return false;
        }
        Generation++;
        return true;
    }
}

/// <summary>Fake native control generation: records the order of its lifecycle calls in a shared journal.</summary>
sealed class FakeControlInstance(int generation, List<string> journal) : IRdpControlInstance
{
    public int Generation => generation;

    /// <summary>The control's Connected property.</summary>
    public int State { get; set; }
    public bool Started { get; set; }
    public bool Initialized { get; private set; }
    public bool EventsAttached { get; private set; }
    public bool Disposed => DisposeCalls > 0;
    public int ConnectCalls { get; set; }
    public int DisconnectRequests { get; set; }
    public int DetachCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool FailInitialize { get; init; }
    public bool ThrowOnRelease { get; set; }

    public void Initialize()
    {
        journal.Add($"initialize {generation}");
        // Events are connected before a later step fails, as a partly started ActiveX control would be.
        EventsAttached = true;
        if (FailInitialize)
        {
            throw new InvalidOperationException("simulated: the control couldn't be started");
        }
        Initialized = true;
    }

    public void RequestDisconnectIfActive()
    {
        journal.Add($"disconnect-request {generation}");
        ThrowIfRequested();
    }

    public void DetachEvents()
    {
        journal.Add($"detach {generation}");
        DetachCalls++;
        EventsAttached = false;
        ThrowIfRequested();
    }

    public void Dispose()
    {
        journal.Add($"dispose {generation}");
        DisposeCalls++;
        State = RdpConnectionController.NativeNotConnected;
        ThrowIfRequested();
    }

    private void ThrowIfRequested()
    {
        if (ThrowOnRelease)
        {
            throw new InvalidOperationException("simulated release failure");
        }
    }
}

/// <summary>
/// The session host as EmbeddedRdpControl wires it, with fake control generations: native events are routed through the
/// real <see cref="RdpControlSlot{TInstance}"/> by generation, a replacement runs as separate pending work (as
/// SchedulePendingWork does), and a fresh control is used only once the host reports it started. Only the ActiveX
/// specifics (AxHost window, COM connection point) are not modelled; those are covered by the live tests.
/// </summary>
sealed class FakeSessionHost : IRdpNativeClient
{
    public readonly List<string> Journal = [];
    public readonly List<FakeControlInstance> Created = [];
    public readonly RdpControlSlot<FakeControlInstance> Slot;
    public readonly RdpConnectionController Controller;
    public readonly ManualClock Clock = new();
    public readonly List<RdpSessionSnapshot> Published = [];
    public int? FailCreateFor { get; set; }
    public int? FailInitializeFor { get; set; }
    public Func<int, ServerAuthenticationStatus> AuthenticationFor { get; set; } =
        _ => ServerAuthentication.Evaluate(ServerAuthentication.RequestedMode, (int)ServerAuthentication.RequestedMode, null);

    public FakeSessionHost(SessionIdentity identity)
    {
        Slot = new RdpControlSlot<FakeControlInstance>(Create, (_, _) => { });
        Controller = new RdpConnectionController(identity, this, Clock.Now) { NativeEventsAvailable = true };
        Controller.Changed += Published.Add;
        Slot.TryCreate();
    }

    public FakeControlInstance Current => Slot.Current ?? throw new InvalidOperationException("No current control.");

    private FakeControlInstance Create(int generation)
    {
        Journal.Add($"create {generation}");
        if (FailCreateFor == generation)
        {
            throw new InvalidOperationException("simulated: the control can't be created");
        }
        var instance = new FakeControlInstance(generation, Journal) { FailInitialize = FailInitializeFor == generation };
        Created.Add(instance);
        return instance;
    }

    /// <summary>EmbeddedRdpControl's posted start once a generation's window exists.</summary>
    public void StartCurrent()
    {
        if (Slot.Current is { Started: false, Initialized: true } current)
        {
            current.Started = true;
            Controller.Start();
        }
    }

    /// <summary>EmbeddedRdpControl.SchedulePendingWork: background work, outside native callbacks.</summary>
    public void RunPendingWork()
    {
        if (Controller.HasPendingWork)
        {
            Controller.Poll();
        }
    }

    /// <summary>A native event from <paramref name="source"/>, routed as EmbeddedRdpControl routes it.</summary>
    public bool Raise(FakeControlInstance source, Action<RdpConnectionController> handler) =>
        Slot.Route(source.Generation, () => handler(Controller));

    /// <summary>OnAutoReconnecting from <paramref name="source"/>: a released generation is always told to stop.</summary>
    public bool AskContinue(FakeControlInstance source, int attempt) =>
        Slot.IsCurrent(source.Generation) && Controller.OnNativeAutoReconnectDecision(attempt);

    /// <summary>EmbeddedRdpControl.Dispose (close tab, exit).</summary>
    public void CloseTab()
    {
        Controller.Shutdown();
        Slot.Release(requestDisconnect: false);
    }

    public bool IsReady => Slot.Current is { Started: true };

    public int ReadConnectionState() =>
        Slot.Current?.State ?? throw new RdpClientException("The Remote Desktop control is not available.");

    public ServerAuthenticationStatus BeginConnect(SessionIdentity identity)
    {
        var current = Current;
        current.ConnectCalls++;
        current.State = RdpConnectionController.NativeConnecting;
        return AuthenticationFor(current.Generation);
    }

    public void RequestDisconnect() => Current.DisconnectRequests++;

    public RdpDisconnectInfo DescribeDisconnect(int reason) => new(0, null);

    public bool ReplaceControl() => Slot.Replace();
}

sealed class ManualClock
{
    private long _now = 1_000;
    public long Now() => _now;
    public void Advance(long milliseconds) => _now += milliseconds;
}

static class TestWindows
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>The first descendant window whose class name starts with the prefix.</summary>
    public static IntPtr FindChild(IntPtr parent, string classPrefix)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (!name.ToString().StartsWith(classPrefix, StringComparison.Ordinal))
            {
                return true;
            }
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The window's own WS_VISIBLE style, regardless of its parents.</summary>
    public static bool HasVisibleStyle(IntPtr hwnd) => (GetWindowLong(hwnd, -16) & 0x10000000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    public static IntPtr Parent(IntPtr hwnd) => GetParent(hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE: Windows then rescales the window for the new monitor's DPI.</summary>
    public static void MoveWithoutResizing(IntPtr hwnd, int x, int y) => SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    public static System.Drawing.Rectangle Bounds(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static System.Drawing.Size ClientSize(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var rect);
        return new System.Drawing.Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}

/// <summary>Records display updates; can reject them as a server without dynamic display support would.</summary>
sealed class FakeDisplayClient : IRemoteDisplayClient
{
    public readonly List<RemoteDisplaySettings> Applied = [];
    public bool Connected { get; set; }
    public bool Reject { get; set; }
    public Action<RemoteDisplaySettings>? OnApply { get; set; }

    public void Apply(RemoteDisplaySettings settings)
    {
        if (Reject)
        {
            throw new RemoteDisplayUpdateException("The remote computer rejected the display change.",
                new System.Runtime.InteropServices.COMException("simulated", unchecked((int)0x80004005)));
        }
        Applied.Add(settings);
        OnApply?.Invoke(settings);
    }
}

/// <summary>The settle delay as a restartable timer: it fires only if started since it last fired.</summary>
sealed class FakeSettleDelay
{
    public int Restarts { get; private set; }
    public bool Pending { get; private set; }

    public void Restart()
    {
        Restarts++;
        Pending = true;
    }

    public void Elapse(RemoteDisplayCoordinator display)
    {
        if (Pending)
        {
            Pending = false;
            display.OnSettleDelayElapsed();
        }
    }
}

sealed class CollectingTraceListener(List<string> lines) : System.Diagnostics.TraceListener
{
    public override void Write(string? message) => lines.Add(message ?? "");
    public override void WriteLine(string? message) => lines.Add(message ?? "");
}

static class NativeResources
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern IntPtr FindResource(IntPtr module, nint name, nint type);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern IntPtr LockResource(IntPtr data);
}
