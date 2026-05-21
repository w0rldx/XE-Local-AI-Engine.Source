namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentContractAttributeTests
{
    [Test]
    public void PublicContractProperties_AreRequiredAndJsonRequired()
    {
        var contractTypes = typeof(HostAgentStatusDto).Assembly.GetExportedTypes()
                                                      .Where(type => type is { IsClass: true, IsAbstract: false })
                                                      .OrderBy(type => type.FullName, StringComparer.Ordinal)
                                                      .ToArray();

        AssertEx.NotEmpty(contractTypes);

        foreach (var property in contractTypes.SelectMany(GetPublicInstanceProperties))
        {
            AssertEx.True(property.GetCustomAttribute<RequiredMemberAttribute>() is not null,
                $"{property.DeclaringType?.FullName}.{property.Name} must use the C# required modifier.");

            AssertEx.True(property.GetCustomAttribute<JsonRequiredAttribute>() is not null,
                $"{property.DeclaringType?.FullName}.{property.Name} must use [JsonRequired].");
        }
    }

    private static IEnumerable<PropertyInfo> GetPublicInstanceProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
    }
}
