namespace XE_Local_AI_Engine.Cli.Auth;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using XE_Local_AI_Engine.Cli.Api;

/// <summary>Keeps a token pair between runs in an owner-only file under the user's config directory.</summary>
/// <remarks>
///     A pair issued by another origin is ignored on read, so a node on a new port forces a fresh sign-in rather than
///     sending one node's refresh token to another. Owner-only is mode 0600 on Linux and macOS, and on Windows a
///     protected DACL that grants the current user alone, set on the temp file before it replaces the target.
/// </remarks>
internal static class TokenFileStore
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory = OwnerReadWrite | UnixFileMode.UserExecute;

    /// <summary><c>$XDG_CONFIG_HOME/xe/tokens.json</c> (or <c>~/.config</c>) on Linux, <c>%APPDATA%\xe\tokens.json</c> on Windows.</summary>
    internal static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "xe",
            "tokens.json");

    internal static async Task SaveAsync(string path, TokenPair tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(tokens);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerOnlyDirectory);
        }

        // A temp name per write, so two xe processes saving at once never touch each other's pending file.
        var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = OwnerReadWrite;
        }

        try
        {
            await using (var stream = new FileStream(temporary, fileOptions))
            {
                await JsonSerializer.SerializeAsync(stream, tokens, NodeHttp.Json, cancellationToken);
            }

            if (OperatingSystem.IsWindows())
            {
                RestrictToCurrentUser(temporary);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    ///     Gives the file a protected DACL with one FullControl rule for the current user, so it inherits nothing from
    ///     the profile folder. The same rule as the node's <c>SecureFilePermissions.Apply</c>, inlined because the CLI
    ///     references no engine project.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    /// <summary>The stored pair for <paramref name="origin" />, or null when there is none or it cannot be read.</summary>
    internal static async Task<TokenPair?> LoadAsync(string path, Uri origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(origin);

        try
        {
            await using var stream = File.OpenRead(path);
            var tokens = await JsonSerializer.DeserializeAsync<TokenPair>(stream, NodeHttp.Json, cancellationToken);
            return tokens is not null
                   && !string.IsNullOrEmpty(tokens.AccessToken)
                   && string.Equals(tokens.Origin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                ? tokens
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
