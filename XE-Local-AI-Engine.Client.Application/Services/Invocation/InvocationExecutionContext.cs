namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Everything one invocation needs beyond its <see cref="Client.Models.RuntimePackage" />: the assistant message
///     it answers, the optional admission policy, and the timings the entry path already measured.
/// </summary>
/// <remarks>
///     Every invocation is built through <see cref="CreatePlain" />, and the type holds no key material, so it owns
///     nothing to zero on dispose.
/// </remarks>
public sealed class InvocationExecutionContext
{
    public required Client.Models.RuntimePackage Package { get; init; }

    public required Guid MessageId { get; init; }

    /// <summary>
    ///     Optional post-warm, pre-generation admission policy. Existing chat and scheduler callers leave it unset and
    ///     retain their current behavior.
    /// </summary>
    public IInvocationGenerationAdmissionPolicy? GenerationAdmissionPolicy { get; init; }

    /// <summary>
    ///     Optional monotonic timestamp captured by the product entry path before chat admission/context/persistence.
    ///     The runner uses it only for end-to-end harness latency; callers that leave it unset get the runner-entry
    ///     timestamp.
    /// </summary>
    public long? HarnessStartedTimestamp { get; init; }

    /// <summary>Elapsed pre-run chat admission/context/persistence time, when supplied by the local chat path.</summary>
    public double? PreRunDurationMs { get; init; }

    /// <summary>Elapsed collision-slot queue time, when supplied by the local chat path.</summary>
    public double? QueueDurationMs { get; init; }

    public static InvocationExecutionContext CreatePlain(Client.Models.RuntimePackage package,
        Guid messageId,
        long? harnessStartedTimestamp = null,
        double? preRunDurationMs = null,
        double? queueDurationMs = null,
        IInvocationGenerationAdmissionPolicy? generationAdmissionPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            GenerationAdmissionPolicy = generationAdmissionPolicy,
            HarnessStartedTimestamp = harnessStartedTimestamp,
            PreRunDurationMs = preRunDurationMs,
            QueueDurationMs = queueDurationMs
        };
    }
}
