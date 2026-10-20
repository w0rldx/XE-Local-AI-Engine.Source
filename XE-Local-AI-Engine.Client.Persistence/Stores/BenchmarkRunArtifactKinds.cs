namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The closed artifact-kind vocabulary, mirrored by the table's CHECK: a new kind is a table rebuild.</summary>
public static class BenchmarkRunArtifactKinds
{
    public const string PythonTestsStdout = "pythonTestsStdout";
    public const string PythonTestsStderr = "pythonTestsStderr";

    public static bool IsKnown(string? kind) =>
        kind is PythonTestsStdout or PythonTestsStderr;
}
