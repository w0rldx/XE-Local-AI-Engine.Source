namespace XE_Local_AI_Engine.Cli;

using XE_Local_AI_Engine.Cli.Commands;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        CliRoot.RunAsync(args, CliEnvironment.FromProcess(), CancellationToken.None);
}
