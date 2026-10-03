namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Client.Services.Vault;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The locked pre-host in process on a real loopback socket: its wire contract, rate limit and stop paths. The
///     vault file uses the minimum PBKDF2 count so each unwrap is fast; the real-process flow is in
///     <see cref="EngineCliProcessTests" />.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class VaultUnlockHostTests : IDisposable
{
    private const string Password = "correct horse battery";

    private const string NewPassword = "A new long passw0rd!";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "xe-vault-unlock-host-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _masterKey = Enumerable.Range(1, VaultKdf.KeyLength).Select(static value => (byte)value).ToArray();
    private readonly TaskCompletionSource _parentLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _recoveryCode;

    public VaultUnlockHostTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        var (file, recoveryCode) = VaultFileCodec.Create(_masterKey, Password, DateTimeOffset.UnixEpoch, VaultKdf.MinimumIterations);
        File.WriteAllBytes(Path.Combine(_dataDirectory, VaultFileCodec.KeyFileName), VaultFileCodec.Serialize(file));
        _recoveryCode = recoveryCode;
    }

    public void Dispose()
    {
        _parentLost.TrySetResult();
        Directory.Delete(_dataDirectory, recursive: true);
    }

    [Test]
    public async Task Locked_ServesReadinessStatusAnd503_RejectsAWrongPassword_ThenUnlocksWithTheRightOne()
    {
        var (run, url) = await StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(url)
        };

        using (var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative)))
        {
            AssertEx.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var status = await client.GetFromJsonAsync<JsonElement>(Route(LocalApiRoutes.Auth.Status));
        AssertEx.False(status.GetProperty("setupRequired").GetBoolean());
        AssertEx.False(status.GetProperty("authenticated").GetBoolean());
        AssertEx.Equal(NodeAuthVaultStatus.Locked, status.GetProperty("vault").GetString());

        using (var other = await client.GetAsync(Route("node/settings")))
        {
            AssertEx.Equal(HttpStatusCode.ServiceUnavailable, other.StatusCode);
            var problem = await other.Content.ReadFromJsonAsync<JsonElement>();
            AssertEx.Equal("Vault locked", problem.GetProperty("title").GetString());
        }

        var started = Stopwatch.GetTimestamp();
        using (var wrong = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = "not the password"
               }))
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        AssertEx.True(Stopwatch.GetElapsedTime(started) >= VaultUnlockHost.MinimumFailureLatency, "A failed unlock must not answer faster than the minimum latency.");

        using (var empty = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = string.Empty
               }))
        {
            AssertEx.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        AssertEx.False(run.IsCompleted, "A failed unlock must leave the pre-host serving.");

        using (var right = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = Password
               }))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        var outcome = AssertEx.NotNull(await run.WaitAsync(Deadline));
        AssertEx.True(outcome.MasterKey.AsSpan().SequenceEqual(_masterKey), "The pre-host must return the unwrapped master key.");
        AssertEx.Equal(url, outcome.BoundUrl);
        AssertEx.Null(outcome.ResetPassword);
    }

    [Test]
    public async Task RecoveryUnlock_AcceptsALooselyTypedCode_AndHandsTheResetToTheRealHost()
    {
        var (run, url) = await StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(url)
        };

        using (var weak = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlockRecovery), new
               {
                   recoveryCode = _recoveryCode,
                   newPassword = "short"
               }))
        {
            AssertEx.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
            var body = await weak.Content.ReadFromJsonAsync<JsonElement>();
            AssertEx.True(body.GetProperty("errors").GetArrayLength() > 0, "A policy violation must name its errors.");
        }

        using (var simple = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlockRecovery), new
               {
                   recoveryCode = _recoveryCode,
                   newPassword = "long but lower case only"
               }))
        {
            AssertEx.Equal(HttpStatusCode.BadRequest, simple.StatusCode, "The pre-host must refuse a password the real host's Identity policy would reject.");
        }

        var wrongCode = new string('A', 40);
        using (var wrong = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlockRecovery), new
               {
                   recoveryCode = wrongCode,
                   newPassword = NewPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Dashes become spaces and the case flips: the parser ignores both.
#pragma warning disable CA1308 // Lower case is the point: the code is displayed upper case and must be accepted as typed.
        var typed = _recoveryCode.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();
#pragma warning restore CA1308
        using (var right = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlockRecovery), new
               {
                   recoveryCode = typed,
                   newPassword = NewPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        var outcome = AssertEx.NotNull(await run.WaitAsync(Deadline));
        AssertEx.True(outcome.MasterKey.AsSpan().SequenceEqual(_masterKey), "The recovery code must unwrap the same master key.");
        AssertEx.Equal(NewPassword, outcome.ResetPassword);
        AssertEx.Equal(typed, outcome.ResetRecoveryCode);
    }

    [Test]
    public async Task UnlockAttempts_BeyondTheWindowLimit_Answer429()
    {
        var (run, url) = await StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(url)
        };

        for (var attempt = 0; attempt < VaultUnlockHost.AttemptLimit; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
            {
                password = "not the password"
            });
            AssertEx.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var limited = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = Password
               }))
        {
            AssertEx.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        AssertEx.False(run.IsCompleted, "A rate-limited attempt must not unlock even with the right password.");
        _parentLost.TrySetResult();
        AssertEx.Null(await run.WaitAsync(Deadline));
    }

    [Test]
    public async Task ParentLoss_StopsThePreHostWithoutUnlocking_AndRemovesTheReadinessFile()
    {
        var (run, _) = await StartAsync();

        _parentLost.TrySetResult();

        AssertEx.Null(await run.WaitAsync(Deadline));
        AssertEx.Null(await DesktopPortStore.ReadReadyAsync(_dataDirectory));
    }

    private static Uri Route(string route) =>
        new($"/{LocalApiRoutes.Prefix}/{route}", UriKind.Relative);

    private async Task<(Task<VaultUnlockOutcome?> Run, string Url)> StartAsync()
    {
        var run = VaultUnlockHost.RunAsync(new VaultUnlockHostOptions
        {
            DataDirectory = _dataDirectory,
            BindUrl = DesktopLaunch.LoopbackBindUrl,
            ContentRootPath = _dataDirectory,
            WebRootPath = null,
            EnvironmentName = "Testing",
            Version = "9.9.9",
            SuppressBrowser = true,
            StandardOutput = TextWriter.Null,
            ParentLost = _parentLost.Task
        }, CancellationToken.None);

        ReadyInfo? ready = null;
        await AssertEx.EventuallyAsync(async () =>
            {
                ready = await DesktopPortStore.ReadReadyAsync(_dataDirectory);
                return run.IsCompleted || ready is not null;
            },
            Deadline,
            "The pre-host never published readiness.");
        if (run.IsCompleted)
        {
            await run;
            throw new InvalidOperationException("The pre-host stopped before publishing readiness.");
        }

        return (run, AssertEx.NotNull(ready).Url);
    }
}
