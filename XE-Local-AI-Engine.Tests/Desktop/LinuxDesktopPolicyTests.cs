namespace XE_Local_AI_Engine.Tests.Desktop;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Desktop.Linux;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class LinuxDesktopPolicyTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:35207/");

    [Test]
    public void ActualDocument_RequiresExactSingleRestrictionsAndTrustedHtmlResponse()
    {
        static bool Trusted(string? current = "http://127.0.0.1:35207/settings", string? resource = "http://127.0.0.1:35207/",
            uint status = 200, string? mime = "text/html", string[]? csp = null, string[]? permissions = null) =>
            GtkDocumentPolicy.IsTrusted(Origin, current, resource, status, mime,
                csp ?? [GtkDocumentPolicy.ContentSecurityPolicy], permissions ?? [GtkDocumentPolicy.PermissionsPolicy]);
        AssertEx.True(Trusted());
        AssertEx.False(Trusted(current: "http://127.0.0.1:35208/settings"));
        AssertEx.False(Trusted(current: "http://user@127.0.0.1:35207/settings"));
        AssertEx.False(Trusted(resource: "https://example.com/"));
        AssertEx.False(Trusted(resource: null));
        AssertEx.False(Trusted(status: 302));
        AssertEx.False(Trusted(status: 500));
        AssertEx.False(Trusted(mime: "application/json"));
        AssertEx.False(Trusted(csp: []));
        AssertEx.False(Trusted(csp: [GtkDocumentPolicy.ContentSecurityPolicy, GtkDocumentPolicy.ContentSecurityPolicy]));
        AssertEx.False(Trusted(csp: ["frame-src *; object-src 'none'"]));
        AssertEx.False(Trusted(permissions: ["microphone=*, camera=(), display-capture=()"]));
        AssertEx.False(Trusted(permissions: [GtkDocumentPolicy.PermissionsPolicy, "camera=*"]));
    }

    [Test]
    public void MicrophonePolicy_RejectsCameraDisplayAndNonAudioRequests()
    {
        AssertEx.True(GtkDocumentPolicy.AudioOnly(true, false, false));
        for (var bits = 0; bits < 8; bits++)
        {
            if (bits == 1) { continue; }
            AssertEx.False(GtkDocumentPolicy.AudioOnly((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0));
        }
    }

    [Test]
    public async Task DelayedConsent_InvalidationCannotRearmOrGrantTheOldDocument()
    {
        var gate = new GtkDocumentGate();
        var first = gate.Invalidate();
        AssertEx.True(gate.Arm(first));
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueConsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grants = 0;
        async Task<bool> ConsentAsync()
        {
            verified.SetResult();
            await continueConsent.Task;
            return gate.ExecuteIfCurrent(first, () => grants++);
        }

        var consent = ConsentAsync();
        await verified.Task;
        var next = gate.Invalidate(); // A frame insertion or a navigation revokes the pending permission.
        continueConsent.SetResult();
        AssertEx.False(await consent);
        AssertEx.Equal(0, grants);
        AssertEx.False(gate.Arm(first));
        AssertEx.True(gate.Arm(next));
        AssertEx.True(gate.ExecuteIfCurrent(next, () => grants++));
        gate.Close();
        AssertEx.False(gate.ExecuteIfCurrent(next, () => grants++));
        AssertEx.False(gate.Arm(gate.Generation));
        AssertEx.Equal(1, grants);
    }

    [Test]
    public void SaveIntent_RejectsBadMessagesPathsOriginsAndUnboundedPayloads()
    {
        const string nonce = "nonce";
        const string url = "blob:http://127.0.0.1:35207/11111111-1111-4111-8111-111111111111";
        static string Message(string location = url, string filename = "export.json", long size = 12, string requestNonce = nonce, string? hash = null) =>
            JsonSerializer.Serialize(new { kind = "xe-save", nonce = requestNonce, id = "22222222-2222-4222-8222-222222222222", url = location,
                filename, size, sha256 = hash ?? new string('a', 64) });
        AssertEx.NotNull(GtkSaveIntent.Parse(Message(), Origin, nonce));
        foreach (var invalid in new[] { "{}", "[]", "not json", new string('x', 4097), Message(requestNonce: "old"),
                     Message(filename: "../export.json"), Message(filename: "a/b"), Message(filename: ".."), Message(filename: "export\n.json"),
                     Message(size: -1), Message(size: GtkSaveIntent.MaximumBytes + 1), Message(hash: "invalid"),
                     Message(location: "https://example.com/file"), Message(location: url.Replace("35207", "35208", StringComparison.Ordinal)),
                     Message(location: "blob:http://user@127.0.0.1:35207/11111111-1111-4111-8111-111111111111") })
        {
            AssertEx.Null(GtkSaveIntent.Parse(invalid, Origin, nonce));
        }
    }

    [Test]
    public async Task SaveCommit_AfterDelayedVerificationRejectsCancellationAndNavigation()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "export.json");
        var temporary = GtkDownload.TemporaryPath(destination);
        await File.WriteAllTextAsync(temporary, "verified export");
        var gate = new GtkDocumentGate();
        var generation = gate.Generation;
        AssertEx.True(gate.Arm(generation));
        using var cancellation = new CancellationTokenSource();
        var verification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> CommitAsync()
        {
            await verification.Task;
            return GtkDownload.Commit(gate, generation, temporary, destination, false, cancellation.Token);
        }

        var commit = CommitAsync();
        await cancellation.CancelAsync();
        verification.SetResult();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => { _ = await commit; });
        AssertEx.False(File.Exists(destination));
        AssertEx.True(File.Exists(temporary));
        gate.Invalidate();
        AssertEx.False(GtkDownload.Commit(gate, generation, temporary, destination, false, CancellationToken.None));
        AssertEx.False(File.Exists(destination));
        AssertEx.True(gate.Arm(gate.Generation));
        AssertEx.True(GtkDownload.Commit(gate, gate.Generation, temporary, destination, false, CancellationToken.None));
        AssertEx.Equal("verified export", await File.ReadAllTextAsync(destination));
        AssertEx.False(File.Exists(temporary));
    }

    [Test]
    public void SaveDestination_IsLocalUniqueSiblingAndRejectsRelativeOrControlPaths()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "export.json");
        var first = GtkDownload.TemporaryPath(destination);
        var second = GtkDownload.TemporaryPath(destination);
        AssertEx.Equal(directory.Path, Path.GetDirectoryName(first));
        AssertEx.False(string.Equals(first, second, StringComparison.Ordinal));
        AssertEx.False(File.Exists(first));
        _ = AssertEx.Throws<ArgumentException>(() => GtkDownload.TemporaryPath("relative.json"));
        _ = AssertEx.Throws<ArgumentException>(() => GtkDownload.TemporaryPath(destination + "\n"));
    }

    [Test]
    public void ExportBridgeScript_PinsTheSameLimitsTheNativeSideEnforces()
    {
        var script = ExportBridgeScript();
        AssertEx.Equal(GtkSaveIntent.MaximumBytes, ScriptConstant(script, "MAX_BLOB_BYTES"));
        AssertEx.Equal((long)GtkDesktopBridge.SaveTimeout.TotalMilliseconds, ScriptConstant(script, "SAVE_TIMEOUT_MS"));
        AssertEx.Equal(256L, ScriptConstant(script, "MAX_CACHED_BLOBS"));
    }

    [Test]
    public void ExportBridgeScript_KeepsTheNonceTokenEveryMessageKindAndTheNativeSurface()
    {
        var script = ExportBridgeScript();
        AssertEx.Contains(script, "__XE_NONCE__"); // GtkDesktopBridge.ArmAsync substitutes the armed nonce here.
        foreach (var kind in new[] { "xe-save", "xe-save-cancel", "xe-save-unavailable", "xe-frame-blocked" })
        {
            AssertEx.Contains(script, $"kind: '{kind}'");
        }

        AssertEx.Contains(script, "globalThis.__xeSaveBridge = {");
        AssertEx.Contains(script, "return 'installed';");
    }

    private static string ExportBridgeScript()
    {
        using var stream = typeof(GtkSaveIntent).Assembly.GetManifestResourceStream("DesktopDownloadBridge.js");
        using var reader = new StreamReader(AssertEx.NotNull(stream, "The Desktop assembly no longer embeds DesktopDownloadBridge.js."));
        return reader.ReadToEnd();
    }

    private static long ScriptConstant(string script, string name)
    {
        var declaration = $"const {name} = ";
        var start = script.IndexOf(declaration, StringComparison.Ordinal);
        AssertEx.True(start >= 0, $"DesktopDownloadBridge.js no longer declares {name}.");
        var end = script.IndexOf(';', start);
        AssertEx.True(end > start, $"The {name} declaration in DesktopDownloadBridge.js is unterminated.");
        return script[(start + declaration.Length)..end]
            .Split('*')
            .Aggregate(1L, (product, factor) => product * long.Parse(factor.Trim(), CultureInfo.InvariantCulture));
    }
}
