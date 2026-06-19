namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the login coordinator's status lifecycle: start exposes the authorize URL and a
///     pending status, completion flips the status to succeeded, and a second start supersedes the first.
/// </summary>
public sealed class CodexLoginCoordinatorTests
{
    [Test]
    public void Start_ReturnsAuthorizeUrl_AndReportsPendingStatus()
    {
        var authService = new FakeAuthService();
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService), NullLogger<CodexLoginCoordinator>.Instance);

        var url = coordinator.Start();
        var status = coordinator.GetStatus();

        AssertEx.Equal(authService.LastHandle!.AuthorizeUrl, url);
        AssertEx.Equal(CodexLoginState.Pending, status.State);
        AssertEx.Equal(url, AssertEx.NotNull(status.AuthorizeUrl));
    }

    [Test]
    public async Task Start_WhenLoginCompletes_FlipsStatusToSucceeded()
    {
        var authService = new FakeAuthService();
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService), NullLogger<CodexLoginCoordinator>.Instance);

        coordinator.Start();
        authService.CompleteWithSuccess(new CodexTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), "acct"));

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == CodexLoginState.Succeeded,
            TimeSpan.FromSeconds(2));
        AssertEx.Null(coordinator.GetStatus().AuthorizeUrl);
    }

    [Test]
    public async Task Start_WhenLoginCompletes_InvokesOnLoginSucceeded()
    {
        // A sign-in must invalidate the active-cloud selection snapshot so the next send routes to Codex
        // immediately. The coordinator fires onLoginSucceeded on the success transition; the host wires it to
        // IActiveCloudChatClientFactory.InvalidateSelectionCache().
        var authService = new FakeAuthService();
        var invalidations = 0;
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService),
            NullLogger<CodexLoginCoordinator>.Instance,
            () => Interlocked.Increment(ref invalidations));

        coordinator.Start();
        authService.CompleteWithSuccess(new CodexTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), "acct"));

        await AssertEx.EventuallyAsync(() => Volatile.Read(ref invalidations) == 1, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Start_WhenLoginFails_DoesNotInvokeOnLoginSucceeded()
    {
        var authService = new FakeAuthService();
        var invalidations = 0;
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService),
            NullLogger<CodexLoginCoordinator>.Instance,
            () => Interlocked.Increment(ref invalidations));

        coordinator.Start();
        authService.CompleteWithFailure(new CodexAuthException("token endpoint returned 400"));

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == CodexLoginState.Failed,
            TimeSpan.FromSeconds(2));
        AssertEx.Equal(0, Volatile.Read(ref invalidations));
    }

    [Test]
    public async Task Start_WhenLoginFaults_FlipsStatusToFailed()
    {
        var authService = new FakeAuthService();
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService), NullLogger<CodexLoginCoordinator>.Instance);

        coordinator.Start();
        authService.CompleteWithFailure(new CodexAuthException("token endpoint returned 400"));

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == CodexLoginState.Failed,
            TimeSpan.FromSeconds(2));
    }

    [Test]
    public void Start_WhenCalledAgain_SupersedesAndReportsTheNewPendingUrl()
    {
        var authService = new FakeAuthService();
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService), NullLogger<CodexLoginCoordinator>.Instance);

        var firstUrl = coordinator.Start();
        var secondUrl = coordinator.Start();

        AssertEx.NotEqual(firstUrl, secondUrl);
        var status = coordinator.GetStatus();
        AssertEx.Equal(CodexLoginState.Pending, status.State);
        AssertEx.Equal(secondUrl, AssertEx.NotNull(status.AuthorizeUrl));
    }

    /// <summary>
    ///     A controllable <see cref="ICodexAuthService" />: each <see cref="BeginLogin" /> hands back a fresh handle
    ///     with a unique authorize URL and a completion the test resolves on demand.
    /// </summary>
    private sealed class FakeAuthService : ICodexAuthService
    {
        private int _counter;

        public FakeHandle? LastHandle { get; private set; }

        public CodexLoginHandle BeginLogin(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _counter);
            var completion = new TaskCompletionSource<CodexTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled());
            var handle = new CodexLoginHandle(new Uri($"https://auth.openai.com/authorize?attempt={index}"), completion.Task);
            LastHandle = new FakeHandle(handle, completion);
            return handle;
        }

        public Task<CodexTokens> LoginAsync(CancellationToken cancellationToken = default)
        {
            return BeginLogin(cancellationToken).Completion;
        }

        public Task<CodexTokens> RefreshAsync(CodexTokens current, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(current);
        }

        public void CompleteWithSuccess(CodexTokens tokens)
        {
            LastHandle!.Completion.TrySetResult(tokens);
        }

        public void CompleteWithFailure(Exception exception)
        {
            LastHandle!.Completion.TrySetException(exception);
        }
    }

    private sealed record FakeHandle(CodexLoginHandle Handle, TaskCompletionSource<CodexTokens> Completion)
    {
        public Uri AuthorizeUrl => Handle.AuthorizeUrl;
    }
}
