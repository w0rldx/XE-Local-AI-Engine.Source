namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;

/// <summary>The options every command accepts; they are recursive, so they may follow the subcommand.</summary>
internal static class CliOptions
{
    public static readonly Option<string?> Url = new("--url")
    {
        Description = "The node's loopback origin, such as http://127.0.0.1:5123. Default: XE_URL, then the engine's ready.json.",
        Recursive = true
    };

    public static readonly Option<bool> Json = new("--json")
    {
        Description = "Write machine-readable JSON to standard output instead of tables and text.",
        Recursive = true
    };

    public static readonly Option<bool> PasswordStdin = new("--password-stdin")
    {
        Description = "Read the admin password from the first line of standard input (else XE_ADMIN_PASSWORD, else a prompt on a terminal).",
        Recursive = true
    };

    public static readonly Option<bool> InsecureDevCert = new("--insecure-dev-cert")
    {
        Description = "Accept the development host's self-signed certificate (loopback https origins only).",
        Recursive = true
    };

    public static readonly Option<bool> NoTokenFile = new("--no-token-file")
    {
        Description = "Keep the session tokens in memory only; nothing is written to disk.",
        Recursive = true
    };

    public static readonly Option<string?> TokenFile = new("--token-file")
    {
        Description = "Where to keep the session tokens (an owner-only file). Default: the user's config directory, xe/tokens.json.",
        Recursive = true
    };

    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Options.Add(Url);
        root.Options.Add(Json);
        root.Options.Add(PasswordStdin);
        root.Options.Add(InsecureDevCert);
        root.Options.Add(NoTokenFile);
        root.Options.Add(TokenFile);
    }
}
