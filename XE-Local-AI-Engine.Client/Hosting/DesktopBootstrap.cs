namespace XE_Local_AI_Engine.Client.Hosting;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

/// <summary>
///     Fills the two configuration values a self-contained desktop launch needs but that no env/Aspire source supplies:
///     the node SQLite connection string (SEC-1) and the operator secret (SEC-2). Everything here is strictly
///     desktop-mode-only — the caller invokes it solely inside the <c>XE_LAUNCH_MODE=desktop</c> / <c>--desktop</c>
///     branch — and each key is layered into in-memory configuration ONLY when it is not already supplied. That keeps the
///     headless / Aspire / CI configuration byte-identical: off the desktop flag this type is never reached, and even on
///     it, a value that was already provided via env or Aspire is left untouched.
/// </summary>
/// <remarks>
///     The operator key is generated once and persisted to a per-user file so it is DETERMINISTIC across launches: a
///     fresh random key on every start would brick the encrypted node database. The connection string targets a per-user
///     data directory under <see cref="Environment.SpecialFolder.LocalApplicationData" /> (Windows
///     <c>%LOCALAPPDATA%</c>; Linux <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>) so a single-file exe — whose
///     <c>AppContext.BaseDirectory</c> is a volatile bundle-extraction temp — keeps its data between runs.
/// </remarks>
internal static class DesktopBootstrap
{
    /// <summary>Configuration key the EF connection-string consumers read via <c>GetConnectionString("node-sqlite")</c>.</summary>
    internal const string NodeSqliteConnectionStringKey = "ConnectionStrings:node-sqlite";

    /// <summary>
    ///     Configuration key the node-data-directory abstraction (<c>INodeDataDirectory</c>) reads. Set to the per-user
    ///     data dir so every per-node runtime artifact (settings, the encrypted credential stores, cert pins, the
    ///     AgentHome workspace, the hardware-profile cache) lands beside <c>node.sqlite</c>/<c>node.key</c> rather than in
    ///     the shared/shipped install directory.
    /// </summary>
    internal const string NodeDataDirectoryKey = "NodeData:Directory";

    /// <summary>The per-user application data sub-directory that holds the desktop database, key, and models.</summary>
    internal const string ApplicationDataFolderName = "XE-Local-AI-Engine";

    /// <summary>The SQLite database file name under the per-user data directory.</summary>
    internal const string DatabaseFileName = "node.sqlite";

    /// <summary>The operator-secret key file name under the per-user data directory.</summary>
    internal const string KeyFileName = "node.key";

    /// <summary>The models sub-directory under the per-user data directory (HuggingFace GGUF cache + provider map).</summary>
    internal const string ModelsFolderName = "models";

    /// <summary>Configuration key the HuggingFace options bind for the GGUF models directory.</summary>
    internal const string HuggingFaceModelsDirectoryKey = "HuggingFace:ModelsDirectory";

    /// <summary>Configuration key for the node's default chat model (<c>LocalChatAgentOptions.DefaultModel</c>).</summary>
    internal const string LocalChatDefaultModelKey = "Agent:LocalChat:DefaultModel";

    /// <summary>Configuration keys describing the GGUF starter model desktop mode provisions on first run.</summary>
    internal const string FirstRunModelEnabledKey = "FirstRunModel:Enabled";

    internal const string FirstRunModelRepoIdKey = "FirstRunModel:RepoId";
    internal const string FirstRunModelQuantKey = "FirstRunModel:Quant";

    /// <summary>
    ///     Ensures the desktop data directory, connection string, operator secret, and models directory are present in
    ///     configuration. Each value is filled only when absent, so an env/Aspire-supplied value always wins. The folder
    ///     resolver is injected (mirroring <see cref="DesktopLaunch.IsDesktopMode(string[], Func{string, string?})" />)
    ///     so tests never touch the real <c>%LOCALAPPDATA%</c>.
    /// </summary>
    /// <param name="configuration">
    ///     The builder configuration. A <c>WebApplicationBuilder.Configuration</c> is an
    ///     <see cref="IConfigurationManager" />, which is both an <see cref="IConfiguration" /> (read) and an
    ///     <see cref="IConfigurationBuilder" /> (layer-in), so this method can both inspect and append.
    /// </param>
    /// <param name="folderResolver">Resolves a <see cref="Environment.SpecialFolder" /> to an absolute path.</param>
    internal static void EnsureLocalDataConfiguration(IConfigurationManager configuration,
        Func<Environment.SpecialFolder, string> folderResolver)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(folderResolver);

        var dataDirectory = ResolveDataDirectory(folderResolver);
        EnsureDirectory(dataDirectory);

        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Point the node-data-directory abstraction at the same per-user data dir the DB/key/models already use, so all
        // per-node runtime state is co-located there instead of the shared install/ContentRoot dir. Desktop-only and
        // unconditional: this in-memory layer is only reached behind the desktop flag, so headless/Aspire/CI never set it
        // and INodeDataDirectory falls back to ContentRootPath (the off-flag byte-behavior invariant).
        overrides[NodeDataDirectoryKey] = dataDirectory;

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("node-sqlite")))
        {
            var databasePath = Path.Combine(dataDirectory, DatabaseFileName);
            overrides[NodeSqliteConnectionStringKey] = $"Data Source={databasePath}";
        }

        if (string.IsNullOrWhiteSpace(ResolveExistingOperatorSecret(configuration)))
        {
            var keyPath = Path.Combine(dataDirectory, KeyFileName);
            overrides[NodeOperatorSecretProvider.EnvVarName] = EnsureOperatorSecret(keyPath);
        }

        if (string.IsNullOrWhiteSpace(configuration[HuggingFaceModelsDirectoryKey]))
        {
            overrides[HuggingFaceModelsDirectoryKey] = Path.Combine(dataDirectory, ModelsFolderName);
        }

        // Point the default chat model at the GGUF starter model desktop mode actually provisions. The stock
        // Agent:LocalChat:DefaultModel ("qwen3:0.6b") is an Ollama-era id that desktop mode never installs; until
        // first-run provisioning persists the selected model (only AFTER the multi-hundred-MB download completes), the
        // chat composer falls back to this default — and a first send against the uninstalled Ollama id fails with
        // "the requested model is not installed". The override is derived from FirstRunModel:{RepoId,Quant} so it stays
        // in lockstep with the exact identity provisioning installs ("repo:quant"). Desktop-only and unconditional: this
        // in-memory layer (added last) intentionally wins over appsettings, but is only reached behind the desktop flag,
        // so headless/Aspire keep the Ollama default untouched.
        var firstRunModel = ResolveFirstRunModelIdentity(configuration);
        if (!string.IsNullOrWhiteSpace(firstRunModel))
        {
            overrides[LocalChatDefaultModelKey] = firstRunModel;
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }
    }

    /// <summary>
    ///     The canonical identity of the first-run GGUF starter model (<c>repo:quant</c>, or just <c>repo</c> when no
    ///     quant is configured), matching what <c>FirstRunModelProvisioningService</c> installs and selects. Returns
    ///     <c>null</c> when first-run provisioning is disabled or no repo id is configured, in which case the stock
    ///     default chat model is left in place.
    /// </summary>
    private static string? ResolveFirstRunModelIdentity(IConfiguration configuration)
    {
        if (!configuration.GetValue(FirstRunModelEnabledKey, true))
        {
            return null;
        }

        var repoId = configuration[FirstRunModelRepoIdKey]?.Trim();
        if (string.IsNullOrWhiteSpace(repoId))
        {
            return null;
        }

        var quant = configuration[FirstRunModelQuantKey]?.Trim();
        return string.IsNullOrWhiteSpace(quant) ? repoId : $"{repoId}:{quant}";
    }

    /// <summary>Convenience overload reading from the real process environment. Used by <c>Program.cs</c>.</summary>
    internal static void EnsureLocalDataConfiguration(IConfigurationManager configuration)
    {
        EnsureLocalDataConfiguration(configuration, Environment.GetFolderPath);
    }

    private static string ResolveDataDirectory(Func<Environment.SpecialFolder, string> folderResolver)
    {
        var localApplicationData = folderResolver(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, ApplicationDataFolderName);
    }

    private static void EnsureDirectory(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fail loudly: a desktop user whose data directory cannot be created must see a clear startup error rather
            // than silently fall back to a volatile location that would lose their database.
            throw new InvalidOperationException($"The desktop data directory '{dataDirectory}' could not be created. Check filesystem permissions.",
                exception);
        }
    }

    private static string? ResolveExistingOperatorSecret(IConfiguration configuration)
    {
        // Mirror the precedence NodeOperatorSecretProvider already honors so we never overwrite a real env/Aspire/file
        // secret with a generated one — the generated key is a last resort only when nothing else supplies it.
        var envValue = Environment.GetEnvironmentVariable(NodeOperatorSecretProvider.EnvVarName)
                       ?? configuration[NodeOperatorSecretProvider.EnvVarName];
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
        }

        if (File.Exists(NodeOperatorSecretProvider.SecretFilePath))
        {
            return NodeOperatorSecretProvider.SecretFilePath;
        }

        return configuration[NodeOperatorSecretProvider.AspireParameterPath];
    }

    private static string EnsureOperatorSecret(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            return ReadAndValidateExistingSecret(keyPath);
        }

        return GenerateAndPersistSecret(keyPath);
    }

    private static string ReadAndValidateExistingSecret(string keyPath)
    {
        string base64;
        try
        {
            base64 = File.ReadAllText(keyPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The desktop operator key file '{keyPath}' exists but could not be read. Check filesystem permissions.",
                exception);
        }

        // A torn / corrupt key must FAIL LOUDLY: silently regenerating would change the key and brick the encrypted
        // database that the previous (intact) key wrote.
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The desktop operator key file '{keyPath}' is corrupt (not valid base64). Restore the original key or "
                                                + "delete the encrypted database to start fresh; the file will not be regenerated automatically to avoid "
                                                + "silently losing data.",
                exception);
        }

        if (decoded.Length != NodeOperatorSecretProvider.ExpectedSecretLength)
        {
            throw new InvalidOperationException($"The desktop operator key file '{keyPath}' does not contain exactly "
                                                + $"{NodeOperatorSecretProvider.ExpectedSecretLength} bytes. Restore the original key or delete the "
                                                + "encrypted database to start fresh; the file will not be regenerated automatically to avoid silently "
                                                + "losing data.");
        }

        return base64;
    }

    private static string GenerateAndPersistSecret(string keyPath)
    {
        var secret = RandomNumberGenerator.GetBytes(NodeOperatorSecretProvider.ExpectedSecretLength);
        var base64 = Convert.ToBase64String(secret);

        // Write atomically (temp file + move) so a crash mid-write can never leave a torn key that would brick the DB.
        var tempPath = keyPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, base64);
            ProtectKeyFile(tempPath);
            File.Move(tempPath, keyPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            throw new InvalidOperationException($"The desktop operator key file '{keyPath}' could not be written. Check filesystem permissions.",
                exception);
        }

        return base64;
    }

    private static void ProtectKeyFile(string path)
    {
        // On non-Windows restrict to owner read/write (0600). On Windows the per-user %LOCALAPPDATA% ACL is relied on
        // (a DPAPI/ProtectedData wrap is a post-RC hardening, tracked as a risk).
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp file; the original write failure is already being surfaced.
        }
    }
}
