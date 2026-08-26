namespace XE_Local_AI_Engine.Tests.CloudSettings;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The save policy owns every custom-header and host-suffix rule instead of inlining them in the endpoint.
///     It must see the PREVIOUSLY STORED headers: a blank secret header is legal only when a stored secret of the same
///     name will merge into it, and messages must never carry a header value.
/// </summary>
public sealed class CloudSettingsPolicyTests
{
    [Test]
    public void ValidHeadersAndSuffixes_ProduceNoErrors()
    {
        var errors = Validate([Header("X-Trace", "abc"), Header("X-Secret", "s3cr3t", isSecret: true)],
            [".azure-api.net"]);

        AssertEx.Empty(errors);
    }

    [Test]
    public void ReservedName_InvalidCharset_AndDuplicate_AreEachRejected()
    {
        var errors = Validate([Header("Authorization", "x"), Header("Bad Name", "x"), Header("X-Dup", "1"), Header("x-dup", "2")], []);

        AssertEx.Contains(errors, "Custom header name 'Authorization' is reserved and cannot be set.");
        AssertEx.Contains(errors, "Custom header name 'Bad Name' contains invalid characters.");
        AssertEx.Contains(errors, "Custom header name 'x-dup' is duplicated.");
    }

    [Test]
    public void BlankName_WithValue_IsRejected_ButFullyBlankRowIsIgnored()
    {
        var withValue = Validate([Header(" ", "orphan")], []);
        AssertEx.Contains(withValue, "A custom header value was provided without a header name.");

        var blankRow = Validate([Header(string.Empty, value: null)], []);
        AssertEx.Empty(blankRow);
    }

    [Test]
    public void BlankSecretHeader_IsRejected_UnlessAStoredSecretOfTheSameNameExists()
    {
        var fresh = Validate([Header("X-Api-Secret", value: null, isSecret: true)], []);
        AssertEx.Contains(fresh, "Secret custom header 'X-Api-Secret' requires a value.");

        var merged = Validate([Header("X-Api-Secret", value: null, isSecret: true)],
            [],
            existingHeaders: [Header("x-api-secret", "stored", isSecret: true)]);
        AssertEx.Empty(merged);

        // A stored NON-secret header of the same name is not a secret to merge against.
        var storedNonSecret = Validate([Header("X-Api-Secret", value: null, isSecret: true)],
            [],
            existingHeaders: [Header("X-Api-Secret", "stored")]);
        AssertEx.Contains(storedNonSecret, "Secret custom header 'X-Api-Secret' requires a value.");
    }

    [Test]
    public void ControlCharacterValue_AndOverlongName_AreRejected_WithoutLeakingTheValue()
    {
        var longName = new string('a', AzureFoundryHeaderRules.MaxHeaderNameLength + 1);
        var errors = Validate([Header("X-Crlf", "bad\r\nvalue"), Header(longName, "x")], []);

        AssertEx.Contains(errors, "Custom header 'X-Crlf' value contains invalid control characters.");
        AssertEx.Contains(errors, $"Custom header name '{longName}' exceeds {AzureFoundryHeaderRules.MaxHeaderNameLength} characters.");
        AssertEx.Empty(errors.Where(error => error.Contains("bad", StringComparison.Ordinal)));
    }

    [Test]
    public void OverCaps_AreRejected()
    {
        var headers = Enumerable.Range(0, AzureFoundryHeaderRules.MaxHeaderCount + 1)
                                .Select(index => Header($"X-H{index}", "v"))
                                .ToArray();
        var suffixes = Enumerable.Range(0, AzureFoundryHeaderRules.MaxHostSuffixCount + 1)
                                 .Select(index => $".host{index}.example.com")
                                 .ToArray();

        var errors = Validate(headers, suffixes);

        AssertEx.Contains(errors, $"A maximum of {AzureFoundryHeaderRules.MaxHeaderCount} custom headers is allowed.");
        AssertEx.Contains(errors, $"A maximum of {AzureFoundryHeaderRules.MaxHostSuffixCount} allowed host suffixes is allowed.");
    }

    [Test]
    public void MalformedHostSuffix_IsRejected_AndBlankSuffixIsIgnored()
    {
        var errors = Validate([], ["azure-api.net", "  ", null]);

        AssertEx.ContainsSingle(errors, error => error == "Allowed host suffix 'azure-api.net' is not a valid domain suffix.");
    }

    private static StoredAzureFoundryHeader Header(string name, string? value, bool isSecret = false)
    {
        return new StoredAzureFoundryHeader
        {
            Name = name,
            Value = value,
            IsSecret = isSecret
        };
    }

    private static IReadOnlyList<string> Validate(IReadOnlyList<StoredAzureFoundryHeader> headers,
        IReadOnlyList<string?> suffixes,
        IReadOnlyList<StoredAzureFoundryHeader>? existingHeaders = null)
    {
        return CloudSettingsPolicy.ValidateHeadersAndSuffixes(headers, suffixes, existingHeaders ?? []);
    }
}
