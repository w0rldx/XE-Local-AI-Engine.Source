namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Text.Json;
using System.Net;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class EngineCommandDispatchTests
{
    [Test]
    public async Task StatusJson_WhenStopped_IsOneShotAndDoesNotCreateDataDirectory()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-status-tests", Guid.NewGuid().ToString("N"));
        var originalDataDirectory = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, dataDirectory);
            var result = await Program.CreateAppAsync(["--status", "--json", "--mcp-only"], new ProgramAppCustomization
            {
                StandardOutput = output
            }).ConfigureAwait(false);

            AssertEx.Null(result.App);
            AssertEx.Equal(expected: 1, result.ExitCode);
            AssertEx.False(Directory.Exists(dataDirectory), "Status path resolution must not create the data directory.");
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            AssertEx.False(root.GetProperty("running").GetBoolean());
            AssertEx.Equal(dataDirectory, root.GetProperty("dataDir").GetString());
            AssertEx.Equal("unmanaged", root.GetProperty("installKind").GetString());
            AssertEx.Equal(JsonValueKind.Null, root.GetProperty("setupRequired").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, originalDataDirectory);
        }
    }

    [Test]
    public async Task Setup_WhenCredentialsAreMissing_ReturnsUsageWithoutCreatingDataDirectory()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-setup-tests", Guid.NewGuid().ToString("N"));
        var originalDataDirectory = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, dataDirectory);

            var result = await Program.CreateAppAsync(["--setup"], new ProgramAppCustomization()).ConfigureAwait(false);

            AssertEx.Null(result.App);
            AssertEx.Equal(expected: 2, result.ExitCode);
            AssertEx.False(Directory.Exists(dataDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, originalDataDirectory);
        }
    }

    [Test]
    public async Task Help_IsOneShotAndDocumentsCredentialAndExitContracts()
    {
        using var output = new StringWriter();

        var result = await Program.CreateAppAsync(["--help", "--desktop"], new ProgramAppCustomization
        {
            StandardOutput = output
        }).ConfigureAwait(false);

        AssertEx.Null(result.App);
        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Contains(output.ToString(), "--admin-password <password> | --admin-password-stdin");
        AssertEx.Contains(output.ToString(), "scripts and installers must use XE_ADMIN_PASSWORD or --admin-password-stdin, never --admin-password on argv");
        AssertEx.Contains(output.ToString(), "--reset-admin-password <password>");
        AssertEx.Contains(output.ToString(), "XE_DATA_DIR must be an absolute path");
        AssertEx.Contains(output.ToString(), "Exit codes: 0 success; 1 stopped/unexpected failure; 2 usage; 3 validation; 4 instance busy; 5 setup/command failure; 6 requested port unavailable.");
    }

    [Test]
    public async Task OneShotUnexpectedFailure_IsRedactedAndReturnsExitOne()
    {
        using var error = new StringWriter();

        var result = await Program.CreateAppAsync(["--status"], new ProgramAppCustomization
        {
            StandardError = error,
            BeforeOneShotCommand = static () => throw new InvalidOperationException("secret-value")
        }).ConfigureAwait(false);

        AssertEx.Null(result.App);
        AssertEx.Equal(expected: 1, result.ExitCode);
        AssertEx.Equal(
            $"The engine command failed unexpectedly (stage=preparation, type=InvalidOperationException).{Environment.NewLine}",
            error.ToString());
        AssertEx.False(error.ToString().Contains("secret-value", StringComparison.Ordinal));
    }

    [Test]
    public async Task Status_InvalidReadyEvidence_IsReportedAndRemainsStopped()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-status-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, DesktopPortStore.ReadyFileName), "{not-json").ConfigureAwait(false);
        var original = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, dataDirectory);
            var result = await Program.CreateAppAsync(["--status", "--json"], new ProgramAppCustomization
            {
                StandardOutput = output,
                StandardError = error
            }).ConfigureAwait(false);

            AssertEx.Equal(expected: 1, result.ExitCode);
            AssertEx.Contains(error.ToString(), "readiness file is invalid");
            using var document = JsonDocument.Parse(output.ToString());
            AssertEx.False(document.RootElement.GetProperty("running").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, original);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Status_AuthProbeFailureCannotReportRunningTrueWithUnknownSetupState()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-status-auth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var original = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        DesktopPortStore.PersistReady(dataDirectory,
            new ReadyInfo("1.0.0", "http://127.0.0.1:41234", "http://127.0.0.1:41234/api/local/v1/mcp/server",
                dataDirectory, Environment.ProcessId, DateTimeOffset.UtcNow),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, dataDirectory);
            var result = await Program.CreateAppAsync(["--status", "--json"], new ProgramAppCustomization
            {
                StandardOutput = output,
                StatusHttpClientFactory = static () => new HttpClient(new StatusProbeHandler())
            }).ConfigureAwait(false);

            AssertEx.Equal(expected: 1, result.ExitCode);
            using var document = JsonDocument.Parse(output.ToString());
            AssertEx.False(document.RootElement.GetProperty("running").GetBoolean());
            AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty("setupRequired").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, original);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Setup_MalformedExplicitEmailIsUsageAndCapturedEnvironmentPasswordIsScrubbed()
    {
        var originalEmail = Environment.GetEnvironmentVariable(DesktopLaunch.AdminEmailEnvironmentVariable);
        var originalPassword = Environment.GetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable);
        var originalDataDirectory = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopLaunch.AdminEmailEnvironmentVariable, "fallback@example.test");
            Environment.SetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable, "secret-value");
            var malformed = await Program.CreateAppAsync(["--setup", "--admin-email="], new ProgramAppCustomization()).ConfigureAwait(false);
            AssertEx.Equal(expected: 2, malformed.ExitCode);

            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, "relative/path");
            var captured = await Program.CreateAppAsync(["--setup"], new ProgramAppCustomization()).ConfigureAwait(false);
            AssertEx.Equal(expected: 1, captured.ExitCode);
            AssertEx.Null(Environment.GetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopLaunch.AdminEmailEnvironmentVariable, originalEmail);
            Environment.SetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable, originalPassword);
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, originalDataDirectory);
        }
    }

    private sealed class StatusProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = request.RequestUri?.AbsolutePath == "/health/ready" ? HttpStatusCode.OK : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
