namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The apply endpoint's response contract: running work becomes the typed 409 body, and an apply carries the version
///     it installs.
/// </summary>
/// <remarks>
///     The route maps only on a desktop host and the test host is always headless, so the endpoint's mappers are driven
///     directly, as <see cref="AppUpdateContractTests" /> drives the status mapper. FastEndpoints' static unit-test
///     factory is avoided: its service resolver outlives a disposed test host and fails by test order.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ApplyAppUpdateEndpointTests
{
    [Test]
    public void BusyResult_MapsToThe409BodyWithCamelCaseKinds()
    {
        var blocked = ApplyAppUpdateEndpoint.ToBlockedResponse(new AppUpdateApplyResult
        {
            Applying = false,
            BusyItems =
            [
                new AppUpdateBusyItem
                {
                    Kind = AppUpdateBusyKind.TrainingRun
                },
                new AppUpdateBusyItem
                {
                    Kind = AppUpdateBusyKind.ModelDownload,
                    DisplayName = "qwen3-8b.gguf"
                }
            ]
        });

        var body = AssertEx.NotNull(blocked);
        AssertEx.Equal("trainingRun:,modelDownload:qwen3-8b.gguf",
            string.Join(',', body.BusyItems.Select(static item => $"{item.Kind}:{item.DisplayName}")));
        AssertEx.False(string.IsNullOrWhiteSpace(body.Message));

        // The wire names the SPA reads.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
        AssertEx.Equal(2, json.RootElement.GetProperty("busyItems").GetArrayLength());
    }

    [Test]
    public void IdleResult_IsNotBlocked_AndCarriesTheTargetVersion()
    {
        var result = new AppUpdateApplyResult
        {
            Applying = true,
            TargetVersion = "0.3.0"
        };

        AssertEx.Null(ApplyAppUpdateEndpoint.ToBlockedResponse(result));
        var response = ApplyAppUpdateEndpoint.ToResponse(result);
        AssertEx.True(response.Applying);
        AssertEx.Equal("0.3.0", response.TargetVersion);
    }
}
