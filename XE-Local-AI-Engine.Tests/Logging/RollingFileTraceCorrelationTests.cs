namespace XE_Local_AI_Engine.Tests.Logging;

using System.Diagnostics;
using Serilog;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Tests.Testing;

// LOW-001: the rolling file log must carry the W3C TraceId/SpanId so a file log line can be correlated with the trace
// id surfaced to the client in ProblemDetails. Serilog attaches those from the ambient Activity; this asserts the file
// output template actually renders them.
public sealed class RollingFileTraceCorrelationTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "xe-rolling-trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Test]
    public async Task RollingFile_WhenLoggedUnderAnActivity_WritesTheW3CTraceIdAndSpanId()
    {
        using var activity = new Activity("test-request");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        var traceId = activity.TraceId.ToString();
        var spanId = activity.SpanId.ToString();

        // Build the same rolling-file sink production uses, log one line under the active Activity, then dispose to
        // flush the sink before reading the file back.
        using (var logger = new LoggerConfiguration().WriteToRollingFile(_logDirectory).CreateLogger())
        {
            logger.Error("Retention correlation probe line.");
        }

        var logFile = Directory.EnumerateFiles(_logDirectory, "xe-node-*.log").Single();
        var contents = await File.ReadAllTextAsync(logFile).ConfigureAwait(false);

        AssertEx.Contains(contents, traceId);
        AssertEx.Contains(contents, spanId);
    }
}
