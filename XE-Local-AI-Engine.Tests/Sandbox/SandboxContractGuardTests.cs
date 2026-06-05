namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SandboxContractGuardTests
{
    private const string ContractNamespace = "XE_Local_AI_Engine.Client.Services.Sandbox";

    private static readonly string[] ForbiddenNamespaceTokens = ["Docker", "OpenSandbox", "Grpc", "Hyperlight"];

    [Test]
    public void SandboxContracts_DoNotReferenceProviderSdkTypes()
    {
        var contractTypes = typeof(ISandboxRuntimeProvider).Assembly
                                                           .GetTypes()
                                                           .Where(static type => string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal))
                                                           .ToArray();

        AssertEx.NotEmpty(contractTypes);

        var referencedNamespaces = contractTypes
                                   .SelectMany(CollectReferencedTypes)
                                   .Select(static type => type.Namespace ?? string.Empty)
                                   .Distinct(StringComparer.Ordinal)
                                   .ToArray();

        var leaks = referencedNamespaces
                    .Where(static referenced => ForbiddenNamespaceTokens.Any(token => referenced.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

        AssertEx.Empty(leaks, $"Sandbox contracts must not reference provider SDK namespaces, but found: {string.Join(", ", leaks)}.");
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        const BindingFlags memberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var propertyTypes = type.GetProperties(memberFlags).Select(static property => property.PropertyType);
        var methods = type.GetMethods(memberFlags);
        var parameterTypes = methods.SelectMany(static method => method.GetParameters()).Select(static parameter => parameter.ParameterType);
        var returnTypes = methods.Select(static method => method.ReturnType);

        return propertyTypes.Concat(parameterTypes).Concat(returnTypes).SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }
}
