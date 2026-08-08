namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Author-time CRUD + validation for the custom-tool library. Validation is the trust boundary that decides what
///     can ever reach the P2 executors; it reuses the exact same helpers those executors run at execution time
///     (<see cref="CustomToolValidation" />, <see cref="CustomToolTemplate" />, <see cref="CustomToolSchemaCompiler" />,
///     <see cref="CustomToolSsrfGuard" />, <see cref="HostExecutableGuard" />) so author-time acceptance and run-time
///     safety can never disagree. The store owns id/version/timestamp stamping; this service never touches versioning.
/// </summary>
internal sealed partial class CustomToolService : ICustomToolService
{
    private const int MaxDescriptionLength = 1024;
    private const int MaxParameterNameLength = 64;

    // Mirrors HostProcessExecutor.MaxTimeoutSeconds so a timeout accepted here always survives the executor's clamp.
    private const int MaxTimeoutSeconds = 300;

    private static readonly HashSet<string> AllowedHttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "HEAD"
    };

    private static readonly HashSet<string> AllowedParameterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "number",
        "integer",
        "boolean"
    };

    private readonly ICustomToolStore _store;
    private readonly ILocalToolOfferProvider _offerProvider;

    public CustomToolService(ICustomToolStore store, ILocalToolOfferProvider offerProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierRegex();

    public async Task<CustomToolView> CreateAsync(CustomToolDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var input = await ValidateAndBuildInputAsync(definition, existingId: null, existing: null, cancellationToken).ConfigureAwait(false);
        var record = await _store.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return ToView(record);
    }

    public async Task<CustomToolView?> UpdateAsync(Guid id, CustomToolDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Read the raw stored record (secrets in the clear) so a masked secret the client round-trips resolves back to
        // the stored value instead of overwriting it with the sentinel.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        var input = await ValidateAndBuildInputAsync(definition, id, existing, cancellationToken).ConfigureAwait(false);
        var record = await _store.UpdateAsync(id, input, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToView(record);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    public async Task<CustomToolView?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToView(record);
    }

    public async Task<IReadOnlyList<CustomToolView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(ToView)];
    }

    public HostExecutableProbeResult ProbeExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new HostExecutableProbeResult(Ok: false, "A path is required.", path);
        }

        try
        {
            HostExecutableGuard.Validate(path);
            return new HostExecutableProbeResult(Ok: true, Reason: null, path);
        }
        catch (CustomToolExecutionException exception)
        {
            // HostExecutableGuard messages describe the rule and echo no filesystem contents, so they are safe to surface.
            return new HostExecutableProbeResult(Ok: false, exception.Message, path);
        }
    }

    private async Task<CustomToolInput> ValidateAndBuildInputAsync(CustomToolDefinition definition,
        Guid? existingId,
        CustomToolRecord? existing,
        CancellationToken cancellationToken)
    {
        definition = ResolveMaskedSecrets(definition, existing);

        var name = NormalizeName(definition.Name);
        if (!CustomToolValidation.IsValidToolName(name))
        {
            throw new CustomToolValidationException(
                $"Name must be a MAF-safe '{CustomToolValidation.ToolNamePrefix}' tool name: the prefix followed by a lowercase [a-z0-9_] slug that starts and ends alphanumeric.");
        }

        if (string.IsNullOrWhiteSpace(definition.Description))
        {
            throw new CustomToolValidationException("Description is required.");
        }

        if (definition.Description.Length > MaxDescriptionLength)
        {
            throw new CustomToolValidationException($"Description must be at most {MaxDescriptionLength} characters.");
        }

        // M2: the danger acknowledgement is enforced server-side, not just by the client checkbox — a client that skips
        // it cannot author or edit a tool.
        if (!definition.Acknowledged)
        {
            throw new CustomToolValidationException("The custom-tool danger acknowledgement is required.");
        }

        var parameters = ValidateAndMapParameters(definition);
        var declaredNames = new HashSet<string>(parameters.Select(static parameter => parameter.Name), StringComparer.Ordinal);

        // Assert the compiled schema stays GBNF-safe (the compiler is safe by construction; this catches a regression
        // that let a length/range/format bound reach the wire and break the llama.cpp grammar sampler).
        var schema = CustomToolSchemaCompiler.Compile(definition.Mode, parameters);
        var bannedKeyword = CustomToolSchemaCompiler.BannedSchemaKeywords
                                                    .FirstOrDefault(keyword => schema.Contains(keyword, StringComparison.Ordinal));
        if (bannedKeyword is not null)
        {
            throw new CustomToolValidationException($"The compiled parameter schema contains the GBNF-unsafe keyword '{bannedKeyword}'.");
        }

        var configJson = definition.Kind switch
        {
            CustomToolKind.HttpFetch => BuildHttpFetchConfigJson(definition, declaredNames),
            CustomToolKind.Command => BuildCommandConfigJson(definition, declaredNames),
            _ => throw new CustomToolValidationException($"Unknown custom-tool kind '{definition.Kind}'.")
        };

        await EnsureNameIsAvailableAsync(name, existingId, cancellationToken).ConfigureAwait(false);

        var parametersJson = JsonSerializer.Serialize(parameters, CustomToolJson.Options);
        return new CustomToolInput(name,
            definition.Description,
            definition.Kind,
            definition.Mode,
            configJson,
            parametersJson,
            definition.Enabled,
            definition.Acknowledged);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.StartsWith(CustomToolValidation.ToolNamePrefix, StringComparison.Ordinal)
            ? trimmed
            : CustomToolValidation.ToolNamePrefix + trimmed;
    }

    private static IReadOnlyList<CustomToolParameter> ValidateAndMapParameters(CustomToolDefinition definition)
    {
        var declared = definition.Parameters ?? [];

        // A Fixed tool takes no model input; declaring parameters on one is a contradiction (the schema compiler ignores
        // them and every template placeholder would then be undeclared at run time). Reject it outright.
        if (definition.Mode == CustomToolMode.Fixed && declared.Count > 0)
        {
            throw new CustomToolValidationException("A Fixed-mode tool must not declare parameters.");
        }

        var mapped = new List<CustomToolParameter>(declared.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in declared)
        {
            var parameterName = (parameter.Name ?? string.Empty).Trim();
            if (parameterName.Length == 0 || parameterName.Length > MaxParameterNameLength || !IdentifierRegex().IsMatch(parameterName))
            {
                throw new CustomToolValidationException($"Each parameter name must be a [A-Za-z_][A-Za-z0-9_]* identifier of at most {MaxParameterNameLength} characters.");
            }

            if (!seen.Add(parameterName))
            {
                throw new CustomToolValidationException($"Duplicate parameter name '{parameterName}'.");
            }

            var type = (parameter.Type ?? string.Empty).Trim();
            if (!AllowedParameterTypes.TryGetValue(type, out var canonicalType))
            {
                throw new CustomToolValidationException($"Parameter '{parameterName}' has an unsupported type '{type}' (expected string, number, integer, or boolean).");
            }

            mapped.Add(new CustomToolParameter(parameterName, canonicalType, parameter.Description ?? string.Empty, parameter.Required));
        }

        return mapped;
    }

    private static string BuildHttpFetchConfigJson(CustomToolDefinition definition, IReadOnlySet<string> declaredNames)
    {
        var http = definition.Http
                   ?? throw new CustomToolValidationException("An HttpFetch tool requires an http configuration.");

        var method = (http.Method ?? string.Empty).Trim();
        if (method.Length == 0)
        {
            throw new CustomToolValidationException("The HTTP method is required.");
        }

        if (method.Contains('{', StringComparison.Ordinal))
        {
            throw new CustomToolValidationException("The HTTP method must not be parameterized.");
        }

        if (!AllowedHttpMethods.Contains(method))
        {
            throw new CustomToolValidationException($"The HTTP method '{method}' is not allowed (GET, POST, PUT, PATCH, DELETE, HEAD).");
        }

        var urlTemplate = (http.UrlTemplate ?? string.Empty).Trim();
        if (urlTemplate.Length == 0)
        {
            throw new CustomToolValidationException("The URL template is required.");
        }

        var schemeSeparator = urlTemplate.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            throw new CustomToolValidationException("The URL template must be an absolute http(s) URL.");
        }

        var scheme = urlTemplate[..schemeSeparator];
        if (scheme.Contains('{', StringComparison.Ordinal)
            || (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomToolValidationException("The URL template must use a non-parameterized http or https scheme.");
        }

        var hostIsParameterized = AuthoritySectionIsParameterized(urlTemplate, schemeSeparator);

        var dummy = BuildDummyBindings(declaredNames);
        AssertTemplatePlaceholdersDeclared(urlTemplate, declaredNames, dummy, "URL template");

        var headers = new List<CustomToolHeader>((http.Headers ?? []).Count);
        foreach (var header in http.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                throw new CustomToolValidationException("Each header must have a non-blank name.");
            }

            AssertTemplatePlaceholdersDeclared(header.Value ?? string.Empty, declaredNames, dummy, $"header '{header.Name}'");
            headers.Add(new CustomToolHeader(header.Name, header.Value ?? string.Empty, header.IsSecret));
        }

        if (!string.IsNullOrEmpty(http.BodyTemplate))
        {
            AssertTemplatePlaceholdersDeclared(http.BodyTemplate, declaredNames, dummy, "body template");
        }

        var allowedHosts = (http.AllowedHosts ?? [])
                           .Select(static host => (host ?? string.Empty).Trim())
                           .Where(static host => host.Length > 0)
                           .ToList();

        // H2: a model-fillable host must be pinned to an operator allow-list, otherwise the model could point the fetch
        // anywhere. The SSRF guard re-checks this at run time; rejecting here gives the author immediate feedback.
        if (hostIsParameterized && allowedHosts.Count == 0)
        {
            throw new CustomToolValidationException("A tool whose URL host is parameterized must declare at least one allowedHost.");
        }

        // Fixed host: pre-validate the assembled URL now so an author cannot save a tool that targets a private,
        // loopback, or metadata address. A parameterized host cannot be resolved to a concrete literal here, so it is
        // left to the run-time pinned-connect SSRF guard.
        if (!hostIsParameterized)
        {
            var assembled = CustomToolTemplate.Substitute(urlTemplate, dummy, declaredNames, Uri.EscapeDataString);
            if (!Uri.TryCreate(assembled, UriKind.Absolute, out var probeUrl))
            {
                throw new CustomToolValidationException("The URL template does not assemble into a valid absolute URL.");
            }

            try
            {
                CustomToolSsrfGuard.ValidateRequestUrl(probeUrl, allowedHosts, hostIsParameterized: false);
            }
            catch (CustomToolExecutionException exception)
            {
                throw new CustomToolValidationException(exception.Message, exception);
            }
        }

        var config = new HttpFetchConfig(method, urlTemplate, headers, http.BodyTemplate, allowedHosts);
        return JsonSerializer.Serialize(config, CustomToolJson.Options);
    }

    private static string BuildCommandConfigJson(CustomToolDefinition definition, IReadOnlySet<string> declaredNames)
    {
        var command = definition.Command
                      ?? throw new CustomToolValidationException("A Command tool requires a command configuration.");

        var executable = (command.Executable ?? string.Empty).Trim();
        if (executable.Length == 0)
        {
            throw new CustomToolValidationException("The command executable is required.");
        }

        // C1/M3: the executable is fixed — never a placeholder — absolute, and not a shell/interpreter/script.
        if (executable.Contains('{', StringComparison.Ordinal))
        {
            throw new CustomToolValidationException("The command executable must be a fixed path, not a parameter.");
        }

        if (!CustomToolValidation.IsAbsolutePath(executable))
        {
            throw new CustomToolValidationException("The command executable must be an absolute path.");
        }

        if (CustomToolValidation.IsInterpreterOrShell(executable))
        {
            throw new CustomToolValidationException("The command executable must not be a shell, interpreter, or script.");
        }

        var workingDirectory = command.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (workingDirectory.Contains('{', StringComparison.Ordinal))
            {
                throw new CustomToolValidationException("The working directory must be a fixed path, not a parameter.");
            }

            if (!CustomToolValidation.IsAbsolutePath(workingDirectory))
            {
                throw new CustomToolValidationException("The working directory must be an absolute path.");
            }
        }

        var dummy = BuildDummyBindings(declaredNames);
        var argsTemplate = (command.ArgsTemplate ?? []).ToList();
        foreach (var argument in argsTemplate)
        {
            AssertTemplatePlaceholdersDeclared(argument ?? string.Empty, declaredNames, dummy, "argument");
        }

        var env = new List<CustomToolEnvironmentVariable>((command.Env ?? []).Count);
        foreach (var variable in command.Env ?? [])
        {
            var variableName = (variable.Name ?? string.Empty).Trim();
            if (variableName.Length == 0 || !IdentifierRegex().IsMatch(variableName))
            {
                throw new CustomToolValidationException("Each environment variable name must be a [A-Za-z_][A-Za-z0-9_]* identifier.");
            }

            // Env values are operator-fixed (they carry secrets); the model never fills them, so a placeholder is a
            // configuration error rather than a substitution point.
            if ((variable.Value ?? string.Empty).Contains('{', StringComparison.Ordinal))
            {
                throw new CustomToolValidationException($"The environment variable '{variableName}' value must not be parameterized.");
            }

            env.Add(new CustomToolEnvironmentVariable(variableName, variable.Value ?? string.Empty, variable.IsSecret));
        }

        if (command.TimeoutSeconds < 0 || command.TimeoutSeconds > MaxTimeoutSeconds)
        {
            throw new CustomToolValidationException($"The timeout must be between 0 (default) and {MaxTimeoutSeconds} seconds.");
        }

        var config = new CommandConfig(executable, argsTemplate, workingDirectory, command.TimeoutSeconds, env);
        return JsonSerializer.Serialize(config, CustomToolJson.Options);
    }

    private static bool AuthoritySectionIsParameterized(string urlTemplate, int schemeSeparator)
    {
        // The authority is between "://" and the first '/', '?', or '#'; a placeholder there lets the model fill the host.
        var authorityStart = schemeSeparator + 3;
        var authorityEnd = urlTemplate.Length;
        for (var index = authorityStart; index < urlTemplate.Length; index++)
        {
            if (urlTemplate[index] is '/' or '?' or '#')
            {
                authorityEnd = index;
                break;
            }
        }

        return urlTemplate.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('{');
    }

    private static IReadOnlyDictionary<string, string> BuildDummyBindings(IReadOnlySet<string> declaredNames)
    {
        var dummy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaredName in declaredNames)
        {
            dummy[declaredName] = "1";
        }

        return dummy;
    }

    private static void AssertTemplatePlaceholdersDeclared(string template,
        IReadOnlySet<string> declaredNames,
        IReadOnlyDictionary<string, string> dummy,
        string fieldLabel)
    {
        try
        {
            // Substitute fails closed on any {token} that is not a declared parameter — the same rejection the executor
            // performs at run time, so an undeclared placeholder is caught at author time with a clear field label.
            CustomToolTemplate.Substitute(template, dummy, declaredNames);
        }
        catch (CustomToolExecutionException exception)
        {
            throw new CustomToolValidationException($"The {fieldLabel} references an undeclared parameter: {exception.Message}", exception);
        }
    }

    private async Task EnsureNameIsAvailableAsync(string name, Guid? existingId, CancellationToken cancellationToken)
    {
        // Collision with a built-in or MCP tool name would let a custom tool shadow a trusted one at resolution time. This
        // uses the SYNC (built-in + MCP) known-names view deliberately: it is a pure in-memory read (no store I/O), and the
        // async view additionally lists existing custom tools, which would make an unchanged-name UPDATE collide with
        // itself. Custom-vs-custom uniqueness (with self-exclusion) is the store check immediately below.
#pragma warning disable CA1849, S6966 // GetKnownToolNames() does no I/O; the async twin would introduce a self-collision on update. See comment above.
        if (_offerProvider.GetKnownToolNames().Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)))
#pragma warning restore CA1849, S6966
        {
            throw new CustomToolValidationException($"The name '{name}' collides with an existing built-in or MCP tool.");
        }

        var existing = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(tool =>
                (existingId is null || tool.Id != existingId.Value)
                && string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomToolValidationException($"A custom tool named '{name}' already exists.");
        }
    }

    // The read path masks secret header/env values to the sentinel; when a client edits a tool it round-trips that
    // sentinel back. Resolve it here against the stored record so an unrelated edit never overwrites a secret with the
    // placeholder. On create (no prior record) the sentinel is reserved and rejected.
    private static CustomToolDefinition ResolveMaskedSecrets(CustomToolDefinition definition, CustomToolRecord? existing)
    {
        var hasExisting = existing is not null;

        if (definition.Kind == CustomToolKind.HttpFetch && definition.Http is { } http)
        {
            var priorSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (existing is { Kind: CustomToolKind.HttpFetch })
            {
                foreach (var header in CustomToolConfigParser.ParseHttpFetch(existing.ConfigJson).Headers.Where(static header => header.IsSecret))
                {
                    priorSecrets[header.Name] = header.Value;
                }
            }

            var headers = http.Headers
                              .Select(header => header with
                              {
                                  Value = ResolveMaskedValue(header.Name, header.Value, header.IsSecret, priorSecrets, hasExisting)
                              })
                              .ToList();
            return definition with
            {
                Http = http with
                {
                    Headers = headers
                }
            };
        }

        if (definition.Kind == CustomToolKind.Command && definition.Command is { } command)
        {
            var priorSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (existing is { Kind: CustomToolKind.Command })
            {
                foreach (var variable in CustomToolConfigParser.ParseCommand(existing.ConfigJson).Env.Where(static variable => variable.IsSecret))
                {
                    priorSecrets[variable.Name] = variable.Value;
                }
            }

            var env = command.Env
                             .Select(variable => variable with
                             {
                                 Value = ResolveMaskedValue(variable.Name, variable.Value, variable.IsSecret, priorSecrets, hasExisting)
                             })
                             .ToList();
            return definition with
            {
                Command = command with
                {
                    Env = env
                }
            };
        }

        return definition;
    }

    private static string ResolveMaskedValue(string name, string value, bool isSecret, IReadOnlyDictionary<string, string> priorSecrets, bool hasExisting)
    {
        if (!isSecret || !string.Equals(value, CustomToolSecrets.Sentinel, StringComparison.Ordinal))
        {
            return value;
        }

        if (priorSecrets.TryGetValue(name, out var priorValue))
        {
            return priorValue;
        }

        if (!hasExisting)
        {
            throw new CustomToolValidationException("The secret placeholder value is reserved and cannot be submitted as a secret value.");
        }

        // The client sent the mask for a secret that has no stored counterpart (e.g. a renamed header): treat it as unset.
        return string.Empty;
    }

    private static CustomToolView ToView(CustomToolRecord record)
    {
        var parameters = CustomToolConfigParser.ParseParameters(record.ParametersJson)
                                               .Select(static parameter => new CustomToolParameterModel
                                               {
                                                   Name = parameter.Name,
                                                   Type = parameter.Type,
                                                   Description = parameter.Description,
                                                   Required = parameter.Required
                                               })
                                               .ToList();

        HttpFetchDefinition? http = null;
        CommandDefinition? command = null;
        if (record.Kind == CustomToolKind.HttpFetch)
        {
            var config = CustomToolConfigParser.ParseHttpFetch(record.ConfigJson);
            http = new HttpFetchDefinition
            {
                Method = config.Method,
                UrlTemplate = config.UrlTemplate,
                Headers =
                [
                    .. config.Headers.Select(static header => new CustomToolHeaderModel
                    {
                        Name = header.Name,
                        Value = MaskSecret(header.Value, header.IsSecret),
                        IsSecret = header.IsSecret
                    })
                ],
                BodyTemplate = config.BodyTemplate,
                AllowedHosts = config.AllowedHosts
            };
        }
        else
        {
            var config = CustomToolConfigParser.ParseCommand(record.ConfigJson);
            command = new CommandDefinition
            {
                Executable = config.Executable,
                ArgsTemplate = config.ArgsTemplate,
                WorkingDirectory = config.WorkingDirectory,
                TimeoutSeconds = config.TimeoutSeconds,
                Env =
                [
                    .. config.Env.Select(static variable => new CustomToolEnvironmentVariableModel
                    {
                        Name = variable.Name,
                        Value = MaskSecret(variable.Value, variable.IsSecret),
                        IsSecret = variable.IsSecret
                    })
                ]
            };
        }

        return new CustomToolView
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Kind = record.Kind,
            Mode = record.Mode,
            Enabled = record.Enabled,
            Acknowledged = record.Acknowledged,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            Parameters = parameters,
            Http = http,
            Command = command
        };
    }

    // A secret value never leaves the node on the read path: a set secret becomes the sentinel, an unset one stays empty.
    private static string MaskSecret(string value, bool isSecret)
    {
        if (!isSecret)
        {
            return value;
        }

        return string.IsNullOrEmpty(value) ? string.Empty : CustomToolSecrets.Sentinel;
    }
}
