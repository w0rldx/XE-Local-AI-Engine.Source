namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client;

/// <summary>
///     Gives a .NET SignalR test client the node's own JSON policy, so it reads what the hubs actually write.
/// </summary>
/// <remarks>
///     The host sends a CLR enum as its NAME. A client on the SignalR defaults has no enum converter, so binding that
///     frame into a typed payload throws inside the protocol reader and the handler never runs — the test then waits out
///     its timeout with no failed assertion to point at. Only a typed .NET client has this problem, never the browser.
///     Apply it to every connection here, not only to the payloads carrying an enum today.
/// </remarks>
internal static class NodeHubClient
{
    public static IHubConnectionBuilder WithNodeJsonProtocol(this IHubConnectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddJsonProtocol(options => ConfigureServices.ConfigureJsonSerializerOptions(options.PayloadSerializerOptions));
    }

    /// <summary>
    ///     Starts the connection and returns only once the server has registered it for broadcasts.
    /// </summary>
    /// <remarks>
    ///     <see cref="HubConnection.StartAsync" /> completes on the handshake response, which the server writes BEFORE
    ///     it registers the connection with the hub lifetime manager, so a <c>Clients.All</c> push in that window is
    ///     silently dropped. The server reads client frames only after registration, so its error reply to a probe for
    ///     a method that does not exist proves the connection is listed.
    /// </remarks>
    public static async Task StartAndAwaitRegistrationAsync(this HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("RegistrationProbe");
        }
        catch (HubException)
        {
            // Expected: the reply to an unknown method, which the server sends only after registering the connection.
        }
    }
}
