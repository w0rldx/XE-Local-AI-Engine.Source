namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The code-owned command-profile catalog: two .NET profiles, a pytest profile and the git-only fallback, and no
///     user-defined ones.
/// </summary>
/// <remarks>
///     A custom profile would let a repository describe its own build and test commands, which needs the container
///     isolation that is not built yet; and a <c>cargo</c> profile on a host without <c>cargo</c> would only upgrade
///     "missing solution" into "command not found".
/// </remarks>
internal static class DevelopmentCommandProfileCatalog
{
    public const string DotnetSlnx = "dotnet-slnx";
    public const string DotnetCsproj = "dotnet-csproj";

    /// <summary>The profile for a Python repository whose tests run under the operator's own host <c>pytest</c>.</summary>
    /// <remarks>
    ///     Nothing installs, downloads or pins pytest: the operator installs it for the host <c>python3</c>, and a host
    ///     without it fails validation with a message saying so rather than reading as a pass.
    /// </remarks>
    public const string PythonPytest = "python-pytest";

    /// <summary>The profile for a repository with no detected .NET build system.</summary>
    /// <remarks>
    ///     Its validation profile is the whitespace check alone, which is honest rather than false-green: the gate
    ///     reports exactly what it verified, and the profile is surfaced to the operator at confirmation, so "no
    ///     build system was detected" is a visible decision rather than a silent downgrade.
    /// </remarks>
    public const string GenericGit = "generic-git";

    /// <summary>
    ///     Bumped whenever the command set, argument vectors, timeouts or protected paths of any profile below change,
    ///     and equally whenever <see cref="DependencyManifestPaths" /> or <see cref="PythonRunnerPaths" /> gains or loses a rule.
    /// </summary>
    /// <remarks>
    ///     That set is a gate applied to every attempt run under a profile this catalog issued. A stored profile whose
    ///     <c>(ProfileId, ProfileVersion)</c> still resolves here but whose canonical content no longer matches is
    ///     rejected rather than silently re-interpreted — see <see cref="ResolveStored" />.
    /// </remarks>
    public const string CurrentVersion = "v3";

    /// <summary>
    ///     The files whose content decides what <c>restore</c> resolves; changing one fails deterministic validation
    ///     with <see cref="DevelopmentValidationFailureCodes.DependencyManifestChanged" />, a verdict rather than a
    ///     security exception.
    /// </summary>
    /// <remarks>
    ///     The set is the whole of the control: a packaging system missing from it is a hole, not a gap in coverage,
    ///     and adding one is a source change here plus a <see cref="CurrentVersion" /> bump. It is not a field of
    ///     <see cref="DevelopmentCommandProfile" /> — code-owned and identical for every profile, so the canonical
    ///     digest would invalidate every stored profile to say nothing new. <c>Directory.Build.props</c> and
    ///     <c>.targets</c> are in because either can carry a <c>PackageReference</c>.
    /// </remarks>
    public static readonly string[] DependencyManifestPaths =
    [
        "**/*.csproj",
        "**/Directory.Packages.props",
        "**/Directory.Build.props",
        "**/Directory.Build.targets",
        "**/packages.lock.json",
        "**/global.json",
        "**/NuGet.config",
        "**/package.json",
        "**/package-lock.json",
        "**/pnpm-lock.yaml",
        "**/yarn.lock",
        "**/Cargo.toml",
        "**/Cargo.lock",
        "**/requirements*.txt",
        "**/pyproject.toml",
        "**/uv.lock",
        "**/poetry.lock"
    ];

    /// <summary>
    ///     Paths the agent may create but may not modify or delete once they existed at <c>BaseCommit</c> — the
    ///     test-write policy.
    /// </summary>
    /// <remarks>
    ///     The filename rules carry most of the weight — <c>*Tests.cs</c> matches 543 files across two real
    ///     repositories with zero false positives — while the directory rules exist only to close the shared-helper
    ///     hole and are scoped to <c>*.cs</c>, so an agent can still add a package reference to an existing test
    ///     project. What each pattern is grounded in, and what is deliberately left out, is in
    ///     <c>docs/wiki/12-security-and-privacy.md</c> ("The test-write policy's protected-path set").
    /// </remarks>
    public static readonly string[] DefaultProtectedPaths =
    [
        "**/*Tests.cs",
        "**/*.test.ts",
        "**/*.test.tsx",
        "**/*.Tests/**/*.cs",
        "**/*.UnitTests/**/*.cs",
        "**/*.IntegrationTests/**/*.cs",
        "**/*.E2ETests/**/*.cs",
        "tests/**/*.cs",
        "test/**/*.cs"
    ];

    /// <summary>
    ///     The pytest profile's additions to <see cref="DefaultProtectedPaths" />: pytest's own discovery patterns and
    ///     the conventional <c>tests/</c> tree. Conftest and option files are in <see cref="PythonRunnerPaths" />.
    /// </summary>
    public static readonly string[] PythonProtectedPaths =
    [
        .. DefaultProtectedPaths,
        "tests/**/*.py",
        "**/test_*.py",
        "**/*_test.py"
    ];

    /// <summary>
    ///     Paths an attempt under <see cref="PythonPytest" /> may not add, modify, delete or rename: modules that would
    ///     shadow pytest or its imports, compiled bytecode and conftest files. <see cref="IsRunnerPath" /> adds the name rule.
    /// </summary>
    /// <remarks>
    ///     Unlike the protected set, adding one is refused too: each runs inside the runner, so a new one can forge the
    ///     summary the gate reads. Configuration files need no entry: the launch reads none of them.
    /// </remarks>
    public static readonly string[] PythonRunnerPaths =
    [
        "**/pytest.py",
        "**/pytest/**",
        "**/_pytest/**",
        "**/py.py",
        "**/pluggy.py",
        "**/pluggy/**",
        "**/conftest.py",
        "**/*.pyc",
        "**/__pycache__/**"
    ];

    /// <summary>The paths no attempt under this profile may touch in any way, including by adding them.</summary>
    public static IReadOnlyList<string> RunnerPathsFor(string profileId) =>
        string.Equals(profileId, PythonPytest, StringComparison.Ordinal) ? PythonRunnerPaths : [];

    /// <summary>
    ///     Whether a change to <paramref name="repositoryRelativePath" /> touches what the profile's runner loads: a
    ///     <see cref="RunnerPathsFor" /> glob, or a module or package at the repository root or under <c>src/</c>
    ///     named like a standard-library module or a pytest dependency.
    /// </summary>
    /// <remarks>
    ///     Both roots sit after the standard library on <c>sys.path</c> but before site-packages, so neither a pytest
    ///     dependency nor a module the platform lacks (<c>_winapi</c> off Windows) is safe by order alone. A non-module
    ///     file or a directory without an <c>__init__</c> cannot shadow one, so <c>test/test_pdb.py</c> stays writable.
    /// </remarks>
    public static bool IsRunnerPath(string profileId, string repositoryRelativePath)
    {
        var path = repositoryRelativePath.Replace('\\', '/').TrimStart('/');
        if (RunnerPathsFor(profileId).Any(pattern => DevelopmentGlob.IsMatch(pattern, path)))
        {
            return true;
        }

        if (!string.Equals(profileId, PythonPytest, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split('/');
        var top = segments.Length > 1 && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var isModuleFile = segments.Length == top + 1 && PythonModuleSuffixes.Any(suffix => segments[top].EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        var isPackageInit = segments.Length == top + 2 && segments[top + 1].StartsWith("__init__.", StringComparison.OrdinalIgnoreCase);
        return (isModuleFile || isPackageInit) && ShadowableModuleNames.Contains(segments[top].Split('.')[0]);
    }

    /// <summary>
    ///     Builds the profile for a code-owned id. <paramref name="buildTarget" /> is the repository-relative solution or
    ///     project file the .NET commands operate on, and must be null for <see cref="GenericGit" /> and
    ///     <see cref="PythonPytest" />.
    /// </summary>
    public static DevelopmentCommandProfile Materialize(string profileId,
        string? buildTarget,
        string? templateId = null,
        string? importDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var normalizedTarget = NormalizeTarget(profileId, buildTarget);
        return profileId switch
        {
            GenericGit => new DevelopmentCommandProfile(GenericGit,
                CurrentVersion,
                templateId,
                BuildTarget: null,
                importDigest,
                [GitStatus(), GitDiffCheck()],
                [DevelopmentCommandIds.GitDiffCheck],
                DefaultProtectedPaths,
                IsCustom: false).Validated(),
            PythonPytest => new DevelopmentCommandProfile(PythonPytest,
                CurrentVersion,
                templateId,
                BuildTarget: null,
                importDigest,
                [
                    GitStatus(),
                    GitDiffCheck(),

                    // -B and no cache plugin keep __pycache__ and .pytest_cache out of the coder's patch; the
                    // rootdir and confcutdir pins keep conftest discovery inside the workspace.
                    new DevelopmentProfileCommand(DevelopmentCommandIds.PythonPytest,
                        PythonExecutable,
                        ["-I", "-B", "-c", PytestBootstrap, "-q", "-p", "no:cacheprovider", "--rootdir=.", "--confcutdir=.", "--import-mode=importlib"],
                        TestTimeoutSeconds)
                ],
                [DevelopmentCommandIds.GitDiffCheck, DevelopmentCommandIds.PythonPytest],
                PythonProtectedPaths,
                IsCustom: false).Validated(),
            DotnetSlnx or DotnetCsproj => new DevelopmentCommandProfile(profileId,
                CurrentVersion,
                templateId,
                normalizedTarget,
                importDigest,
                [
                    GitStatus(),
                    GitDiffCheck(),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetRestore,
                        "dotnet",
                        ["restore", normalizedTarget!],
                        RestoreTimeoutSeconds),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetBuildRelease,
                        "dotnet",
                        ["build", normalizedTarget!, "--configuration", "Release", "--no-restore"],
                        BuildTimeoutSeconds),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetTestRelease,
                        "dotnet",
                        ["test", normalizedTarget!, "--configuration", "Release", "--no-build", "--max-parallel-test-modules", "1"],
                        TestTimeoutSeconds)
                ],
                [
                    DevelopmentCommandIds.GitDiffCheck,
                    DevelopmentCommandIds.DotnetRestore,
                    DevelopmentCommandIds.DotnetBuildRelease,
                    DevelopmentCommandIds.DotnetTestRelease
                ],
                DefaultProtectedPaths,
                IsCustom: false).Validated(),
            _ => throw new DevelopmentWorkspaceSecurityException("The requested Development command profile is not in the code-owned catalog.")
        };
    }

    /// <summary>
    ///     Resolves a profile snapshotted into the database, re-deriving it from the code-owned catalog to prove the
    ///     definition has not changed underneath it.
    /// </summary>
    /// <remarks>
    ///     Editing a profile's commands without bumping <see cref="CurrentVersion" /> would silently start every
    ///     already-created project on different commands under a version string claiming otherwise, and the stored
    ///     bytes are the operator-confirmed agreement, so rejecting is the only safe answer. This does not touch the
    ///     three artifact protocol versions (<c>development-workspace-v1</c>, <c>development-validation-v2</c>,
    ///     <c>development-review-v1</c>) or their gates: those describe shape compatibility, this command content.
    /// </remarks>
    public static DevelopmentCommandProfile ResolveStored(string? storedProfileJson)
    {
        if (string.IsNullOrWhiteSpace(storedProfileJson))
        {
            throw new DevelopmentWorkspaceSecurityException("The Development project has no command profile. Re-register the repository to detect and confirm one.");
        }

        var stored = DevelopmentCommandProfile.FromCanonicalJson(storedProfileJson);
        if (stored.IsCustom)
        {
            throw new DevelopmentWorkspaceSecurityException("Custom Development command profiles are not supported yet; they require the container sandbox.");
        }

        var expected = Materialize(stored.ProfileId, stored.BuildTarget, stored.TemplateId, stored.ImportDigest);
        if (!string.Equals(expected.ProfileVersion, stored.ProfileVersion, StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException(
                $"This Development project's command profile was produced by catalog version {stored.ProfileVersion}, and this engine runs version {CurrentVersion}. Re-create this Development project to adopt the current command profiles.");
        }

        if (!expected.ToCanonicalUtf8().AsSpan().SequenceEqual(stored.ToCanonicalUtf8()))
        {
            throw new DevelopmentWorkspaceSecurityException("The code-owned Development command profile changed without a version bump, so the stored profile can no longer be trusted.");
        }

        return stored;
    }

    private static readonly string[] PythonModuleSuffixes = [".py", ".pyc", ".pyo", ".pyw", ".pyd", ".so"];

    // simplified: Python 3.14's sys.stdlib_module_names, private names included, the interpreter-startup hooks site imports
    // (sitecustomize, usercustomize), plus pytest's own imports, frozen here. Ceiling: a later Python's new module is missing.
    private static readonly HashSet<string> ShadowableModuleNames = new(("abc annotationlib antigravity argparse array ast asyncio atexit base64 bdb binascii bisect builtins bz2 "
                                                                         + "cProfile calendar cmath cmd code codecs codeop collections colorsys compileall compression concurrent "
                                                                         + "configparser contextlib contextvars copy copyreg csv ctypes curses dataclasses datetime dbm decimal difflib "
                                                                         + "dis doctest email encodings ensurepip enum errno faulthandler fcntl filecmp fileinput fnmatch fractions "
                                                                         + "ftplib functools gc genericpath getopt getpass gettext glob graphlib grp gzip hashlib heapq hmac html http "
                                                                         + "idlelib imaplib importlib inspect io ipaddress itertools json keyword linecache locale logging lzma mailbox "
                                                                         + "marshal math mimetypes mmap modulefinder msvcrt multiprocessing netrc nt ntpath nturl2path numbers opcode "
                                                                         + "operator optparse os pathlib pdb pickle pickletools pkgutil platform plistlib poplib posix posixpath pprint "
                                                                         + "profile pstats pty pwd py_compile pyclbr pydoc pydoc_data pyexpat queue quopri random re readline reprlib "
                                                                         + "resource rlcompleter runpy sched secrets select selectors shelve shlex shutil signal site smtplib socket "
                                                                         + "socketserver sqlite3 sre_compile sre_constants sre_parse ssl stat statistics string stringprep struct "
                                                                         + "subprocess symtable sys sysconfig syslog tabnanny tarfile tempfile termios textwrap this threading time "
                                                                         + "timeit tkinter token tokenize tomllib trace traceback tracemalloc tty turtle turtledemo types typing "
                                                                         + "unicodedata unittest urllib uuid venv warnings wave weakref webbrowser winreg winsound wsgiref xml xmlrpc "
                                                                         + "zipapp zipfile zipimport zlib zoneinfo"
                                                                         + " __future__ _abc _aix_support _android_support _apple_support _ast _ast_unparse _asyncio _bisect _blake2"
                                                                         + " _bz2 _codecs _codecs_cn _codecs_hk _codecs_iso2022 _codecs_jp _codecs_kr _codecs_tw _collections"
                                                                         + " _collections_abc _colorize _compat_pickle _contextvars _csv _ctypes _curses _curses_panel _datetime _dbm"
                                                                         + " _decimal _elementtree _frozen_importlib _frozen_importlib_external _functools _gdbm _hashlib _heapq _hmac"
                                                                         + " _imp _interpchannels _interpqueues _interpreters _io _ios_support _json _locale _lsprof _lzma _markupbase"
                                                                         + " _md5 _multibytecodec _multiprocessing _opcode _opcode_metadata _operator _osx_support _overlapped _pickle"
                                                                         + " _posixshmem _posixsubprocess _py_abc _py_warnings _pydatetime _pydecimal _pyio _pylong _pyrepl _queue"
                                                                         + " _random _remote_debugging _scproxy _sha1 _sha2 _sha3 _signal _sitebuiltins _socket _sqlite3 _sre _ssl"
                                                                         + " _stat _statistics _string _strptime _struct _suggestions _symtable _sysconfig _thread _threading_local"
                                                                         + " _tkinter _tokenize _tracemalloc _types _typing _uuid _warnings _weakref _weakrefset _winapi _wmi _zoneinfo"
                                                                         + " _zstd"
                                                                         + " sitecustomize usercustomize"
                                                                         + " pytest _pytest py pluggy iniconfig packaging pygments colorama exceptiongroup tomli argcomplete").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved through the host PATH like <c>dotnet</c> and <c>git</c>, never a bundled interpreter.</summary>
    private const string PythonExecutable = "python3";

    /// <summary>Every executable a profile in this catalog names, which the bubblewrap chain must be able to find.</summary>
    /// <remarks>
    ///     <c>DevelopmentWorkspaceProvider</c> binds their host install trees read-only and composes the jail <c>PATH</c> from this list,
    ///     so a profile command naming anything else would run "not found" inside the filesystem boundary.
    /// </remarks>
    internal static readonly string[] SandboxedExecutables = [AgentHomeGit.Executable, "dotnet", PythonExecutable];

    /// <summary>
    ///     The engine-owned pytest launch, run by an isolated (<c>-I</c>) interpreter: pytest is imported before the
    ///     workspace and its <c>src/</c> join <c>sys.path</c>, after the standard library and before site-packages.
    /// </summary>
    /// <remarks>
    ///     <c>-c os.devnull</c> replaces every pytest configuration file, the repository's and any ancestor's, so
    ///     neither can add options or plugins; host plugins do not autoload either. The repository's own pytest
    ///     settings are deliberately ignored.
    /// </remarks>
    private const string PytestBootstrap =
        "import os, site, sys, pytest; root = os.getcwd(); sp = set(site.getsitepackages()); "
        + "i = next((n for n, p in enumerate(sys.path) if p in sp), len(sys.path)); sys.path[i:i] = [root, os.path.join(root, 'src')]; "
        + "os.environ['PYTEST_DISABLE_PLUGIN_AUTOLOAD'] = '1'; sys.argv[1:1] = ['-c', os.devnull]; raise SystemExit(pytest.main())";

    private const int GitTimeoutSeconds = 120;
    private const int RestoreTimeoutSeconds = 900;
    private const int BuildTimeoutSeconds = 1800;
    private const int TestTimeoutSeconds = 1800;

    private static DevelopmentProfileCommand GitStatus() =>
        new(DevelopmentCommandIds.GitStatus,
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("status", "--short", "--branch", "--untracked-files=all", "--", "."),
            GitTimeoutSeconds);

    private static DevelopmentProfileCommand GitDiffCheck() =>
        new(DevelopmentCommandIds.GitDiffCheck,
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("diff", "--check", "HEAD", "--", "."),
            GitTimeoutSeconds);

    /// <summary>
    ///     The build target is a repository-relative path that becomes a literal process argument, so it is confined by
    ///     the same rules as any agent-supplied workspace path before it can reach an argument vector.
    /// </summary>
    private static string? NormalizeTarget(string profileId, string? buildTarget)
    {
        if (profileId is GenericGit or PythonPytest)
        {
            return buildTarget is null
                ? null
                : throw new DevelopmentWorkspaceSecurityException("This Development command profile does not take a build target.");
        }

        if (string.IsNullOrWhiteSpace(buildTarget))
        {
            throw new DevelopmentWorkspaceSecurityException("A .NET Development command profile requires a solution or project build target.");
        }

        var confined = DevelopmentWorkspaceSecurity.Confine(buildTarget, allowRoot: false);
        if (!confined.IsAccepted)
        {
            throw new DevelopmentWorkspaceSecurityException(confined.RejectionReason
                                                            ?? "The Development build target path was rejected.");
        }

        var expectedExtension = string.Equals(profileId, DotnetSlnx, StringComparison.Ordinal)
            ? new[]
            {
                ".slnx",
                ".sln"
            }
            : [".csproj"];
        if (!expectedExtension.Any(extension => confined.RelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DevelopmentWorkspaceSecurityException("The Development build target does not match the selected command profile.");
        }

        return confined.RelativePath;
    }
}
