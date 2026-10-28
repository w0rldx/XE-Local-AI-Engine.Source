namespace XE_Local_AI_Engine.Tests.Compute;

using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <c>run_python</c> on Windows end to end: a real compute venv provisioned into a scratch root, the real gateway, and the real
///     provider serving the MXC AppContainer boundary with execution previews on.
/// </summary>
/// <remarks>
///     The Windows counterpart of <see cref="ComputeSandboxLiveTests" />, which proves the bubblewrap chain. Skips only off Windows, on an
///     unpinned platform, or where MXC reports the boundary unavailable; any other failure is a red. It downloads uv, a CPython and wheels.
/// </remarks>
[Category(TestCategories.ExternalInfra)]
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.26100.0")]
public sealed class ComputeSandboxAppContainerWindowsTests
{
    private const string CanarySecret = "compute-canary-secret";

    [Test]
    public async Task RunPython_UnderTheAppContainer_ImportsTheClosure_AndCannotReachOutsideItsJail()
    {
        Skip.Unless(OperatingSystem.IsWindows(), "this suite proves the Windows AppContainer boundary.");
        Skip.Unless(ManagedPythonPins.IsCurrentPlatformSupported, "Managed Python has no pin for this platform (Linux x64 and Windows x64 only).");
        var launcher = new SandboxLauncher(new HostSandboxContainmentProbe());
        var containment = launcher.Containment;
        Skip.Unless(containment.SupportsAppContainerBoundary, $"the AppContainer boundary is unavailable here: {containment.AppContainerBoundaryUnavailableReason}");

        var scratch = Directory.CreateTempSubdirectory("xe-cw-").FullName;
        // Beside the jails, under the container root every jail of every provider lives in, and granted to none of them.
        var canary = Path.Combine(SandboxPaths.ContainerRoot, $"compute-canary-{Guid.NewGuid():N}.txt");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var http = new HttpClient();
        var toolchain = new ManagedPythonToolchain(Path.Combine(scratch, "python"));
        using var environment = new ComputePythonEnvironment(new UvBinaryAcquirer(http),
            PythonToolRunner.ForCurrentPlatform(),
            NullLogger<ComputePythonEnvironment>.Instance,
            Path.Combine(scratch, "compute-runtime"),
            RepositoryPaths.Combine("tools/compute"),
            toolchain);
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            launcher,
            previewPolicy: new PreviewsOn());
        try
        {
            Directory.CreateDirectory(SandboxPaths.ContainerRoot);
            await File.WriteAllTextAsync(canary, CanarySecret);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var runtime = await environment.GetRuntimeAsync();
            // uv's real Windows venv has a launcher, not a link: only the pyvenv.cfg route can prove it, which a failed rebuild relies on.
            AssertEx.True(ComputePythonEnvironment.RunsOnStore(runtime.InterpreterPath, toolchain.PythonInstallDirectory),
                "the provisioned venv must read as running on the shared store, or a failed rebuild cannot keep it in service");
            var venvRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtime.InterpreterPath)!)!;
            var prefixProbe = Path.Combine(venvRoot, "xe-write-probe.txt");
            AssertEx.NotNull(runtime.BaseInterpreterPath, "on Windows the gateway must run the store interpreter, not uv's trampoline (N-60)");

            var rendered = await CreateGateway(provider, environment).ExecuteAsync(new ComputeRunToolRequest
            {
                Code = BuildScript(canary, toolchain.PinnedUvExecutable, port) + BuildTempScript(venvRoot, runtime.InterpreterPath)
            });

            AssertEx.True(rendered.StartsWith("exit_code: 0\nstdout:\n", StringComparison.Ordinal), rendered);
            AssertEx.True(rendered.EndsWith("\nstderr:\n", StringComparison.Ordinal), $"the call must leave stderr empty (N-60): {rendered}");
            AssertEx.Contains(rendered, "PREFIX-IS-VENV True");
            AssertEx.Contains(rendered, "EXE-IS-LAUNCHER True");
            AssertEx.Contains(rendered, "EXISTS TEMP True");
            AssertEx.Contains(rendered, "EXISTS TMP True");
            AssertEx.Contains(rendered, "EXISTS LOCALAPPDATA True");
            AssertEx.Contains(rendered, "TEMP WRITABLE");

            AssertEx.Contains(rendered, "INTEGRAL x**2");
            AssertEx.Contains(rendered, "SUM 6");
            AssertEx.Contains(rendered, "QUAD 9.0");
            AssertEx.Contains(rendered, "CANARY DENIED");
            AssertEx.Contains(rendered, "UV_BINARY DENIED");
            AssertEx.False(rendered.Contains(CanarySecret, StringComparison.Ordinal), "the canary beside the jail was readable");
            AssertEx.Contains(rendered, "INSIDE HOME True");
            AssertEx.Contains(rendered, "INSIDE USERPROFILE True");
            AssertEx.Contains(rendered, "INSIDE TEMPDIR True");
            AssertEx.Contains(rendered, "PREFIX DENIED");
            AssertEx.Contains(rendered, "LOOPBACK DENIED");

            // Host side: the negatives above must not be explained by a missing canary, a deleted venv file or a dropped listener.
            AssertEx.True(File.Exists(canary), "the canary must be invisible inside the sandbox, not deleted");
            AssertEx.False(File.Exists(prefixProbe), "the script wrote into the venv");
            AssertEx.False(listener.Pending(), "the script reached the host loopback listener");
        }
        finally
        {
            listener.Stop();
            File.Delete(canary);
            DeleteQuietly(scratch);
        }
    }

    private static string BuildScript(string canary, string uvBinary, int port) =>
        $$"""
          import os, socket, sys, tempfile
          import numpy, scipy.integrate, sympy

          x = sympy.symbols("x")
          print("INTEGRAL", sympy.integrate(2 * x, x))
          print("SUM", numpy.array([1, 2, 3]).sum())
          print("QUAD", round(scipy.integrate.quad(lambda t: t * t, 0, 3)[0], 6))


          def probe(label, path):
              try:
                  with open(path) as handle:
                      print(label, "READ", handle.read())
              except OSError as error:
                  print(label, "DENIED", type(error).__name__)


          probe("CANARY", {{ToPythonLiteral(canary)}})
          probe("UV_BINARY", {{ToPythonLiteral(uvBinary)}})

          jail = os.path.normcase(os.path.realpath(os.getcwd()))


          def inside(path):
              path = os.path.normcase(os.path.realpath(path))
              return path == jail or path.startswith(jail + os.sep)


          for label, value in (("HOME", os.environ.get("HOME")), ("USERPROFILE", os.environ.get("USERPROFILE")), ("TEMPDIR", tempfile.gettempdir())):
              print("INSIDE", label, bool(value) and inside(value), value)

          try:
              with open(os.path.join(sys.prefix, "xe-write-probe.txt"), "w") as handle:
                  handle.write("x")
              print("PREFIX WRITABLE", sys.prefix)
          except OSError as error:
              print("PREFIX DENIED", type(error).__name__)

          try:
              socket.create_connection(("127.0.0.1", {{port}}), timeout=5).close()
              print("LOOPBACK CONNECTED")
          except OSError as error:
              print("LOOPBACK DENIED", type(error).__name__)
          """;

    // Appended to the boundary script: the two faults (TEMP folders that do not exist, the trampoline's stderr line) and what the
    // store interpreter with __PYVENV_LAUNCHER__ must keep (the venv prefix, the launcher as sys.executable).
    private static string BuildTempScript(string venvRoot, string launcher) =>
        $$"""

          for name in ("TEMP", "TMP", "LOCALAPPDATA"):
              value = os.environ.get(name)
              print("EXISTS", name, bool(value) and os.path.isdir(value), value)

          print("PREFIX-IS-VENV", os.path.normcase(sys.prefix) == os.path.normcase({{ToPythonLiteral(venvRoot)}}))
          print("EXE-IS-LAUNCHER", os.path.normcase(sys.executable) == os.path.normcase({{ToPythonLiteral(launcher)}}))
          try:
              with open(os.path.join(os.environ["TEMP"], "xe-temp-probe.txt"), "w") as handle:
                  handle.write("x")
              print("TEMP WRITABLE")
          except (OSError, KeyError) as error:
              print("TEMP DENIED", type(error).__name__)
          """;

    private static ComputeToolGateway CreateGateway(IAgentSandboxRuntimeProvider provider, IComputePythonEnvironment environment) =>
        new(provider,
            new StubIdentityProvider(),
            environment,
            Options.Create(new ComputeOptions
            {
                Enabled = true,
                // A cold numpy and scipy import from a fresh venv on a shared runner can be slow; the default 30 s would flake there.
                TimeoutSeconds = 120
            }),
            Options.Create(new LocalContainerOptions()),
            Options.Create(new AgentToolPipelineOptions()),
            SeededNodeRuntimeSettings.FromSeed("Compute:Enabled", value: true),
            NullLogger<ComputeToolGateway>.Instance);

    // A backslash in a Windows path would otherwise be read as an escape inside the script's string literal.
    private static string ToPythonLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void DeleteQuietly(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only cache entry left behind in %TEMP% is not a test failure.
        }
    }

    private sealed class PreviewsOn : IExecutionPreviewPolicy
    {
        public bool PreviewMechanismsEnabled => true;
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner-windows",
                NodeId = "node-windows"
            });
    }
}
