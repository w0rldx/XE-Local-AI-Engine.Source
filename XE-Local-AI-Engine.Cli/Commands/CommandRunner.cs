namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using System.Net;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Discovery;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>Runs a command body and turns every expected failure into its exit code and one printed message.</summary>
/// <remarks>
///     With <c>--json</c> the message is a <see cref="CliJsonError" /> line on standard output, so a script reading
///     the stream sees it; otherwise it is one <c>xe: ...</c> line on standard error. An unexpected exception is left
///     to the parser's default handler, which prints it and exits 1.
/// </remarks>
internal static class CommandRunner
{
    public static void SetAction(Command command, CliEnvironment environment, Func<ParseResult, CancellationToken, Task<int>> body)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(body);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, environment, body, cancellationToken));
    }

    /// <summary>The process token joined with Ctrl-C, for work outside a streaming turn, where Ctrl-C means exit 130.</summary>
    public static CancellationTokenSource LinkInterrupt(CliEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, environment.Interrupts.Token);
    }

    /// <summary>The exit code of an expected failure, or null for one the parser's default handler should report.</summary>
    public static int? ExitCodeFor(Exception exception) =>
        exception switch
        {
            CliCommandException command => command.ExitCode,
            NodeDiscoveryException { Failure: NodeDiscoveryFailure.InvalidUrl or NodeDiscoveryFailure.InvalidDataDirectory } => ExitCodes.Usage,
            NodeDiscoveryException => ExitCodes.NodeNotRunning,
            HttpRequestException => ExitCodes.NodeNotRunning,
            NodeApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable } => ExitCodes.AuthOrVault,
            NodeApiException or HubException or ChatStreamWatchdogException => ExitCodes.Failure,
            _ => null
        };

    public static async Task<int> FailAsync(ParseResult parse, CliEnvironment environment, int exitCode, string message)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(environment);

        if (parse.GetValue(CliOptions.Json))
        {
            await JsonTurnRenderer.WriteLineAsync(environment.Out,
                new CliJsonError
                {
                    ExitCode = exitCode,
                    Message = message
                },
                CancellationToken.None);
        }
        else
        {
            await environment.Error.WriteLineAsync($"xe: {message}".AsMemory(), CancellationToken.None);
        }

        return exitCode;
    }

    private static async Task<int> RunAsync(ParseResult parse,
        CliEnvironment environment,
        Func<ParseResult, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body(parse, cancellationToken);
        }
        catch (OperationCanceledException) when (environment.Interrupts.Token.IsCancellationRequested)
        {
            return await FailAsync(parse, environment, ExitCodes.Interrupted, "Interrupted.");
        }
        catch (Exception exception) when (ExitCodeFor(exception) is not null)
        {
            return await FailAsync(parse, environment, ExitCodeFor(exception)!.Value, exception.Message);
        }
    }
}
