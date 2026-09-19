namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Refuses a <c>required</c> property that the serializer is allowed to leave out of the JSON, because the two
///     rules contradict each other on the wire.
/// </summary>
/// <remarks>
///     NJsonSchema derives the OpenAPI <c>required</c> array from the <c>required</c> modifier alone, and the
///     generated Zod schema then rejects a response missing the key — which a <c>WhenWriting…</c> condition on the
///     same member produces on purpose. Reflection over the product assemblies, so the rule holds wherever the type
///     lives. Rationale: <c>docs/wiki/16-code-conventions.md</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class RequiredMemberSerializationTests
{
    /// <summary>
    ///     The product assemblies whose types can reach a JSON payload. Every assembly that declares a
    ///     <c>required</c> member belongs here: the rule is about the member, not about where it lives, and an
    ///     assembly outside this list is not scanned at all, so a violation in it passes in silence. The provider
    ///     assemblies each own the wire shape of one runtime or model source and were the gap.
    /// </summary>
    private static readonly Assembly[] ProductAssemblies =
    [
        typeof(WorkerHealthCheck).Assembly,
        typeof(RuntimePackageValidationResult).Assembly,
        typeof(NodeChatDbContext).Assembly,
        typeof(IInvocationAgentFactory).Assembly,
        typeof(TelemetrySourceNames).Assembly,
        typeof(ILocalModelProvider).Assembly,
        typeof(IProcessLaunchAdmissionLease).Assembly,
        typeof(WhisperCppReleasePins).Assembly,
        typeof(IStableDiffusionManagedSourceBuildSignal).Assembly,
        typeof(GgufMetadataReaderServiceCollectionExtensions).Assembly,
        typeof(ITrainingProcessHandle).Assembly,
        typeof(RunningModelSnapshotMapper).Assembly,
        typeof(ICodexAuthService).Assembly,
        typeof(CapabilitiesServiceCollectionExtensions).Assembly,
        typeof(ExternalProviderConstants).Assembly,
        typeof(OpenAICompatibleRequestBody).Assembly
    ];

    /// <summary>
    ///     Non-vacuity floors, set well under the real counts: a reflection walk that stopped seeing either
    ///     attribute would find nothing to forbid and pass in silence.
    /// </summary>
    private const int ConditionallyIgnoredFloor = 5;
    private const int RequiredPropertyFloor = 2000;

    [Test]
    public void NoRequiredProperty_IsConditionallyOmittedFromTheJson()
    {
        var offenders = new List<string>();
        var conditionallyIgnored = 0;
        var requiredProperties = 0;

        foreach (var type in ProductAssemblies.SelectMany(SafeTypes))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isRequired = property.IsDefined(typeof(RequiredMemberAttribute), inherit: false);
                var condition = property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false)?.Condition;
                var isConditional = condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault;

                if (isRequired)
                {
                    requiredProperties++;
                }

                if (isConditional)
                {
                    conditionallyIgnored++;
                }

                if (isRequired && isConditional)
                {
                    offenders.Add($"{type.FullName}.{property.Name} ({condition})");
                }
            }
        }

        AssertEx.True(conditionallyIgnored >= ConditionallyIgnoredFloor,
            $"Only {conditionallyIgnored} conditionally ignored properties were seen, below the floor of "
            + $"{ConditionallyIgnoredFloor}. The JsonIgnore side of the scan has stopped working.");
        AssertEx.True(requiredProperties >= RequiredPropertyFloor,
            $"Only {requiredProperties} required properties were seen, below the floor of {RequiredPropertyFloor}. "
            + "The required-member side of the scan has stopped working.");

        AssertEx.Empty(offenders,
            "A conditionally omitted member cannot be required: the OpenAPI schema marks it required and the client "
            + "rejects the response that omits it. Drop the required modifier from the member(s) below, or drop the "
            + "condition and write the key every time:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Select(type => type!);
        }
    }
}
