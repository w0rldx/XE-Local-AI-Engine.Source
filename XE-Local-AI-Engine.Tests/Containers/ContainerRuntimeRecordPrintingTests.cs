namespace XE_Local_AI_Engine.Tests.Containers;

using System.Reflection;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A container specification carries the application's decrypted variables — an admin password among them — and a
///     record's compiler-generated <c>ToString()</c> prints every property. One <c>LogDebug("{Spec}", spec)</c>
///     downstream would therefore write that password into the node log, where it outlives the instance and is read by
///     whoever collects logs.
///     <para>
///         Suppressing the printer makes that impossible rather than merely forbidden, which is why this is a test and
///         not a review note: a reviewer cannot see the log statement a later slice adds, and this can.
///     </para>
/// </summary>
public sealed class ContainerRuntimeRecordPrintingTests
{
    private const string KnownSecret = "s3cr3t-odysseus-admin-password";

    [Test]
    public void ContainerSpecification_ToString_DoesNotContainAnEnvironmentValue()
    {
        var rendered = Specification().ToString();

        AssertEx.NotNullOrEmpty(rendered);
        AssertEx.False(rendered.Contains(KnownSecret, StringComparison.Ordinal),
            $"ContainerSpecification.ToString() printed a decrypted variable value: {rendered}");
        AssertEx.False(rendered.Contains("ODYSSEUS_ADMIN_PASSWORD", StringComparison.Ordinal),
            $"ContainerSpecification.ToString() printed an environment key: {rendered}");
    }

    [Test]
    public void ContainerSpecification_ToString_PrintsNoPropertyAtAll()
    {
        // Not "no secrets" but "nothing": an allow-list of safe properties would be one edit away from printing the
        // map again, and the record has no property a log line needs badly enough to keep the printer alive for.
        var rendered = Specification().ToString();

        AssertEx.Equal(nameof(ContainerSpecification) + " { }", rendered);
    }

    /// <summary>
    ///     R2-7 by enumeration rather than by inspection: any record in this namespace that carries an environment map
    ///     must suppress its printer the same way. The check is what keeps the rule true for records a later slice
    ///     adds, since nothing else in the tree would notice a new one.
    /// </summary>
    [Test]
    public void EveryRecordCarryingAnEnvironmentMap_SuppressesItsPrinter()
    {
        var carriers = typeof(ContainerSpecification).Assembly
                                                     .GetTypes()
                                                     .Where(static type => type.Namespace == typeof(ContainerSpecification).Namespace)
                                                     .Where(CarriesAnEnvironmentMap)
                                                     .ToArray();

        AssertEx.NotEmpty(carriers,
            "No record in the containers namespace carries an environment map any more. If that is deliberate the "
            + "suppression rule has lost its subject; if it is not, this test has stopped guarding anything.");
        AssertEx.Contains(carriers, typeof(ContainerSpecification));

        foreach (var carrier in carriers)
        {
            var printer = carrier.GetMethod("PrintMembers",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                [typeof(StringBuilder)],
                modifiers: null);

            AssertEx.NotNull(printer,
                $"{carrier.Name} carries an environment map and does not declare its own "
                + "`private bool PrintMembers(StringBuilder)`, so its generated ToString() prints every decrypted "
                + "value it holds.");
            AssertEx.True(printer!.IsPrivate,
                $"{carrier.Name}.PrintMembers must be private. On a sealed record whose base is object the compiler "
                + "recognises only that signature as the printer; `protected override` does not compile, and any "
                + "other shape is a method the compiler ignores while the real printer keeps printing.");
        }
    }

    private static bool CarriesAnEnvironmentMap(Type type)
    {
        return type.GetProperty("Environment", BindingFlags.Instance | BindingFlags.Public) is { } property
               && property.PropertyType == typeof(IReadOnlyDictionary<string, string>);
    }

    private static ContainerSpecification Specification()
    {
        return new ContainerSpecification
        {
            Image = "ghcr.io/example/app@sha256:0000000000000000000000000000000000000000000000000000000000000000",
            Name = "xe-app-odysseus",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.instance"] = "instance-1"
            },
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ODYSSEUS_ADMIN_PASSWORD"] = KnownSecret
            },
            Mounts = [],
            PublishedPorts = [],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = [],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = false,
            NetworkName = "xe-app-odysseus-net",
            NetworkAliases = ["odysseus"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512
        };
    }
}
