namespace XE_Local_AI_Engine.Tests.Compute;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <para>
///         LIVE end-to-end coverage of the compute tool: the real uv-provisioned interpreter, the real process sandbox,
///         the real gateway. It proves the properties the unit suites cannot — that the pinned closure really imports,
///         that egress really is denied inside the jail, and that a non-terminating script really is killed at the
///         timeout rather than hanging the turn.
///     </para>
///     <para>
///         <b>Opt-in, and it SKIPS only when the host genuinely lacks the mechanism.</b> The first run provisions a
///         real venv (a network download of tens of megabytes) and creates real systemd user scopes and mount
///         namespaces, so it is gated on <c>XE_COMPUTE_LIVE=1</c> and on a root-owned <c>bwrap</c> existing. Once one
///         does, this host is expected to isolate and anything else FAILS: a gate that skipped on any probe failure
///         would turn every regression in the chain into a silent green run, which is the failure mode
///         <see cref="XE_Local_AI_Engine.Tests.Sandbox.SandboxIsolationLiveTests" /> was written around.
///     </para>
/// </summary>
public sealed class ComputeSandboxLiveTests : IDisposable
{
    /// <summary>Set to <c>1</c> to allow this suite to provision the pinned compute venv and spawn real processes.</summary>
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    // One environment for the whole class, so the venv provision is paid once rather than per test. Constructed even on
    // a skipped run (it does nothing until an interpreter is asked for), which keeps the disposal contract simple.
    private readonly HttpClient _httpClient = new();
    private readonly ComputePythonEnvironment _environment;

    public ComputeSandboxLiveTests()
    {
        _environment = new ComputePythonEnvironment(_httpClient, NullLogger<ComputePythonEnvironment>.Instance);
    }

    public void Dispose()
    {
        _environment.Dispose();
        _httpClient.Dispose();
    }

    [Test]
    public async Task RunPython_ImportsThePinnedClosure_AndReturnsWhatTheScriptPrinted()
    {
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import numpy, scipy, sympy
                   x = sympy.symbols('x')
                   print(sympy.integrate(2 * x, x))
                   print(numpy.array([1, 2, 3]).sum())
                   """
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        // sympy must be importable AND usable — this is what proves the venv provision end to end.
        AssertEx.Contains(rendered, "x**2");
        AssertEx.Contains(rendered, "6");
    }

    [Test]
    public async Task RunPython_WhenTheScriptRaises_ReportsANonZeroExitAndTheTraceback()
    {
        // The failure path is a feature, not an edge case: the persona's instructions tell the model to read the
        // traceback and fix its script, which only works if the traceback actually reaches it on stderr.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "raise ValueError('nope')" });

        AssertEx.False(rendered.Contains("exit_code: 0", StringComparison.Ordinal), "a raising script must not report success");
        AssertEx.Contains(rendered, "ValueError");
        AssertEx.Contains(rendered, "nope");
    }

    [Test]
    public async Task RunPython_WhenTheScriptDoesNotTerminate_IsKilledAtTheTimeout()
    {
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider, new ComputeOptions { TimeoutSeconds = 5 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "while True: pass" });

        AssertEx.Contains(rendered, "did not finish within 5s");
        AssertEx.Contains(rendered, "terminated");
    }

    [Test]
    public async Task RunPython_WhenOutputIsHuge_IsTruncatedWithTheMarker()
    {
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider, new ComputeOptions { MaxOutputBytes = 2048 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print('a' * 200000)" });

        AssertEx.Contains(rendered, "…[output truncated]");
        AssertEx.True(rendered.Length < 20000, "a runaway print must not reach the model in full");
    }

    [Test]
    public async Task RunPython_WhenTheScriptFillsTheJail_IsKilledAtTheComputeDiskCeiling_NotTheNodeWideOne()
    {
        // The node-wide ceiling stays at its default (512 MiB) while compute asks for 4 MiB, so anything that stops
        // this script is the PER-SANDBOX ceiling — the whole point of the option. A generous timeout keeps the two
        // controls distinguishable: a timeout kill would report the wall clock, not the disk ceiling.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider,
            new ComputeOptions
            {
                TimeoutSeconds = 60,
                MaxJailDiskBytes = 4L * 1024 * 1024
            });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import pathlib, time
                   chunk = b"0" * (1024 * 1024)
                   for index in range(64):
                       pathlib.Path(f"fill-{index}.bin").write_bytes(chunk)
                       time.sleep(0.1)
                   print("FILLED")
                   """
        });

        AssertEx.Contains(rendered, "disk ceiling");
        AssertEx.Contains(rendered, (4L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        AssertEx.False(rendered.Contains("FILLED", StringComparison.Ordinal),
            "a script that blows the compute disk ceiling must not run to completion");
    }

    [Test]
    public async Task RunPython_WhenTheScriptFillsItsTempDirectory_IsStillKilledAtTheComputeDiskCeiling()
    {
        // The same ceiling, reached through TMPDIR instead of the working directory. While the scratch sat beside the
        // venv under the node data directory it was OUTSIDE the jail the watchdog walks, so this exact script — the
        // obvious one for anything doing real work, since that is where `tempfile` writes — filled the host disk with
        // the ceiling reporting nothing. It is a hole that reads as a bound, which is worse than having no bound.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider,
            new ComputeOptions
            {
                TimeoutSeconds = 60,
                MaxJailDiskBytes = 4L * 1024 * 1024
            });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import pathlib, tempfile, time
                   # Through tempfile.gettempdir() rather than the raw variable, so this really is the path a library
                   # would write to. That it resolves to the engine's scratch is asserted separately, on a run that is
                   # allowed to finish — an over-cap kill returns no stdout to assert on.
                   chunk = b"0" * (1024 * 1024)
                   for index in range(64):
                       pathlib.Path(tempfile.gettempdir(), f"fill-{index}.bin").write_bytes(chunk)
                       time.sleep(0.1)
                   print("FILLED")
                   """
        });

        AssertEx.Contains(rendered, "disk ceiling");
        AssertEx.False(rendered.Contains("FILLED", StringComparison.Ordinal),
            "a script that fills its TMPDIR must hit the same ceiling as one filling its working directory");
    }

    [Test]
    public async Task RunPython_PointsHomeAndTmpdirAtSeparateWritableDirectoriesInsideTheSandbox()
    {
        // The child's own view of the facts the metering depends on. Inside the boundary the two scratch directories
        // are the SANDBOX's paths, not host paths: HOME is /work/home, under the writable tree, and TMPDIR is /tmp,
        // which is a mount of a jail subdirectory rather than a tmpfs — so it does not show up under /work while
        // still costing the same disk ceiling. Both must exist, be writable, and be distinct.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os, pathlib, tempfile
                   work = pathlib.Path.cwd()
                   home = pathlib.Path(os.environ["HOME"])
                   temp = pathlib.Path(os.environ["TMPDIR"])
                   print("CWD", work)
                   print("HOME", home)
                   print("TMPDIR", temp)
                   # What a library actually writes to has to be the same directory, or metering TMPDIR proves nothing.
                   print("TEMPFILE_AGREES", pathlib.Path(tempfile.gettempdir()) == temp)
                   print("HOME_UNDER_WORK", work in home.parents)
                   print("DISTINCT", home != temp)
                   (home / "probe.txt").write_text("home")
                   (temp / "probe.txt").write_text("temp")
                   (work / "probe.txt").write_text("work")
                   print("WRITABLE", (home / "probe.txt").exists(), (temp / "probe.txt").exists(), (work / "probe.txt").exists())
                   """
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        AssertEx.Contains(rendered, "CWD /work");
        AssertEx.Contains(rendered, "HOME /work/home");
        AssertEx.Contains(rendered, "TMPDIR /tmp");
        AssertEx.Contains(rendered, "TEMPFILE_AGREES True");
        AssertEx.Contains(rendered, "HOME_UNDER_WORK True");
        AssertEx.Contains(rendered, "DISTINCT True");
        AssertEx.Contains(rendered, "WRITABLE True True True");
    }

    [Test]
    public async Task RunPython_CannotSeeWhatAnEarlierCallWrote()
    {
        // The advertised contract is that a call leaves nothing behind. ONE provider and ONE gateway across both calls
        // is the point: that is the shape a real turn has, and the shape that used to reattach to the same jail and the
        // same scratch directory, handing the second script the first one's files.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        // Both the working directory (the jail) and HOME (the scratch) — the two writable surfaces a script has.
        var wrote = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os, pathlib
                   pathlib.Path("leaked-cwd.txt").write_text("from call one")
                   pathlib.Path(os.environ["HOME"], "leaked-home.txt").write_text("from call one")
                   print("WROTE")
                   """
        });
        AssertEx.Contains(wrote, "exit_code: 0");
        AssertEx.Contains(wrote, "WROTE");

        var read = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os, pathlib
                   print("CWD", pathlib.Path("leaked-cwd.txt").exists())
                   print("HOME", pathlib.Path(os.environ["HOME"], "leaked-home.txt").exists())
                   """
        });

        AssertEx.Contains(read, "exit_code: 0");
        AssertEx.Contains(read, "CWD False");
        AssertEx.Contains(read, "HOME False");
    }

    [Test]
    public async Task RunPython_ConcurrentCalls_GetTheirOwnJailAndSurviveEachOther()
    {
        // Two genuinely overlapping invocations through ONE gateway — the shape a research loop produces. With a
        // constant attach key the registry handed both the SAME live jail: they shared a working directory, and the
        // first to finish killed it under the one still running. Each script writes a file named after itself, sleeps
        // past the other's start, then lists its own directory, so a shared jail shows up as the sibling's file.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        Task<string> RunAsync(string tag)
        {
            // Only the .txt files: the jail root also holds this call's own HOME and TMPDIR now, and listing those
            // would say nothing about whether the two calls shared a jail — which is the whole question here.
            return gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = $"""
                        import pathlib, time
                        pathlib.Path("{tag}.txt").write_text("{tag}")
                        time.sleep(2)
                        print("SAW", sorted(p.name for p in pathlib.Path(".").glob("*.txt")))
                        """
            });
        }

        var results = await Task.WhenAll(RunAsync("alpha"), RunAsync("beta"));

        // Both must COMPLETE: under one shared jail the loser was torn down mid-run.
        AssertEx.Contains(results[0], "exit_code: 0");
        AssertEx.Contains(results[1], "exit_code: 0");
        AssertEx.Contains(results[0], "SAW ['alpha.txt']");
        AssertEx.Contains(results[1], "SAW ['beta.txt']");
    }

    [Test]
    public async Task RunPython_CannotReachTheNetwork()
    {
        // The empty network namespace is bwrap's own --unshare-net under the isolated mode, not the separate
        // unshare(1) mechanism this test used to gate on — so the gate is the boundary's, like every other test here.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        // A connect to a LIVE local listener would be the strongest probe, but any egress at all is enough here and a
        // DNS-free literal keeps the failure unambiguous: with an empty netns even loopback is unreachable.
        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import socket
                   try:
                       socket.create_connection(("1.1.1.1", 53), timeout=3)
                       print("REACHED")
                   except OSError as error:
                       print("DENIED", type(error).__name__)
                   """
        });

        AssertEx.Contains(rendered, "DENIED");
        AssertEx.False(rendered.Contains("REACHED", StringComparison.Ordinal),
            "a script inside the compute jail must not reach the network");
    }

    [Test]
    public async Task RunPython_CannotWriteIntoTheProvisionedVenv_EvenAfterChmoddingItBack()
    {
        // site-packages is imported by every later call, so a script that can drop a module there turns one approval
        // into code that runs on all the following ones. The chmod is the point of this version: the venv's cleared
        // write bits are defence in depth, and the script OWNS those inodes — before the mount boundary existed, a
        // deliberate os.chmod restored them and the write went through. Under a read-only bind the ownership is
        // irrelevant: the chmod itself fails, and so does the write, with EROFS rather than EPERM.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var attempt = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import errno, os, pathlib, stat, sys, sysconfig


                   def attempt(label, action):
                       try:
                           action()
                           print(label, "WROTE")
                       except OSError as error:
                           print(label, "DENIED", errno.errorcode.get(error.errno, error.errno))


                   purelib = pathlib.Path(sysconfig.get_paths()["purelib"])
                   interpreter = pathlib.Path(sys.executable)
                   attempt("CHMOD_PURELIB", lambda: os.chmod(purelib, 0o777))
                   attempt("CHMOD_INTERPRETER", lambda: os.chmod(interpreter, 0o777))
                   attempt("PLANT_MODULE", lambda: (purelib / "xe_trojan.py").write_text("import os"))
                   attempt("OVERWRITE_INTERPRETER", lambda: interpreter.write_bytes(b"#!/bin/sh\n"))
                   attempt("PLANT_IN_NUMPY", lambda: (purelib / "numpy" / "xe_trojan.py").write_text("import os"))
                   """
        });

        AssertEx.Contains(attempt, "exit_code: 0");
        AssertEx.Contains(attempt, "CHMOD_PURELIB DENIED EROFS");
        AssertEx.Contains(attempt, "CHMOD_INTERPRETER DENIED EROFS");
        AssertEx.Contains(attempt, "PLANT_MODULE DENIED EROFS");
        AssertEx.Contains(attempt, "OVERWRITE_INTERPRETER DENIED EROFS");
        AssertEx.Contains(attempt, "PLANT_IN_NUMPY DENIED EROFS");
        AssertEx.False(attempt.Contains("WROTE", StringComparison.Ordinal),
            "a script must not be able to drop a module into the venv every later call imports");

        // The lockdown must not have broken the closure it protects: a read-only venv that cannot import numpy would
        // be a worse bug than the one being fixed.
        var reuse = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "import numpy; print('IMPORT OK', numpy.ndarray)"
        });

        AssertEx.Contains(reuse, "exit_code: 0");
        AssertEx.Contains(reuse, "IMPORT OK");
    }

    [Test]
    public async Task RunPython_ImportsNumpyAndScipy_AndRunsARealBlasCall()
    {
        // Importing is not the same as WORKING: numpy and scipy load compiled extension modules and a bundled BLAS
        // out of the venv, which the read-only bind has to make both readable and executable. A symmetric
        // eigendecomposition exercises that path end to end — and its eigenvalues are checkable, so a silently wrong
        // BLAS is a failure rather than a pass.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        // The venv root as the engine knows it: <cache>/venv/.venv, two directories above bin/python.
        var venvRoot = Path.GetDirectoryName(Path.GetDirectoryName((await _environment.GetRuntimeAsync()).InterpreterPath));

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import numpy, scipy, sys
                   from scipy import linalg

                   rng = numpy.random.default_rng(1234)
                   a = rng.standard_normal((200, 200))
                   spd = a @ a.T + 200 * numpy.eye(200)
                   values = numpy.linalg.eigvalsh(spd)
                   print("SHAPE", values.shape)
                   print("SPD", bool(values.min() > 0))
                   # Trace equals the sum of the eigenvalues for any symmetric matrix; if the BLAS returned nonsense
                   # this fails while the shape assertion above would still have passed.
                   print("TRACE_AGREES", bool(numpy.isclose(values.sum(), numpy.trace(spd), rtol=1e-8)))
                   print("SCIPY_SOLVE", bool(numpy.allclose(linalg.solve(spd, spd @ numpy.ones(200)), numpy.ones(200), atol=1e-6)))
                   print("PREFIX_IS_VENV", sys.prefix != sys.base_prefix)
                   print("PREFIX", sys.prefix)
                   """
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        AssertEx.Contains(rendered, "SHAPE (200,)");
        AssertEx.Contains(rendered, "SPD True");
        AssertEx.Contains(rendered, "TRACE_AGREES True");
        AssertEx.Contains(rendered, "SCIPY_SOLVE True");
        // sys.prefix is the bound venv, not the managed CPython underneath it. Exec'ing the real interpreter binary
        // instead of the venv's symlink would resolve `import numpy` against an empty site-packages and this is the
        // assertion that notices.
        AssertEx.Contains(rendered, "PREFIX_IS_VENV True");
        AssertEx.Contains(rendered, $"PREFIX {AssertEx.NotNull(venvRoot)}");
    }

    [Test]
    public async Task RunPython_CannotReadTheHostFilesystem_NotUnderHomeAndNotUnderTheNodeDataDirectory()
    {
        // The two canaries are the two places that matter and they fail differently if the boundary is wrong: $HOME
        // is where the operator's own secrets live, and the node data directory is where the engine's do — including
        // the compute cache root the interpreter trees are bound OUT of, which is exactly the tree a too-wide bind
        // would have exposed. Both must be ENOENT inside while still existing outside.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var homeCanary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $".xe-compute-canary-{Guid.NewGuid():N}");
        var dataCanary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine",
            "compute-runtime",
            $"canary-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(homeCanary, "host-home");
        Directory.CreateDirectory(Path.GetDirectoryName(dataCanary)!);
        await File.WriteAllTextAsync(dataCanary, "host-data");
        try
        {
            var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = $"""
                        import errno, pathlib


                        def probe(label, path):
                            try:
                                print(label, "READ", pathlib.Path(path).read_text())
                            except OSError as error:
                                print(label, "DENIED", errno.errorcode.get(error.errno, error.errno))


                        probe("HOME_CANARY", {ToPythonLiteral(homeCanary)})
                        probe("DATA_CANARY", {ToPythonLiteral(dataCanary)})
                        probe("UV_CACHE", {ToPythonLiteral(Path.Combine(Path.GetDirectoryName(dataCanary)!, "uv-cache"))})
                        probe("PASSWD_SHADOW", "/etc/shadow")
                        accounts = [line.split(":")[0] for line in pathlib.Path("/etc/passwd").read_text().splitlines()]
                        print("ETC_ACCOUNTS", ",".join(sorted(accounts)))
                        """
            });

            AssertEx.Contains(rendered, "exit_code: 0");
            AssertEx.Contains(rendered, "HOME_CANARY DENIED ENOENT");
            AssertEx.Contains(rendered, "DATA_CANARY DENIED ENOENT");
            // The compute cache root is the PARENT of the two bound trees, and it also holds the uv download cache
            // and the lockfile state. Binding it instead of its two children would have handed all of that over.
            AssertEx.Contains(rendered, "UV_CACHE DENIED ENOENT");
            AssertEx.Contains(rendered, "PASSWD_SHADOW DENIED ENOENT");
            // The /etc the script does see is the INVENTED one — root plus the single synthetic account the jail maps
            // — and not the machine's account database, which a plain read-only bind of the host /etc would have been.
            AssertEx.Contains(rendered, "ETC_ACCOUNTS root,xe");
            AssertEx.False(rendered.Contains(Environment.UserName, StringComparison.Ordinal),
                "the operator's own account name must not appear in the jail's account database");
            AssertEx.False(rendered.Contains("host-home", StringComparison.Ordinal));
            AssertEx.False(rendered.Contains("host-data", StringComparison.Ordinal));

            // Still there on the host side. Without this the ENOENT above would be equally consistent with the test
            // never having written the canaries at all, which is the way a boundary assertion goes quietly green.
            AssertEx.True(File.Exists(homeCanary), "the canary must be invisible inside the sandbox, not deleted");
            AssertEx.True(File.Exists(dataCanary));
        }
        finally
        {
            File.Delete(homeCanary);
            File.Delete(dataCanary);
        }
    }

    [Test]
    public async Task RunPython_PinsTheNumericLibraryThreadCount_ToTheConfiguredLimit()
    {
        // The libraries size their pools from the HOST's core count, read out of /proc, which is not what the
        // sandbox's CPU quota allows. The variables are set by the isolated chain from the create request's thread
        // limit, and this is the assertion that the compute option actually reaches it.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider, new ComputeOptions { ThreadLimit = 3 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os
                   for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
                       print(name, os.environ.get(name))
                   """
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        AssertEx.Contains(rendered, "OMP_NUM_THREADS 3");
        AssertEx.Contains(rendered, "OPENBLAS_NUM_THREADS 3");
        AssertEx.Contains(rendered, "MKL_NUM_THREADS 3");
        AssertEx.Contains(rendered, "NUMEXPR_NUM_THREADS 3");
    }

    [Test]
    public async Task RunPython_WhenTheProviderCannotIsolate_RefusesWithoutProvisioningOrCreatingAJail()
    {
        // The refusal path, against a provider that advertises everything EXCEPT the boundary. It needs no host
        // capability of its own — which is why it is the one test here that only needs the opt-in — and it asserts
        // the ordering the production check depends on: nothing is provisioned and no jail is created.
        RequireOptIn();
        var provider = new UnisolatedSandboxProvider();
        var environment = new RecordingEnvironment();
        var gateway = new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            environment,
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLogger<ComputeToolGateway>.Instance);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        AssertEx.Contains(rendered, "run_python rejected");
        AssertEx.Contains(rendered, "isolate");
        AssertEx.False(environment.Requested, "a host without the boundary must not provision an interpreter it can never run");
        AssertEx.False(provider.CreateAttempted, "and must not get as far as creating a jail");
    }

    private ComputeToolGateway CreateGateway(IAgentSandboxRuntimeProvider provider, ComputeOptions? options = null)
    {
        return new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            _environment,
            Options.Create(options ?? new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLogger<ComputeToolGateway>.Instance);
    }

    private static ProcessSandboxRuntimeProvider CreateHostProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System);
    }

    private static void RequireOptIn()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("the compute runtime is Linux-only (the uv pin and the process runner both are).");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            Skip($"set {EnabledVariable}=1 to allow this suite to provision the pinned compute venv and spawn real processes.");
        }
    }

    /// <summary>
    ///     Skips only when the host has no mechanism at all, and FAILS when it has one but the boundary does not hold.
    ///     <para>
    ///         Every test in this suite goes through it, because <c>run_python</c> now REFUSES on a host that cannot
    ///         isolate — so without the boundary there is no run to assert anything about, and a suite that skipped on
    ///         a broken chain would report the tool's central guarantee as untested-but-green.
    ///     </para>
    /// </summary>
    private static void RequireIsolationCapableHost()
    {
        RequireOptIn();

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            Skip("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        var containment = new HostSandboxContainmentProbe().Containment;
        if (!containment.SupportsFilesystemIsolation)
        {
            AssertEx.True(condition: false,
                $"this host has a trusted bwrap, so the compute filesystem boundary must hold; the probe reported: {containment.FilesystemIsolationUnavailableReason}");
        }
    }

    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    /// <summary>
    ///     Renders a host path as a Python string literal. The canary paths are generated, but they still go into a
    ///     script as data, and a backslash or a quote in a home directory would otherwise change what the script says.
    /// </summary>
    private static string ToPythonLiteral(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner-live", "node-live"));
        }
    }

    /// <summary>
    ///     A provider that advertises every containment flag EXCEPT the filesystem boundary, and records whether the
    ///     gateway tried to create a sandbox anyway. It exists here rather than only in the unit suite because the
    ///     refusal is the tool's central fail-closed behaviour and is worth asserting beside the live proof that the
    ///     boundary works when it is present.
    /// </summary>
    private sealed class UnisolatedSandboxProvider : IAgentSandboxRuntimeProvider
    {
        public bool CreateAttempted { get; private set; }

        public string ProviderName => "unisolated";

        public SandboxProviderCapabilities Capabilities =>
            SandboxProviderCapabilities.SupportsNetworkPolicy
            | SandboxProviderCapabilities.SupportsResourceLimits
            | SandboxProviderCapabilities.SupportsKill;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateAttempted = true;
            throw new NotSupportedException("the refusal must land before this is ever reached");
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Records whether provisioning was asked for — the cost the early refusal exists to avoid.</summary>
    private sealed class RecordingEnvironment : IComputePythonEnvironment
    {
        public bool Requested { get; private set; }

        public Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default)
        {
            Requested = true;

            return Task.FromResult(new ComputePythonRuntime("/never/used", ["/never/used"]));
        }
    }
}
