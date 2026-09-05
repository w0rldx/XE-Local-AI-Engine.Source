namespace XE_Local_AI_Engine.Tests.Testing;

using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Proves that TUnit's built-in platform gates really skip, so the ~120 tests that carry
///     <c>[RunOn]</c>/<c>[ExcludeOn]</c> instead of an <c>if (!OperatingSystem.IsX()) return;</c> guard cannot go back
///     to reporting green on a platform where they did not run.
///     <para>
///         The four cases are symmetric, so this file is the same proof on every OS: on any host exactly two of them
///         run and two report <b>skipped</b>. Each body asserts the platform it was gated to — so if a gate ever stops
///         engaging, the body runs on the wrong OS and the test FAILS rather than passing quietly, which is the exact
///         failure mode the guards had.
///     </para>
///     <para>
///         Nothing here is a custom attribute: TUnit 1.65 ships <c>RunOnAttribute</c> and <c>ExcludeOnAttribute</c>
///         (both <c>SkipAttribute</c> subclasses that call <c>TestRegisteredContext.SetSkipped</c> at registration),
///         and they carry a reason naming the platform — "Test is restricted to run on the following operating
///         systems: `Linux`.". A guard that also depends on something other than the OS keeps
///         <c>Skip.Unless(...)</c> in the body on top of the attribute.
///     </para>
/// </summary>
public sealed class PlatformSkipTests
{
    [Test]
    [RunOn(OS.Windows)]
    public void RunOnWindows_BodyOnlyEverRunsOnWindows()
    {
        AssertEx.True(OperatingSystem.IsWindows(),
            "[RunOn(OS.Windows)] let the body run on a non-Windows host: the platform skip did not engage.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public void RunOnLinux_BodyOnlyEverRunsOnLinux()
    {
        AssertEx.True(OperatingSystem.IsLinux(),
            "[RunOn(OS.Linux)] let the body run on a non-Linux host: the platform skip did not engage.");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public void ExcludeOnWindows_BodyNeverRunsOnWindows()
    {
        AssertEx.False(OperatingSystem.IsWindows(),
            "[ExcludeOn(OS.Windows)] let the body run on Windows: the platform skip did not engage.");
    }

    [Test]
    [ExcludeOn(OS.Linux)]
    public void ExcludeOnLinux_BodyNeverRunsOnLinux()
    {
        AssertEx.False(OperatingSystem.IsLinux(),
            "[ExcludeOn(OS.Linux)] let the body run on Linux: the platform skip did not engage.");
    }
}
