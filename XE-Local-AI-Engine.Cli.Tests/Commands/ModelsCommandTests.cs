namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ModelsCommandTests
{
    [Test]
    public void Merge_AModelRunningInTwoRoles_AppearsOnce()
    {
        var rows = ModelsCommand.Merge(Installed("repo/chat:Q4"), Running(Process("repo/chat:Q4", "ready"), Process("REPO/chat:q4", "ready")));

        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal("repo/chat:Q4", rows[0].ModelName);
        AssertEx.True(rows[0].Installed);
        AssertEx.True(rows[0].Running);
    }

    [Test]
    public void Merge_ALoadedModelTheInstalledListLacks_FollowsOnceAsNotInstalled()
    {
        var rows = ModelsCommand.Merge(Installed("repo/chat:Q4"), Running(Process("repo/gone:Q4", "ready"), Process("repo/gone:Q4", "loading")));

        AssertEx.Equal(expected: 2, rows.Count);
        AssertEx.False(rows[0].Running);
        AssertEx.Equal("repo/gone:Q4", rows[1].ModelName);
        AssertEx.False(rows[1].Installed);
        AssertEx.True(rows[1].Running);
    }

    [Test]
    public void Merge_LoadedModelsTheInstalledListLacks_FollowSortedByName()
    {
        var rows = ModelsCommand.Merge(Installed("repo/chat:Q4"), Running(Process("repo/zeta:Q4", "ready"), Process("REPO/beta:Q4", "ready"), Process("repo/alpha:Q4", "ready")));

        AssertEx.Equal("repo/chat:Q4,repo/alpha:Q4,REPO/beta:Q4,repo/zeta:Q4", string.Join(",", rows.Select(static row => row.ModelName)));
    }

    [Test]
    public void Merge_AnExitedProcess_DoesNotCountAsRunning()
    {
        var rows = ModelsCommand.Merge(Installed("repo/chat:Q4"), Running(Process("repo/chat:Q4", RunningModel.ExitedDetailCode), Process("repo/gone:Q4", RunningModel.ExitedDetailCode)));

        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal("repo/chat:Q4", rows[0].ModelName);
        AssertEx.False(rows[0].Running);
    }

    [Test]
    public void Merge_AModelWithAnExitedAndALiveProcess_IsRunning()
    {
        var rows = ModelsCommand.Merge(Installed("repo/chat:Q4"), Running(Process("repo/chat:Q4", RunningModel.ExitedDetailCode), Process("repo/chat:Q4", "ready")));

        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.True(rows[0].Running);
    }

    private static LocalModelList Installed(params string[] names) =>
        new()
        {
            Items =
            [
                .. names.Select(static name => new LocalModel
                {
                    ModelName = name
                })
            ]
        };

    private static RunningModelList Running(params RunningModel[] processes) =>
        new()
        {
            Items = processes
        };

    private static RunningModel Process(string modelName, string detailCode) =>
        new()
        {
            ModelName = modelName,
            DetailCode = detailCode
        };
}
