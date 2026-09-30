namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>Everything one deployment attempt will create, in the order to create it.</summary>
internal sealed record DeploymentPlan(string NetworkName, IReadOnlyList<ServiceDeployment> Services)
{
    // The services carry environments holding values decrypted from the instance's variables, so a structured log of
    // a plan would write an admin password to the node log. Why these four analyzers: ContainerSpecification.PrintMembers.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>One service of a plan: what to create, what it must wait for, and what it publishes.</summary>
internal sealed record ServiceDeployment(
    string ServiceName,
    string ContainerName,
    ContainerSpecification Specification,
    IReadOnlyList<ServiceDependency> DependsOn,
    IReadOnlyList<int> UiContainerPorts)
{
#pragma warning disable CA1822, S2325, S1172, IDE0060 // Suppressed printer, same rationale as the record above.
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     A start-ordering edge. <see cref="RequiresHealthy" /> rather than the manifest's condition string, so the
///     waiter branches on a boolean it cannot mistype.
/// </summary>
internal sealed class ServiceDependency
{
    public required string Service { get; init; }

    public required bool RequiresHealthy { get; init; }
}
