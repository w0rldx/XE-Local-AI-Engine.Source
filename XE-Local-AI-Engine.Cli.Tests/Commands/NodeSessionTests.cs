namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using System.Net;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The bootstrap every command shares: setup required, a locked vault, and where the password comes from.</summary>
[Category(TestCategories.Unit)]
public sealed class NodeSessionTests
{
    private const string NodeInfoJson = """{"version":"1.0.0","flavour":"cpu","osDescription":"Linux","uptimeSeconds":5,"warnings":[]}""";

    [Test]
    public async Task AnyCommand_WhenSetupIsRequired_ExitsWithTheSignInCodeAndTheSetupInstruction()
    {
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/status" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":true,"authenticated":false,"vault":"pending"}"""),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var cli = new TestCli(() => handler, new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordSource.EnvironmentVariable] = "secret" });

        var exitCode = await cli.RunAsync("models", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.AuthOrVault, exitCode);
        AssertEx.Contains(cli.Error, "--setup");
        AssertEx.Equal("GET /api/local/v1/auth/status", string.Join(',', handler.Requests));
    }

    [Test]
    public async Task Status_WhenTheVaultIsLocked_UnlocksWithThePasswordFromTheEnvironmentThenSignsIn()
    {
        var unlocked = false;
        using var handler = new ScriptedHttpHandler(request => (request.Method, request.Path) switch
        {
            ("GET", "/api/local/v1/auth/status") => ScriptedHttpHandler.Json(HttpStatusCode.OK,
                $$"""{"setupRequired":false,"authenticated":false,"vault":"{{(unlocked ? "unlocked" : "locked")}}"}"""),
            ("POST", "/api/local/v1/auth/vault/unlock") => Unlock(),
            ("POST", "/api/local/v1/auth/login") => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
            ("GET", "/api/local/v1/diagnostics/node-info") when request.Bearer == "access-1" => ScriptedHttpHandler.Json(HttpStatusCode.OK, NodeInfoJson),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var cli = new TestCli(() => handler, new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordSource.EnvironmentVariable] = "from-env" });

        var exitCode = await cli.RunAsync("status", "--json", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.Success, exitCode, cli.Error);
        var unlock = handler.Requests.Single(request => request.Path == "/api/local/v1/auth/vault/unlock");
        AssertEx.Contains(unlock.Body, "\"password\":\"from-env\"");
        AssertEx.Contains(handler.Requests.Single(request => request.Path == "/api/local/v1/auth/login").Body, "\"password\":\"from-env\"");
        var status = cli.JsonLines.Single();
        AssertEx.Equal("unlocked", status.GetProperty("vault").GetString());
        AssertEx.Equal("1.0.0", status.GetProperty("node").GetProperty("version").GetString());

        HttpResponseMessage Unlock()
        {
            unlocked = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    [Test]
    public async Task Status_WhenTheVaultIsLockedAndNoPasswordIsAvailable_ExitsWithTheVaultCodeWithoutTryingToUnlock()
    {
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/status" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":false,"authenticated":false,"vault":"locked"}"""),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var cli = new TestCli(() => handler);

        var exitCode = await cli.RunAsync("status", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.AuthOrVault, exitCode);
        AssertEx.Contains(cli.Error, "XE_ADMIN_PASSWORD");
        AssertEx.Equal(1, handler.Requests.Count);
    }

    [Test]
    public async Task Agents_WithPasswordStdin_SignsInWithTheFirstInputLine()
    {
        using var handler = new ScriptedHttpHandler(request => (request.Method, request.Path) switch
        {
            ("GET", "/api/local/v1/auth/status") => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":false,"authenticated":false,"vault":"unlocked"}"""),
            ("POST", "/api/local/v1/auth/login") => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
            ("GET", "/api/local/v1/agents") => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"6f1c0a4e-0000-4000-8000-000000000001","name":"Coder","kind":"saved","allowedToolNames":["read_file"]}]}"""),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var cli = new TestCli(() => handler, standardInput: "from-stdin\nignored\n");

        var exitCode = await cli.RunAsync("agents", "--json", "--password-stdin", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.Success, exitCode, cli.Error);
        AssertEx.Contains(handler.Requests.Single(request => request.Path == "/api/local/v1/auth/login").Body, "\"password\":\"from-stdin\"");
        AssertEx.Equal("Coder", cli.JsonLines.Single()[0].GetProperty("name").GetString());
    }
}
