namespace XE_Local_AI_Engine.Tests.Endpoints.NodeBackups.V1;

using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node-backups routes on the real host: operator-gated, a snapshot taken on request shows up in the list, and a
///     restore validates the name and the file, writes the marker the next start applies, then stops the host.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeBackupsEndpointTests
{
    private const string BackupsPath = "/api/local/v1/node/backups";

    [Test]
    [Arguments("GET", BackupsPath)]
    [Arguments("POST", BackupsPath)]
    [Arguments("POST", BackupsPath + "/node-chat-20260101T000000000Z.sqlite/restore")]
    public async Task WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_Returns201_AndTheListShowsItNewestFirst()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var created = await SendAsync(factory, client, HttpMethod.Post, BackupsPath);
        var createdBody = await created.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.Created, created.StatusCode, createdBody);
        using var createdJson = JsonDocument.Parse(createdBody);
        var name = createdJson.RootElement.GetProperty("name").GetString()!;
        AssertEx.True(NodeDbRestoreStaging.IsValidSnapshotName(name), name);
        AssertEx.True(createdJson.RootElement.GetProperty("sizeBytes").GetInt64() > 0, createdBody);
        AssertEx.True(File.Exists(Path.Combine(factory.NodeDataDirectoryPath, "backups", name)), "The snapshot lands in the data directory's backups folder.");

        using var listed = await SendAsync(factory, client, HttpMethod.Get, BackupsPath);
        var listBody = await listed.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, listed.StatusCode, listBody);
        using var listJson = JsonDocument.Parse(listBody);
        AssertEx.Equal(name, listJson.RootElement.GetProperty("backups")[0].GetProperty("name").GetString());
        // The fixture copies an already migrated database, so the start had nothing pending and took no automatic backup.
        AssertEx.Equal("NotRun", listJson.RootElement.GetProperty("lastAutomaticBackup").GetProperty("outcome").GetString(), listBody);
    }

    [Test]
    [Arguments("other-20260101.sqlite")]
    [Arguments("node-chat-20260101T000000000Z.txt")]
    [Arguments("node-chat-..%5Cnode.sqlite")]
    public async Task Restore_WithAnInvalidName_Returns400(string name)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/{name}/restore");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Restore_OfAnUnlistedSnapshot_Returns404()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/node-chat-20260101T000000000Z.sqlite/restore");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Restore_OnAHeadlessHost_Returns409_AndStagesNothing()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var name = await CreateSnapshotAsync(factory, client);

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/{name}/restore");

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertEx.False(File.Exists(MarkerPath(factory)), "A host that would ignore the marker must not write one.");
    }

    [Test]
    public async Task Restore_OfACorruptSnapshot_Returns409_AndStagesNothing()
    {
        await using var factory = LocalModeFactory();
        using var client = factory.CreateClient();
        var backups = Path.Combine(factory.NodeDataDirectoryPath, "backups");
        Directory.CreateDirectory(backups);
        const string name = "node-chat-20260101T000000000Z.sqlite";
        await File.WriteAllTextAsync(Path.Combine(backups, name), "this is not a SQLite database at all");

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/{name}/restore");

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertEx.False(File.Exists(MarkerPath(factory)), "A snapshot that fails its check is never staged.");
    }

    [Test]
    public async Task Restore_OfASnapshotFromANewerBuild_Returns409_AndStagesNothing()
    {
        await using var factory = LocalModeFactory();
        using var client = factory.CreateClient();
        var name = await CreateSnapshotAsync(factory, client);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(factory.NodeDataDirectoryPath, "backups", name)}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20991231000000_FromANewerBuild', '10.0.0');""";
            await command.ExecuteNonQueryAsync();
        }

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/{name}/restore");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, body);
        AssertEx.True(body.Contains("20991231000000_FromANewerBuild", StringComparison.Ordinal), body);
        AssertEx.False(File.Exists(MarkerPath(factory)), "A snapshot this binary cannot migrate is never staged.");
    }

    [Test]
    public async Task Restore_InLocalMode_Returns202_WritesTheMarker_AndStopsTheHost()
    {
        await using var factory = LocalModeFactory();
        using var client = factory.CreateClient();
        var name = await CreateSnapshotAsync(factory, client);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());

        using var response = await SendAsync(factory, client, HttpMethod.Post, $"{BackupsPath}/{name}/restore");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        AssertEx.True(json.RootElement.GetProperty("nodeStopping").GetBoolean(), body);
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(MarkerPath(factory)));
        AssertEx.Equal(name, marker.RootElement.GetProperty("SnapshotName").GetString());
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static TestServerWebAppFactory LocalModeFactory() =>
        new()
        {
            ConfigureAdditionalTestServices = static services => services.AddSingleton(new NodeLaunchContext
            {
                IsLocalMode = true
            })
        };

    private static string MarkerPath(TestServerWebAppFactory factory) =>
        Path.Combine(factory.NodeDataDirectoryPath, "backups", NodeDbRestoreStaging.MarkerFileName);

    private static async Task<string> CreateSnapshotAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var created = await SendAsync(factory, client, HttpMethod.Post, BackupsPath);
        var body = await created.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.Created, created.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("name").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }
}
