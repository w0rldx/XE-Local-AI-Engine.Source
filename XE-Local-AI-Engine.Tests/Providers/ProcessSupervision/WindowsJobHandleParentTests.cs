namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The handle parent's ordering, rollback, pending cleanup and helper retirement (ADR 0025), through a fake native seam.</summary>
/// <remarks>The real kernel32 calls are proven by <c>WindowsJobSandboxWindowsTests</c>.</remarks>
[Category(TestCategories.Unit)]
public sealed class WindowsJobHandleParentTests
{
    private static readonly WindowsJobStartInfo Command = new()
    {
        CommandLine = "cmd.exe /d /c exit 0",
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };

    [Test]
    public async Task ALaunch_ParksTheThreeEnds_ListsOnlyTheParkedValues_AndClosesEachInTheHelperOnce()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var ends = Ends();

        using var first = Launch(parent, ends);
        using var second = Launch(parent, Ends());

        // The second launch first checks that the retained helper still lives, then parks into it.
        AssertEx.Equal("CreateParentJob#1,CreateHelper#1,IsProcessInJob#1,Park#1,Park#2,Park#3,CreateCommand#1,Unpark#1,Unpark#2,Unpark#3,"
                       + "Wait#1,Park#4,Park#5,Park#6,CreateCommand#2,Unpark#4,Unpark#5,Unpark#6",
            string.Join(',', native.Calls));
        AssertEx.Equal(1, native.Count("CreateHelper"), "one helper serves every launch while it lives");
        AssertEx.Equal(ends[0], native.ParkedEnds[0]);
        AssertEx.Equal(ends[1], native.ParkedEnds[1]);
        AssertEx.Equal(ends[2], native.ParkedEnds[2]);
        AssertEx.Equal(3, native.Listed[0].Length);
        AssertEx.Equal(6, native.Listed.SelectMany(static values => values).Distinct().Count(), "every launch lists its own fresh parked values");
        AssertEx.Equal(6, native.Count("Unpark"), "each parked value is closed in the helper exactly once");
        AssertEx.Empty(native.HelperTable);
        AssertEx.True(native.IsAlive(native.Helpers[0]), "the helper is never ended by a successful launch");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("throws")]
    [Arguments("not-a-member")]
    public void AHelperThatFailsVerification_IsEndedAndConfirmed_AndTheNextLaunchMakesANewOneInTheSameParentJob(string failure)
    {
        var native = new FakeWindowsJobLaunchNative();
        if (failure == "throws")
        {
            native.FailAt.Add("IsProcessInJob#1");
        }
        else
        {
            native.HelperIsMember = false;
        }

        using var parent = new WindowsJobHandleParent(native);

        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not verify its helper");
        AssertEx.False(native.IsAlive(native.Helpers[0]), "the unverified helper is ended");
        AssertEx.True(native.Helpers[0].IsClosed, "its handle is released after a confirmed exit");
        AssertEx.Equal(0, native.Count("Park"), "nothing is parked in an unverified helper");
        native.HelperIsMember = true;
        using var next = Launch(parent, Ends());
        AssertEx.Equal(2, native.Helpers.Count);
        AssertEx.Equal(1, native.ParentJobsCreated, "the parent job outlives a failed helper");
    }

    [Test]
    public async Task AParentJobThatCannotBeCreated_RefusesTheLaunch_WithNothingCreated()
    {
        var native = new FakeWindowsJobLaunchNative();
        native.FailAt.Add("CreateParentJob#1");
        using var parent = new WindowsJobHandleParent(native);

        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not create its parent job");
        AssertEx.Equal(0, native.Count("CreateHelper"));
        await Task.CompletedTask;
    }

    [Test]
    public void APartialPark_ClosesTheEarlierValue_CreatesNothing_AndKeepsALiveHelper()
    {
        var native = new FakeWindowsJobLaunchNative();
        native.FailAt.Add("Park#2");
        using var parent = new WindowsJobHandleParent(native);

        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not park the command's stdout in its helper");
        AssertEx.Equal(0, native.Count("CreateCommand"));
        AssertEx.Equal(1, native.Count("Unpark"), "only the value that exists is closed");
        AssertEx.Empty(native.HelperTable);
        AssertEx.True(native.IsAlive(native.Helpers[0]), "a live helper survives a plain park failure");
        using var next = Launch(parent, Ends());
        AssertEx.Equal(1, native.Helpers.Count);
    }

    [Test]
    public async Task AFailedCreate_WithTheHelperAlive_KeepsTheHelper_AndClosesEveryParkedValue()
    {
        var native = new FakeWindowsJobLaunchNative();
        native.FailAt.Add("CreateCommand#1");
        using var parent = new WindowsJobHandleParent(native);

        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not create the command");
        AssertEx.Equal(3, native.Count("Unpark"));
        AssertEx.Empty(native.HelperTable);
        AssertEx.Empty(native.Commands);
        AssertEx.True(native.IsAlive(native.Helpers[0]));
        await Task.CompletedTask;
    }

    [Test]
    public void AParkFailure_AfterTheHelperDied_RetiresIt_AndTheNextLaunchMakesANewOne()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        native.FailAt.Add("Park#2");
        native.Before = call =>
        {
            if (call == "Park#2")
            {
                native.Exit(native.Helpers[0]);
            }
        };

        _ = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.True(native.Helpers[0].IsClosed, "a dead helper is retired at once");
        using var next = Launch(parent, Ends());
        AssertEx.Equal(2, native.Helpers.Count);
    }

    [Test]
    public void AnUnparkFailure_EndsTheCreatedCommand_AndRetiresTheHelper_AfterTryingEveryValue()
    {
        var native = new FakeWindowsJobLaunchNative();
        native.FailAt.Add("Unpark#1");
        using var parent = new WindowsJobHandleParent(native);

        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not close a parked handle in its helper");
        AssertEx.Equal(3, native.Count("Unpark"), "a failed close never stops the others");
        var command = native.Commands.Single();
        AssertEx.False(native.IsAlive(command), "the created command is ended directly");
        AssertEx.True(command.IsClosed, "and released once its exit is confirmed");
        AssertEx.True(native.Threads.All(static thread => thread.IsClosed), "every thread handle is closed");
        AssertEx.False(native.IsAlive(native.Helpers[0]), "a helper that may still hold a parked value is retired");
        using var next = Launch(parent, Ends());
        AssertEx.Equal(2, native.Helpers.Count);
    }

    [Test]
    public void AnUnconfirmedExit_IsHeld_AndRefusesEveryLaunch_UntilItsExitIsConfirmed()
    {
        var native = new FakeWindowsJobLaunchNative();
        native.FailAt.Add("Unpark#1");
        native.Before = call =>
        {
            if (call == "Unpark#1")
            {
                native.Hold(native.Commands[0]);
            }
        };
        using var parent = new WindowsJobHandleParent(native);

        var first = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));
        var parks = native.Count("Park");
        var second = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(first.Message, "exit not confirmed");
        AssertEx.Contains(second.Message, "a process the handle parent ended has not exited (command, pid");
        AssertEx.Equal(parks, native.Count("Park"), "a refused launch parks nothing");
        AssertEx.Equal(1, native.Helpers.Count, "no new helper starts beside an unconfirmed process");
        AssertEx.False(native.Commands[0].IsClosed, "a held process keeps its handle");

        native.Release(native.Commands[0]);
        using var third = Launch(parent, Ends());

        AssertEx.True(native.Commands[0].IsClosed, "released only after a signalled wait");
        AssertEx.Equal(2, native.Commands.Count);
    }

    [Test]
    public void AnAdoptFailure_EndsTheCommand_ClosesItsThread_AndRethrowsTheOriginal()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var ends = Ends();

        var refused = AssertEx.Throws<IOException>(() =>
            parent.CreateCommand(NewJob(), Command, ends[0], ends[1], ends[2], static _ => throw new IOException("injected: a stream could not be built")));

        AssertEx.Equal("injected: a stream could not be built", refused.Message);
        var command = native.Commands.Single();
        AssertEx.False(native.IsAlive(command));
        AssertEx.True(command.IsClosed);
        AssertEx.True(native.Threads.All(static thread => thread.IsClosed));
        AssertEx.True(native.IsAlive(native.Helpers[0]), "the helper did nothing wrong and stays");
    }

    [Test]
    public async Task AnAdoptFailure_WhoseCommandWillNotExit_JoinsTheReason_AndBlocksTheNextLaunch()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var ends = Ends();

        var refused = AssertEx.Throws<InvalidOperationException>(() => parent.CreateCommand(NewJob(),
            Command,
            ends[0],
            ends[1],
            ends[2],
            created =>
            {
                native.Hold(created.Process);
                throw new IOException("injected: a stream could not be built");
            }));

        AssertEx.Contains(refused.Message, "injected: a stream could not be built");
        AssertEx.Contains(refused.Message, "exit not confirmed");
        AssertEx.True(refused.InnerException is IOException, "the original failure is kept as the cause");
        AssertEx.Contains(AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends())).Message, "has not exited");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AHelperThatDiedBetweenLaunches_IsReplaced_InTheSameParentJob()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        using var first = Launch(parent, Ends());

        native.Exit(native.Helpers[0]);
        using var second = Launch(parent, Ends());

        AssertEx.Equal(2, native.Helpers.Count);
        AssertEx.True(native.Helpers[0].IsClosed, "the dead helper's handle is released");
        AssertEx.Equal(1, native.ParentJobsCreated);
        await Task.CompletedTask;
    }

    [Test]
    public void AHelperWhoseWaitFails_IsRetired_AndTheLaunchIsRefused()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        using var first = Launch(parent, Ends());

        native.WaitsFail = true;
        var refused = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));

        AssertEx.Contains(refused.Message, "the handle parent could not check its helper");
        AssertEx.False(native.IsAlive(native.Helpers[0]), "a helper that cannot be checked is ended");
        AssertEx.Contains(refused.Message, "exit not confirmed");
        native.WaitsFail = false;
        using var next = Launch(parent, Ends());
        AssertEx.Equal(2, native.Helpers.Count);
    }

    [Test]
    public void TerminateAndConfirm_TakesTheHandle_AndHoldsAnUnconfirmedOne_UntilItExits()
    {
        var native = new FakeWindowsJobLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var confirmed = Launch(parent, Ends());
        var held = Launch(parent, Ends());

        AssertEx.Null(parent.TerminateAndConfirm(confirmed.ReleaseProcess(), confirmed.ProcessId));
        native.Hold(held.Process);
        var reason = parent.TerminateAndConfirm(held.ReleaseProcess(), held.ProcessId);
        confirmed.Dispose();
        held.Dispose();

        AssertEx.True(native.Commands[0].IsClosed, "a confirmed exit releases the handle");
        AssertEx.Contains(AssertEx.NotNull(reason), "exit not confirmed");
        AssertEx.False(native.Commands[1].IsClosed, "the child's Dispose never closes a handed-off process");
        _ = AssertEx.Throws<InvalidOperationException>(() => Launch(parent, Ends()));
        native.Release(native.Commands[1]);
        using var next = Launch(parent, Ends());
        AssertEx.True(native.Commands[1].IsClosed);
    }

    [Test]
    public async Task Dispose_ClosesTheParentJob_TheHelper_AndEveryHeldProcess()
    {
        var native = new FakeWindowsJobLaunchNative();
        var parent = new WindowsJobHandleParent(native);
        var held = Launch(parent, Ends());
        native.Hold(held.Process);
        _ = parent.TerminateAndConfirm(held.ReleaseProcess(), held.ProcessId);
        held.Dispose();

        parent.Dispose();

        AssertEx.Equal(0, native.OpenHandles, "the parent job, the helper and the held process are all closed");
        await Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Fake handles own no native resource.")]
    private static WindowsJobChild Launch(WindowsJobHandleParent parent, SafeHandle[] ends) =>
        parent.CreateCommand(NewJob(), Command, ends[0], ends[1], ends[2], static created => new WindowsJobChild
        {
            Process = created.Process,
            Thread = created.Thread,
            ProcessId = created.ProcessId,
            StandardInput = new MemoryStream(),
            StandardOutput = new MemoryStream(),
            StandardError = new MemoryStream()
        });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Fake handles own no native resource.")]
    private static SafeHandle NewJob() => new FakeWindowsJobApi.FakeHandle("job");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Fake handles own no native resource.")]
    private static SafeHandle[] Ends() =>
        [new FakeWindowsJobApi.FakeHandle("stdin-read"), new FakeWindowsJobApi.FakeHandle("stdout-write"), new FakeWindowsJobApi.FakeHandle("stderr-write")];
}
