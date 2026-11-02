using System.IO;
using RDCM;

var testCount = 0;
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
RunFeatureTests();
Console.WriteLine($"Passed {testCount} RDCM checks.");

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

// ---------------------------------------------------------------------------------------------
// Preferences and window placement
// ---------------------------------------------------------------------------------------------

void RunPreferenceTests()
{
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
        IReadOnlyList<System.Drawing.Rectangle> monitors = [new(0, 0, 1920, 1040)];
        Assert(WindowPlacementSettings.Validate(new() { Left = 5000, Top = 100, Width = 1200, Height = 800 }, monitors) is null);
        Assert(WindowPlacementSettings.Validate(new() { Left = 100, Top = -2000, Width = 1200, Height = 800 }, monitors) is null);
        Assert(WindowPlacementSettings.Validate(new() { Left = 0, Top = 0, Width = 10, Height = 10 }, monitors) is null);
        Assert(WindowPlacementSettings.Validate(new() { Left = 0, Top = 0, Width = 1200, Height = 800 }, []) is null);
        Assert(WindowPlacementSettings.Validate(null, monitors) is null);
    });

    Run("window placement: partially visible windows are moved fully on screen", () =>
    {
        IReadOnlyList<System.Drawing.Rectangle> monitors = [new(0, 0, 1920, 1040), new(1920, 0, 1280, 984)];
        var clamped = WindowPlacementSettings.Validate(new() { Left = 1500, Top = 500, Width = 1000, Height = 800, Maximized = true }, monitors)!;
        // Most of the window is on the second monitor, so it is moved fully onto that monitor.
        Assert(clamped.Left == 1920 && clamped.Top == 184 && clamped.Width == 1000 && clamped.Height == 800 && clamped.Maximized);
        var inside = WindowPlacementSettings.Validate(new() { Left = 1200, Top = 700, Width = 1000, Height = 600 }, [new(0, 0, 1920, 1040)])!;
        Assert(inside.Left == 920 && inside.Top == 440);
        var shrunk = WindowPlacementSettings.Validate(new() { Left = 2000, Top = 10, Width = 3000, Height = 2000 }, monitors)!;
        Assert(shrunk.Left == 1920 && shrunk.Width == 1280 && shrunk.Height == 984);
    });
}

void Run(string name, Action test)
{
    test();
    testCount++;
    Console.WriteLine($"PASS {name}");
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

    public void BeginConnect(SessionIdentity identity)
    {
        ConnectCalls++;
        LastIdentity = identity;
        State = RdpConnectionController.NativeConnecting;
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
}

sealed class ManualClock
{
    private long _now = 1_000;
    public long Now() => _now;
    public void Advance(long milliseconds) => _now += milliseconds;
}
