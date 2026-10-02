namespace XE_Local_AI_Engine.Tests.Testing;

using NSubstitute;

/// <summary>A container with one service answered by a test's own instance, for building a type the way the host would.</summary>
internal static class ServiceProviderOverride
{
    public static IServiceProvider With<TService>(this IServiceProvider inner, TService replacement)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(inner);
        var serving = Substitute.For<IServiceProvider>();
        _ = serving.GetService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() == typeof(TService) ? replacement : inner.GetService(call.Arg<Type>()));
        return serving;
    }
}
