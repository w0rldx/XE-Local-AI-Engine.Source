namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.ComponentModel.DataAnnotations;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two members no data annotation can state, and the defaults the feature flag depends on. A mistyped
///     instance root that only surfaced at the first install would look like a storage failure on the instance rather
///     than the configuration error it is.
/// </summary>
public sealed class ExternalAppsOptionsValidatorTests
{
    [Test]
    public void Defaults_LeaveTheFeatureOff()
    {
        var options = new ExternalAppsOptions();

        AssertEx.False(options.Enabled, "External Apps must be off until an operator turns it on.");
        AssertEx.Null(options.InstanceRoot);
        AssertEx.Null(options.ContainerIdentity);
        AssertEx.Equal(expected: 300, options.ServiceReadyTimeoutSeconds);
        AssertEx.Equal(expected: 30, options.StopGracePeriodSeconds);
        AssertEx.Equal(expected: 2000, options.MaxLogTailLines);
        AssertEx.Equal(expected: 15, options.ObserverIntervalSeconds);
        AssertEx.Equal("ExternalApps", ExternalAppsOptions.SectionName);

        // The storage helper runs as in-container root over an application's data, so its default must be pinned
        // like every other image this feature runs.
        AssertEx.Contains(options.StorageHelperImage, "@sha256:");
    }

    [Test]
    public void Validate_WithNeitherOverrideSet_Succeeds()
    {
        var result = new ExternalAppsOptionsValidator().Validate(name: null, new ExternalAppsOptions());

        AssertEx.True(result.Succeeded, "Defaults must validate.");
    }

    [Test]
    public void Validate_WithAnAbsoluteInstanceRootAndAUidGidIdentity_Succeeds()
    {
        var options = new ExternalAppsOptions
        {
            InstanceRoot = Path.Combine(Path.GetTempPath(), "xe-external-apps"),
            ContainerIdentity = "1000:1000"
        };

        var result = new ExternalAppsOptionsValidator().Validate(name: null, options);

        AssertEx.True(result.Succeeded, "An absolute root and a uid:gid identity are the supported shapes.");
    }

    /// <summary>
    ///     The catalog's own digest rule, not a substring test for <c>@sha256:</c>. The last four cases are what a
    ///     substring test admits: a truncated digest, a non-hex one, an uppercase one, and a reference that is nothing
    ///     but the digest. Each of them lets a different image answer to the configured name, and this one runs as
    ///     in-container root over an application's data.
    /// </summary>
    [Test]
    [Arguments("busybox:1.37")]
    [Arguments("busybox")]
    [Arguments("")]
    [Arguments("busybox@sha256:9db7b5")]
    [Arguments("busybox@sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [Arguments("busybox@sha256:9DB7B59979C38555A39DEF84A31FB98B5296952F9E3AFD4F6F11F05B07ADFAB0")]
    [Arguments("@sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0")]
    public void Validate_WithAStorageHelperImageThatIsNotDigestPinned_FailsNamingTheKey(string image)
    {
        var result = new ExternalAppsOptionsValidator().Validate(name: null, new ExternalAppsOptions { StorageHelperImage = image });

        AssertEx.True(result.Failed, $"'{image}' is not digest-pinned and must be refused at start-up.");
        AssertEx.Contains(result.FailureMessage, "ExternalApps:StorageHelperImage");
    }

    [Test]
    public void Validate_WithADigestPinnedStorageHelperImage_Succeeds()
    {
        var options = new ExternalAppsOptions
        {
            StorageHelperImage = "ghcr.io/example/helper@sha256:00000000000000000000000000000000000000000000000000000000000000aa"
        };

        var result = new ExternalAppsOptionsValidator().Validate(name: null, options);

        AssertEx.True(result.Succeeded, "A digest-pinned override is the supported shape; without this the test above proves only that everything fails.");
    }

    [Test]
    public void Validate_WithARelativeInstanceRoot_FailsNamingTheKey()
    {
        var result = new ExternalAppsOptionsValidator().Validate(name: null, new ExternalAppsOptions { InstanceRoot = "relative/path" });

        AssertEx.True(result.Failed, "A relative instance root must be refused at start-up.");
        AssertEx.Contains(result.FailureMessage, "ExternalApps:InstanceRoot");
    }

    [Test]
    [Arguments("1000")]
    [Arguments("1000:")]
    [Arguments("abc:def")]
    [Arguments("-1:0")]
    [Arguments("1000:1000:1000")]
    public void Validate_WithAMalformedContainerIdentity_FailsNamingTheKey(string identity)
    {
        var result = new ExternalAppsOptionsValidator().Validate(name: null, new ExternalAppsOptions { ContainerIdentity = identity });

        AssertEx.True(result.Failed, $"'{identity}' is not a uid:gid pair.");
        AssertEx.Contains(result.FailureMessage, "ExternalApps:ContainerIdentity");
    }

    /// <summary>
    ///     The ranges are declared as annotations, so this asserts that the binding path really evaluates them rather
    ///     than that the attribute is present: an out-of-range value must not start the node.
    /// </summary>
    [Test]
    public void DataAnnotations_RejectAnObserverIntervalBelowTheFloor()
    {
        var options = new ExternalAppsOptions { ObserverIntervalSeconds = 1 };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        AssertEx.False(valid, "A one-second observer interval must be refused.");
        AssertEx.Contains(results, static result => result.MemberNames.Contains(nameof(ExternalAppsOptions.ObserverIntervalSeconds)));
    }
}
