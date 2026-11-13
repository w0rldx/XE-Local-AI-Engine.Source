namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The optional output observer on <see cref="ProcessSandboxRuntimeProvider" />, against REAL child processes: the line split
///     is the runtime's, so it is measured here rather than assumed, and the observer must leave the result untouched.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ProcessSandboxOutputObserverTests
{
    [Test]
    [RunOn(OS.Linux)]
    public async Task Execute_WithObserver_ReceivesStdoutAndStderrLines_SplitOnLoneCarriageReturnToo()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var observer = new RecordingSandboxOutputObserver();

        var result = await provider.ExecuteAsync(handle, Shell("observe-split", @"printf 'a\rb\r\nc\n'; printf 'e1\ne2' >&2", observer));

        AssertEx.True(result.Completed, result.StandardError);
        // A progress bar redraws with a lone \r; each redraw is its own line, as the captured result also splits it.
        AssertEx.Equal("a|b|c", string.Join('|', observer.StandardOutput));
        AssertEx.Equal("e1|e2", string.Join('|', observer.StandardError));
        AssertEx.Equal("a\nb\nc\n", result.StandardOutput);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Execute_ResultIsIdenticalWithAndWithoutObserver_AndTheObserverSeesLinesPastTheCaptureCap()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        // 200,000 lines of 81 bytes is about 16 MB, four times the 4 MB per-stream capture cap; some `yes` builds report the
        // broken pipe on stderr when head exits, so its stderr is dropped.
        const string Script = "yes 'αααααααααααααααααααααααααααααααααααααααα' 2>/dev/null | head -n 200000; echo done >&2; exit 3";
        var observer = new RecordingSandboxOutputObserver();

        var plain = await provider.ExecuteAsync(handle, Shell("observe-plain", Script, observer: null));
        var observed = await provider.ExecuteAsync(handle, Shell("observe-on", Script, observer));

        AssertEx.Equal(expected: 3, plain.ExitCode);
        AssertEx.True(plain.StandardOutputTruncated, "the script must outgrow the capture cap for this comparison to mean anything");
        AssertEx.Equal(plain.ExitCode, observed.ExitCode);
        AssertEx.Equal(plain.Completed, observed.Completed);
        AssertEx.Equal(plain.StandardOutput, observed.StandardOutput);
        AssertEx.Equal(plain.StandardError, observed.StandardError);
        AssertEx.Equal(plain.StandardOutputTruncated, observed.StandardOutputTruncated);
        AssertEx.Equal(plain.StandardErrorTruncated, observed.StandardErrorTruncated);
        AssertEx.Equal(expected: 200_000, observer.StandardOutput.Count);
        AssertEx.Equal("done", string.Join('|', observer.StandardError));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Execute_WithThrowingObserver_CompletesNormally_AndDetachesTheObserverAfterTheFirstFault()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var observer = new ThrowingObserver();

        var result = await provider.ExecuteAsync(handle, Shell("observe-throw", "printf 'one\\ntwo\\nthree\\n'", observer));

        AssertEx.True(result.Completed, result.StandardError);
        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Equal("one\ntwo\nthree\n", result.StandardOutput);
        AssertEx.Equal(expected: 1, observer.Calls);
    }

    private static SandboxCommandRequest Shell(string executionId, string script, ISandboxOutputObserver? observer)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = executionId,
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = TimeSpan.FromSeconds(60),
            OutputObserver = observer
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedContainmentProbe(SandboxContainment.None)));
    }

    private static SandboxCreateRequest CreateRequest()
    {
        var key = new SandboxAttachKey
        {
            OwnerUserId = "owner-observer",
            NodeId = "node-observer",
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = 1
        };
        return new SandboxCreateRequest
        {
            AttachKey = key,
            RuntimeProfile = key.RuntimeProfile,
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        };
    }

    private sealed class FixedContainmentProbe : ISandboxContainmentProbe
    {
        public FixedContainmentProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }

    private sealed class ThrowingObserver : ISandboxOutputObserver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnLine(SandboxOutputSource source, string line)
        {
            _ = Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("observer failure under test");
        }
    }
}
