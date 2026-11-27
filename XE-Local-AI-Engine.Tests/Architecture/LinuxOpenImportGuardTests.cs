namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     libc <c>open</c>/<c>openat</c>/<c>openat2</c> is declared only in <c>LinuxOpenFlags</c>, and no file that opens holds a literal
///     <c>O_NOFOLLOW</c>/<c>O_DIRECTORY</c> value.
/// </summary>
/// <remarks>
///     <c>0x20000</c> is <c>O_NOFOLLOW</c> only on x86-64 and <c>O_LARGEFILE</c> on arm64, so a copied literal silently drops the no-follow
///     guarantee there. A raw <c>syscall</c> import used for <c>openat2</c> (number 437 or an <c>open_how</c>) must take its flags from
///     <c>LinuxOpenFlags</c>; other <c>syscall</c> imports, such as the pidfd ones, are out of scope. Production projects only, comments stripped.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class LinuxOpenImportGuardTests
{
    private const string HelperFile = "XE-Local-AI-Engine.Providers.Abstractions/LinuxOpenFlags.cs";
    private const string Openat2File = "XE-Local-AI-Engine.Client.Application/Services/Sandbox/Implementation/Launch/Isolation/SandboxTrustedDescriptorOpener.cs";

    private const string SyntheticOpenat2 = """
                                            [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
                                            private static extern long syscall(long number, int fd, byte[] path, byte[] how, ulong size);
                                            private const long OpenAt2 = 437;
                                            private const ulong OpenDirectory = 0x10000;
                                            """;

    private const string SyntheticOpen = """
                                         [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
                                         private static partial int Open(byte[] path, int flags);
                                         """;

    [Test]
    public void LibcOpen_IsDeclaredOnlyInLinuxOpenFlags_AndNoOpeningFileHoldsAFlagLiteral()
    {
        var scanned = ProductionSources();
        var offenders = scanned.SelectMany(file => Offenders(file.Key, file.Value)).ToList();

        AssertEx.Empty(offenders,
            "Open Linux files through LinuxOpenFlags.Open with its NoFollow/Directory values, which follow the process architecture:\n"
            + string.Join('\n', offenders));
        AssertEx.True(scanned.TryGetValue(HelperFile, out var helper) && DeclaresOpen(helper),
            "LinuxOpenFlags must declare the libc open import, or this guard checks nothing.");
        AssertEx.True(scanned.TryGetValue(Openat2File, out var openat2) && UsesOpenat2Syscall(openat2),
            "The openat2 syscall rule must recognize its one production user, or that half of the guard checks nothing.");
    }

    [Test]
    public void AnOpenat2SyscallWithALiteralFlag_IsCaught()
    {
        AssertEx.NotEmpty(Offenders("Some/File.cs", SyntheticOpenat2));
    }

    [Test]
    public void ALibcOpenOutsideTheHelper_IsCaught()
    {
        AssertEx.NotEmpty(Offenders("Some/File.cs", SyntheticOpen));
    }

    /// <summary>The pidfd syscalls open no file, so they carry no open flag to get wrong.</summary>
    [Test]
    public void ThePidFdSyscalls_PassTheGuard()
    {
        const string pidFdFile = "XE-Local-AI-Engine.Providers.Abstractions/LinuxPidFd.cs";

        AssertEx.Empty(Offenders(pidFdFile, ProductionSources()[pidFdFile]));
    }

    private static IEnumerable<string> Offenders(string relative, string source)
    {
        if (relative == HelperFile)
        {
            yield break;
        }

        var declaresOpen = DeclaresOpen(source);
        var usesOpenat2 = UsesOpenat2Syscall(source);
        if (declaresOpen)
        {
            yield return $"{relative}: declares a libc open import";
        }

        if (usesOpenat2 && !source.Contains("LinuxOpenFlags.", StringComparison.Ordinal))
        {
            yield return $"{relative}: an openat2 syscall whose flags do not come from LinuxOpenFlags";
        }

        if ((declaresOpen || usesOpenat2 || source.Contains("LinuxOpenFlags.", StringComparison.Ordinal)) && FlagLiteral().IsMatch(source))
        {
            yield return $"{relative}: a literal O_NOFOLLOW/O_DIRECTORY value beside a file open";
        }
    }

    private static bool DeclaresOpen(string source) =>
        OpenEntryPoint().IsMatch(source) || OpenDeclaration().IsMatch(source);

    private static bool UsesOpenat2Syscall(string source) =>
        SyscallEntryPoint().IsMatch(source) && Openat2Marker().IsMatch(source);

    private static Dictionary<string, string> ProductionSources() =>
        EnforcedSourceFiles.All()
                           .Where(file => !IsTestProject(file.Relative))
                           .ToDictionary(file => file.Relative, file => SourceCommentStripper.StripComments(File.ReadAllText(file.Path)), StringComparer.Ordinal);

    private static bool IsTestProject(string relative)
    {
        var project = relative.Split('/')[0];
        return project.EndsWith("Tests", StringComparison.Ordinal) || project.Contains(".Testing", StringComparison.Ordinal);
    }

    [GeneratedRegex("""EntryPoint\s*=\s*"(open|open64|openat|openat64|openat2)"\s*[,)]""")]
    private static partial Regex OpenEntryPoint();

    [GeneratedRegex(@"\b(extern|partial)\s+\w+\s+(open|open64|openat|openat64|openat2)\s*\(")]
    private static partial Regex OpenDeclaration();

    [GeneratedRegex("""EntryPoint\s*=\s*"syscall"\s*[,)]""")]
    private static partial Regex SyscallEntryPoint();

    [GeneratedRegex(@"\b437\b|open_how|OpenHow")]
    private static partial Regex Openat2Marker();

    [GeneratedRegex(@"\b0[xX]0*(20000|10000|8000|4000)\b")]
    private static partial Regex FlagLiteral();
}
