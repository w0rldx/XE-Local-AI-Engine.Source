namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using System.Security.Authentication;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Cli.Discovery;

/// <summary>
///     The bootstrap every command shares: locate the node, read <c>auth/status</c>, stop when setup is required,
///     unlock a locked vault with the admin password, then make sure a live access token exists.
/// </summary>
internal sealed class NodeSession : IDisposable
{
    internal const string SetupRequiredMessage =
        "The node has no admin account yet. Run the engine once with --setup (on Windows through "
        + "XE-Local-AI-Engine.WindowsLauncher.exe --setup), or finish setup in the web UI, then try again.";

    internal const string PasswordMissingMessage =
        "The node's vault is locked and no admin password was given: set XE_ADMIN_PASSWORD, pass --password-stdin, "
        + "or run xe in a terminal to be prompted.";

    internal NodeSession(DiscoveredNode node, NodeConnectionOptions connection, HttpClient http, NodeAuthClient auth, NodeAuthStatus status)
    {
        Node = node;
        Connection = connection;
        Http = http;
        Auth = auth;
        Api = new NodeApiClient(http, auth);
        StatusAtStart = status;
    }

    public DiscoveredNode Node { get; }

    public NodeConnectionOptions Connection { get; }

    public HttpClient Http { get; }

    public NodeAuthClient Auth { get; }

    public NodeApiClient Api { get; }

    /// <summary><c>auth/status</c> as the node answered before this run unlocked or signed in.</summary>
    public NodeAuthStatus StatusAtStart { get; }

    public static async Task<NodeSession> OpenAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(environment);

        var tokenFile = ResolveTokenFile(parse, environment);
        var node = await NodeLocator.LocateAsync(parse.GetValue(CliOptions.Url), cancellationToken);
        var connection = new NodeConnectionOptions
        {
            Origin = node.Origin,
            AcceptAnyServerCertificate = parse.GetValue(CliOptions.InsecureDevCert),
            HandlerFactory = environment.HandlerFactory
        };
        var password = new PasswordSource(parse.GetValue(CliOptions.PasswordStdin), environment);
        var http = NodeHttp.CreateClient(connection);
        var auth = new NodeAuthClient(http, node.Origin, environment.TimeProvider, tokenFile, password.GetAsync);
        try
        {
            var status = await ReadStatusAsync(auth, node.Origin, cancellationToken);
            if (status.SetupRequired)
            {
                throw new CliCommandException(ExitCodes.AuthOrVault, SetupRequiredMessage);
            }

            if (string.Equals(status.Vault, NodeAuthClient.LockedVault, StringComparison.Ordinal))
            {
                var secret = await password.GetAsync(cancellationToken) ?? throw new CliCommandException(ExitCodes.AuthOrVault, PasswordMissingMessage);
                await UnlockAsync(auth, secret, cancellationToken);
            }

            _ = await auth.GetAccessTokenAsync(cancellationToken);
            return new NodeSession(node, connection, http, auth, status);
        }
        catch
        {
            auth.Dispose();
            http.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Auth.Dispose();
        Http.Dispose();
    }

    private static string? ResolveTokenFile(ParseResult parse, CliEnvironment environment)
    {
        var path = parse.GetValue(CliOptions.TokenFile);
        if (parse.GetValue(CliOptions.NoTokenFile))
        {
            return path is null
                ? null
                : throw new CliCommandException(ExitCodes.Usage, "--token-file and --no-token-file cannot be combined.");
        }

        return string.IsNullOrWhiteSpace(path) ? environment.DefaultTokenFilePath : Path.GetFullPath(path);
    }

    private static async Task<NodeAuthStatus> ReadStatusAsync(NodeAuthClient auth, Uri origin, CancellationToken cancellationToken)
    {
        try
        {
            return await auth.GetStatusAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            var hint = exception.InnerException is AuthenticationException
                ? " The certificate was refused; for the development host pass --insecure-dev-cert."
                : string.Empty;
            throw new CliCommandException(ExitCodes.NodeNotRunning, $"The node at {origin} does not answer: {exception.Message}{hint}");
        }
    }

    private static async Task UnlockAsync(NodeAuthClient auth, string password, CancellationToken cancellationToken)
    {
        try
        {
            await auth.UnlockVaultAsync(password, cancellationToken);
        }
        catch (NodeApiException exception)
        {
            throw new CliCommandException(ExitCodes.AuthOrVault, $"The vault could not be unlocked: {exception.Message}");
        }
    }
}
