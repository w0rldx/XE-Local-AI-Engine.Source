namespace XE_Local_AI_Engine.Tests.Testing;

using System.Reflection;
using Microsoft.Extensions.Hosting;

internal static class BackgroundServiceTestHelper
{
    public static Task RunExecuteAsync(BackgroundService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var method = service.GetType().GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            throw new InvalidOperationException($"ExecuteAsync was not found on {service.GetType().FullName}.");
        }

        return method.Invoke(service, [cancellationToken]) as Task
               ?? throw new InvalidOperationException($"ExecuteAsync on {service.GetType().FullName} did not return a Task.");
    }
}
