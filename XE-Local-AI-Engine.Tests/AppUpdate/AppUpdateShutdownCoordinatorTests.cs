namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.AppUpdate;

public sealed class AppUpdateShutdownCoordinatorTests
{
    [Test]
    public async Task StopAfterResponseCompleted_DoesNotStopTheHostBeforeTheSuccessResponseCompletes()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var coordinator = new AppUpdateShutdownCoordinator(lifetime);
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        var response = new DefaultHttpContext(features).Response;

        coordinator.StopAfterResponseCompleted(response);

        lifetime.DidNotReceive().StopApplication();
        await responseFeature.CompleteAsync();
        lifetime.Received(1).StopApplication();
    }

    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => false;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
            _completed.Add((callback, state));
        }

        public async Task CompleteAsync()
        {
            for (var index = _completed.Count - 1; index >= 0; index--)
            {
                var (callback, state) = _completed[index];
                await callback(state).ConfigureAwait(false);
            }
        }
    }
}
