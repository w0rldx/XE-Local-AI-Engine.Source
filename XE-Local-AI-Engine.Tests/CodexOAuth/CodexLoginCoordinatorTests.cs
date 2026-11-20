namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the login coordinator's status lifecycle: start exposes the authorize URL and a
///     pending status, completion flips the status to succeeded, and a second start supersedes the first.
/// </summary>
[Category(TestCategories.Unit)]
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
        // A sign-in invalidates the active-cloud selection snapshot so the next send routes to Codex at once: the host
        // wires onLoginSucceeded to IActiveCloudChatClientFactory.InvalidateSelectionCache().
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
        AssertEx.Equal(expected: 0, Volatile.Read(ref invalidations));
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

    [Test]
    public async Task Start_WhenASupersedingStartCannotBind_ReportsFailedAndALaterStartStillTracksItsOwnAttempt()
    {
        // A second "Sign in" while the first listener still holds the callback port makes BeginLogin throw; a late first
        // completion must not clobber the third attempt either.
        var bindFailure = new CodexAuthException("Could not bind the Codex loopback callback port.");
        var authService = new FakeAuthService
        {
            IgnoreCancellationOnAttempt = 1,
            FailOnAttempt = 2,
            Failure = bindFailure
        };
        using var coordinator = new CodexLoginCoordinator(new Lazy<ICodexAuthService>(() => authService), NullLogger<CodexLoginCoordinator>.Instance);

        coordinator.Start();
        var first = authService.LastHandle!;

        var thrown = AssertEx.Throws<CodexAuthException>(() => coordinator.Start());

        AssertEx.True(ReferenceEquals(bindFailure, thrown), "The endpoint must see the auth service's own exception.");
        AssertEx.Equal(CodexLoginState.Failed, coordinator.GetStatus().State);
        AssertEx.Null(coordinator.GetStatus().AuthorizeUrl);
        AssertEx.True(IsDisposed(authService.Tokens[1]), "The failing attempt's CancellationTokenSource must be disposed.");

        var thirdUrl = coordinator.Start();
        first.Completion.TrySetResult(new CodexTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), "acct"));

        // The first attempt's tracking task disposes its CTS last, so a disposed token means its status update has run.
        await AssertEx.EventuallyAsync(() => IsDisposed(authService.Tokens[0]), TimeSpan.FromSeconds(2));
        AssertEx.Equal(CodexLoginState.Pending, coordinator.GetStatus().State);
        AssertEx.Equal(thirdUrl, AssertEx.NotNull(coordinator.GetStatus().AuthorizeUrl));

        authService.CompleteWithSuccess(new CodexTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), "acct"));
        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == CodexLoginState.Succeeded,
            TimeSpan.FromSeconds(2));
    }

    private static bool IsDisposed(CancellationToken token)
    {
        try
        {
            _ = token.WaitHandle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    ///     A controllable <see cref="ICodexAuthService" />: each <see cref="BeginLogin" /> hands back a fresh handle
    ///     with a unique authorize URL and a completion the test resolves on demand.
    /// </summary>
    private sealed class FakeAuthService : ICodexAuthService
    {
        private int _counter;

        public FakeHandle? LastHandle { get; private set; }

        /// <summary>The token each <see cref="BeginLogin" /> call received, in call order.</summary>
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>The 1-based attempt whose completion ignores cancellation, standing in for a token exchange already under way.</summary>
        public int IgnoreCancellationOnAttempt { get; init; }

        /// <summary>The 1-based attempt that throws <see cref="Failure" /> instead of returning a handle.</summary>
        public int FailOnAttempt { get; init; }

        public Exception? Failure { get; init; }

        public CodexLoginHandle BeginLogin(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _counter);
            Tokens.Add(cancellationToken);
            if (index == FailOnAttempt)
            {
                throw Failure!;
            }

            var completion = new TaskCompletionSource<CodexTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (index != IgnoreCancellationOnAttempt)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(CancellationToken.None));
            }

            var handle = new CodexLoginHandle
            {
                AuthorizeUrl = new Uri($"https://auth.openai.com/authorize?attempt={index}"),
                Completion = completion.Task
            };
            LastHandle = new FakeHandle
            {
                Handle = handle,
                Completion = completion
            };
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

    private sealed record FakeHandle
    {
        public required CodexLoginHandle Handle { get; init; }

        public required TaskCompletionSource<CodexTokens> Completion { get; init; }

        public Uri AuthorizeUrl => Handle.AuthorizeUrl;
    }
}
