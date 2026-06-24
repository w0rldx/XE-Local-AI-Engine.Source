namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The store's <c>Normalize</c> step must clamp every new field independently (an out-of-range value falls back to
///     null so the accessor re-seeds it), trim/validate strings, gate the recommended-tag format, and — crucially — let
///     an old <c>node-settings.json</c> missing all the new fields deserialize to defaults without throwing.
/// </summary>
public sealed class StoredNodeSettingsNormalizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-stored-settings-normalize-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task OldFileMissingNewFields_LoadsToDefaults_WithoutThrowing()
    {
        // A pre-migration file: only the two original fields are present.
        await WriteSettingsJsonAsync("{ \"maxMessageRequestTimeoutSeconds\": 120, \"defaultModelName\": \"legacy-model\" }");
        var loaded = await LoadAsync();

        AssertEx.Equal(expected: 120, loaded.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal("legacy-model", loaded.DefaultModelName);
        AssertEx.Null(loaded.EnableTools);
        AssertEx.Null(loaded.ToolCapableModels);
        AssertEx.Null(loaded.OllamaEndpoint);
        AssertEx.Null(loaded.LlamaMaxLoadedProcesses);
        AssertEx.Null(loaded.RecommendedLlamaCppTag);
        AssertEx.Null(loaded.SamplingDefaults);
    }

    [Test]
    public async Task StoredFields_WithinRange_RoundTrip()
    {
        var saved = new StoredNodeSettings
        {
            DefaultModelName = "  spaced-model  ",
            EnableTools = false,
            ToolCapableModels = ["  qwen3:8b  ", "", "gemma3:12b"],
            OllamaEndpoint = "http://127.0.0.1:12345",
            HuggingFaceDefaultQuant = "Q5_K_M",
            HuggingFaceDiskMarginBytes = 2_000_000_000,
            LlamaMaxLoadedProcesses = 8,
            LlamaIdleTimeToLiveSeconds = 600,
            MaxResponseSizeMb = 50,
            RecommendedLlamaCppTag = "b9999",
            OrchestrationIdleTimeoutSeconds = 300,
            MaxPendingToolCallAgeMinutes = 30
        };
        var loaded = await SaveAndReloadAsync(saved);

        AssertEx.Equal("spaced-model", loaded.DefaultModelName);
        AssertEx.Equal(expected: false, loaded.EnableTools);
        AssertEx.NotNull(loaded.ToolCapableModels);
        AssertEx.Equal(expected: 2, loaded.ToolCapableModels!.Count);
        AssertEx.Equal("qwen3:8b", loaded.ToolCapableModels![0]);
        AssertEx.Equal("http://127.0.0.1:12345", loaded.OllamaEndpoint);
        AssertEx.Equal("Q5_K_M", loaded.HuggingFaceDefaultQuant);
        AssertEx.Equal(expected: 2_000_000_000L, loaded.HuggingFaceDiskMarginBytes);
        AssertEx.Equal(expected: 8, loaded.LlamaMaxLoadedProcesses);
        AssertEx.Equal(expected: 600, loaded.LlamaIdleTimeToLiveSeconds);
        AssertEx.Equal(expected: 50, loaded.MaxResponseSizeMb);
        AssertEx.Equal("b9999", loaded.RecommendedLlamaCppTag);
        AssertEx.Equal(expected: 300, loaded.OrchestrationIdleTimeoutSeconds);
        AssertEx.Equal(expected: 30, loaded.MaxPendingToolCallAgeMinutes);
    }

    [Test]
    [Arguments(0)]
    [Arguments(17)]
    public async Task LlamaMaxLoadedProcesses_OutOfRange_FallsBackToNull(int value)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            LlamaMaxLoadedProcesses = value
        });
        AssertEx.Null(loaded.LlamaMaxLoadedProcesses);
    }

    [Test]
    [Arguments(0)]
    [Arguments(101)]
    public async Task MaxResponseSizeMb_OutOfRange_FallsBackToNull(int value)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            MaxResponseSizeMb = value
        });
        AssertEx.Null(loaded.MaxResponseSizeMb);
    }

    [Test]
    public async Task DiskMarginBytes_NonPositive_FallsBackToNull()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            HuggingFaceDiskMarginBytes = 0
        });
        AssertEx.Null(loaded.HuggingFaceDiskMarginBytes);
    }

    [Test]
    [Arguments("9692")]
    [Arguments("v9692")]
    [Arguments("bxyz")]
    [Arguments("b")]
    public async Task RecommendedLlamaCppTag_Malformed_FallsBackToNull(string tag)
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            RecommendedLlamaCppTag = tag
        });
        AssertEx.Null(loaded.RecommendedLlamaCppTag);
    }

    [Test]
    [Arguments("b1")]
    [Arguments("b9692")]
    [Arguments("b12345")]
    public void RecommendedLlamaCppTag_WellFormed_IsValid(string tag)
    {
        AssertEx.True(StoredNodeSettings.IsValidRecommendedLlamaCppTag(tag));
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(5000)]
    public async Task TimeoutOutOfRange_ClampsTimeoutOnly_KeepsOtherFields(int badTimeout)
    {
        // A single out-of-range timeout must reset ONLY that field to its default, not discard every other setting.
        var saved = new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = badTimeout,
            DefaultModelName = "keep-me",
            EnableTools = false,
            LlamaMaxLoadedProcesses = 8,
            RecommendedLlamaCppTag = "b9999"
        };
        var loaded = await SaveAndReloadAsync(saved);

        AssertEx.Equal(StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds, loaded.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal("keep-me", loaded.DefaultModelName);
        AssertEx.Equal(expected: false, loaded.EnableTools);
        AssertEx.Equal(expected: 8, loaded.LlamaMaxLoadedProcesses);
        AssertEx.Equal("b9999", loaded.RecommendedLlamaCppTag);
    }

    [Test]
    public async Task OllamaEndpoint_NonUrl_FallsBackToNull()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            OllamaEndpoint = "not a url"
        });
        AssertEx.Null(loaded.OllamaEndpoint);
    }

    [Test]
    public async Task ToolCapableModels_AllBlank_FallsBackToNull()
    {
        var loaded = await SaveAndReloadAsync(new StoredNodeSettings
        {
            ToolCapableModels = ["", "   "]
        });
        AssertEx.Null(loaded.ToolCapableModels);
    }

    private async Task<StoredNodeSettings> SaveAndReloadAsync(StoredNodeSettings settings)
    {
        using var store = NewStore();
        await store.SaveAsync(settings);
        return await store.LoadAsync();
    }

    private async Task<StoredNodeSettings> LoadAsync()
    {
        using var store = NewStore();
        return await store.LoadAsync();
    }

    private async Task WriteSettingsJsonAsync(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "node-settings.json"), json);
    }

    private NodeSettingsStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new NodeSettingsStore(new FakeNodeDataDirectory(_root), NullLogger<NodeSettingsStore>.Instance);
    }
}
