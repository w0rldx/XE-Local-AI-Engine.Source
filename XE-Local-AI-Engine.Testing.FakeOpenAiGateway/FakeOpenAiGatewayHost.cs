namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

using System.Globalization;

/// <summary>
///     Runs the fake gateway by hand for a browser round:
///     <c>--token T --header Name=Value --model id:ctx --port N</c>, each flag optional, header and model repeatable.
/// </summary>
/// <remarks>Not named Program: the test-category scan resolves types by simple name and would confuse it with the node's.</remarks>
internal static class FakeOpenAiGatewayHost
{
    public static async Task<int> Main(string[] args)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<FakeOpenAiGatewayModel>();
        string? token = null;
        var port = 0;

        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            var value = args[index + 1];
            switch (args[index])
            {
                case "--token":
                    token = value;
                    break;
                case "--header" when value.Split('=', 2) is [var name, var headerValue]:
                    headers[name] = headerValue;
                    break;
                case "--model" when value.LastIndexOf(':') is var colon and > 0:
                    models.Add(new FakeOpenAiGatewayModel
                    {
                        Id = value[..colon],
                        ContextLength = int.Parse(value[(colon + 1)..], CultureInfo.InvariantCulture)
                    });
                    break;
                case "--port":
                    port = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    await Console.Error.WriteLineAsync($"Unknown or malformed argument: {args[index]} {value}");
                    return 2;
            }
        }

        var options = new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = token,
            RequiredHeaders = headers,
            Port = port
        };
        if (models.Count > 0)
        {
            options = options with
            {
                Models = models
            };
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        await using var server = await FakeOpenAiGatewayServer.StartAsync(options, stop.Token);
        await Console.Out.WriteLineAsync(server.BaseAddress.ToString());

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is the only way out.
        }

        return 0;
    }
}
