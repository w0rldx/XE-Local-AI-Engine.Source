namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

// W3C trace-correlation backend behavior: the node must emit
// the W3C trace id carried on an inbound `traceparent` even when Aspire/OpenTelemetry is OFF (the desktop/RC default,
// where there is no OTel ActivityListener). Program.cs forces the W3C Activity id format and registers an
// ActivityListener scoped to the "Microsoft.AspNetCore" source (not every source in the process) so ASP.NET creates a
// request Activity from the inbound header; the trace id then flows to ProblemDetails.traceId on the error path and the
// `traceresponse` header on the success path. The traceresponse trace-flags byte is derived from the request activity's
// ActivityTraceFlags.Recorded state (TraceResponseHeader.Build) rather than a hardcoded "01": a recorded activity yields
// the "-01" suffix, a not-recorded one yields "-00".
//
// Which byte the live pipeline emits depends on the listeners attached, and that differs by host:
//   * The bare TestingWebAppFactory strips every IHostedService, so the OpenTelemetry TracerProvider never starts and
//     the ONLY listener on this source is Program.cs's scoped listener, which samples AllData (not AllDataAndRecorded).
//     Request activities are therefore never marked Recorded and the header is "-00" regardless of the inbound sampled
//     flag — the *_WhenInboundTraceparentAndAspireOff_* / bare-factory success tests below assert that fixture behavior.
//   * The production host also runs the TracerProvider (AddServiceDefaults/ConfigureOpenTelemetry registers it in every
//     mode) whose default ParentBased(AlwaysOn) sampler DOES record: a sampled inbound parent yields a recorded activity
//     ("-01"), an unsampled parent stays "-00". The *_WhenProductionTracerRunning_* tests force that provider to build
//     and assert both branches, so the suite represents the real host, not only the hosted-service-stripped fixture.
// The recorded ("-01") formatting is additionally exercised directly against TraceResponseHeader.Build.
//
// The production-sampler tests attach a global recorded ActivityListener for the duration of their host; NotInParallel
// serializes every test in this class so that listener can never leak into the bare-factory "-00" assertions.
[NotInParallel(nameof(BackendTraceCorrelationTests))]
public sealed class BackendTraceCorrelationTests
{
    private const string InboundTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string InboundSpanId = "b7ad6b7169203331";
    private const string InboundTraceparent = $"00-{InboundTraceId}-{InboundSpanId}-01";
    private const string InboundTraceparentUnsampled = $"00-{InboundTraceId}-{InboundSpanId}-00";

    private static readonly Regex W3CTraceId = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    // Test matrix: backend_traceId_isW3C_aspireOff.
    [Test]
    public async Task WithTraceId_WhenInboundTraceparentAndAspireOff_EmitsW3CTraceId()
    {
        AssertEx.NotEqual("true", Environment.GetEnvironmentVariable("ASPIRE_ENABLED"),
            "This test asserts the Aspire-OFF path; ASPIRE_ENABLED must not be enabled.");

        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/exception-probe");
        factory.AddNodeBearerToken(request);
        request.Headers.TryAddWithoutValidation("traceparent", InboundTraceparent);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);

        AssertEx.True(document.RootElement.TryGetProperty("traceId", out var traceId),
            "Expected ProblemDetails to contain a traceId extension.");
        var traceIdValue = traceId.GetString();

        // The W3C trace id from the inbound traceparent, NOT the Kestrel connection id (e.g. "0HN...:00000001").
        AssertEx.Equal(InboundTraceId, traceIdValue);
        AssertEx.True(W3CTraceId.IsMatch(traceIdValue!), $"Expected a 32-hex W3C trace id but was \"{traceIdValue}\".");
    }

    // Test matrix: backend_traceResponseHeader_onSuccess.
    [Test]
    public async Task SuccessResponse_WhenInboundTraceparent_CarriesTraceResponseHeader()
    {
        AssertEx.NotEqual("true", Environment.GetEnvironmentVariable("ASPIRE_ENABLED"),
            "This test asserts the Aspire-OFF path; ASPIRE_ENABLED must not be enabled.");

        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = "trace-probe"
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.TryAddWithoutValidation("traceparent", InboundTraceparent);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertEx.True(response.Headers.TryGetValues("traceresponse", out var headerValues),
            "Expected a traceresponse header on the 2xx response.");

        var traceResponse = headerValues!.Single();
        // Format: 00-<32hex traceId>-<16hex spanId>-<flags>.
        var segments = traceResponse.Split('-');
        AssertEx.Equal(expected: 4, segments.Length, $"Expected a W3C traceresponse but was \"{traceResponse}\".");

        var responseTraceId = segments[1];
        AssertEx.True(W3CTraceId.IsMatch(responseTraceId),
            $"Expected a 32-hex W3C trace id in the traceresponse header but was \"{responseTraceId}\".");
        // The response trace id shares the inbound trace id (the request Activity is a child of the inbound parent).
        AssertEx.Equal(InboundTraceId, responseTraceId);
        // This fixture strips every IHostedService, so the OpenTelemetry TracerProvider never starts and the only
        // listener on this source is the scoped AllData one — the request activity is not Recorded and the trace-flags
        // byte is "00" even though the inbound parent was "-01". With the production TracerProvider running the flag
        // would follow the sampler instead (see SuccessResponse_WhenProductionTracerRunningAndInboundSampled...).
        AssertEx.Equal("00", segments[3], $"Expected a not-recorded trace-flags byte but was \"{traceResponse}\".");
    }

    // Test matrix: backend_traceResponseHeader_flagsFollowProductionSampler (recorded branch). Represents the production
    // host: AddServiceDefaults starts the OpenTelemetry TracerProvider, whose default ParentBased(AlwaysOn) sampler
    // records a sampled inbound parent. Forcing the provider to build attaches that listener to "Microsoft.AspNetCore".
    [Test]
    public async Task SuccessResponse_WhenProductionTracerRunningAndInboundSampled_EmitsRecordedFlag()
    {
        AssertEx.NotEqual("true", Environment.GetEnvironmentVariable("ASPIRE_ENABLED"),
            "This test asserts the production tracer-provider path; ASPIRE_ENABLED must not be enabled.");

        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        // Build the production TracerProvider so its ParentBased(AlwaysOn) ActivityListener is attached before the
        // request creates the ASP.NET Core activity. The base fixture strips the OTel hosted service that would
        // otherwise start it (see TestingWebAppFactory.RemoveAll<IHostedService>).
        _ = factory.Services.GetRequiredService<TracerProvider>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = "trace-probe"
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.TryAddWithoutValidation("traceparent", InboundTraceparent);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertEx.True(response.Headers.TryGetValues("traceresponse", out var headerValues),
            "Expected a traceresponse header on the 2xx response.");

        var traceResponse = headerValues!.Single();
        var segments = traceResponse.Split('-');
        AssertEx.Equal(expected: 4, segments.Length, $"Expected a W3C traceresponse but was \"{traceResponse}\".");
        AssertEx.Equal(InboundTraceId, segments[1]);
        // The ParentBased(AlwaysOn) sampler records the sampled inbound parent, so the flag correctly advertises "-01".
        AssertEx.Equal("01", segments[3], $"Expected a recorded trace-flags byte but was \"{traceResponse}\".");
    }

    // Test matrix: backend_traceResponseHeader_flagsFollowProductionSampler (not-recorded branch). Even with the
    // production TracerProvider running, an unsampled inbound parent stays "-00": ParentBased defers to the parent's
    // drop decision so the activity is not recorded, and the flag reflects that.
    [Test]
    public async Task SuccessResponse_WhenProductionTracerRunningAndInboundUnsampled_EmitsNotRecordedFlag()
    {
        AssertEx.NotEqual("true", Environment.GetEnvironmentVariable("ASPIRE_ENABLED"),
            "This test asserts the production tracer-provider path; ASPIRE_ENABLED must not be enabled.");

        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        _ = factory.Services.GetRequiredService<TracerProvider>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = "trace-probe"
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.TryAddWithoutValidation("traceparent", InboundTraceparentUnsampled);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertEx.True(response.Headers.TryGetValues("traceresponse", out var headerValues),
            "Expected a traceresponse header on the 2xx response.");

        var traceResponse = headerValues!.Single();
        var segments = traceResponse.Split('-');
        AssertEx.Equal(expected: 4, segments.Length, $"Expected a W3C traceresponse but was \"{traceResponse}\".");
        AssertEx.Equal(InboundTraceId, segments[1]);
        // ParentBased defers to the unsampled parent's drop decision, so the activity is not recorded and stays "-00".
        AssertEx.Equal("00", segments[3], $"Expected a not-recorded trace-flags byte but was \"{traceResponse}\".");
    }

    // Test matrix: backend_traceResponseHeader_flagsFromRecordedState (recorded branch).
    [Test]
    public void TraceResponseHeader_WhenActivityRecorded_EmitsRecordedFlag()
    {
        using var activity = StartActivity(ActivityTraceFlags.Recorded);

        var traceResponse = TraceResponseHeader.Build(activity);

        var segments = traceResponse.Split('-');
        AssertEx.Equal(expected: 4, segments.Length, $"Expected a W3C traceresponse but was \"{traceResponse}\".");
        AssertEx.Equal(InboundTraceId, segments[1]);
        AssertEx.True(W3CTraceId.IsMatch(segments[1]),
            $"Expected a 32-hex W3C trace id but was \"{segments[1]}\".");
        // A recorded activity advertises "-01" so a downstream reader is correctly told the span was sampled.
        AssertEx.Equal("01", segments[3], $"Expected a recorded trace-flags byte but was \"{traceResponse}\".");
    }

    // Test matrix: backend_traceResponseHeader_flagsFromRecordedState (not-recorded branch).
    [Test]
    public void TraceResponseHeader_WhenActivityNotRecorded_EmitsNotRecordedFlag()
    {
        using var activity = StartActivity(ActivityTraceFlags.None);

        var traceResponse = TraceResponseHeader.Build(activity);

        var segments = traceResponse.Split('-');
        AssertEx.Equal(expected: 4, segments.Length, $"Expected a W3C traceresponse but was \"{traceResponse}\".");
        AssertEx.Equal(InboundTraceId, segments[1]);
        // A not-recorded activity advertises "-00" so a downstream reader is not told the span was sampled when it was not.
        AssertEx.Equal("00", segments[3], $"Expected a not-recorded trace-flags byte but was \"{traceResponse}\".");
    }

    // Builds a started W3C activity carrying the known inbound trace id and the requested recorded state, matching the
    // shape of the request activity the middleware sees (parent trace id preserved, fresh span id).
    private static Activity StartActivity(ActivityTraceFlags flags)
    {
        var traceId = ActivityTraceId.CreateFromString(InboundTraceId.AsSpan());
        var parentSpanId = ActivitySpanId.CreateFromString(InboundSpanId.AsSpan());

        var activity = new Activity("trace-correlation-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.SetParentId(traceId, parentSpanId, flags);
        activity.Start();
        return activity;
    }
}
