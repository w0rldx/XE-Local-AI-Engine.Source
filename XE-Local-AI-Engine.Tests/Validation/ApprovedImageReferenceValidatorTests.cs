namespace XE_Local_AI_Engine.Tests.Validation;

using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ApprovedImageReferenceValidatorTests
{
    private const string ValidReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    private readonly ApprovedImageReferenceValidator _validator = new();

    [Test]
    public void Validate_WhenReferenceIsCanonicalAndAllowlisted_IsValidAndParsesParts()
    {
        var result = _validator.Validate(ValidReference);

        AssertEx.True(result.IsValid, "The verified-correct llmfit reference must validate.");
        AssertEx.Equal("ghcr.io/alexsjones/llmfit", result.Repository);
        AssertEx.Equal("0.9.30", result.Tag);
        AssertEx.Equal("sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c", result.Digest);
        AssertEx.Null(result.Error);
    }

    [Test]
    public void IsValid_WhenReferenceIsCanonicalAndAllowlisted_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid(ValidReference));
    }

    // -------------------------------------------------------------------------
    // Rejection cases — each must be IsValid == false with a specific error.
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_WhenReferenceIsNull_IsInvalid()
    {
        AssertRejected(_validator.Validate(null));
    }

    [Test]
    public void Validate_WhenReferenceIsEmpty_IsInvalid()
    {
        AssertRejected(_validator.Validate(string.Empty));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:latest@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    public void Validate_WhenTagIsLatest_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30")]
    public void Validate_WhenDigestIsMissing_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    public void Validate_WhenTagIsMissing_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a519")]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    public void Validate_WhenDigestIsMalformed_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@sha512:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c465a5197257a3d34a22a52b1e4ea5aec")]
    public void Validate_WhenDigestAlgorithmIsNotSha256_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465A5197257A3D34A22A52B1E4EA5AECEFC1973788C0F6A0A8FD5A4F93C7F93C")]
    public void Validate_WhenDigestHexIsUppercase_IsInvalidAndNotLowercased(string reference)
    {
        // The validator must reject uppercase hex, never silently lowercase an untrusted reference into a trusted one.
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("docker.io/library/alpine:3.20@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    public void Validate_WhenRepositoryIsNotAllowlisted_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments(" ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c ")]
    public void Validate_WhenReferenceHasSurroundingWhitespace_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    public void Validate_WhenReferenceHasMoreThanOneAt_IsInvalid(string reference)
    {
        AssertRejected(_validator.Validate(reference));
    }

    [Test]
    public void Validate_WithCustomAllowlist_AcceptsOnlyTheAllowlistedRepository()
    {
        var customAllowlist = new HashSet<string>(StringComparer.Ordinal) { "ghcr.io/example/util" };
        var validator = new ApprovedImageReferenceValidator(customAllowlist);
        const string accepted =
            "ghcr.io/example/util:1.2.3@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

        AssertEx.True(validator.IsValid(accepted), "A reference matching the custom allowlist must validate.");
        // The default llmfit repository is not on the custom allowlist, so it must now be rejected.
        AssertRejected(validator.Validate(ValidReference));
    }

    private static void AssertRejected(ImageReferenceValidationResult result)
    {
        AssertEx.False(result.IsValid, "Reference should have been rejected.");
        AssertEx.NotNull(result.Error, "A rejected reference must carry a specific error.");
    }
}
