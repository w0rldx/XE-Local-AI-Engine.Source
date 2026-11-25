namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>Each SPA hub path authenticates its socket by the bearer subprotocol, and no console line or URL ever carries the token.</summary>
/// <remarks>
///     Per path: a socket URL with only the connection id, <c>xe.bearer, &lt;jwt&gt;</c> offered, a 101 echoing
///     <c>xe.bearer</c>, and frames received, also after a refused upgrade: one negotiate answer gets an unknown connection
///     token, so the node answers the browser's own upgrade with 404 and Chromium logs its failure line. Offline mode fails
///     the negotiate before any socket opens, and this Chromium has no back-forward cache. Listeners attach before the
///     first navigation; leak checks are booleans and messages name only the check, so no failure prints a credential.
/// </remarks>
[Category("Page")]
public sealed class HubSocketTokenE2ETests : XEPooledE2ETestBase
{
    private const string BearerProtocol = "xe.bearer";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private static readonly Regex JwtShape = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]+", RegexOptions.None, TimeSpan.FromSeconds(5));

    private readonly Lock _gate = new();
    private readonly List<HubSocket> _sockets = [];
    private readonly List<ConsoleLine> _consoleLines = [];
    private readonly List<string> _requestUrls = [];
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly Channel<bool> _changed = Channel.CreateUnbounded<bool>();

    protected override async Task SignInAsync()
    {
        Page.Console += (_, message) => AddConsoleLine(message.Type, message.Text);
        Page.PageError += (_, error) => AddConsoleLine("pageerror", error);
        Page.Request += (_, request) => Record(() => _requestUrls.Add(request.Url));

        var cdp = await Context.NewCDPSessionAsync(Page);
        cdp.Event("Network.requestWillBeSent").OnEvent += (_, payload) => OnRequest(payload);
        cdp.Event("Network.webSocketCreated").OnEvent += (_, payload) => OnSocketCreated(payload);
        cdp.Event("Network.webSocketWillSendHandshakeRequest").OnEvent += (_, payload) => OnHandshakeRequest(payload);
        cdp.Event("Network.webSocketHandshakeResponseReceived").OnEvent += (_, payload) => OnHandshakeResponse(payload);
        cdp.Event("Network.webSocketFrameReceived").OnEvent += (_, payload) => OnFrameReceived(payload);
        cdp.Event("Log.entryAdded").OnEvent += (_, payload) => OnLogEntry(payload);
        await cdp.SendAsync("Network.enable");
        await cdp.SendAsync("Log.enable");

        await base.SignInAsync();
    }

    [Test]
    [Category("Page")]
    public async Task HubSockets_CarryTheTokenOnlyInTheBearerSubprotocol_PerPath_AndThroughAFailedUpgrade()
    {
        await GotoAsync("/chat");
        await WaitForWorkingSocketAsync(LocalApiRoutes.LocalChat.Hub, createdAfter: 0, "chat hub socket offering xe.bearer and a JWT, with 101, echoed xe.bearer and frames");
        await GotoAsync("/scheduler");
        await WaitForWorkingSocketAsync(LocalApiRoutes.Scheduler.Hub, createdAfter: 0, "scheduler hub socket offering xe.bearer and a JWT, with 101, echoed xe.bearer and frames");

        int consoleMark;
        lock (_gate)
        {
            consoleMark = _consoleLines.Count;
        }

        await Page.RouteAsync($"**{LocalApiRoutes.Scheduler.Hub}/negotiate*", async route =>
        {
            var response = await route.FetchAsync();
            var negotiation = JsonNode.Parse(await response.TextAsync()) ?? throw new InvalidOperationException("The scheduler hub negotiate answer must be JSON.");
            negotiation["connectionToken"] = "unknown-connection";
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = response,
                Body = negotiation.ToJsonString()
            });
        }, new PageRouteOptions
        {
            Times = 1
        });
        // Not GotoAsync: after the refused upgrade SignalR falls back to long polling, so the network never idles.
        await Page.GotoAsync($"{NodeAppUrl}/scheduler");
        await WaitForAsync(() => _consoleLines.Skip(consoleMark).Any(line => IsError(line)
                                                                             && line.Text.StartsWith("WebSocket connection to", StringComparison.Ordinal)
                                                                             && line.Text.Contains(LocalApiRoutes.Scheduler.Hub, StringComparison.Ordinal)),
            "browser failure line naming the refused scheduler hub socket");

        int socketMark;
        lock (_gate)
        {
            socketMark = _sockets.Count;
        }

        await Page.ReloadAsync(new PageReloadOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await WaitForWorkingSocketAsync(LocalApiRoutes.Scheduler.Hub, socketMark, "scheduler hub socket working again after the refused upgrade");

        bool anyConsoleLineLeaks;
        bool anyRequestUrlLeaks;
        bool anySocketUrlLeaks;
        lock (_gate)
        {
            anyConsoleLineLeaks = _consoleLines.Any(line => Leaks(line.Text));
            anyRequestUrlLeaks = _requestUrls.Any(Leaks);
            anySocketUrlLeaks = _sockets.Any(socket => Leaks(socket.Url));
        }

        await Assert.That(anyConsoleLineLeaks).IsFalse().Because("no console line may carry access_token, the signed-in token or a JWT");
        await Assert.That(anyRequestUrlLeaks).IsFalse().Because("no request URL may carry access_token, the signed-in token or a JWT");
        await Assert.That(anySocketUrlLeaks).IsFalse().Because("no socket URL may carry access_token, the signed-in token or a JWT");
    }

    private static bool IsError(ConsoleLine line) =>
        line.Level is "error" or "pageerror";

    private static string? Field(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    private static string? Header(JsonElement message, string section, string name)
    {
        if (!message.TryGetProperty(section, out var part) || !part.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return headers.EnumerateObject()
                      .Where(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                      .Select(header => header.Value.GetString())
                      .FirstOrDefault();
    }

    private async Task GotoAsync(string path) =>
        await Page.GotoAsync($"{NodeAppUrl}{path}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

    private Task WaitForWorkingSocketAsync(string hubPath, int createdAfter, string check) =>
        WaitForAsync(() => _sockets.Skip(createdAfter)
                                   .Any(socket => string.Equals(new Uri(socket.Url).AbsolutePath, hubPath, StringComparison.Ordinal)
                                                  && socket.OfferedBearerProtocol
                                                  && socket.Status == "101"
                                                  && string.Equals(socket.EchoedProtocol, BearerProtocol, StringComparison.Ordinal)
                                                  && socket.HandshakeAnswered),
            check);

    // Re-evaluates the condition after every recorded event, under one deadline; no sleep.
    private async Task WaitForAsync(Func<bool> condition, string check)
    {
        using var deadline = new CancellationTokenSource(Budget);
        while (true)
        {
            lock (_gate)
            {
                if (condition())
                {
                    return;
                }
            }

            try
            {
                _ = await _changed.Reader.ReadAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Timed out waiting for: {check}.");
            }
        }
    }

    private bool Leaks(string text)
    {
        return text.Contains("access_token=", StringComparison.Ordinal)
               || JwtShape.IsMatch(text)
               || _tokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    private void Record(Action change)
    {
        lock (_gate)
        {
            change();
        }

        _changed.Writer.TryWrite(true);
    }

    private void AddConsoleLine(string level, string text) =>
        Record(() => _consoleLines.Add(new ConsoleLine(level, text)));

    private HubSocket? Socket(JsonElement message) =>
        Field(message, "requestId") is { } requestId ? _sockets.Find(socket => socket.RequestId == requestId) : null;

    private void OnRequest(JsonElement? payload)
    {
        if (payload is not { } message)
        {
            return;
        }

        Record(() =>
        {
            if (Field(message, "request", "url") is { } url)
            {
                _requestUrls.Add(url);
            }

            if (Header(message, "request", "Authorization") is { } authorization
                && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _tokens.Add(authorization["Bearer ".Length..]);
            }
        });
    }

    private void OnSocketCreated(JsonElement? payload)
    {
        if (payload is { } message && Field(message, "requestId") is { } requestId && Field(message, "url") is { } url)
        {
            Record(() => _sockets.Add(new HubSocket(requestId, url)));
        }
    }

    private void OnHandshakeRequest(JsonElement? payload)
    {
        if (payload is not { } message)
        {
            return;
        }

        Record(() =>
        {
            var offered = Header(message, "request", "Sec-WebSocket-Protocol")?.Split(',', StringSplitOptions.TrimEntries);
            if (Socket(message) is { } socket && offered is [BearerProtocol, var token])
            {
                socket.OfferedBearerProtocol = JwtShape.IsMatch(token);
                _tokens.Add(token);
            }
        });
    }

    private void OnHandshakeResponse(JsonElement? payload)
    {
        if (payload is not { } message)
        {
            return;
        }

        Record(() =>
        {
            if (Socket(message) is { } socket)
            {
                socket.Status = Field(message, "response", "status");
                socket.EchoedProtocol = Header(message, "response", "Sec-WebSocket-Protocol");
            }
        });
    }

    private void OnFrameReceived(JsonElement? payload)
    {
        if (payload is not { } message)
        {
            return;
        }

        Record(() =>
        {
            if (Socket(message) is { } socket && Field(message, "response", "payloadData") is { } data && data.StartsWith("{}\u001e", StringComparison.Ordinal))
            {
                socket.HandshakeAnswered = true;
            }
        });
    }

    private void OnLogEntry(JsonElement? payload)
    {
        if (payload is not { } message)
        {
            return;
        }

        var level = Field(message, "entry", "level") ?? string.Empty;
        var text = $"{Field(message, "entry", "text")} {Field(message, "entry", "url")}";
        AddConsoleLine(level, text);
    }

    private sealed class ConsoleLine
    {
        public ConsoleLine(string level, string text)
        {
            Level = level;
            Text = text;
        }

        public string Level { get; }

        public string Text { get; }
    }

    private sealed class HubSocket
    {
        public HubSocket(string requestId, string url)
        {
            RequestId = requestId;
            Url = url;
        }

        public string RequestId { get; }

        public string Url { get; }

        public string? Status { get; set; }

        public string? EchoedProtocol { get; set; }

        public bool OfferedBearerProtocol { get; set; }

        public bool HandshakeAnswered { get; set; }
    }
}
