namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ApprovedImageResolver" /> tests: the reusable approved-image guard rejects unknown, disabled,
///     deprecated, purpose-mismatched, and invalid-reference descriptors, and resolves a valid enabled one.
/// </summary>
public sealed class ApprovedImageResolverTests
{
    private const string ValidReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    private static ApprovedImageResolver CreateResolver(params ApprovedUtilityImageRecord[] descriptors)
    {
        return new ApprovedImageResolver(new FakeApprovedUtilityImageStore(descriptors), new ApprovedImageReferenceValidator());
    }

    private static ApprovedUtilityImageRecord Descriptor(string id = "llmfit-recommender-0-9-30",
        UtilityImagePurpose purpose = UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
        string imageReference = ValidReference,
        bool enabled = true,
        long? deprecatedAtUtc = null)
    {
        return new ApprovedUtilityImageRecord(ApprovedImageId: id,
            DisplayName: "llmfit",
            Description: null,
            Purpose: purpose,
            ImageReference: imageReference,
            SourceUrl: null,
            UpstreamVersion: "0.9.30",
            Enabled: enabled,
            DeprecatedAtUtc: deprecatedAtUtc,
            ReplacementApprovedImageId: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LastUsedAtUtc: null,
            LastSuccessfulRunAtUtc: null,
            DiagnosticsJson: null);
    }

    [Test]
    public async Task ResolveAsync_WhenEnabledAndSanctioned_ResolvesValidatedReference()
    {
        var resolver = CreateResolver(Descriptor());

        var resolution = await resolver.ResolveAsync("llmfit-recommender-0-9-30", ModelFitOperation.Recommend);

        AssertEx.True(resolution.IsResolved, "an enabled, sanctioned, validly-referenced descriptor must resolve.");
        AssertEx.Equal(ValidReference, resolution.ImageReference!);
        AssertEx.Equal(ApprovedImageRejectionCode.None, resolution.RejectionCode);
    }

    [Test]
    public async Task ResolveAsync_WhenNotFound_RejectsNotFound()
    {
        var resolver = CreateResolver();

        var resolution = await resolver.ResolveAsync("missing", ModelFitOperation.Recommend);

        AssertEx.False(resolution.IsResolved);
        AssertEx.Equal(ApprovedImageRejectionCode.NotFound, resolution.RejectionCode);
    }

    [Test]
    public async Task ResolveAsync_WhenDisabled_RejectsDisabled()
    {
        var resolver = CreateResolver(Descriptor(enabled: false));

        var resolution = await resolver.ResolveAsync("llmfit-recommender-0-9-30", ModelFitOperation.Recommend);

        AssertEx.False(resolution.IsResolved);
        AssertEx.Equal(ApprovedImageRejectionCode.Disabled, resolution.RejectionCode);
    }

    [Test]
    public async Task ResolveAsync_WhenDeprecated_RejectsDeprecated()
    {
        var resolver = CreateResolver(Descriptor(deprecatedAtUtc: 1_700_000_000));

        var resolution = await resolver.ResolveAsync("llmfit-recommender-0-9-30", ModelFitOperation.Recommend);

        AssertEx.False(resolution.IsResolved);
        AssertEx.Equal(ApprovedImageRejectionCode.Deprecated, resolution.RejectionCode);
    }

    [Test]
    public async Task ResolveAsync_WhenPurposeDoesNotCoverOperation_RejectsPurposeMismatch()
    {
        // A recommendation-only descriptor cannot serve a benchmark.
        var resolver = CreateResolver(Descriptor(purpose: UtilityImagePurpose.ModelRecommendation));

        var resolution = await resolver.ResolveAsync("llmfit-recommender-0-9-30", ModelFitOperation.Benchmark);

        AssertEx.False(resolution.IsResolved);
        AssertEx.Equal(ApprovedImageRejectionCode.PurposeMismatch, resolution.RejectionCode);
    }

    [Test]
    public async Task ResolveAsync_WhenStoredReferenceIsInvalid_RejectsInvalidReference()
    {
        // Defense in depth against drift: a descriptor whose stored reference is not canonical must never resolve.
        var resolver = CreateResolver(Descriptor(imageReference: "ghcr.io/alexsjones/llmfit:latest"));

        var resolution = await resolver.ResolveAsync("llmfit-recommender-0-9-30", ModelFitOperation.Recommend);

        AssertEx.False(resolution.IsResolved);
        AssertEx.Equal(ApprovedImageRejectionCode.InvalidReference, resolution.RejectionCode);
    }

    private sealed class FakeApprovedUtilityImageStore : IApprovedUtilityImageStore
    {
        private readonly Dictionary<string, ApprovedUtilityImageRecord> _records;

        public FakeApprovedUtilityImageStore(IEnumerable<ApprovedUtilityImageRecord> records)
        {
            _records = records.ToDictionary(record => record.ApprovedImageId, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<ApprovedUtilityImageRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ApprovedUtilityImageRecord>>(_records.Values.ToArray());
        }

        public Task<ApprovedUtilityImageRecord?> GetByIdAsync(string approvedImageId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_records.GetValueOrDefault(approvedImageId));
        }

        public Task<ApprovedUtilityImageRecord> UpsertSeedAsync(ApprovedUtilityImageRecord record, CancellationToken cancellationToken = default)
        {
            _records[record.ApprovedImageId] = record;
            return Task.FromResult(record);
        }

        public Task<ApprovedUtilityImageRecord?> SetEnabledAsync(string approvedImageId, bool enabled, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(approvedImageId, out var record))
            {
                return Task.FromResult<ApprovedUtilityImageRecord?>(null);
            }

            var updated = record with
            {
                Enabled = enabled
            };
            _records[approvedImageId] = updated;
            return Task.FromResult<ApprovedUtilityImageRecord?>(updated);
        }

        public Task<ApprovedUtilityImageRecord?> TouchUsedAsync(string approvedImageId,
            long lastUsedAtUtc,
            long? lastSuccessfulRunAtUtc = null,
            CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(approvedImageId, out var record))
            {
                return Task.FromResult<ApprovedUtilityImageRecord?>(null);
            }

            var updated = record with
            {
                LastUsedAtUtc = lastUsedAtUtc,
                LastSuccessfulRunAtUtc = lastSuccessfulRunAtUtc ?? record.LastSuccessfulRunAtUtc
            };
            _records[approvedImageId] = updated;
            return Task.FromResult<ApprovedUtilityImageRecord?>(updated);
        }
    }
}
