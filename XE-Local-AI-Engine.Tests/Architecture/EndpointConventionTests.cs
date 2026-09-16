namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Extensions;
using FastEndpoints;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
// ArchUnitNET declares its own Assembly and Type; every one below is the CLR type, so the loader is aliased
// rather than imported by namespace.
using ArchLoader = ArchUnitNET.Loader.ArchLoader;
using Assembly = System.Reflection.Assembly;

/// <summary>
///     Freezes the endpoint conventions the host already follows, so the next endpoint cannot quietly break them.
///     <para>
///         R1/R2 — one endpoint type per file, and the file is named after it. R3 — the thirty-four grouping files
///         that predate the rule are named, and nothing else may be plural. R4 — every endpoint is sealed. R5 — a
///         route is never a string literal; it comes from <c>LocalApiRoutes</c>, which is what keeps the SPA client,
///         the MCP surface and the endpoint in agreement about a path.
///     </para>
///     <para>
///         Reflection alone cannot see which FILE a type came from (IL carries no file identity — the same reason
///         <see cref="PlacementConventionTests" /> says a placement rule needs a source scan), and a source scan
///         alone cannot tell an endpoint from any other class. So both run: reflection over the compiled
///         <c>Client</c> assembly is the ground truth for WHICH types are endpoints, and a comment-stripped scan of
///         the WHOLE project locates each one's declaration. Scanning the whole project rather than
///         <c>Endpoints/**</c> is what makes this a completeness check: an endpoint declared somewhere else is
///         found and rejected instead of silently escaping the filename rules while the floor still passes.
///     </para>
///     <para>
///         R5 is a text rule because that is what it polices: the shape of the expression a developer typed. It
///         accepts three forms and nothing else, and deliberately never trusts "the text mentions LocalApiRoutes" —
///         <c>var fake = "LocalApiRoutes"; Get(fake);</c> mentions it and routes nowhere near it. A new legitimate
///         route-building shape is a reason to widen the rule on purpose, not a false positive to silence.
///     </para>
/// </summary>
public sealed class EndpointConventionTests
{
    /// <summary>
    ///     Non-vacuity floor for the reflected endpoint set. The real count is four hundred and forty-five; the floor
    ///     sits well under it because it exists to catch a broken marker or an empty load, not to notice that one
    ///     endpoint was retired.
    /// </summary>
    private const int EndpointFloor = 400;

    /// <summary>Floor for the host's own route mappings (nineteen hubs, one MCP map, eight minimal-API maps).</summary>
    private const int MappingFloor = 27;

    private const string EndpointsPrefix = "XE-Local-AI-Engine.Client/Endpoints/";

    /// <summary>
    ///     Files that group several endpoints under one plural name. They predate R1/R2 and are grandfathered by
    ///     path, not by a rule: §3a of the slice plan measured every mechanical "the filename is the resource noun"
    ///     heuristic against them and eighty-five of roughly one hundred and forty member types fail it, so an
    ///     allowlist is the only honest mechanism. A file leaves this list by being split, never by being renamed.
    /// </summary>
    private static readonly string[] PluralEndpointFiles =
    [
        "Auth/V1/NodeAuthEndpoints.cs",
        "Automation/V1/SlashCommandEndpoints.cs",
        "Benchmarks/V1/BenchmarkCatalogEndpoints.cs",
        "Benchmarks/V1/BenchmarkExportEndpoints.cs",
        "Benchmarks/V1/BenchmarkFidelityEndpoints.cs",
        "Benchmarks/V1/BenchmarkPairwiseEndpoints.cs",
        "Benchmarks/V1/BenchmarkProjectEndpoints.cs",
        "Benchmarks/V1/BenchmarkRunEndpoints.cs",
        "Benchmarks/V1/BenchmarkTaskItemEndpoints.cs",
        "Development/V1/ArtifactDevelopmentEndpoints.cs",
        "Development/V1/CapabilityDevelopmentEndpoints.cs",
        "Development/V1/PatchDevelopmentEndpoints.cs",
        "Development/V1/ProjectDevelopmentEndpoints.cs",
        "Development/V1/RepositoryDevelopmentEndpoints.cs",
        "Development/V1/TaskDevelopmentEndpoints.cs",
        "Development/V1/TemplateDevelopmentEndpoints.cs",
        "DevelopmentWorkflows/V1/DevWorkflowDefinitionEndpoints.cs",
        "DevelopmentWorkflows/V1/DevWorkflowNodeRunEndpoints.cs",
        "DevelopmentWorkflows/V1/DevWorkflowRuleSetEndpoints.cs",
        "DevelopmentWorkflows/V1/DevWorkflowRunEndpoints.cs",
        "DevelopmentWorkflows/V1/DevWorkflowWorkItemEndpoints.cs",
        "LocalChat/V1/ManageNodeChatConversationEndpoints.cs",
        "LocalChat/V1/ManageNodeChatMessageEndpoints.cs",
        "Training/Comparisons/V1/TrainingComparisonEndpoints.cs",
        "Training/Datasets/V1/TrainingDatasetEndpoints.cs",
        "Training/Definitions/V1/TrainingDefinitionEndpoints.cs",
        "Training/Evaluations/V1/TrainingEvaluationEndpoints.cs",
        "Training/Exports/V1/TrainingExportEndpoints.cs",
        "Training/Mocks/V1/ToolMockEndpoints.cs",
        "Training/Runs/V1/TrainingRunEndpoints.cs",
        "TutorialState/V1/TutorialStateEndpoints.cs",
        "WorkSessions/V1/WorkSessionCrudEndpoints.cs",
        "WorkSessions/V1/WorkSessionFeedEndpoints.cs",
        "WorkSessions/V1/WorkSessionLifecycleEndpoints.cs"
    ];

    private static readonly Assembly ClientAssembly = typeof(WorkerHealthCheck).Assembly;

    // FastEndpoints is loaded because AreAssignableTo(Type) resolves the base type inside the architecture and
    // throws TypeDoesNotExistInArchitecture otherwise; nothing in it is asserted over.
    private static readonly Architecture Architecture =
        new ArchLoader().LoadAssemblies(ClientAssembly, typeof(BaseEndpoint).Assembly).Build();

    private static readonly IReadOnlyList<Type> EndpointTypes =
    [
        .. ClientAssembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(BaseEndpoint).IsAssignableFrom(type))
    ];

    private static readonly Regex ClassDeclaration = new(@"\bclass\s+(\w+)", RegexOptions.None, TimeSpan.FromSeconds(5));

    // The lookbehind keeps the rule on FastEndpoints' own verb calls, which an endpoint makes unqualified inside
    // Configure(). A qualified call is somebody else's method that happens to share the name — response.Cookies
    // .Delete(name, options) is a cookie, not a route, and the repo bans this-qualification so nothing legitimate
    // arrives here with a dot in front of it.
    private static readonly Regex VerbCall = new(@"(?<![\w.])(?<call>Get|Post|Put|Delete|Patch|Routes)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly Regex VerbsCall = new(@"(?<![\w.])Verbs\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex MapCall = new(@"\b(?<call>MapHub\s*<[^>]+>|MapMcp|MapGet|MapPost|MapPut|MapDelete|MapPatch)\s*\(",
        RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex MappingFileMarker = new(@"\bMap(Get|Post|Put|Delete|Patch|Hub\s*<|Mcp|Fallback)", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly Regex RouteMemberChain = new(@"^LocalApiRoutes(\.\w+)+$", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly Regex Identifier = new(@"^\w+$", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    ///     Repo-relative path of every file in the host project that declares at least one reflected endpoint type,
    ///     to the type names it declares. Built once: the scan reads the whole project.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EndpointDeclarations = ScanForEndpointDeclarations();

    [Test]
    public void EveryEndpointTypeIsDeclaredInExactlyOneFileNamedAfterIt()
    {
        RequireHostProject();

        AssertEx.True(EndpointTypes.Count >= EndpointFloor,
            $"Expected at least {EndpointFloor} FastEndpoints endpoint types in the host assembly; found {EndpointTypes.Count}. "
            + "The assembly marker or the reflection filter is broken, and every rule below would pass over an empty set.");

        var duplicateNames = EndpointTypes.GroupBy(type => type.Name, StringComparer.Ordinal)
                                          .Where(group => group.Count() > 1)
                                          .Select(group => $"{group.Key}: {string.Join(", ", group.Select(type => type.FullName))}")
                                          .ToList();

        AssertEx.Empty(duplicateNames,
            "Two endpoint types share a simple name, so matching a declaration to a type by name would silently pick "
            + $"one of them and this guard would stop meaning anything. Rename one: {string.Join("; ", duplicateNames)}");

        var filesByTypeName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (file, declared) in EndpointDeclarations)
        {
            foreach (var name in declared)
            {
                if (!filesByTypeName.TryGetValue(name, out var files))
                {
                    files = [];
                    filesByTypeName[name] = files;
                }

                files.Add(file);
            }
        }

        var offenders = new List<string>();
        foreach (var type in EndpointTypes)
        {
            if (!filesByTypeName.TryGetValue(type.Name, out var files))
            {
                offenders.Add($"'{type.FullName}' is an endpoint the compiler knows about but no source file in the host project declares. "
                              + "Either it is generated, or the scan has stopped reading part of the project.");
                continue;
            }

            if (files.Count > 1)
            {
                offenders.Add($"'{type.FullName}' is declared in {files.Count} files ({string.Join(", ", files)}). An endpoint split "
                              + "across partial declarations, or duplicated, has no single file to be named after.");
                continue;
            }

            var file = files[0];
            if (!file.StartsWith(EndpointsPrefix, StringComparison.Ordinal))
            {
                offenders.Add($"'{type.FullName}' is declared in '{file}', outside '{EndpointsPrefix}'. Every endpoint lives under the "
                              + "Endpoints tree, where the area and version folders are the contract's index.");
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith("Endpoints", StringComparison.Ordinal))
            {
                // A plural file is the grouping exception; PluralEndpointFilesAreOnlyTheNamedGroupingExceptions owns it.
                continue;
            }

            var declaredHere = EndpointDeclarations[file];
            if (declaredHere.Count != 1)
            {
                offenders.Add($"'{file}' declares {declaredHere.Count} endpoint types ({string.Join(", ", declaredHere)}). A file holding "
                              + "more than one endpoint is named plurally and listed as a grouping exception, or it is split.");
            }
            else if (!string.Equals(stem, type.Name, StringComparison.Ordinal))
            {
                offenders.Add($"'{file}' declares '{type.Name}'. A file holding one endpoint is named after it, so the type name is the "
                              + "only thing anyone needs to find the file.");
            }
        }

        AssertEx.Empty(offenders,
            $"Endpoint file naming (R1/R2): {string.Join(" | ", offenders)}");
    }

    [Test]
    public void PluralEndpointFilesAreOnlyTheNamedGroupingExceptions()
    {
        RequireHostProject();

        AssertEx.Equal(PluralEndpointFiles.Length, PluralEndpointFiles.Distinct(StringComparer.Ordinal).Count(),
            "The grouping allowlist has a duplicate entry.");
        AssertEx.True(PluralEndpointFiles.SequenceEqual(PluralEndpointFiles.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "The grouping allowlist is kept sorted so a diff to it reads as one added or removed line.");

        var allowed = PluralEndpointFiles.ToHashSet(StringComparer.Ordinal);

        var unlisted = EndpointDeclarations.Keys
                                           .Where(file => file.StartsWith(EndpointsPrefix, StringComparison.Ordinal)
                                                          && Path.GetFileNameWithoutExtension(file).EndsWith("Endpoints", StringComparison.Ordinal))
                                           .Select(file => file[EndpointsPrefix.Length..])
                                           .Where(relative => !allowed.Contains(relative))
                                           .Order(StringComparer.Ordinal)
                                           .ToList();

        AssertEx.Empty(unlisted,
            "A new plural '*Endpoints.cs' file declares endpoints. The rule is one endpoint per file named after its type; "
            + "the listed thirty-four are grandfathered and the list does not grow: "
            + string.Join(", ", unlisted));

        var stale = PluralEndpointFiles
                    .Where(relative => !EndpointDeclarations.TryGetValue(EndpointsPrefix + relative, out var declared) || declared.Count <= 1)
                    .ToList();

        AssertEx.Empty(stale,
            "A grouping exception is no longer one: the file was split, renamed or reduced to a single endpoint. Delete the entry "
            + $"so the allowlist keeps shrinking and never outlives what it excuses: {string.Join(", ", stale)}");
    }

    [Test]
    public void EveryEndpointTypeIsSealed()
    {
        AssertEx.True(EndpointTypes.Count >= EndpointFloor,
            $"Expected at least {EndpointFloor} endpoint types to assert over; found {EndpointTypes.Count}.");

        var rule = Classes().That().AreAssignableTo(typeof(BaseEndpoint)).And().ResideInAssembly(ClientAssembly)
                            .Should().BeSealed().WithoutRequiringPositiveResults();

        if (rule.HasNoViolations(Architecture))
        {
            return;
        }

        AssertEx.True(false,
            "An endpoint is a leaf: FastEndpoints instantiates it by reflection and nothing derives from it, so an unsealed one "
            + "only invites an inheritance chain that the framework will not honour."
            + $"{Environment.NewLine}{rule.Evaluate(Architecture).ToErrorMessage()}");
    }

    [Test]
    public void EveryRouteComesFromLocalApiRoutes()
    {
        RequireHostProject();

        var violations = new List<string>();
        var verbSites = 0;
        var mapSites = 0;

        foreach (var file in HostSourceFiles())
        {
            var relative = RepositoryRelative(file);
            var code = SourceCommentStripper.StripComments(File.ReadAllText(file));
            var isEndpointFile = relative.StartsWith(EndpointsPrefix, StringComparison.Ordinal);
            var isMappingFile = MappingFileMarker.IsMatch(code);

            if (!isEndpointFile && !isMappingFile)
            {
                continue;
            }

            var result = ScanRoutes(relative, code, checkVerbs: isEndpointFile, checkMappings: isMappingFile);
            violations.AddRange(result.Violations);
            verbSites += result.VerbCallSites;
            mapSites += result.MapCallSites;
        }

        AssertEx.True(verbSites >= EndpointFloor,
            $"Expected at least {EndpointFloor} FastEndpoints verb calls under '{EndpointsPrefix}'; found {verbSites}. The scan is broken.");
        AssertEx.True(mapSites >= MappingFloor,
            $"Expected at least {MappingFloor} host route mappings (hubs, the MCP map, the minimal-API maps); found {mapSites}. "
            + "Either the mapping file moved and the enumeration no longer finds it, or the scan is broken.");

        AssertEx.Empty(violations,
            "A route spelled as a literal is a path that only this call site knows. LocalApiRoutes is the one place the SPA client, "
            + $"the MCP surface and the endpoint agree on a path: {string.Join(" | ", violations)}");
    }

    /// <summary>
    ///     The route scan's own check. Without it, a scan that stopped recognising call sites would report zero
    ///     violations forever, and the accepted forms would drift into "anything that mentions LocalApiRoutes".
    /// </summary>
    [Test]
    public void TheRouteScan_AcceptsOnlyRoutesDerivedFromLocalApiRoutes()
    {
        (string Case, string Source)[] accepted =
        [
            ("member chain", """Configure() { Get(LocalApiRoutes.Benchmarks.List); }"""),
            ("several member chains", """Configure() { Routes(LocalApiRoutes.Images.List, LocalApiRoutes.Images.Detail); }"""),
            ("hub map", """app.MapHub<LocalChatHub>(LocalApiRoutes.LocalChat.Hub);"""),
            ("interpolated holes only", """app.MapMcp($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}");"""),
            ("traced local prefix", """
                                    var proxyRoutePrefix = $"/{LocalApiRoutes.Prefix}/";
                                    app.MapGet(proxyRoutePrefix + LocalApiRoutes.Proxy.Models, Handle);
                                    """),
            ("multi-line call", """
                                Configure()
                                {
                                    Get(
                                        LocalApiRoutes.Benchmarks.List);
                                }
                                """),
            ("verbs take enum values", """Configure() { Verbs(Http.GET, Http.POST); Routes(LocalApiRoutes.Images.List); }"""),
            ("a qualified call of the same name is somebody else's method",
                """HandleAsync() { response.Cookies.Delete(RefreshCookieName, CookieOptions()); }""")
        ];

        (string Case, string Source)[] rejected =
        [
            ("zero-hole interpolated literal", """Configure() { Get($"/literal"); }"""),
            ("literal after whitespace", """Configure() { Get ("/literal"); }"""),
            ("literal on the next line", """
                                         Configure()
                                         {
                                             Get(
                                                 "/literal");
                                         }
                                         """),
            ("two concatenated literals", """Configure() { Post("/a" + "/b"); }"""),
            ("verbatim literal", """Configure() { Get(@"/literal"); }"""),
            ("untraceable local", """Configure() { Get(someUnrelatedLocal); }"""),
            ("local whose declaration is a plain literal", """
                                                           var someUnrelatedLocal = ComputeRoute();
                                                           Configure() { Get(someUnrelatedLocal); }
                                                           """),
            ("local that merely spells the type name", """
                                                       var fakeRoute = "LocalApiRoutes";
                                                       Configure() { Get(fakeRoute); }
                                                       """),
            ("second argument is a literal", """Configure() { Get(LocalApiRoutes.Images.List, "/literal"); }"""),
            ("hub mapped to a literal", """app.MapHub<LocalChatHub>("/hubs/chat");"""),
            ("interpolated hole that is not a route", """app.MapMcp($"/{someOtherPrefix}/mcp");"""),
            ("verbs given a string", """Configure() { Verbs(Http.GET, "/literal"); }"""),
            // A literal-skipper that reads @"""a"" b" as a raw literal never finds the closing run, so the argument
            // list never closes and the call site is dropped instead of judged — a silent pass, not a loud failure.
            ("non-route argument after a verbatim string opening with an escaped quote",
                """"Configure() { Get(Wrap(@"""a"" b"), LocalApiRoutes.Images.List); }"""")
        ];

        foreach (var (name, source) in accepted)
        {
            AssertEx.Empty(ScanRoutes(name, source, checkVerbs: true, checkMappings: true).Violations,
                $"The '{name}' fixture is the shape the host really uses and must stay accepted, or this guard becomes a tax on "
                + "writing endpoints rather than a rule about routes.");
        }

        foreach (var (name, source) in rejected)
        {
            AssertEx.NotEmpty(ScanRoutes(name, source, checkVerbs: true, checkMappings: true).Violations,
                $"The '{name}' fixture went unnoticed. It is a literal route, or an expression this rule cannot trace back to "
                + "LocalApiRoutes, and a scan that passes it would pass the real thing too.");
        }
    }

    private static RouteScanResult ScanRoutes(string label, string code, bool checkVerbs, bool checkMappings)
    {
        var violations = new List<string>();
        var verbSites = 0;
        var mapSites = 0;

        if (checkVerbs)
        {
            foreach (Match match in VerbCall.Matches(code))
            {
                var arguments = SplitArguments(code, match.Index + match.Length - 1);
                if (arguments.Count == 0)
                {
                    continue;
                }

                verbSites++;

                // FastEndpoints verb methods take params string[] routePatterns, so every argument is a route.
                foreach (var argument in arguments.Where(argument => !IsRouteExpression(argument, code, depth: 0)))
                {
                    violations.Add($"{label}: {match.Groups["call"].Value}(…) is given '{argument}', which is not a LocalApiRoutes-derived route.");
                }
            }

            foreach (Match match in VerbsCall.Matches(code))
            {
                foreach (var argument in SplitArguments(code, match.Index + match.Length - 1).Where(IsStringLiteral))
                {
                    violations.Add($"{label}: Verbs(…) takes Http enum values, never a route; '{argument}' is a string literal.");
                }
            }
        }

        if (checkMappings)
        {
            foreach (Match match in MapCall.Matches(code))
            {
                var arguments = SplitArguments(code, match.Index + match.Length - 1);
                mapSites++;

                // These take the route as their first argument only; the rest are delegates and options.
                if (arguments.Count == 0 || !IsRouteExpression(arguments[0], code, depth: 0))
                {
                    var shown = arguments.Count == 0 ? "no argument" : $"'{arguments[0]}'";
                    violations.Add($"{label}: {match.Groups["call"].Value}(…) is mapped to {shown}, which is not a LocalApiRoutes-derived route.");
                }
            }
        }

        return new RouteScanResult(violations, verbSites, mapSites);
    }

    /// <summary>
    ///     The three accepted forms, and the one way an identifier earns acceptance: by its own declaration being
    ///     one of them. A textual "this mentions LocalApiRoutes" test would accept
    ///     <c>var fake = "LocalApiRoutes";</c>, which is a literal route wearing the right word.
    /// </summary>
    private static bool IsRouteExpression(string expression, string code, int depth)
    {
        var trimmed = expression.Trim();

        if (trimmed.Length == 0 || depth > 4)
        {
            return false;
        }

        if (RouteMemberChain.IsMatch(trimmed))
        {
            return true;
        }

        var operands = SplitTopLevel(trimmed, '+');
        if (operands.Count > 1)
        {
            return operands.All(operand => IsRouteExpression(operand, code, depth + 1));
        }

        if (IsStringLiteral(trimmed))
        {
            var holes = InterpolationHoles(trimmed);
            return holes.Count > 0 && holes.All(hole => RouteMemberChain.IsMatch(hole.Trim()));
        }

        if (!Identifier.IsMatch(trimmed))
        {
            return false;
        }

        var declaration = SoleDeclarationOf(trimmed, code);
        return declaration is not null && IsRouteExpression(declaration, code, depth + 1);
    }

    /// <summary>
    ///     Resolves <c>var name = …;</c> / <c>const string name = …;</c> in the same file, and only when there is
    ///     exactly one such declaration — two candidates mean the scan cannot know which one reaches the call.
    /// </summary>
    private static string? SoleDeclarationOf(string name, string code)
    {
        var pattern = new Regex($@"\b(?:var|string|const\s+string|readonly\s+string)\s+{Regex.Escape(name)}\s*=\s*",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        var matches = pattern.Matches(code);

        if (matches.Count != 1)
        {
            return null;
        }

        var start = matches[0].Index + matches[0].Length;
        var index = start;

        while (index < code.Length)
        {
            if (IsLiteralStart(code, index))
            {
                index = SkipLiteral(code, index);
                continue;
            }

            if (code[index] == ';')
            {
                return code[start..index];
            }

            index++;
        }

        return null;
    }

    /// <summary>
    ///     Splits an argument list at top level, from the opening parenthesis to its match. Literals are stepped
    ///     over whole, so a comma or parenthesis inside a route string never splits anything.
    /// </summary>
    private static List<string> SplitArguments(string code, int openParen)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = openParen + 1;
        var index = openParen;

        while (index < code.Length)
        {
            if (IsLiteralStart(code, index))
            {
                index = SkipLiteral(code, index);
                continue;
            }

            var current = code[index];

            if (current is '(' or '[' or '{')
            {
                depth++;
                index++;
                continue;
            }

            if (current is ')' or ']' or '}')
            {
                depth--;

                if (depth == 0)
                {
                    Add(code[start..index]);
                    return arguments;
                }

                index++;
                continue;
            }

            if (current == ',' && depth == 1)
            {
                Add(code[start..index]);
                start = index + 1;
            }

            index++;
        }

        return arguments;

        void Add(string argument)
        {
            var trimmed = argument.Trim();

            if (trimmed.Length > 0)
            {
                arguments.Add(trimmed);
            }
        }
    }

    private static List<string> SplitTopLevel(string expression, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var index = 0;

        while (index < expression.Length)
        {
            if (IsLiteralStart(expression, index))
            {
                index = SkipLiteral(expression, index);
                continue;
            }

            var current = expression[index];

            if (current is '(' or '[' or '{')
            {
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                depth--;
            }
            else if (current == separator && depth == 0)
            {
                parts.Add(expression[start..index]);
                start = index + 1;
            }

            index++;
        }

        parts.Add(expression[start..]);
        return parts;
    }

    /// <summary>
    ///     The holes of a simple <c>$"…"</c> literal, hole expressions only (an alignment or format suffix is not
    ///     part of the route). Verbatim and raw interpolated literals return nothing and are therefore rejected: no
    ///     route is written that way today, and widening the rule should be a decision, not an accident.
    /// </summary>
    private static List<string> InterpolationHoles(string literal)
    {
        var holes = new List<string>();

        if (!literal.StartsWith("$\"", StringComparison.Ordinal) || literal.StartsWith("$\"\"\"", StringComparison.Ordinal))
        {
            return holes;
        }

        var index = 2;
        while (index < literal.Length)
        {
            var current = literal[index];

            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == '"')
            {
                return holes;
            }

            if (current is '{' or '}' && index + 1 < literal.Length && literal[index + 1] == current)
            {
                index += 2;
                continue;
            }

            if (current != '{')
            {
                index++;
                continue;
            }

            var depth = 0;
            var start = ++index;
            var end = -1;

            while (index < literal.Length)
            {
                if (IsLiteralStart(literal, index))
                {
                    index = SkipLiteral(literal, index);
                    continue;
                }

                var inner = literal[index];

                if (inner is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (inner is ')' or ']')
                {
                    depth--;
                }
                else if (inner == '}')
                {
                    if (depth == 0)
                    {
                        end = index;
                        break;
                    }

                    depth--;
                }
                else if (inner == ':' && depth == 0)
                {
                    end = index;
                    break;
                }

                index++;
            }

            if (end < 0)
            {
                return holes;
            }

            holes.Add(literal[start..end]);

            while (index < literal.Length && literal[index] != '}')
            {
                index++;
            }

            index++;
        }

        return holes;
    }

    private static bool IsStringLiteral(string expression)
    {
        var index = 0;

        while (index < expression.Length && expression[index] is '$' or '@')
        {
            index++;
        }

        return index < expression.Length && expression[index] == '"';
    }

    private static bool IsLiteralStart(string text, int index)
    {
        var current = text[index];

        if (current is '"' or '\'')
        {
            return true;
        }

        if (current is not ('$' or '@'))
        {
            return false;
        }

        var scan = index;
        while (scan < text.Length && text[scan] is '$' or '@')
        {
            scan++;
        }

        return scan < text.Length && text[scan] == '"';
    }

    /// <summary>Returns the index just past the literal that starts at <paramref name="index" />.</summary>
    private static int SkipLiteral(string text, int index)
    {
        var verbatim = false;
        var scan = index;

        while (scan < text.Length && text[scan] is '$' or '@')
        {
            verbatim |= text[scan] == '@';
            scan++;
        }

        if (scan >= text.Length)
        {
            return scan;
        }

        if (text[scan] == '\'')
        {
            scan++;
            while (scan < text.Length)
            {
                if (text[scan] == '\\')
                {
                    scan += 2;
                    continue;
                }

                if (text[scan] == '\'')
                {
                    return scan + 1;
                }

                scan++;
            }

            return scan;
        }

        var quotes = 0;
        while (scan + quotes < text.Length && text[scan + quotes] == '"')
        {
            quotes++;
        }

        // Verbatim wins over the quote run, as in SourceCommentStripper: @ and raw literals are mutually exclusive,
        // so @"""a"" b" opens with an escaped quote rather than a raw delimiter. Testing the run first reads it as a
        // raw literal with no closing run, and every argument list after it goes unparsed.
        if (!verbatim && quotes >= 3)
        {
            scan += quotes;
            while (scan < text.Length)
            {
                if (text[scan] != '"')
                {
                    scan++;
                    continue;
                }

                var run = 0;
                while (scan + run < text.Length && text[scan + run] == '"')
                {
                    run++;
                }

                scan += run;

                if (run >= quotes)
                {
                    return scan;
                }
            }

            return scan;
        }

        scan++;
        while (scan < text.Length)
        {
            if (!verbatim && text[scan] == '\\')
            {
                scan += 2;
                continue;
            }

            if (text[scan] == '"')
            {
                if (verbatim && scan + 1 < text.Length && text[scan + 1] == '"')
                {
                    scan += 2;
                    continue;
                }

                return scan + 1;
            }

            scan++;
        }

        return scan;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ScanForEndpointDeclarations()
    {
        var endpointNames = EndpointTypes.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var declarations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var file in HostSourceFiles())
        {
            var code = SourceCommentStripper.StripComments(File.ReadAllText(file));
            var declared = ClassDeclaration.Matches(code)
                                           .Select(match => match.Groups[1].Value)
                                           .Where(endpointNames.Contains)
                                           .ToList();

            if (declared.Count > 0)
            {
                declarations[RepositoryRelative(file)] = declared;
            }
        }

        return declarations;
    }

    private static IEnumerable<string> HostSourceFiles()
    {
        var project = RepositoryPaths.ClientProject();

        if (!Directory.Exists(project))
        {
            return [];
        }

        return Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                        .Where(file =>
                        {
                            var relative = Path.GetRelativePath(project, file).Replace('\\', '/');
                            return !relative.StartsWith("bin/", StringComparison.Ordinal) && !relative.StartsWith("obj/", StringComparison.Ordinal);
                        });
    }

    private static string RepositoryRelative(string file)
    {
        return Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');
    }

    private static void RequireHostProject()
    {
        var project = RepositoryPaths.ClientProject();

        if (!Directory.Exists(project))
        {
            throw new SkipTestException($"SKIPPED — '{project}' does not exist, so this guard would scan nothing and pass over an empty set.");
        }
    }

    private sealed record RouteScanResult(IReadOnlyList<string> Violations, int VerbCallSites, int MapCallSites);
}
