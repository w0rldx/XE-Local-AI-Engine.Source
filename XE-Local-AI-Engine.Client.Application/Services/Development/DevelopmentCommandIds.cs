namespace XE_Local_AI_Engine.Client.Services.Development;

public static class DevelopmentCommandIds
{
    public const string GitStatus = "git_status";
    public const string GitDiffCheck = "git_diff_check";
    public const string DotnetRestore = "dotnet_restore";
    public const string DotnetBuildRelease = "dotnet_build_release_no_restore";
    public const string DotnetTestRelease = "dotnet_test_release_no_build";
}
